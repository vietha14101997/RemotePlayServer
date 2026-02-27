#nullable enable
using System;
using Concentus;
using Concentus.Enums;
using RemotePlayServer.Core;

namespace RemotePlayServer.Infrastructure.Encoding;

/// <summary>
/// Encodes 16-bit PCM audio to Opus packets for WebRTC streaming.
/// Accumulates PCM into exact 20ms frames before encoding.
/// Uses Concentus (pure C# Opus encoder) which is already a transitive dependency via SIPSorcery.
/// </summary>
public sealed class OpusAudioEncoder : IDisposable
{
    public const int SAMPLE_RATE = 48000;
    public const int CHANNELS = 2;
    public const int FRAME_DURATION_MS = 10;
    public const int SAMPLES_PER_FRAME = SAMPLE_RATE * FRAME_DURATION_MS / 1000; // 480
    public const int BYTES_PER_FRAME = SAMPLES_PER_FRAME * CHANNELS * 2;          // 1920 (16-bit stereo)
    public const uint RTP_DURATION_PER_FRAME = (uint)(SAMPLE_RATE * FRAME_DURATION_MS / 1000); // 480

    private readonly IOpusEncoder _encoder;
    private readonly byte[] _frameBuffer;
    private int _frameBufferOffset;
    private readonly short[] _pcmShortBuffer;
    private readonly byte[] _opusOutputBuffer;
    private bool _disposed;

    /// <summary>
    /// Fired when an Opus packet is ready to send.
    /// Parameters: (byte[] opusData, int opusLength, uint rtpDuration)
    /// </summary>
    public event Action<byte[], int, uint>? OnEncodedAudio;

    public OpusAudioEncoder()
    {
        _encoder = OpusCodecFactory.CreateEncoder(SAMPLE_RATE, CHANNELS, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = 128000; // 128 kbps - good quality for desktop audio
        _encoder.Complexity = 5;   // Balance quality/CPU
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;

        _frameBuffer = new byte[BYTES_PER_FRAME];
        _frameBufferOffset = 0;
        _pcmShortBuffer = new short[SAMPLES_PER_FRAME * CHANNELS]; // 1920 shorts
        _opusOutputBuffer = new byte[4000]; // Max Opus packet size

        Logger.Info($"[OpusEncoder] Created: {SAMPLE_RATE}Hz, {CHANNELS}ch, {_encoder.Bitrate}bps, frame={FRAME_DURATION_MS}ms ({BYTES_PER_FRAME}B)");
    }

    /// <summary>
    /// Feed PCM 16-bit data. Encoded Opus packets are delivered via OnEncodedAudio.
    /// Handles accumulation of partial frames.
    /// </summary>
    /// <param name="pcm16Data">16-bit signed PCM data (little-endian)</param>
    /// <param name="length">Number of bytes in pcm16Data</param>
    /// <param name="inputSampleRate">Sample rate of input (will be skipped if != 48000)</param>
    /// <param name="inputChannels">Channel count of input</param>
    public void EncodePcm(byte[] pcm16Data, int length, int inputSampleRate, int inputChannels)
    {
        if (_disposed || length <= 0) return;

        // Skip if sample rate doesn't match (resample not implemented yet)
        if (inputSampleRate != SAMPLE_RATE)
        {
            // TODO: Implement resampling if needed
            // Most Windows systems use 48kHz for default audio output
            return;
        }

        // Handle mono -> stereo or channel mismatch
        byte[] data = pcm16Data;
        int dataLength = length;
        if (inputChannels == 1 && CHANNELS == 2)
        {
            // Mono to stereo: duplicate each sample
            dataLength = length * 2;
            data = new byte[dataLength];
            for (int i = 0; i < length; i += 2)
            {
                data[i * 2] = pcm16Data[i];
                data[i * 2 + 1] = pcm16Data[i + 1];
                data[i * 2 + 2] = pcm16Data[i];
                data[i * 2 + 3] = pcm16Data[i + 1];
            }
        }
        else if (inputChannels != CHANNELS)
        {
            return; // Unsupported channel config
        }

        int offset = 0;
        while (offset < dataLength)
        {
            int needed = BYTES_PER_FRAME - _frameBufferOffset;
            int available = dataLength - offset;
            int toCopy = Math.Min(needed, available);

            Buffer.BlockCopy(data, offset, _frameBuffer, _frameBufferOffset, toCopy);
            _frameBufferOffset += toCopy;
            offset += toCopy;

            // Full frame ready -> encode
            if (_frameBufferOffset >= BYTES_PER_FRAME)
            {
                EncodeFrame();
                _frameBufferOffset = 0;
            }
        }
    }

    private void EncodeFrame()
    {
        try
        {
            // Convert byte[] to short[] (16-bit PCM)
            Buffer.BlockCopy(_frameBuffer, 0, _pcmShortBuffer, 0, BYTES_PER_FRAME);

            int encodedBytes = _encoder.Encode(
                _pcmShortBuffer.AsSpan(0, SAMPLES_PER_FRAME * CHANNELS),
                SAMPLES_PER_FRAME,
                _opusOutputBuffer.AsSpan(),
                _opusOutputBuffer.Length);

            if (encodedBytes > 0)
            {
                OnEncodedAudio?.Invoke(_opusOutputBuffer, encodedBytes, RTP_DURATION_PER_FRAME);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[OpusEncoder] Encode error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frameBufferOffset = 0;
        // OpusEncoder doesn't implement IDisposable but we clear our state
        Logger.Info("[OpusEncoder] Disposed");
    }
}
