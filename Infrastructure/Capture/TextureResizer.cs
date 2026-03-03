#nullable enable
using System;
using System.Collections.Generic;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Capture
{
    /// <summary>
    /// GPU-accelerated texture resizer using Compute Shader.
    /// Resizes textures to a target resolution (default: 1080p) while maintaining aspect ratio.
    /// Uses bilinear filtering for high-quality scaling.
    /// Supports multiple D3D11 devices (one scaler per device).
    /// 
    /// The server always outputs at the target height (default 1080p).
    /// Client can request a different target height during streaming via update_config.
    /// </summary>
    public class TextureResizer : IDisposable
    {
        private readonly int _monitorCount;

        /// <summary>
        /// Target output height in pixels. The width is calculated proportionally.
        /// Default: 1080 (1080p output regardless of source resolution).
        /// </summary>
        public int TargetHeight { get; private set; }

        // Per-device GPU scalers (key = device pointer)
        private readonly Dictionary<IntPtr, GpuTextureScaler> _scalers = new();
        private readonly HashSet<IntPtr> _failedDevices = new();
        private readonly object _lock = new();

        private bool _disposed;

        /// <summary>
        /// Default output height: 1080p.
        /// </summary>
        public const int DEFAULT_TARGET_HEIGHT = 1080;

        public TextureResizer(int monitorCount, int targetHeight = DEFAULT_TARGET_HEIGHT)
        {
            _monitorCount = monitorCount;
            TargetHeight = Math.Max(targetHeight, 240); // minimum 240p
            Logger.Info($"[TextureResizer] Initialized for {monitorCount} monitors, targetHeight={TargetHeight}p");
        }

        /// <summary>
        /// Dynamically update target resolution during streaming.
        /// Called when client sends update_config with a new resolutionHeight.
        /// </summary>
        public void UpdateTargetHeight(int newTargetHeight)
        {
            int clamped = Math.Clamp(newTargetHeight, 240, 4320); // 240p to 8K
            Logger.Info($"[TextureResizer] Target height changed: {TargetHeight}p → {clamped}p");
            TargetHeight = clamped;

            // Clear cached scalers - they'll be recreated with new dimensions on next frame
            lock (_lock)
            {
                foreach (var scaler in _scalers.Values)
                {
                    try { scaler.Dispose(); } catch { }
                }
                _scalers.Clear();
                _failedDevices.Clear();
            }
        }

        /// <summary>
        /// Check if resize is needed (source height differs from target).
        /// </summary>
        public bool NeedsResize(int width, int height)
        {
            return height != TargetHeight;
        }

        /// <summary>
        /// Calculate target dimensions by scaling proportionally to TargetHeight.
        /// Ensures even dimensions (required by many encoders).
        /// </summary>
        public (int targetWidth, int targetHeight) CalculateTargetSize(int width, int height)
        {
            if (height == TargetHeight)
                return (width, height); // Already at target

            // Scale proportionally based on height ratio
            double scale = (double)TargetHeight / height;
            int targetWidth = (int)(width * scale);
            int targetHeight = TargetHeight;

            // Ensure even dimensions (required by many encoders)
            targetWidth = (targetWidth + 1) & ~1;
            targetHeight = (targetHeight + 1) & ~1;

            // Ensure minimum dimensions
            targetWidth = Math.Max(targetWidth, 2);
            targetHeight = Math.Max(targetHeight, 2);

            return (targetWidth, targetHeight);
        }

        /// <summary>
        /// Get or create a GPU scaler for the specified device.
        /// </summary>
        private GpuTextureScaler? GetOrCreateScaler(ID3D11Device device, int monitorIndex)
        {
            var devicePtr = device.NativePointer;
            
            lock (_lock)
            {
                // Check if already failed for this device
                if (_failedDevices.Contains(devicePtr))
                    return null;
                
                // Check if scaler already exists
                if (_scalers.TryGetValue(devicePtr, out var existingScaler))
                    return existingScaler;
                
                // Create new scaler for this device
                try
                {
                    var scaler = new GpuTextureScaler(device, monitorIndex);
                    _scalers[devicePtr] = scaler;
                    Logger.Info($"[TextureResizer] Created GPU scaler for monitor {monitorIndex} (device={devicePtr:X})");
                    return scaler;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[TextureResizer] Failed to create scaler for monitor {monitorIndex}: {ex.Message}");
                    _failedDevices.Add(devicePtr);
                    return null;
                }
            }
        }

        /// <summary>
        /// Resize a BGRA texture to TargetHeight.
        /// Returns (resizedTexture, targetWidth, targetHeight).
        /// If no resize needed, returns (originalTexture, originalWidth, originalHeight).
        /// Uses GPU Compute Shader for high-quality bilinear scaling.
        /// </summary>
        public (ID3D11Texture2D texture, int width, int height) ResizeBgraTexture(
            ID3D11Device device, ID3D11Texture2D sourceTexture, int sourceWidth, int sourceHeight, int monitorIndex)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TextureResizer));

            // Check if resize is needed
            if (!NeedsResize(sourceWidth, sourceHeight))
            {
                return (sourceTexture, sourceWidth, sourceHeight);
            }

            // Calculate target size
            (int targetWidth, int targetHeight) = CalculateTargetSize(sourceWidth, sourceHeight);

            // Ensure target size is different from source
            if (targetWidth == sourceWidth && targetHeight == sourceHeight)
            {
                return (sourceTexture, sourceWidth, sourceHeight);
            }

            // Get or create scaler for this device
            var scaler = GetOrCreateScaler(device, monitorIndex);
            if (scaler == null)
            {
                return (sourceTexture, sourceWidth, sourceHeight);
            }

            // Perform GPU scaling
            var scaled = scaler.Scale(sourceTexture, sourceWidth, sourceHeight, targetWidth, targetHeight, monitorIndex);
            
            if (scaled != null)
            {
                return (scaled, targetWidth, targetHeight);
            }

            // Fallback to original if scaling failed
            return (sourceTexture, sourceWidth, sourceHeight);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                foreach (var scaler in _scalers.Values)
                {
                    try { scaler.Dispose(); } catch { }
                }
                _scalers.Clear();
                _failedDevices.Clear();
            }
        }
    }
}
