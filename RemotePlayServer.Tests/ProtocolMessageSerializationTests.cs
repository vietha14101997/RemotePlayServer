using System.Text.Json;
using RemotePlayServer.Core.Models;

namespace RemotePlayServer.Tests;

public class ProtocolMessageSerializationTests
{
    // ==================== GetMessageType ====================

    [Fact]
    public void GetMessageType_ExtractsTypeFromJson()
    {
        var json = """{"type":"hardware_info","device":{}}""";
        Assert.Equal("hardware_info", ProtocolMessageParser.GetMessageType(json));
    }

    [Fact]
    public void GetMessageType_ReturnsNull_ForInvalidJson()
    {
        Assert.Null(ProtocolMessageParser.GetMessageType("not json"));
    }

    [Fact]
    public void GetMessageType_ReturnsNull_ForMissingType()
    {
        Assert.Null(ProtocolMessageParser.GetMessageType("""{"foo":"bar"}"""));
    }

    // ==================== Round-trip Serialization ====================

    [Fact]
    public void HardwareInfoMessage_RoundTrip()
    {
        var msg = new HardwareInfoMessage
        {
            Device = new DeviceInfo
            {
                Name = "TestPC",
                Processor = "Intel i9-13900K",
                Gpu = "NVIDIA RTX 4090",
                GpuVramGB = 24,
                RamGB = 64,
                Os = "Windows 11"
            },
            Encoder = new EncoderInfo { Type = "NVENC", HwAccel = true },
            Monitors = new List<MonitorInfoDto>
            {
                new() { Id = 0, Name = "Monitor 1", Width = 1920, Height = 1080, RefreshRate = 60 }
            }
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<HardwareInfoMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal("hardware_info", parsed!.Type);
        Assert.Equal("TestPC", parsed.Device.Name);
        Assert.Equal("NVENC", parsed.Encoder.Type);
        Assert.True(parsed.Encoder.HwAccel);
        Assert.Single(parsed.Monitors);
        Assert.Equal(1920, parsed.Monitors[0].Width);
    }

    // ==================== Phase 5: ICE Restart (F8 capability + messages) ====================

    [Fact]
    public void HardwareInfoMessage_SupportsIceRestart_SerializesAsCamelCaseKey()
    {
        // F8: wire key MUST be exactly "supportsIceRestart" (camelCase) to match every other
        // hardware_info field AND the Android client (`@Json(name="supportsIceRestart")`).
        var msg = new HardwareInfoMessage { SupportsIceRestart = true };

        var json = ProtocolMessageParser.Serialize(msg);

        Assert.Contains("\"supportsIceRestart\":true", json);
    }

    [Fact]
    public void HardwareInfoMessage_SupportsIceRestart_RoundTrips()
    {
        var msg = new HardwareInfoMessage { SupportsIceRestart = true };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<HardwareInfoMessage>(json);

        Assert.NotNull(parsed);
        Assert.True(parsed!.SupportsIceRestart);
    }

    [Fact]
    public void IceRestartOfferMessage_RoundTrip()
    {
        var msg = new IceRestartOfferMessage { Sdp = "v=0\r\no=- 1 2 IN IP4 0.0.0.0\r\n" };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<IceRestartOfferMessage>(json);

        Assert.Equal("ice_restart_offer", ProtocolMessageParser.GetMessageType(json));
        Assert.NotNull(parsed);
        Assert.Equal(msg.Sdp, parsed!.Sdp);
    }

    [Fact]
    public void IceRestartAnswerMessage_RoundTrip()
    {
        var msg = new IceRestartAnswerMessage { Sdp = "v=0\r\no=- 2 3 IN IP4 0.0.0.0\r\n" };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<IceRestartAnswerMessage>(json);

        Assert.Equal("ice_restart_answer", ProtocolMessageParser.GetMessageType(json));
        Assert.NotNull(parsed);
        Assert.Equal(msg.Sdp, parsed!.Sdp);
    }

    [Fact]
    public void QualityFeedbackMessage_RoundTrip()
    {
        var msg = new QualityFeedbackMessage
        {
            Timestamp = 1234567890,
            RttMs = 15.5f,
            AvgRttMs = 16.2f,
            JitterMs = 2.1f,
            PacketLossRate = 0.01f,
            AvgPacketLossRate = 0.015f,
            EffectiveFps = 58.5f,
            TargetFps = 60,
            FrameLatencyMs = 8.3f,
            BufferStatus = "healthy",
            ConnectionHealth = 95,
            IsWiFi = true,
            Monitors = new List<MonitorFeedback>
            {
                new() { Index = 0, RenderedFrames = 58, DroppedFrames = 2, RealFrames = 60 }
            }
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<QualityFeedbackMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal("quality_feedback", parsed!.Type);
        Assert.Equal(15.5f, parsed.RttMs);
        Assert.Equal(0.01f, parsed.PacketLossRate);
        Assert.Equal("healthy", parsed.BufferStatus);
        Assert.True(parsed.IsWiFi);
        Assert.Single(parsed.Monitors!);
        Assert.Equal(2, parsed.Monitors![0].DroppedFrames);
    }

    [Fact]
    public void SuggestedConfigMessage_RoundTrip()
    {
        var msg = new SuggestedConfigMessage
        {
            Monitors = 3,
            Resolution = new ResolutionDto { Width = 1440, Height = 810 },
            BitrateKbps = 30000,
            Fps = 60,
            RefreshRate = 60,
            SelectedCodec = "H265",
            ConnectionType = "LAN",
            NetworkInfo = new NetworkInfoDto
            {
                PingMs = 1.5,
                JitterMs = 0.3,
                BandwidthMbps = 900,
                IsUsbMode = false
            }
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<SuggestedConfigMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal("suggested_config", parsed!.Type);
        Assert.Equal(3, parsed.Monitors);
        Assert.Equal(1440, parsed.Resolution.Width);
        Assert.Equal("H265", parsed.SelectedCodec);
        Assert.Equal("LAN", parsed.ConnectionType);
        Assert.NotNull(parsed.NetworkInfo);
        Assert.Equal(900, parsed.NetworkInfo!.BandwidthMbps);
    }

    [Fact]
    public void DisplayConfigMessage_RoundTrip()
    {
        var msg = new DisplayConfigMessage
        {
            Monitors = 2,
            Resolution = new ResolutionDto { Width = 1920, Height = 1080 },
            RefreshRate = 120,
            BitrateKbps = 25000,
            Fps = 60,
            PreferGpu = "NVIDIA"
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<DisplayConfigMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal("display_config", parsed!.Type);
        Assert.Equal(2, parsed.Monitors);
        Assert.Equal(120, parsed.RefreshRate);
        Assert.Equal("NVIDIA", parsed.PreferGpu);
    }

    [Fact]
    public void CursorPositionMessage_RoundTrip()
    {
        var msg = new CursorPositionMessage
        {
            MonitorIndex = 1,
            U = 0.5f,
            V = 0.75f,
            Visible = true,
            CursorTypeValue = (int)CursorType.Hand,
            CursorId = 12345
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<CursorPositionMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal("cursor_position", parsed!.Type);
        Assert.Equal(1, parsed.MonitorIndex);
        Assert.Equal(0.5f, parsed.U);
        Assert.Equal(0.75f, parsed.V);
        Assert.True(parsed.Visible);
        Assert.Equal((int)CursorType.Hand, parsed.CursorTypeValue);
    }

    [Fact]
    public void OfferMessage_RoundTrip()
    {
        var msg = new OfferMessage
        {
            MonitorIndex = 2,
            Sdp = "v=0\r\no=- 12345 2 IN IP4 127.0.0.1\r\n"
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<OfferMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal("offer", parsed!.Type);
        Assert.Equal(2, parsed.MonitorIndex);
        Assert.Contains("v=0", parsed.Sdp);
    }

    [Fact]
    public void ErrorMessage_RoundTrip()
    {
        var msg = new ErrorMessage
        {
            Phase = 2,
            Code = "CAPTURE_FAILED",
            Message = "Failed to initialize DXGI capture"
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<ErrorMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal("error", parsed!.Type);
        Assert.Equal(2, parsed.Phase);
        Assert.Equal("CAPTURE_FAILED", parsed.Code);
    }

    // ==================== Base64 Encoding Safety ====================

    [Fact]
    public void CursorImageMessage_Base64PlusSign_NotEscaped()
    {
        // The '+' character in base64 must NOT be escaped to \u002B
        var msg = new CursorImageMessage
        {
            CursorId = 1,
            CursorTypeValue = 1,
            Width = 32,
            Height = 32,
            ImageBase64 = "abc+def/ghi=="  // Contains '+' and '/'
        };

        var json = ProtocolMessageParser.Serialize(msg);

        // Verify '+' is NOT escaped
        Assert.Contains("abc+def/ghi==", json);
        Assert.DoesNotContain(@"\u002B", json);
    }

    // ==================== Null/Missing Optional Fields ====================

    [Fact]
    public void QualityFeedbackMessage_NullMonitors_Deserializes()
    {
        var json = """{"type":"quality_feedback","rttMs":10,"packetLossRate":0,"effectiveFps":60,"targetFps":60,"bufferStatus":"healthy"}""";
        var parsed = ProtocolMessageParser.Parse<QualityFeedbackMessage>(json);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Monitors);
    }

    [Fact]
    public void SuggestedConfigMessage_NullNetworkInfo_Deserializes()
    {
        var json = """{"type":"suggested_config","monitors":3,"bitrateKbps":20000,"fps":60}""";
        var parsed = ProtocolMessageParser.Parse<SuggestedConfigMessage>(json);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.NetworkInfo);
    }

    [Fact]
    public void Parse_ReturnsNull_ForInvalidJson()
    {
        var result = ProtocolMessageParser.Parse<HardwareInfoMessage>("not valid json");
        Assert.Null(result);
    }

    [Fact]
    public void ParseDocument_ReturnsNull_ForInvalidJson()
    {
        var result = ProtocolMessageParser.ParseDocument("{invalid}");
        Assert.Null(result);
    }

    [Fact]
    public void ParseDocument_ReturnsDocument_ForValidJson()
    {
        var result = ProtocolMessageParser.ParseDocument("""{"type":"ping"}""");
        Assert.NotNull(result);
        result!.Dispose();
    }

    // ==================== Simple Message Types ====================

    [Fact]
    public void PingMessage_HasCorrectType()
    {
        var msg = new PingMessage();
        Assert.Equal("ping", msg.Type);
    }

    [Fact]
    public void PongMessage_HasCorrectType()
    {
        var msg = new PongMessage();
        Assert.Equal("pong", msg.Type);
    }

    [Fact]
    public void StartStreamingMessage_HasCorrectType()
    {
        var msg = new StartStreamingMessage();
        Assert.Equal("start_streaming", msg.Type);
    }

    [Fact]
    public void StopStreamingMessage_HasCorrectType()
    {
        var msg = new StopStreamingMessage();
        Assert.Equal("stop_streaming", msg.Type);
    }

    [Fact]
    public void PauseStreamingMessage_HasCorrectType()
    {
        var msg = new PauseStreamingMessage();
        Assert.Equal("pause_streaming", msg.Type);
    }

    [Fact]
    public void ResumeStreamingMessage_HasCorrectType()
    {
        var msg = new ResumeStreamingMessage();
        Assert.Equal("resume_streaming", msg.Type);
    }

    // ==================== UpdateConfig Message ====================

    [Fact]
    public void UpdateConfigMessage_NullableFields_RoundTrip()
    {
        // Only FPS set, resolutionHeight null
        var msg = new UpdateConfigMessage { Fps = 45, ResolutionHeight = null };
        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<UpdateConfigMessage>(json);

        Assert.NotNull(parsed);
        Assert.Equal(45, parsed!.Fps);
        Assert.Null(parsed.ResolutionHeight);
    }

    // ==================== ClientCodecCapability ====================

    [Fact]
    public void HardwareInfoAckMessage_WithCodecCapability_RoundTrip()
    {
        var msg = new HardwareInfoAckMessage
        {
            ClientCodecs = new ClientCodecCapability
            {
                SupportedCodecs = new[] { "H264", "H265", "VP9" },
                PreferredCodec = "H265",
                SupportsHevc = true,
                SupportsVP9 = true,
                SupportsVP8 = false,
                DeviceModel = "Quest 3",
                ApiLevel = 33
            }
        };

        var json = ProtocolMessageParser.Serialize(msg);
        var parsed = ProtocolMessageParser.Parse<HardwareInfoAckMessage>(json);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.ClientCodecs);
        Assert.Equal("H265", parsed.ClientCodecs!.PreferredCodec);
        Assert.True(parsed.ClientCodecs.SupportsHevc);
        Assert.Equal(3, parsed.ClientCodecs.SupportedCodecs!.Length);
        Assert.Equal("Quest 3", parsed.ClientCodecs.DeviceModel);
    }
}
