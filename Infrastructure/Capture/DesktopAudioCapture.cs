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

        // Silence watchdog: check every 20ms, send silence if no data for 500ms.
        // 500ms threshold prevents false triggers when WASAPI callbacks are delayed
        // by CPU-intensive video encoding (AMF + capture + GPU scaling).
        // Too-low threshold (e.g. 30ms) causes silence injection mid-frame,
        // producing distorted audio and >50 Opus packets/sec (growing jitter buffer delay).
        _silenceTimer = new Timer(SilenceWatchdog, null, 100, 20);

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
    /// Send silence when WASAPI loopback has no data (no system audio playing).
    /// This keeps RTP timestamps progressing so the client doesn't think the stream died.
    /// </summary>
    private void SilenceWatchdog(object? state)
    {
        if (!_running || _paused || _disposed) return;

        long elapsed = Environment.TickCount64 - _lastDataTimeTicks;
        if (elapsed < 500) return; // Only send silence if no data for 500ms (system truly silent)

        // Generate 10ms of silence (PCM16) — matches Opus 10ms frame duration
        // 10ms at 48000Hz, 2ch, 16-bit = 480 samples * 2ch * 2 bytes = 1920 bytes
        int samplesPerFrame = _sampleRate * 10 / 1000;
        int bytesPerFrame = samplesPerFrame * _channels * 2; // 16-bit = 2 bytes
        var silence = new byte[bytesPerFrame]; // All zeros = silence

        _lastDataTimeTicks = Environment.TickCount64;
        OnAudioData?.Invoke(silence, bytesPerFrame, _sampleRate, _channels);
    }
}
