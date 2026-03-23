#nullable enable
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Server;

namespace RemotePlayServer.Application.Protocol
{
    public partial class PhaseProtocolHandler
    {
        /// <summary>
        /// Handle cursor update from PerMonitorCapture DXGI Desktop Duplication.
        /// Called from capture thread - stores data for processing in cursor tracking task.
        /// </summary>
        private void HandleDxgiCursorUpdate(int monitorIndex, byte[] buffer, Vortice.DXGI.OutduplPointerShapeInfo shapeInfo,
            Vortice.DXGI.OutduplPointerPosition position, long shapeId)
        {
            lock (_dxgiCursorLock)
            {
                _pendingDxgiCursorBuffer = buffer;
                _pendingDxgiCursorShapeInfo = shapeInfo;
                _pendingDxgiCursorPosition = position;
                _pendingDxgiCursorShapeId = shapeId;
                _pendingDxgiCursorMonitorIndex = monitorIndex;
            }
        }

        /// <summary>
        /// Start cursor tracking task that sends position and image updates to client.
        /// Uses DXGI Desktop Duplication cursor data from PerMonitorCapture instead of GDI+.
        /// </summary>
        private Task StartCursorTrackingTask()
        {
            _cursorCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var ct = _cursorCts.Token;

            // Clear cache at start to ensure fresh cursors each session
            _sentCursorIds.Clear();
            _lastDxgiCursorShapeId = -1;
            long lastSentShapeId = -1; // Track last shapeId for which image was successfully sent

            return Task.Run(async () =>
            {
                const int POLL_INTERVAL_MS = 8; // ~120Hz (faster cursor updates via DataChannel)
                const float THRESHOLD = 0.001f; // Minimum UV change to send update
                const int MIN_CURSOR_IMAGE_INTERVAL_MS = 50; // Max ~20 cursor images/s to avoid WebSocket flood
                var lastCursorImageSendTime = DateTime.MinValue;

                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    try
                    {
                        // Get cursor data from DXGI Desktop Duplication (thread-safe copy)
                        byte[]? cursorBuffer;
                        Vortice.DXGI.OutduplPointerShapeInfo? shapeInfo;
                        Vortice.DXGI.OutduplPointerPosition? position;
                        long shapeId;
                        int monitorIndex;

                        lock (_dxgiCursorLock)
                        {
                            cursorBuffer = _pendingDxgiCursorBuffer;
                            shapeInfo = _pendingDxgiCursorShapeInfo;
                            position = _pendingDxgiCursorPosition;
                            shapeId = _pendingDxgiCursorShapeId;
                            monitorIndex = _pendingDxgiCursorMonitorIndex;
                        }

                        // Wait for DXGI data before processing
                        if (cursorBuffer == null || !shapeInfo.HasValue || !position.HasValue || monitorIndex < 0)
                        {
                            await Task.Delay(POLL_INTERVAL_MS, ct);
                            continue;
                        }

                        // Use Win32 GetCursorInfo to detect if cursor is truly visible.
                        // DXGI Visible flag only means "cursor moved this frame", NOT "cursor is shown".
                        // Apps/games can hide the cursor (e.g., fullscreen video, 3D games, mouse idle).
                        // GetCursorInfo CURSOR_SHOWING flag reflects the actual OS cursor visibility.
                        bool visible = InputInjector.IsCursorVisible();

                        // Calculate UV coordinates from screen position
                        // DXGI cursor Position is RELATIVE to the monitor (not desktop coordinates!)
                        // Each output's Desktop Duplication reports cursor position relative to that output
                        // So we do NOT subtract monitor origin - just normalize to 0-1 range
                        float u = _lastCursorU, v = _lastCursorV;
                        if (monitorIndex < _monitorRects.Count)
                        {
                            var rect = _monitorRects[monitorIndex];
                            // Position is already relative to monitor - just normalize
                            u = (float)position.Value.Position.X / rect.w;
                            v = (float)position.Value.Position.Y / rect.h;
                            // Clamp to valid range
                            u = Math.Clamp(u, 0f, 1f);
                            v = Math.Clamp(v, 0f, 1f);
                        }

                        // Check if cursor shape changed (need to send new image)
                        bool shapeChanged = shapeId != _lastDxgiCursorShapeId;

                        // Check if position changed significantly
                        bool positionChanged = monitorIndex != _lastCursorMonitor ||
                                               visible != _lastCursorVisible ||
                                               (Math.Abs(u - _lastCursorU) > THRESHOLD || Math.Abs(v - _lastCursorV) > THRESHOLD);

                        // Send cursor image if shape changed and we haven't sent it yet.
                        // With content-based hashing, _sentCursorIds works correctly:
                        // same visual cursor → same shapeId → already in cache → skip.
                        // Rate-limited to avoid flooding WebSocket (cursor images are ~6KB each).
                        if (shapeChanged && visible && !_sentCursorIds.Contains(shapeId))
                        {
                            var timeSinceLastImage = (DateTime.UtcNow - lastCursorImageSendTime).TotalMilliseconds;
                            if (timeSinceLastImage >= MIN_CURSOR_IMAGE_INTERVAL_MS)
                            {
                                var converted = CursorConverter.ConvertDxgiCursorToRgba(cursorBuffer, shapeInfo.Value);
                                if (converted.HasValue)
                                {
                                    var (rgbaData, width, height, hotspotX, hotspotY) = converted.Value;

                                    // CRITICAL: Force exact size to prevent client distortion
                                    int expectedSize = width * height * 4;
                                    if (rgbaData.Length != expectedSize)
                                    {
                                        var fixedData = new byte[expectedSize];
                                        Array.Copy(rgbaData, fixedData, Math.Min(rgbaData.Length, expectedSize));
                                        rgbaData = fixedData;
                                    }

                                    var base64 = Convert.ToBase64String(rgbaData);

                                    var imgMsg = new CursorImageMessage
                                    {
                                        CursorId = shapeId,
                                        CursorTypeValue = (int)shapeInfo.Value.Type,
                                        Width = width,
                                        Height = height,
                                        HotspotX = hotspotX,
                                        HotspotY = hotspotY,
                                        ImageBase64 = base64
                                    };
                                    await SendMessageAsync(imgMsg);
                                    _sentCursorIds.Add(shapeId);
                                    lastSentShapeId = shapeId;
                                    lastCursorImageSendTime = DateTime.UtcNow;
                                }
                            }
                            // If throttled: don't add to _sentCursorIds, so it retries next poll.
                            // Position update below uses lastSentShapeId so client references a cached cursor.
                        }
                        else if (_sentCursorIds.Contains(shapeId))
                        {
                            // Already sent this cursor image before — update lastSentShapeId
                            lastSentShapeId = shapeId;
                        }

                        if (positionChanged || shapeChanged)
                        {
                            _lastCursorMonitor = monitorIndex;
                            _lastCursorU = u;
                            _lastCursorV = v;
                            _lastCursorVisible = visible;
                            _lastDxgiCursorShapeId = shapeId;

                            // Use lastSentShapeId for position updates so client always
                            // references a cursor it already has in cache.
                            // If no image sent yet (-1), fall back to current shapeId.
                            long positionCursorId = lastSentShapeId != -1 ? lastSentShapeId : shapeId;

                            // Prefer DataChannel (UDP-like, low latency) over WebSocket (TCP)
                            if (_streamer?.HasCursorChannel == true)
                            {
                                _streamer.SendCursorPosition(monitorIndex, u, v, visible,
                                    (int)shapeInfo.Value.Type, positionCursorId);
                            }
                            else
                            {
                                // Fallback: WebSocket (for older clients without cursor DC)
                                var msg = new CursorPositionMessage
                                {
                                    MonitorIndex = monitorIndex,
                                    U = u,
                                    V = v,
                                    Visible = visible,
                                    CursorTypeValue = (int)shapeInfo.Value.Type,
                                    CursorId = positionCursorId
                                };
                                await SendMessageAsync(msg);
                            }
                        }

                        await Task.Delay(POLL_INTERVAL_MS, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Cursor] Tracking error: {ex.Message}");
                    }
                }
            }, ct);
        }

        /// <summary>
        /// Stop cursor tracking task and clear sent cursor cache.
        /// </summary>
        private void StopCursorTracking()
        {
            try { _cursorCts?.Cancel(); } catch { }
            _sentCursorIds.Clear();
        }
    }
}
