#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using Vortice.Direct3D11;

namespace RemotePlayServer.Encoding;

/// <summary>
/// Single PeerConnection with multiple video tracks using libdatachannel.
/// Replaces MultiPCStreamer to achieve: 1 ICE negotiation, 1 DTLS handshake.
/// </summary>
public class LibDataChannelStreamer : IDisposable
{
    private readonly int _monitorCount;
    private readonly int _fps;
    private readonly int _bitrateKbps;
    private readonly VideoCodec _codec;
    private ID3D11Device? _sharedDevice;

    private IRtcPeerConnection? _pc;
    private readonly List<TrackInfo> _tracks = new();
    private readonly object _lock = new();

    // Store per-monitor devices before tracks are created
    private readonly Dictionary<int, ID3D11Device> _pendingDevices = new();

    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _connected;

    /// <summary>
    /// Per-track state including encoder and staging texture
    /// </summary>
    private class TrackInfo : IDisposable
    {
        public int Index { get; set; }
        public IRtcTrack? Track { get; set; }
        public string Mid { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }

        // Encoder per track
        public ITextureEncoder? Encoder { get; set; }
        public ID3D11Texture2D? StagingNV12 { get; set; }
        public ID3D11Device? Device { get; set; }

        // Stats
        public long EncodedFrames;
        public long SentFrames;
        public long LastPts100ns = -1;  // Use -1 as sentinel for "not initialized"
        public uint RtpTimestamp;  // Cumulative RTP timestamp (90kHz clock)
        public bool TimestampInitialized;  // Flag to track if timestamp is initialized

        // Keyframe request flag
        public volatile bool ForceNextKeyframe;

        public void Dispose()
        {
            try { Encoder?.Dispose(); } catch { }
            try { StagingNV12?.Dispose(); } catch { }
            Encoder = null;
            StagingNV12 = null;
        }
    }

    // Events
    public event Action? OnAllTracksReady;
    public event Action<string>? OnIceCandidate;  // Single connection - no monitorIndex
    public event Action? OnConnectionFailed;

    public bool IsConnected => _connected;
    public int MonitorCount => _monitorCount;

    public LibDataChannelStreamer(int monitorCount, int fps, int kbps, ID3D11Device? device = null, VideoCodec codec = VideoCodec.H264)
    {
        _monitorCount = monitorCount;
        _fps = fps;
        _bitrateKbps = kbps;
        _sharedDevice = device;
        _codec = codec;

        Console.WriteLine($"[LibDC] Created: {monitorCount} monitors, {fps}fps, {kbps}kbps, codec={codec}");
    }

    public void SetDevice(ID3D11Device device)
    {
        _sharedDevice = device;
    }

