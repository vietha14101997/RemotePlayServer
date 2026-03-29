#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RemotePlayServer.Core;
using SIPSorceryMedia.Abstractions;
using Vortice.Direct3D11;

namespace RemotePlayServer.Application.Streaming;

public class HostStreamConfig
{
    public int MonitorCount { get; set; }
    public VideoCodec Codec { get; set; }
    public int ResolutionHeight { get; set; }
    public int Fps { get; set; }
    public List<Core.Models.MonitorInfoDto> Monitors { get; set; } = new();
}

/// <summary>
/// Quality preset for viewers. Controls frame skip ratio.
/// </summary>
public enum ViewerQuality
{
    High,   // Same as host — no skip
    Medium, // Skip every other P-frame (half FPS, min 30fps)
    Low     // Same as Medium for now (floor at 30fps)
}

/// <summary>
/// Per-viewer state: streamer + quality + frame counter.
/// </summary>
public class ViewerEntry
{
    public SIPSorceryStreamer Streamer { get; set; } = null!;
    public ViewerQuality Quality { get; set; } = ViewerQuality.High;
    public long FrameCounter { get; set; }

    /// <summary>
    /// Frame skip ratio based on quality:
    /// High=1 (send all), Medium=2 (skip every other), Low=3 (send 1/3)
    /// </summary>
    /// <summary>
    /// Frame skip ratio. Max skip = 2 (half FPS) to keep minimum 30fps when host is 60fps.
    /// </summary>
    public int SkipRatio => Quality switch
    {
        ViewerQuality.High => 1,    // 60fps → 60fps
        ViewerQuality.Medium => 2,  // 60fps → 30fps
        ViewerQuality.Low => 2,     // 60fps → 30fps (floor)
        _ => 1
    };
}

/// <summary>
/// Shared encoding manager: host encodes once, viewers receive with adaptive frame skip.
/// Keyframes always sent (decoder needs them). P-frames skipped based on viewer quality preset.
/// </summary>
public class SharedEncoderManager
{
    private static volatile SharedEncoderManager? _instance;
    public static SharedEncoderManager? Instance => _instance;

    private SIPSorceryStreamer? _hostStreamer;
    private HostStreamConfig? _hostConfig;
    private ID3D11Device? _hostDevice;
    private readonly ConcurrentDictionary<Guid, ViewerEntry> _viewers = new();

    public static void Initialize()
    {
        _instance = new SharedEncoderManager();
    }

    public void SetHostStreamer(SIPSorceryStreamer hostStreamer)
    {
        if (_hostStreamer != null)
            _hostStreamer.OnEncodedFrameAvailable -= FanOutFrame;

        _hostStreamer = hostStreamer;
        _hostStreamer.OnEncodedFrameAvailable += FanOutFrame;

        Logger.Info($"[SharedEncoder] Host streamer set, {_viewers.Count} viewers waiting");
    }

    public void SetHostConfig(HostStreamConfig config, ID3D11Device? device)
    {
        _hostConfig = config;
        _hostDevice = device;
        Logger.Info($"[SharedEncoder] Host config: {config.MonitorCount}mon, {config.Codec}, {config.ResolutionHeight}p@{config.Fps}fps");
    }

    public HostStreamConfig? GetHostConfig() => _hostConfig;
    public ID3D11Device? GetHostDevice() => _hostDevice;

    public void AddViewer(Guid clientId, SIPSorceryStreamer viewerStreamer, ViewerQuality quality = ViewerQuality.High)
    {
        _viewers[clientId] = new ViewerEntry
        {
            Streamer = viewerStreamer,
            Quality = quality,
            FrameCounter = 0
        };
        Logger.Info($"[SharedEncoder] Viewer {clientId} added (quality={quality}, skip={GetSkipInfo(quality)}, total={_viewers.Count})");
    }

    public void SetViewerQuality(Guid clientId, ViewerQuality quality)
    {
        if (_viewers.TryGetValue(clientId, out var entry))
        {
            entry.Quality = quality;
            entry.FrameCounter = 0; // Reset counter on quality change
            Logger.Info($"[SharedEncoder] Viewer {clientId} quality → {quality} (skip={GetSkipInfo(quality)})");
        }
    }

    public void RemoveViewer(Guid clientId)
    {
        _viewers.TryRemove(clientId, out _);
        Logger.Info($"[SharedEncoder] Viewer {clientId} removed (remaining={_viewers.Count})");
    }

    public void RemoveHost()
    {
        if (_hostStreamer != null)
        {
            _hostStreamer.OnEncodedFrameAvailable -= FanOutFrame;
            _hostStreamer = null;
        }
        _hostConfig = null;
        _hostDevice = null;
        Logger.Info("[SharedEncoder] Host removed");
    }

    public bool HasHost => _hostStreamer != null && _hostConfig != null;
    public int ViewerCount => _viewers.Count;

    /// <summary>
    /// Fan-out encoded frame to all viewers with per-viewer frame skip.
    /// Keyframes (IDR) always sent — decoder needs them for sync.
    /// P-frames skipped based on viewer's quality preset.
    /// </summary>
    private void FanOutFrame(int trackIndex, byte[] nalBytes, bool isKeyframe, byte[]? paramSets)
    {
        foreach (var kvp in _viewers)
        {
            var entry = kvp.Value;
            try
            {
                if (isKeyframe)
                {
                    // Always send keyframes (IDR) — decoder needs them
                    entry.Streamer.SendPreEncodedFrame(trackIndex, nalBytes, true, paramSets);
                    entry.FrameCounter = 0; // Reset counter after keyframe
                }
                else
                {
                    // P-frame: apply skip ratio
                    entry.FrameCounter++;
                    if (entry.SkipRatio <= 1 || entry.FrameCounter % entry.SkipRatio == 1)
                    {
                        entry.Streamer.SendPreEncodedFrame(trackIndex, nalBytes, false, null);
                    }
                    // else: skip this P-frame for this viewer (save bandwidth)
                }
            }
            catch { }
        }
    }

    private static string GetSkipInfo(ViewerQuality q) => q switch
    {
        ViewerQuality.High => "none (full FPS)",
        ViewerQuality.Medium => "1/2 P-frames (half FPS, min 30fps)",
        ViewerQuality.Low => "1/2 P-frames (half FPS, min 30fps)",
        _ => "unknown"
    };

    public static void Shutdown()
    {
        _instance?.RemoveHost();
        _instance?._viewers.Clear();
        _instance = null;
    }
}
