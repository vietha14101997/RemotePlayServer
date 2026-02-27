#nullable enable
using System;
using System.Threading;
using NAudio.Wave;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Capture;

/// <summary>
/// Captures system audio output via WASAPI loopback.
/// Delivers 16-bit PCM signed integer samples at the system's default sample rate.
/// When no audio is playing, generates silence frames to keep the RTP stream alive.
/// </summary>
public sealed class DesktopAudioCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _disposed;
    private Timer? _silenceTimer;
    private long _lastDataTimeTicks;
    private volatile bool _silenceActive;
    private long _lastSilenceFrameTicks;

    // Output format: always 16-bit PCM at the device's native sample rate
    private int _sampleRate;
    private int _channels;

    /// <summary>
    /// Fired when audio data is available.
    /// Parameters: (byte[] pcm16Data, int bytesRecorded, int sampleRate, int channels)
    /// </summary>
    public event Action<byte[], int, int, int>? OnAudioData;

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public bool IsCapturing => _running && !_paused;

    /// <summary>
    /// Start capturing system audio via WASAPI loopback.
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopAudioCapture));
        if (_running) return;

        _capture = new WasapiLoopbackCapture();

        // WASAPI loopback typically: 32-bit float, 48000Hz, 2ch
        var waveFormat = _capture.WaveFormat;
        _sampleRate = waveFormat.SampleRate;
        _channels = waveFormat.Channels;

        Logger.Info($"[AudioCapture] WASAPI format: {waveFormat.SampleRate}Hz, {waveFormat.Channels}ch, {waveFormat.BitsPerSample}bit, encoding={waveFormat.Encoding}");

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _capture.StartRecording();
        _running = true;
        _lastDataTimeTicks = Environment.TickCount64;

        // Silence watchdog: fires every 10ms (matching Opus frame duration).
        // Uses 100ms detection threshold before entering silence mode — safe from
        // WASAPI callback delays caused by CPU-intensive video encoding (30ms was too low,
        // 500ms caused RTP timestamp drift during silence periods).
        // Once in silence mode, generates continuous 10ms frames at real-time rate.
        _silenceTimer = new Timer(SilenceWatchdog, null, 100, 10);

        Logger.Info($"[AudioCapture] Started: {_sampleRate}Hz, {_channels}ch");
    }

    /// <summary>
    /// Stop capturing.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        _silenceTimer?.Dispose();
        _silenceTimer = null;

        try { _capture?.StopRecording(); } catch { }
    }

    /// <summary>
    /// Pause audio delivery (capture keeps running but data is discarded).
    /// </summary>
    public void Pause()
    {
        _paused = true;
    }

    /// <summary>
    /// Resume audio delivery after pause.
    /// </summary>
    public void Resume()
    {
        _paused = false;
        _silenceActive = false;
        _lastDataTimeTicks = Environment.TickCount64;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();

        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || _paused || e.BytesRecorded == 0) return;

        _lastDataTimeTicks = Environment.TickCount64;
        _silenceActive = false;

        var waveFormat = _capture!.WaveFormat;

        // Convert float32 -> PCM16 if needed
        if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && waveFormat.BitsPerSample == 32)
        {
            int sampleCount = e.BytesRecorded / 4; // 4 bytes per float sample
            int pcm16Bytes = sampleCount * 2;       // 2 bytes per PCM16 sample
            var pcm16 = new byte[pcm16Bytes];

            unsafe
            {
                fixed (byte* srcPtr = e.Buffer)
                fixed (byte* dstPtr = pcm16)
                {
                    var floats = (float*)srcPtr;
                    var shorts = (short*)dstPtr;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        float sample = floats[i];
                        // Clamp to [-1.0, 1.0] then convert to short
                        if (sample > 1.0f) sample = 1.0f;
                        else if (sample < -1.0f) sample = -1.0f;
                        shorts[i] = (short)(sample * 32767f);
                    }
                }
            }

            OnAudioData?.Invoke(pcm16, pcm16Bytes, _sampleRate, _channels);
        }
        else if (waveFormat.BitsPerSample == 16)
        {
            // Already PCM16 - pass through
            var copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            OnAudioData?.Invoke(copy, e.BytesRecorded, _sampleRate, _channels);
        }
        else
        {
            Logger.Error($"[AudioCapture] Unsupported format: {waveFormat.Encoding}, {waveFormat.BitsPerSample}bit");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Logger.Error($"[AudioCapture] Recording stopped with error: {e.Exception.Message}");
        }
        else
        {
            Logger.Info("[AudioCapture] Recording stopped");
        }
    }

    /// <summary>
    /// Continuous silence frame generator to keep RTP timestamps advancing at real-time rate.
    /// Three phases:
    /// 1. Detection: Wait 100ms after last WASAPI data (avoids false triggers from CPU-delayed callbacks)
    /// 2. Catch-up: On first entering silence mode, batch-send missed frames for the detection gap
    /// 3. Generation: Send one 10ms frame per timer tick to maintain real-time RTP clock rate
    ///
    /// Previous bug: sent only 1 frame per 500ms → RTP clock ran 50x slower during silence,
    /// causing progressive audio drift after silence→sound transitions.
    /// </summary>
    private void SilenceWatchdog(object? state)
    {
        if (!_running || _paused || _disposed) return;

        long now = Environment.TickCount64;
        long elapsed = now - _lastDataTimeTicks;

        // Phase 1: Detection — 100ms threshold avoids false triggers during WASAPI callback delays
        // caused by CPU-intensive video encoding (AMF + capture + GPU scaling).
        // 30ms was too low (caused mid-frame silence injection), 500ms was too high (caused drift).
        if (elapsed < 100)
        {
            _silenceActive = false;
            return;
        }

        // Silence frame: 10ms of zeros matching Opus frame duration
        int samplesPerFrame = _sampleRate * 10 / 1000;
        int bytesPerFrame = samplesPerFrame * _channels * 2;
        var silence = new byte[bytesPerFrame];

        // Phase 2: Catch-up — first time entering silence mode, send missed frames
        // to cover the 100ms detection gap and keep RTP timestamps accurate
        if (!_silenceActive)
        {
            _silenceActive = true;
            int missedFrames = (int)(elapsed / 10);
            for (int i = 0; i < missedFrames; i++)
                OnAudioData?.Invoke(silence, bytesPerFrame, _sampleRate, _channels);
            _lastSilenceFrameTicks = now;
            return;
        }

        // Phase 3: Continuous generation — one 10ms frame per timer tick = real-time rate
        long sinceLast = now - _lastSilenceFrameTicks;
        if (sinceLast >= 8) // Allow slight timer jitter (timer resolution ~10-16ms on Windows)
        {
            OnAudioData?.Invoke(silence, bytesPerFrame, _sampleRate, _channels);
            _lastSilenceFrameTicks = now;

            // Drift compensation: if timer was late, send extra frame to catch up
            if (sinceLast >= 18)
                OnAudioData?.Invoke(silence, bytesPerFrame, _sampleRate, _channels);
        }
    }
}