    public void SetDeviceForMonitor(int monitorIndex, ID3D11Device device)
    {
        lock (_lock)
        {
            if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                // Track already exists - set directly
                _tracks[monitorIndex].Device = device;
                Console.WriteLine($"[LibDC] Set device for existing track {monitorIndex}");
            }
            else
            {
                // Track doesn't exist yet - store for later
                _pendingDevices[monitorIndex] = device;
                Console.WriteLine($"[LibDC] Stored pending device for monitor {monitorIndex}");
            }
        }
    }

    /// <summary>
    /// Process single SDP offer (with N m= sections), create N tracks, return single answer
    /// </summary>
    public async Task<string> ProcessOfferAsync(string offerSdp, List<(int w, int h)> dimensions)
    {
        if (_running)
        {
            Console.WriteLine("[LibDC] Closing existing connection for reconnect...");
            CloseConnection();
        }

        Console.WriteLine($"[LibDC] Processing offer for {dimensions.Count} monitors");

        // Parse mids from offer SDP
        var mids = ParseOfferMids(offerSdp);
        Console.WriteLine($"[LibDC] Found {mids.Count} mids in offer: [{string.Join(", ", mids)}]");

        // Parse H264 payload type from offer SDP (must match what browser expects)
        var h264PayloadType = ParseH264PayloadType(offerSdp);
        Console.WriteLine($"[LibDC] Using H264 PayloadType: {h264PayloadType}");

        // Create PeerConnection
        var config = new RtcPeerConfiguration
        {
            IceServers = Array.Empty<string>()  // LAN mode - no STUN needed
        };

        _pc = new RtcPeerConnection(config);
        Console.WriteLine("[LibDC] PeerConnection created");

        // Wire up callbacks BEFORE setting remote description
        SetupCallbacks();

        // IMPORTANT: Set remote description FIRST (this tells libdatachannel what the remote wants)
        try
        {
            var remoteDesc = new RtcDescription
            {
                Sdp = offerSdp,
                Type = RtcDescriptionType.Offer
            };
            Console.WriteLine($"[LibDC] Setting remote offer ({offerSdp.Length} bytes)...");
            _pc.SetRemoteDescription(remoteDesc);
            Console.WriteLine("[LibDC] Remote offer set successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibDC] ERROR SetRemoteDescription failed: {ex.Message}");
            Console.WriteLine($"[LibDC] Offer SDP (first 500 chars): {offerSdp.Substring(0, Math.Min(500, offerSdp.Length))}");
            throw;
        }

        // Now create tracks to match the offer's m= sections
        // After SetRemoteDescription, libdatachannel knows about the remote's recvonly tracks
        // and we can add sendonly tracks to match them
        for (int i = 0; i < dimensions.Count; i++)
        {
            var (w, h) = dimensions[i];
            var mid = mids.Count > i ? mids[i] : i.ToString();

            IRtcTrack track;
            try
            {
                var trackArgs = new RtcCreateTrackArgs
                {
                    Direction = rtcDirection.RTC_DIRECTION_SENDONLY,
                    Codec = _codec switch
                    {
                        VideoCodec.H265 => rtcCodec.RTC_CODEC_H265,
                        VideoCodec.VP9 => rtcCodec.RTC_CODEC_VP9,
                        VideoCodec.VP8 => rtcCodec.RTC_CODEC_VP8,
                        _ => rtcCodec.RTC_CODEC_H264
                    },
                    PayloadType = h264PayloadType,  // Must match browser's expected PT from SDP
                    Mid = mid,
                    Ssrc = (uint)(1000 + i),
                    Name = $"monitor{i}"
                };

                Console.WriteLine($"[LibDC] Creating track {i}: mid={mid}, codec={_codec}, ssrc={trackArgs.Ssrc}");
                track = _pc.CreateTrack(trackArgs);
                Console.WriteLine($"[LibDC] Track {i} created successfully");

                // MUST add H264 packetizer explicitly - track creation alone doesn't configure it
                // Use LONG_START_SEQUENCE - we'll send raw NAL data (without start codes)
                // The packetizer expects to receive raw NAL bytes directly
                var packetizerArgs = new RtcPacketizerInitArgs
                {
                    Ssrc = (int)trackArgs.Ssrc,
                    Cname = $"monitor{i}",
                    PayloadType = (byte)trackArgs.PayloadType,
                    Clockrate = 90000,  // Video clock rate
                    SequenceNumber = 0,
                    Timestamp = 0,
                    MaxFragmentSize = 1200,  // Safe MTU for RTP
                    NalUnitSeparator = rtcNalUnitSeparator.RTC_NAL_SEPARATOR_LONG_START_SEQUENCE  // We strip start codes ourselves
                };

                Console.WriteLine($"[LibDC] Track {i}: Adding H264 packetizer (raw NAL mode)...");
                track.AddH264Packetizer(packetizerArgs);
                Console.WriteLine($"[LibDC] Track {i}: H264 packetizer added successfully");

                // CRITICAL: Add RTCP Sender Report reporter - browser needs SR to know media is being sent
                // Without SR, browser keeps track in "muted" state
                track.AddRtcpSrReporter();
                Console.WriteLine($"[LibDC] Track {i}: RTCP SR reporter added");

                // Add NACK responder for packet loss recovery (use same SSRC as track)
                track.AddRtcpNackResponder((uint)(1000 + i));
                Console.WriteLine($"[LibDC] Track {i}: RTCP NACK responder added");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LibDC] ERROR creating track {i}: {ex.Message}");
                throw;
            }

            // Wire track events
            var trackIndex = i;
            var trackMid = mid;
            track.OnOpen += (t) =>
            {
                Console.WriteLine($"[LibDC] Track {trackIndex} (mid={trackMid}) opened");
                InitializeEncoderForTrack(trackIndex);
                CheckAllTracksReady();
            };
            track.OnClose += (t) =>
            {
                Console.WriteLine($"[LibDC] Track {trackIndex} closed");
            };
            track.OnError += (t, err) =>
            {
                Console.WriteLine($"[LibDC] Track {trackIndex} error: {err}");
            };

            // Check for pending per-monitor device
            ID3D11Device? deviceForTrack = _sharedDevice;
            if (_pendingDevices.TryGetValue(i, out var pendingDevice))
            {
                deviceForTrack = pendingDevice;
                _pendingDevices.Remove(i);
                Console.WriteLine($"[LibDC] Track {i}: Using pending per-monitor device");
            }

            _tracks.Add(new TrackInfo
            {
                Index = i,
                Track = track,
                Mid = mid,
                Width = w,
                Height = h,
                Device = deviceForTrack
            });

            Console.WriteLine($"[LibDC] Created track {i}: mid={mid}, {w}x{h}, hasDevice={deviceForTrack != null}");
        }

        // Wait for local description to be ready
        await Task.Delay(100); // Give libdatachannel time to generate answer

        // Get answer SDP
        var answer = _pc.LocalDescription ?? "";
        if (string.IsNullOrEmpty(answer))
        {
            Console.WriteLine("[LibDC] Warning: LocalDescription is empty, waiting...");
            for (int i = 0; i < 50 && string.IsNullOrEmpty(answer); i++)
            {
                await Task.Delay(50);
                answer = _pc.LocalDescription ?? "";
            }
        }

        _running = true;
        Console.WriteLine($"[LibDC] Answer ready, {answer.Length} bytes");

        // Start stats logging
        _ = Task.Run(LogStatsAsync);

        return answer;
    }

    private void SetupCallbacks()
    {
        if (_pc == null) return;

        // ICE candidate generated
        _pc.OnCandidateSafe += (pc, candidate) =>
        {
            if (!string.IsNullOrEmpty(candidate.Content))
            {
                var candStr = candidate.Content;
                Console.WriteLine($"[LibDC] Local ICE: {candStr.Substring(0, Math.Min(60, candStr.Length))}...");
                OnIceCandidate?.Invoke(candStr);
            }
        };

        // State changes
        _pc.OnConnectionStateChange += (pc, state) =>
        {
            Console.WriteLine($"[LibDC] State: {state}");
            if (state == rtcState.RTC_CONNECTED)
            {
                _connected = true;
            }
            else if (state == rtcState.RTC_FAILED)
            {
                _connected = false;
                OnConnectionFailed?.Invoke();
            }
            else if (state == rtcState.RTC_CLOSED)
            {
                _connected = false;
            }
        };

        // ICE state
        _pc.OnIceStateChange += (pc, iceState) =>
        {
            Console.WriteLine($"[LibDC] ICE state: {iceState}");
        };

        // Gathering state
        _pc.OnGatheringStateChange += (pc, gatherState) =>
        {
            Console.WriteLine($"[LibDC] ICE gathering: {gatherState}");
        };
    }

    /// <summary>
    /// Add a remote ICE candidate
    /// </summary>
    public void AddIceCandidate(string candidate, string? mid = null)
    {
        if (_pc == null || string.IsNullOrEmpty(candidate)) return;

        try
        {
            // Normalize candidate format
            var candStr = candidate.Trim();
            if (candStr.StartsWith("a="))
                candStr = candStr.Substring(2);
            if (!candStr.StartsWith("candidate:"))
                candStr = "candidate:" + candStr;

            var rtcCandidate = new RtcCandidate
            {
                Content = candStr,
                Mid = mid ?? ""
            };
            _pc.AddRemoteCandidate(rtcCandidate);
            Console.WriteLine($"[LibDC] Added remote ICE: {candStr.Substring(0, Math.Min(50, candStr.Length))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LibDC] AddIceCandidate error: {ex.Message}");
        }
    }

    private void InitializeEncoderForTrack(int trackIndex)
    {
        lock (_lock)
        {
            if (trackIndex < 0 || trackIndex >= _tracks.Count) return;
            var track = _tracks[trackIndex];

            if (track.Encoder != null) return; // Already initialized

            var device = track.Device ?? _sharedDevice;
            if (device == null)
            {
                Console.WriteLine($"[LibDC] Track {trackIndex}: No D3D11 device available");
                return;
            }

            try
            {
                // Prefer AMF (AMD) encoder if available
                var gpuVendor = GpuVendorDetector.DetectPrimaryGpuVendor();
                if (gpuVendor == GpuVendorDetector.GpuVendor.AMD && AmfNativeWrapper.IsAvailable())
                {
                    var amf = new AmfNativeWrapper();
                    amf.OnEncodedData += (nal, keyframe, pts) => OnEncodedData(track, nal, keyframe, pts);
                    if (amf.Initialize(track.Width, track.Height, _fps, _bitrateKbps, device))
                    {
                        track.Encoder = amf;
                        Console.WriteLine($"[LibDC] Track {trackIndex}: AMF encoder initialized");
                        return;
                    }
                    amf.Dispose();
                }

                // TODO: Add fallback to LibAv encoder here
                Console.WriteLine($"[LibDC] Track {trackIndex}: No suitable encoder found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LibDC] Track {trackIndex}: Encoder init failed: {ex.Message}");
            }
        }
    }

    private void CheckAllTracksReady()
    {
        lock (_lock)
        {
            if (_tracks.All(t => t.Track?.IsOpen == true))
            {
                Console.WriteLine("[LibDC] All tracks ready!");
                OnAllTracksReady?.Invoke();
            }
        }
    }

    /// <summary>
    /// Push NV12 texture for a specific monitor (track)
    /// </summary>
    public void PushTexture(int monitorIndex, ID3D11Texture2D nv12Texture, int width, int height)
    {
        if (!_running || _disposed || !_connected) return;
        if (monitorIndex < 0 || monitorIndex >= _tracks.Count) return;

        var track = _tracks[monitorIndex];
        if (track.Encoder == null || track.Track?.IsOpen != true) return;

        lock (_lock)
        {
            try
            {
                var device = track.Device ?? _sharedDevice;
                if (device == null) return;

                // Create staging texture if needed
                if (track.StagingNV12 == null)
                {
                    var desc = new Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.NV12,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.None
                    };
                    track.StagingNV12 = device.CreateTexture2D(desc);
                    Console.WriteLine($"[LibDC] Track {monitorIndex}: Created staging NV12 {width}x{height}");
                }

                // Copy and encode
                device.ImmediateContext.CopyResource(track.StagingNV12, nv12Texture);
                bool forceIdr = Interlocked.Read(ref track.EncodedFrames) == 0;

                // Check for client-requested keyframe
                bool clientRequested = track.ForceNextKeyframe;
                if (clientRequested)
                    track.ForceNextKeyframe = false;

                track.Encoder.EncodeTexture(track.StagingNV12, forceKeyframe: forceIdr || clientRequested);
                Interlocked.Increment(ref track.EncodedFrames);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref track.EncodedFrames) % 60 == 0)
                    Console.WriteLine($"[LibDC] Track {monitorIndex} encode error: {ex.Message}");
            }
        }
    }

    private void OnEncodedData(TrackInfo trackInfo, byte[] nalData, bool isKeyframe, long pts100ns)
    {
        if (!_running || _disposed || !_connected) return;
        if (trackInfo.Track?.IsOpen != true) return;

        uint rtpTimestamp = 0;
        byte[] dataToSend = nalData;
        int strippedOffset = 0;
        try
        {
            // Calculate RTP timestamp
            rtpTimestamp = CalculateRtpTimestamp(trackInfo, pts100ns);

            // Strip AUD (Access Unit Delimiter) from beginning if present
            // AUD format: 00 00 00 01 09 XX (6 bytes) - not needed for RTP
            if (nalData.Length > 6 &&
                nalData[0] == 0x00 && nalData[1] == 0x00 && nalData[2] == 0x00 && nalData[3] == 0x01 &&
                (nalData[4] & 0x1F) == 9)  // NAL type 9 = AUD
            {
                // Find next start code after AUD (should be at offset 6)
                for (int i = 5; i < nalData.Length - 3; i++)
                {
                    if (nalData[i] == 0x00 && nalData[i + 1] == 0x00 && nalData[i + 2] == 0x00 && nalData[i + 3] == 0x01)
                    {
                        strippedOffset = i;
                        break;
                    }
                    // Also check 3-byte start code
                    if (nalData[i] == 0x00 && nalData[i + 1] == 0x00 && nalData[i + 2] == 0x01)
                    {
                        strippedOffset = i;
                        break;
                    }
                }
                if (strippedOffset > 0)
                {
                    dataToSend = new byte[nalData.Length - strippedOffset];
                    Buffer.BlockCopy(nalData, strippedOffset, dataToSend, 0, dataToSend.Length);
                }
            }

            // Split Annex B data into individual NAL units (each WITH start code)
            // LONG_START_SEQUENCE mode expects data to have 00 00 00 01 prefix
            var nalUnits = SplitAnnexBNalUnits(dataToSend);

            // Debug: Log first bytes for first few frames
            long frameNum = Interlocked.Read(ref trackInfo.EncodedFrames);
            if (frameNum <= 5)
            {
                var firstNal = nalUnits.Count > 0 ? nalUnits[0] : Array.Empty<byte>();
                var header = firstNal.Length >= 8
                    ? $"{firstNal[0]:X2} {firstNal[1]:X2} {firstNal[2]:X2} {firstNal[3]:X2} {firstNal[4]:X2}"
                    : (firstNal.Length > 0 ? BitConverter.ToString(firstNal, 0, Math.Min(8, firstNal.Length)) : "empty");
                // NAL type is after start code (index 4 for 4-byte start code)
                var nalTypes = string.Join(",", nalUnits.Select(n => n.Length > 4 ? (n[4] & 0x1F).ToString() : "?"));
                Console.WriteLine($"[LibDC] Track {trackInfo.Index} NAL: {header}, nalCount={nalUnits.Count}, types=[{nalTypes}], annexB={dataToSend.Length}B");
            }

            // Set timestamp before write (same for all NALs in access unit)
            trackInfo.Track.Timestamp = rtpTimestamp;

            // Send each NAL unit separately (WITH start code - packetizer strips it)
            foreach (var nal in nalUnits)
            {
                trackInfo.Track.Write(nal);
            }

            long sent = Interlocked.Increment(ref trackInfo.SentFrames);
            if (sent <= 5 || isKeyframe)
            {
                Console.WriteLine($"[LibDC] Track {trackInfo.Index} frame #{sent}: {nalData.Length}B, key={isKeyframe}, ts={rtpTimestamp}, open={trackInfo.Track?.IsOpen}");
            }
        }
        catch (Exception ex)
        {
            long errCount = Interlocked.Read(ref trackInfo.SentFrames);
            if (errCount == 0)
            {
                // First error - log full details including stack trace
                Console.WriteLine($"[LibDC] Track {trackInfo.Index} FIRST send error:");
                Console.WriteLine($"  Exception: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  NAL size: {nalData.Length}, dataToSend size: {dataToSend.Length}");
                Console.WriteLine($"  Timestamp: {rtpTimestamp}, IsOpen: {trackInfo.Track?.IsOpen}");
                Console.WriteLine($"  Track type: {trackInfo.Track?.GetType().FullName}");
                // Try to list available methods
                if (trackInfo.Track != null)
                {
                    var methods = trackInfo.Track.GetType().GetMethods()
                        .Where(m => m.Name.Contains("Send") || m.Name.Contains("Write") || m.Name.Contains("Message"))
                        .Select(m => m.Name)
                        .Distinct();
                    Console.WriteLine($"  Available send methods: [{string.Join(", ", methods)}]");
                }
                if (ex.InnerException != null)
                    Console.WriteLine($"  Inner: {ex.InnerException.Message}");
            }
            else if (errCount % 120 == 0)
                Console.WriteLine($"[LibDC] Track {trackInfo.Index} send error #{errCount}: {ex.Message}");
        }
    }

    private uint CalculateRtpTimestamp(TrackInfo track, long pts100ns)
    {
        const int ClockRate = 90000;

        // Use lock to prevent race condition when multiple encoder callbacks fire simultaneously
        lock (track)
        {
            // Use flag to check initialization (pts100ns=0 is valid for first frame)
            if (!track.TimestampInitialized)
            {
                // First frame - initialize with random-ish start value
                track.LastPts100ns = pts100ns;
                track.RtpTimestamp = (uint)(Environment.TickCount & 0xFFFF);  // Random start
                track.TimestampInitialized = true;
                Console.WriteLine($"[LibDC] Track {track.Index}: Initial RTP timestamp = {track.RtpTimestamp}");
                return track.RtpTimestamp;
            }

            long last = track.LastPts100ns;
            long delta100ns = pts100ns - last;
            uint rtpDelta;

            if (delta100ns <= 0 || delta100ns > 5_000_000) // Invalid or > 0.5 second
            {
                // Use default frame duration
                rtpDelta = (uint)(ClockRate / Math.Max(1, _fps));
            }
            else
            {
                // Convert 100ns units to 90kHz clock
                rtpDelta = (uint)Math.Max(1, ClockRate * delta100ns / 10_000_000L);
            }

            track.LastPts100ns = pts100ns;

            // Add delta to cumulative timestamp (wraps at uint.MaxValue)
            track.RtpTimestamp += rtpDelta;
            return track.RtpTimestamp;
        }
    }

    /// <summary>
    /// Extract raw NAL units from Annex B data (strip start codes).
    /// Returns list of raw NAL bytes without any framing.
    /// </summary>
    private List<byte[]> ExtractRawNalUnits(byte[] annexB)
    {
        var result = new List<byte[]>();
        var nalStarts = new List<(int dataStart, int codeLen)>(); // (NAL data start, start code length)

        // Find all start code positions
        for (int i = 0; i < annexB.Length - 3; i++)
        {
            // Check for 4-byte start code: 00 00 00 01
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                nalStarts.Add((i + 4, 4)); // NAL data starts after 4-byte code
                i += 3;
            }
            // Check for 3-byte start code: 00 00 01
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                nalStarts.Add((i + 3, 3)); // NAL data starts after 3-byte code
                i += 2;
            }
        }

        if (nalStarts.Count == 0)
        {
            // No start codes found - return entire buffer as single NAL
            result.Add(annexB);
            return result;
        }

        for (int i = 0; i < nalStarts.Count; i++)
        {
            int nalStart = nalStarts[i].dataStart;
            int nalEnd;

            if (i + 1 < nalStarts.Count)
            {
                // End is at the start code of the next NAL
                int nextCodeStart = nalStarts[i + 1].dataStart - nalStarts[i + 1].codeLen;
                nalEnd = nextCodeStart;
            }
            else
            {
                nalEnd = annexB.Length;
            }

            int nalLength = nalEnd - nalStart;
            if (nalLength <= 0) continue;

            // Copy raw NAL data (without start code)
            var rawNal = new byte[nalLength];
            Buffer.BlockCopy(annexB, nalStart, rawNal, 0, nalLength);
            result.Add(rawNal);
        }

        return result;
    }

    /// <summary>
    /// Count NAL units in Annex B data (for logging)
    /// </summary>
    private int CountAnnexBNalUnits(byte[] annexB)
    {
        int count = 0;
        for (int i = 0; i < annexB.Length - 3; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                count++;
                i += 3;
            }
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                count++;
                i += 2;
            }
        }
        return count;
    }

    /// <summary>
    /// Split Annex B access unit into individual NAL units (each with start code prefix).
    /// This helps the H264 packetizer handle each NAL correctly.
    /// </summary>
    private List<byte[]> SplitAnnexBNalUnits(byte[] annexB)
    {
        var result = new List<byte[]>();
        var nalStarts = new List<int>();

        // Find all start code positions
        for (int i = 0; i < annexB.Length - 3; i++)
        {
            // Check for 4-byte start code: 00 00 00 01
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                nalStarts.Add(i);
                i += 3;
            }
            // Check for 3-byte start code: 00 00 01
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                nalStarts.Add(i);
                i += 2;
            }
        }

        if (nalStarts.Count == 0)
        {
            result.Add(annexB);
            return result;
        }

        // Extract each NAL unit (including its start code)
        for (int i = 0; i < nalStarts.Count; i++)
        {
            int start = nalStarts[i];
            int end = (i + 1 < nalStarts.Count) ? nalStarts[i + 1] : annexB.Length;
            int length = end - start;

            if (length > 0)
            {
                var nal = new byte[length];
                Buffer.BlockCopy(annexB, start, nal, 0, length);
                result.Add(nal);
            }
        }

        return result;
    }

    /// <summary>
    /// Convert Annex B format (start codes) to AVCC format (length-prefixed).
    /// libdatachannel's H264 packetizer expects length-prefixed NAL units.
    /// </summary>
    private byte[] ConvertAnnexBToAvcc(byte[] annexB)
    {
        // Find all NAL unit start positions
        var nalStarts = new List<int>();
        for (int i = 0; i < annexB.Length - 3; i++)
        {
            // Check for 4-byte start code: 00 00 00 01
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                nalStarts.Add(i + 4); // NAL data starts after start code
                i += 3; // Skip ahead
            }
            // Check for 3-byte start code: 00 00 01
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                nalStarts.Add(i + 3);
                i += 2;
            }
        }

        if (nalStarts.Count == 0)
            return annexB; // No start codes found, return as-is

        // Calculate output size: 4 bytes length prefix per NAL instead of 3-4 byte start code
        // In worst case (all 4-byte start codes), output is same size
        // In best case (all 3-byte start codes), output grows by 1 byte per NAL
        using var output = new System.IO.MemoryStream(annexB.Length + nalStarts.Count);

        for (int i = 0; i < nalStarts.Count; i++)
        {
            int nalStart = nalStarts[i];
            int nalEnd = (i + 1 < nalStarts.Count)
                ? FindStartCodeBefore(annexB, nalStarts[i + 1])
                : annexB.Length;
            int nalLength = nalEnd - nalStart;

            if (nalLength <= 0) continue;

            // Write 4-byte big-endian length
            output.WriteByte((byte)((nalLength >> 24) & 0xFF));
            output.WriteByte((byte)((nalLength >> 16) & 0xFF));
            output.WriteByte((byte)((nalLength >> 8) & 0xFF));
            output.WriteByte((byte)(nalLength & 0xFF));

            // Write NAL data
            output.Write(annexB, nalStart, nalLength);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Find the start of the start code before the given NAL data position.
    /// </summary>
    private int FindStartCodeBefore(byte[] data, int nalDataStart)
    {
        // NAL data starts after start code, so start code is 3-4 bytes before
        if (nalDataStart >= 4 && data[nalDataStart - 4] == 0 && data[nalDataStart - 3] == 0 &&
            data[nalDataStart - 2] == 0 && data[nalDataStart - 1] == 1)
        {
            return nalDataStart - 4;
        }
        if (nalDataStart >= 3 && data[nalDataStart - 3] == 0 && data[nalDataStart - 2] == 0 &&
            data[nalDataStart - 1] == 1)
        {
            return nalDataStart - 3;
        }
        return nalDataStart;
    }

    /// <summary>
    /// Convert Annex B format to list of individual AVCC-formatted NAL units.
    /// Each NAL unit is returned as a separate byte[] with 4-byte length prefix.
    /// This allows sending each NAL unit in a separate Write() call.
    /// </summary>
    private List<byte[]> ConvertAnnexBToAvccNalUnits(byte[] annexB)
    {
        var result = new List<byte[]>();

        // Find all NAL unit start positions
        var nalStarts = new List<int>();
        for (int i = 0; i < annexB.Length - 3; i++)
        {
            // Check for 4-byte start code: 00 00 00 01
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                nalStarts.Add(i + 4); // NAL data starts after start code
                i += 3; // Skip ahead
            }
            // Check for 3-byte start code: 00 00 01
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                nalStarts.Add(i + 3);
                i += 2;
            }
        }

        if (nalStarts.Count == 0)
        {
            // No start codes found - wrap entire buffer with length prefix
            var wrapped = new byte[4 + annexB.Length];
            wrapped[0] = (byte)((annexB.Length >> 24) & 0xFF);
            wrapped[1] = (byte)((annexB.Length >> 16) & 0xFF);
            wrapped[2] = (byte)((annexB.Length >> 8) & 0xFF);
            wrapped[3] = (byte)(annexB.Length & 0xFF);
            Buffer.BlockCopy(annexB, 0, wrapped, 4, annexB.Length);
            result.Add(wrapped);
            return result;
        }

        for (int i = 0; i < nalStarts.Count; i++)
        {
            int nalStart = nalStarts[i];
            int nalEnd = (i + 1 < nalStarts.Count)
                ? FindStartCodeBefore(annexB, nalStarts[i + 1])
                : annexB.Length;
            int nalLength = nalEnd - nalStart;

            if (nalLength <= 0) continue;

            // Create AVCC NAL unit: 4-byte length + NAL data
            var avccNal = new byte[4 + nalLength];
            avccNal[0] = (byte)((nalLength >> 24) & 0xFF);
            avccNal[1] = (byte)((nalLength >> 16) & 0xFF);
            avccNal[2] = (byte)((nalLength >> 8) & 0xFF);
            avccNal[3] = (byte)(nalLength & 0xFF);
            Buffer.BlockCopy(annexB, nalStart, avccNal, 4, nalLength);

            result.Add(avccNal);
        }

        return result;
    }

    private async Task LogStatsAsync()
    {
        while (_running && !_disposed)
        {
            await Task.Delay(3000);
            if (!_running) break;

            lock (_lock)
            {
                var stats = string.Join(", ", _tracks.Select(t =>
                    $"m{t.Index}:{t.SentFrames}"));
                Console.WriteLine($"[LibDC] Stats: {stats}");
            }
        }
    }

    private List<string> ParseOfferMids(string sdp)
    {
        var mids = new List<string>();
        if (string.IsNullOrEmpty(sdp)) return mids;

        foreach (var line in sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith("a=mid:"))
            {
                mids.Add(line.Substring(6).Trim());
            }
        }
        return mids;
    }

    /// <summary>
    /// Parse the H264 payload type from the offer SDP.
    /// Browser offers multiple H264 profiles - we prefer Constrained Baseline (42e01f) with packetization-mode=1.
    /// </summary>
    private int ParseH264PayloadType(string sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return 96; // Fallback

        // Priority order for H264 profiles (most compatible first):
        // 1. Constrained Baseline with packetization-mode=1 (42e01f) - most compatible
        // 2. Baseline with packetization-mode=1 (42001f)
        // 3. Any H264 with packetization-mode=1
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // First pass: find H264 payload types
        var h264PayloadTypes = new Dictionary<int, string>(); // PT -> fmtp line
        foreach (var line in lines)
        {
            // Look for: a=rtpmap:XXX H264/90000
            if (line.StartsWith("a=rtpmap:") && line.Contains("H264/90000"))
            {
                var parts = line.Substring(9).Split(' ');
                if (parts.Length >= 1 && int.TryParse(parts[0], out int pt))
                {
                    h264PayloadTypes[pt] = "";
                }
            }
        }

        // Second pass: get fmtp for each H264 PT
        foreach (var line in lines)
        {
            if (line.StartsWith("a=fmtp:"))
            {
                var spaceIdx = line.IndexOf(' ', 7);
                if (spaceIdx > 7)
                {
                    var ptStr = line.Substring(7, spaceIdx - 7);
                    if (int.TryParse(ptStr, out int pt) && h264PayloadTypes.ContainsKey(pt))
                    {
                        h264PayloadTypes[pt] = line.Substring(spaceIdx + 1);
                    }
                }
            }
        }

        // Find best match
        // Priority 1: Constrained Baseline (42e01f) with packetization-mode=1
        foreach (var kv in h264PayloadTypes)
        {
            if (kv.Value.Contains("profile-level-id=42e01f") && kv.Value.Contains("packetization-mode=1"))
            {
                Console.WriteLine($"[LibDC] Found Constrained Baseline H264 PT={kv.Key}: {kv.Value}");
                return kv.Key;
            }
        }

        // Priority 2: Baseline (42001f) with packetization-mode=1
        foreach (var kv in h264PayloadTypes)
        {
            if (kv.Value.Contains("profile-level-id=42001f") && kv.Value.Contains("packetization-mode=1"))
            {
                Console.WriteLine($"[LibDC] Found Baseline H264 PT={kv.Key}: {kv.Value}");
                return kv.Key;
            }
        }

        // Priority 3: Any H264 with packetization-mode=1
        foreach (var kv in h264PayloadTypes)
        {
            if (kv.Value.Contains("packetization-mode=1"))
            {
                Console.WriteLine($"[LibDC] Found H264 PT={kv.Key} with packetization-mode=1: {kv.Value}");
                return kv.Key;
            }
        }

        // Priority 4: First H264 found
        foreach (var kv in h264PayloadTypes)
        {
            Console.WriteLine($"[LibDC] Using first H264 PT={kv.Key}: {kv.Value}");
            return kv.Key;
        }

        Console.WriteLine("[LibDC] WARNING: No H264 found in offer, falling back to PT=96");
        return 96;
    }

    private void CloseConnection()
    {
        _running = false;
        _connected = false;

        lock (_lock)
        {
            foreach (var track in _tracks)
                track.Dispose();
            _tracks.Clear();
            _pendingDevices.Clear();
        }

        _pc?.Dispose();
        _pc = null;

        Console.WriteLine("[LibDC] Connection closed");
    }

    /// <summary>
    /// Request keyframe from encoder for specified monitor (or all if -1)
    /// </summary>
    public void RequestKeyframe(int monitorIndex = -1)
    {
        lock (_lock)
        {
            if (monitorIndex == -1)
            {
                // Request keyframe for all tracks
                foreach (var track in _tracks)
                {
                    track.ForceNextKeyframe = true;
                }
            }
            else if (monitorIndex >= 0 && monitorIndex < _tracks.Count)
            {
                _tracks[monitorIndex].ForceNextKeyframe = true;
            }
        }
    }

    /// <summary>
    /// Process FPS feedback from client for adaptive encoding (stub - not implemented yet)
    /// </summary>
    public void ProcessFpsFeedback(int monitorIndex, float effectiveFps, int droppedFrames)
    {
        // TODO: Implement adaptive bitrate/FPS based on client feedback
        Console.WriteLine($"[LibDC] FPS feedback m{monitorIndex}: {effectiveFps:F1}fps, dropped={droppedFrames}");
    }

    /// <summary>
    /// Get current target FPS for a monitor (stub - returns configured FPS)
    /// </summary>
    public int GetCurrentTargetFps(int monitorIndex)
    {
        return _fps;
    }

    public void Stop()
    {
        if (!_running && _pc == null) return;
        Console.WriteLine("[LibDC] Stopping...");
        CloseConnection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Console.WriteLine("[LibDC] Disposed");
    }
}
