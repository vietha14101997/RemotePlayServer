#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vortice.DXGI;

#region Minimal FFmpeg pipe encoder (control real bitrate/fps via libx264)
// Encoder chạy ffmpeg qua stdin/stdout để KHÓA thật fps/bitrate/CRF.
// Đầu vào: raw BGRA; Đầu ra: Annex-B H.264 (có AUD) -> tách theo AU và bắn qua WebRTC.
internal sealed class FfmpegPipeEncoder : IDisposable
{
    public enum InputFormat { BGRA, NV12 }
    
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FPS { get; private set; }
    public int BitrateKbps { get; private set; }   // 0 => dùng CRF
    public int CRF { get; private set; }           // <0 => off
    public string Preset { get; private set; }
    public bool ZeroLatency { get; private set; }
    public InputFormat PixelFormat { get; private set; } = InputFormat.BGRA;

    public event Action<uint, byte[]>? OnEncodedAccessUnit;

    Process? _ff;
    Stream? _stdin;
    Stream? _stdout;
    CancellationTokenSource? _cts;
    Task? _readerTask;
    Task? _errTask;
    readonly object _sync = new();

    MemoryStream _auBuf = new();
    uint _lastDurationMs = 1000 / 30;
    const int _maxBufferBytes = 512 * 1024; // Reduced for lower latency
    private readonly string _exePath;
    byte[]? _lastSps, _lastPps;
    enum GpuEnc { None, NVENC, QSV, AMF }
    enum GpuVendor { Unknown, NVIDIA, AMD, Intel }
    static GpuEnc _chosenGpu = GpuEnc.None;
    static GpuVendor _detectedVendor = GpuVendor.Unknown;
    static Dictionary<string, bool> _filterCache = new(StringComparer.OrdinalIgnoreCase);

    static GpuVendor DetectGpuVendor()
    {
        if (_detectedVendor != GpuVendor.Unknown) return _detectedVendor;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure) break;
                var desc = adapter.Description;
                adapter.Dispose();
                
                string name = desc.Description.ToUpperInvariant();
                // Skip Microsoft Basic Render Driver
                if (name.Contains("MICROSOFT") || name.Contains("BASIC")) continue;
                
                if (name.Contains("NVIDIA") || name.Contains("GEFORCE") || name.Contains("GTX") || name.Contains("RTX"))
                {
                    _detectedVendor = GpuVendor.NVIDIA;
                    Console.WriteLine($"[FFMPEG] Detected GPU vendor: NVIDIA ({desc.Description})");
                    return _detectedVendor;
                }
                if (name.Contains("AMD") || name.Contains("RADEON") || name.Contains("RX "))
                {
                    _detectedVendor = GpuVendor.AMD;
                    Console.WriteLine($"[FFMPEG] Detected GPU vendor: AMD ({desc.Description})");
                    return _detectedVendor;
                }
                if (name.Contains("INTEL") || name.Contains("UHD") || name.Contains("IRIS"))
                {
                    _detectedVendor = GpuVendor.Intel;
                    Console.WriteLine($"[FFMPEG] Detected GPU vendor: Intel ({desc.Description})");
                    return _detectedVendor;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FFMPEG] GPU detection error: {ex.Message}");
        }
        Console.WriteLine("[FFMPEG] GPU vendor: Unknown, will probe all encoders");
        return GpuVendor.Unknown;
    }

    bool ProbeFilter(string filterName)
    {
        if (string.IsNullOrWhiteSpace(_exePath)) return false;

        // Trả về từ cache nếu đã hỏi rồi
        if (_filterCache.TryGetValue(filterName, out var ok)) return ok;

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exePath, // đường dẫn ffmpeg.exe hiện tại
                    Arguments = "-hide_banner -loglevel error -filters",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_exePath)!
                }
            };

            p.Start();

            // Đọc cả stdout + stderr (một số build in thông tin ra stderr)
            string textOut = p.StandardOutput.ReadToEnd();
            string textErr = p.StandardError.ReadToEnd();
            p.WaitForExit(3000);

            string all = (textOut + "\n" + textErr);
            bool found = all.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) >= 0;

            _filterCache[filterName] = found; // lưu cache
            Console.WriteLine($"[FFMPEG][probe filter] {filterName} = {found}");
            return found;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FFMPEG][probe filter] {filterName} EX: {ex.Message}");
            _filterCache[filterName] = false;
            return false;
        }
    }

    bool ProbeEncoder(string encName)
    {
        const string probeSize = "640x360"; // <-- tăng size để NVENC chấp nhận
        string args =
            "-hide_banner -loglevel verbose " +
            $"-f lavfi -i color=c=black:s={probeSize} -frames:v 1 " +
            $"-c:v {encName} -f null -";

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_exePath)!
                }
            };
            p.Start();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } }

            Console.WriteLine($"[FFMPEG][probe {encName}] exit={p.ExitCode}");
            if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine(err);

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FFMPEG][probe {encName}] EX: {ex.Message}");
            return false;
        }
    }

    void EnsureChosenEncoder()
    {
        if (_chosenGpu != 0) return; // đã chọn
        
        var vendor = DetectGpuVendor();
        
        // Probe theo thứ tự ưu tiên dựa trên GPU vendor
        switch (vendor)
        {
            case GpuVendor.AMD:
                if (ProbeEncoder("h264_amf")) { _chosenGpu = GpuEnc.AMF; return; }
                if (ProbeEncoder("h264_qsv")) { _chosenGpu = GpuEnc.QSV; return; }
                if (ProbeEncoder("h264_nvenc")) { _chosenGpu = GpuEnc.NVENC; return; }
                break;
            case GpuVendor.NVIDIA:
                if (ProbeEncoder("h264_nvenc")) { _chosenGpu = GpuEnc.NVENC; return; }
                if (ProbeEncoder("h264_qsv")) { _chosenGpu = GpuEnc.QSV; return; }
                if (ProbeEncoder("h264_amf")) { _chosenGpu = GpuEnc.AMF; return; }
                break;
            case GpuVendor.Intel:
                if (ProbeEncoder("h264_qsv")) { _chosenGpu = GpuEnc.QSV; return; }
                if (ProbeEncoder("h264_nvenc")) { _chosenGpu = GpuEnc.NVENC; return; }
                if (ProbeEncoder("h264_amf")) { _chosenGpu = GpuEnc.AMF; return; }
                break;
            default:
                // Unknown - probe all
                if (ProbeEncoder("h264_nvenc")) { _chosenGpu = GpuEnc.NVENC; return; }
                if (ProbeEncoder("h264_amf")) { _chosenGpu = GpuEnc.AMF; return; }
                if (ProbeEncoder("h264_qsv")) { _chosenGpu = GpuEnc.QSV; return; }
                break;
        }
        _chosenGpu = GpuEnc.None;
    }

    static (bool hasIdr, bool hasSps, bool hasPps) ScanNalTypes(ReadOnlySpan<byte> au, out int spsPos, out int ppsPos)
    {
        spsPos = ppsPos = -1;
        bool idr = false, sps = false, pps = false;
        int i = 0;
        while (i + 3 < au.Length)
        {
            int sc = (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1) ? 4 :
                     (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) ? 3 : 0;
            if (sc == 0) { i++; continue; }
            int nalStart = i + sc;
            int nalType = (nalStart < au.Length) ? (au[nalStart] & 0x1F) : -1;
            if (nalType == 7) { sps = true; spsPos = i; }
            else if (nalType == 8) { pps = true; ppsPos = i; }
            else if (nalType == 5) idr = true;
            // next
            i = nalStart + 1;
        }
        return (idr, sps, pps);
    }

    void DrainStderrLoop(CancellationToken ct)
    {
        try
        {
            using var sr = _ff!.StandardError; // StreamReader
            char[] buf = new char[4096];
            while (!ct.IsCancellationRequested)
            {
                int n = sr.Read(buf, 0, buf.Length);
                if (n <= 0) break; // ffmpeg đã thoát
                Console.Write(new string(buf, 0, n));
            }
        }
        catch { /* ignore */ }
    }

    private static string ResolveFFmpeg(string? exe)
    {
        // 1) Nếu truyền cụ thể (file hoặc folder) và tồn tại -> dùng luôn
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var p = exe;
            if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                p = Path.Combine(p, "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }

        // 2) Biến môi trường FFMPEG_PATH (file hoặc folder)
        var env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var p = env.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? env : Path.Combine(env, "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }

        // 3) Ưu tiên publish\bin\ffmpeg.exe ngay cạnh app
        var baseDir = AppContext.BaseDirectory; // ...\publish\
        var pBin = Path.Combine(baseDir, "bin", "ffmpeg.exe");
        if (File.Exists(pBin)) return pBin;

        // 4) fallback: ffmpeg.exe ngay cạnh app
        var pLocal = Path.Combine(baseDir, "ffmpeg.exe");
        if (File.Exists(pLocal)) return pLocal;

        // 5) cuối cùng: để hệ thống tra PATH
        return "ffmpeg";
    }

    public FfmpegPipeEncoder(int w, int h, int fps, int bitrateKbps, int crf, string preset, bool zerolatency, string? ffmpegExe = null, InputFormat inputFormat = InputFormat.BGRA)
    {
        Width = w; Height = h; FPS = Math.Max(5, fps);
        BitrateKbps = Math.Max(0, bitrateKbps);
        CRF = crf < 0 ? -1 : crf;
        Preset = string.IsNullOrWhiteSpace(preset) ? "veryfast" : preset;
        ZeroLatency = zerolatency;
        PixelFormat = inputFormat;
        _exePath = ResolveFFmpeg(ffmpegExe);
    }

    string BuildArgs()
    {
        EnsureChosenEncoder();
        Console.WriteLine("[FFMPEG] chosen encoder = " + _chosenGpu);
        int g = Math.Max(FPS, 2);

        switch (_chosenGpu)
        {
            case GpuEnc.NVENC:
                {
                    // Ultra low latency NVENC settings with CUDA GPU color conversion
                    string inPart;
                    string vfPart = "";
                    
                    if (PixelFormat == InputFormat.NV12)
                    {
                        inPart =
                            "-fflags nobuffer -flags low_delay " +
                            "-probesize 32 -analyzeduration 0 " +
                            $"-f rawvideo -pix_fmt nv12 -s {{Width}}x{{Height}} -r {{FPS}} -i - ";
                    }
                    else
                    {
                        // Convert BGRA to YUV420P (strips alpha), then upload to CUDA for NVENC
                        // This is faster than full CPU NV12 conversion because:
                        // 1. YUV420P is 1.5 bytes/pixel vs BGRA 4 bytes/pixel - less data
                        // 2. NVENC encodes YUV420P directly on GPU
                        inPart =
                            "-fflags nobuffer -flags low_delay " +
                            "-probesize 32 -analyzeduration 0 " +
                            "-init_hw_device cuda=cu:0 -filter_hw_device cu " +
                            $"-f rawvideo -pix_fmt bgra -s {{Width}}x{{Height}} -r {{FPS}} -i - ";
                        vfPart = "-vf format=yuv420p,hwupload_cuda ";
                    }

                    // Minimal buffer CBR for lowest latency
                    string rc;
                    if (BitrateKbps > 0)
                    {
                        int bufsize = Math.Max(BitrateKbps / 30, 100); // ~33ms buffer
                        rc = $"-rc cbr -b:v {BitrateKbps}k -maxrate {BitrateKbps}k -bufsize {bufsize}k";
                    }
                    else
                    {
                        int cq = (CRF >= 0 ? CRF : 23);
                        rc = $"-rc vbr -cq {cq}";
                    }

                    // Level 5.1 required for width > 2048 (e.g., multi-monitor setups)
                    string level = Width > 2048 ? "5.1" : "4.1";
                    string profile = Width > 2048 ? "main" : "baseline";
                    
                    var outPart =
                        "-an -c:v h264_nvenc " +
                        "-preset p1 -tune ll " +
                        $"-profile:v {profile} -level {level} " +
                        "-bf 0 -rc-lookahead 0 -forced-idr 1 " +
                        "-zerolatency 1 -delay 0 " +
                        "-aud 1 " +
                        rc + " " +
                        $"-g {g} " +
                        "-f h264 -";

                    return (inPart + vfPart + outPart)
                        .Replace("{Width}", Width.ToString())
                        .Replace("{Height}", Height.ToString())
                        .Replace("{FPS}", FPS.ToString());
                }
            case GpuEnc.QSV:
                {
                    // Ultra low latency QSV settings
                    string inPart;
                    string vfPart;
                    
                    if (PixelFormat == InputFormat.NV12)
                    {
                        inPart =
                            "-fflags nobuffer -flags low_delay " +
                            "-probesize 32 -analyzeduration 0 " +
                            $"-f rawvideo -pix_fmt nv12 -s {{Width}}x{{Height}} -r {{FPS}} -i - ";
                        vfPart = "";
                    }
                    else
                    {
                        inPart =
                            "-fflags nobuffer -flags low_delay " +
                            "-probesize 32 -analyzeduration 0 " +
                            $"-f rawvideo -pix_fmt bgra -s {{Width}}x{{Height}} -r {{FPS}} -i - ";
                        vfPart = "-vf format=nv12 ";
                    }

                    // Minimal buffer CBR
                    string rc;
                    if (BitrateKbps > 0)
                    {
                        int bufsize = Math.Max(BitrateKbps / 30, 100);
                        rc = $"-b:v {BitrateKbps}k -maxrate {BitrateKbps}k -bufsize {bufsize}k -look_ahead 0";
                    }
                    else
                    {
                        int cq = (CRF >= 0 ? CRF : 23);
                        rc = $"-rc icq -global_quality {cq} -look_ahead 0";
                    }

                    // Level 5.1 required for width > 2048
                    string qsvLevel = Width > 2048 ? "5.1" : "4.1";
                    
                    var outPart =
                        "-an -c:v h264_qsv " +
                        $"-profile:v main -level {qsvLevel} " +
                        $"-bf 0 -g {g} -sc_threshold 0 " +
                        "-async_depth 1 -low_power 1 " +
                        rc + " " +
                        "-bsf:v h264_metadata=aud=insert " +
                        "-f h264 -";

                    return (inPart + vfPart + outPart)
                        .Replace("{Width}", Width.ToString())
                        .Replace("{Height}", Height.ToString())
                        .Replace("{FPS}", FPS.ToString());
                }
            case GpuEnc.AMF:
                {
                    // Ultra low latency AMF settings
                    string inPart;
                    string vfPart;
                    
                    if (PixelFormat == InputFormat.NV12)
                    {
                        inPart =
                            "-fflags nobuffer -flags low_delay " +
                            "-probesize 32 -analyzeduration 0 " +
                            $"-f rawvideo -pix_fmt nv12 -s {{Width}}x{{Height}} -r {{FPS}} -i - ";
                        vfPart = "";
                        Console.WriteLine("[FFMPEG-AMF] Using NV12 input (zero-copy friendly)");
                    }
                    else
                    {
                        inPart =
                            "-fflags nobuffer -flags low_delay " +
                            "-probesize 32 -analyzeduration 0 " +
                            $"-f rawvideo -pix_fmt bgra -s {{Width}}x{{Height}} -r {{FPS}} -i - ";
                        
                        int padW = (Width % 2 == 0) ? Width : Width + 1;
                        if (Width % 2 != 0)
                            vfPart = $"-vf pad={padW}:{{Height}}:0:0,format=nv12 ";
                        else
                            vfPart = "-vf format=nv12 ";
                    }

                    // Minimal buffer for lowest latency (~33ms)
                    string rc;
                    if (BitrateKbps > 0)
                    {
                        int bufsize = Math.Max(BitrateKbps / 30, 100);
                        rc = $"-rc cbr -b:v {BitrateKbps}k -maxrate {BitrateKbps}k -bufsize {bufsize}k";
                    }
                    else
                    {
                        int cq = (CRF >= 0 ? CRF : 23);
                        rc = $"-rc cqp -qp_i {cq} -qp_p {cq + 2}";
                    }

                    // Level 5.1 required for width > 2048
                    string amfLevel = Width > 2048 ? "5.1" : "4.1";
                    
                    var outPart =
                        "-an -c:v h264_amf " +
                        "-usage ultralowlatency " +
                        "-quality speed " +
                        $"-profile:v main -level {amfLevel} " +
                        "-preanalysis false -vbaq false " +
                        "-enforce_hrd false -filler_data false " +
                        "-frame_skipping false " +
                        "-bf:v 0 -log_to_dbg 0 " +
                        $"-g {g} -keyint_min {g} " +
                        rc + " " +
                        "-bsf:v h264_metadata=aud=insert " +
                        "-f h264 -";

                    return (inPart + vfPart + outPart)
                        .Replace("{Width}", Width.ToString())
                        .Replace("{Height}", Height.ToString())
                        .Replace("{FPS}", FPS.ToString());
                }
            case GpuEnc.None:
                {
                    // Ultra low latency libx264 settings
                    var x264Params =
                        "profile=constrained_baseline:level=3.1" +
                        $":keyint={g}:min-keyint={g}:scenecut=0" +
                        ":bframes=0:ref=1:cabac=0" +
                        ":aud=1:repeat-headers=1" +
                        ":sliced-threads=0:slices=1" +
                        ":rc-lookahead=0:sync-lookahead=0" +
                        ":intra-refresh=0:open-gop=0";

                    string inPart;
                    string vfPart;
                    
                    if (PixelFormat == InputFormat.NV12)
                    {
                        inPart = $"-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 " +
                                 $"-f rawvideo -pix_fmt nv12 -s {Width}x{Height} -r {FPS} -i - ";
                        vfPart = "";
                    }
                    else
                    {
                        inPart = $"-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 " +
                                 $"-f rawvideo -pix_fmt bgra -s {Width}x{Height} -r {FPS} -i - ";
                        vfPart = "-vf format=yuv420p ";
                    }

                    var encPart =
                        "-an -c:v libx264 " +
                        $"-preset {Preset} " +
                        "-tune zerolatency " +
                        "-pix_fmt yuv420p " +
                        "-profile:v baseline -level:v 3.1 " +
                        $"-x264-params {x264Params} " +
                        $"-g {g} " +
                        "-f h264 -";

                    string rc;
                    if (BitrateKbps > 0)
                    {
                        int bufsize = Math.Max(BitrateKbps / 30, 100);
                        rc = $"-b:v {BitrateKbps}k -maxrate {BitrateKbps}k -bufsize {bufsize}k ";
                    }
                    else
                    {
                        var crf = CRF >= 0 ? CRF : 23;
                        rc = $"-crf {crf} ";
                    }

                    return inPart + vfPart + encPart.Replace("-f h264 -", rc + "-f h264 -");
                }
            default:
                return "";
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            Stop();
            _cts = new CancellationTokenSource();
            _ff = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exePath, // << dùng đường dẫn đã resolve
                    Arguments = "-hide_banner -loglevel info " + BuildArgs(),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            Console.WriteLine("[FFMPEG] exe=" + _ff.StartInfo.FileName);
            _ff.Start();
            _stdin = _ff.StandardInput.BaseStream;
            _stdout = _ff.StandardOutput.BaseStream;
            _readerTask = Task.Run(() => ReadStdoutLoop(_cts!.Token));
            _errTask = Task.Run(() => DrainStderrLoop(_cts!.Token));
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            try { _cts?.Cancel(); } catch { }
            try { _stdin?.Dispose(); } catch { }
            try { _stdout?.Dispose(); } catch { }
            try { if (_ff != null && !_ff.HasExited) _ff.Kill(true); } catch { }
            try { _ff?.Dispose(); } catch { }
            try { _errTask?.Wait(50); } catch { }
            _stdin = _stdout = null; _ff = null;
            _readerTask = null;
            _cts?.Dispose(); _cts = null;
            _auBuf.SetLength(0);
        }
    }

    public void Dispose() => Stop();

    public void ReconfigureOnResize(int newW, int newH)
    {
        if (newW == Width && newH == Height) return;
        Width = newW; Height = newH;
        Start(); // restart encoder với kích thước mới
    }

    public void PushBGRAFrame(ReadOnlySpan<byte> bgra, int strideBytes, uint durationMs)
    {
        lock (_sync)
        {
            if (_stdin == null) return;
            int rowBytes = Width * 4;
            if (strideBytes == rowBytes)
            {
                _stdin.Write(bgra);
            }
            else
            {
                for (int y = 0; y < Height; y++)
                {
                    var row = bgra.Slice(y * strideBytes, rowBytes);
                    _stdin.Write(row);
                }
            }
            _lastDurationMs = durationMs;
        }
    }

    /// <summary>
    /// Push NV12 frame to encoder. NV12 format is Y plane followed by interleaved UV plane.
    /// Total size: Width * Height * 1.5 bytes
    /// Y plane: Width * Height bytes (full resolution)
    /// UV plane: Width * Height / 2 bytes (half resolution, interleaved U and V)
    /// </summary>
    public void PushNV12Frame(ReadOnlySpan<byte> nv12, int yStride, int uvStride, uint durationMs)
    {
        lock (_sync)
        {
            if (_stdin == null) return;
            
            // Y plane: Height rows of Width bytes each
            int yRowBytes = Width;
            // UV plane: Height/2 rows of Width bytes each (interleaved UV)
            int uvRowBytes = Width;
            int uvHeight = Height / 2;
            
            // Write Y plane
            if (yStride == yRowBytes)
            {
                _stdin.Write(nv12.Slice(0, yRowBytes * Height));
            }
            else
            {
                for (int y = 0; y < Height; y++)
                {
                    var row = nv12.Slice(y * yStride, yRowBytes);
                    _stdin.Write(row);
                }
            }
            
            // Write UV plane (starts after Y plane in input buffer)
            int uvOffset = yStride * Height;
            if (uvStride == uvRowBytes)
            {
                _stdin.Write(nv12.Slice(uvOffset, uvRowBytes * uvHeight));
            }
            else
            {
                for (int y = 0; y < uvHeight; y++)
                {
                    var row = nv12.Slice(uvOffset + y * uvStride, uvRowBytes);
                    _stdin.Write(row);
                }
            }
            
            _lastDurationMs = durationMs;
        }
    }

    /// <summary>
    /// Push NV12 frame from contiguous buffer (Y and UV planes stored sequentially)
    /// </summary>
    public void PushNV12FrameContiguous(ReadOnlySpan<byte> nv12, uint durationMs)
    {
        lock (_sync)
        {
            if (_stdin == null) return;
            // NV12 contiguous: Width * Height * 1.5 bytes total
            int expectedSize = Width * Height * 3 / 2;
            if (nv12.Length >= expectedSize)
            {
                _stdin.Write(nv12.Slice(0, expectedSize));
            }
            _lastDurationMs = durationMs;
        }
    }

    void ReadStdoutLoop(CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = _stdout!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                AppendAndSplit(new ReadOnlySpan<byte>(buf, 0, n));
            }
        }
        catch { /* process ended */ }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    static readonly byte[] AUD = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x09 };
    static int NextStartCode(byte[] buf, int from)
    {
        if (buf == null) throw new ArgumentNullException(nameof(buf));
        int n = buf.Length;
        if (from < 0) from = 0;
        if (from > n - 3) return n; // tối thiểu cần 3 byte để có start code

        for (int i = from; i <= n - 3; i++)
        {
            // 3-byte: 00 00 01
            if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1)
                return i;

            // 4-byte: 00 00 00 01
            if (i <= n - 4 && buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1)
                return i;
        }
        return n;
    }

    void AppendAndSplit(ReadOnlySpan<byte> chunk)
    {
        // Append vào buffer tích luỹ
        _auBuf.Write(chunk);
        var data = _auBuf.GetBuffer();
        int total = (int)_auBuf.Length;

        // Thu thập mọi NAL (pos, scLen, type) — hỗ trợ 3/4 byte start code
        var nals = new List<(int pos, int scLen, int type)>();
        int i = 0;
        while (i + 3 < total)
        {
            if (i + 4 <= total && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                int type = (i + 4 < total) ? (data[i + 4] & 0x1F) : -1;
                nals.Add((i, 4, type)); i += 4; continue;
            }
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                int type = (i + 3 < total) ? (data[i + 3] & 0x1F) : -1;
                nals.Add((i, 3, type)); i += 3; continue;
            }
            i++;
        }
        if (nals.Count == 0) { CapBufferIfTooLarge(); return; }

        // Ưu tiên cắt theo AUD (type=9)
        var audIdxs = new List<int>();
        for (int k = 0; k < nals.Count; k++) if (nals[k].type == 9) audIdxs.Add(k);
        if (audIdxs.Count >= 2)
        {
            for (int a = 0; a < audIdxs.Count - 1; a++)
            {
                int start = nals[audIdxs[a]].pos;
                int end = nals[audIdxs[a + 1]].pos;

                // Có VCL (type 1/5) mới emit
                bool hasVcl = false, hasIdrLocal = false;
                for (int t = audIdxs[a]; t < audIdxs[a + 1]; t++)
                {
                    int ty = nals[t].type;
                    if (ty == 1 || ty == 5) { hasVcl = true; if (ty == 5) hasIdrLocal = true; }
                }
                if (!hasVcl) continue;

                int len = end - start;
                var au = new byte[len];
                Buffer.BlockCopy(data, start, au, 0, len);

                // Cập nhật cache
                var (hasIdr, hasSps, hasPps) = ScanNalTypes(au, out int spsPos, out int ppsPos);
                if (hasSps && spsPos >= 0) _lastSps = au.AsSpan(spsPos, NextStartCode(au, spsPos) - spsPos).ToArray();
                if (hasPps && ppsPos >= 0) _lastPps = au.AsSpan(ppsPos, NextStartCode(au, ppsPos) - ppsPos).ToArray();

                // Warm-up: IDR mà cache chưa có -> bỏ
                if ((hasIdr || hasIdrLocal) && (_lastSps == null || _lastPps == null))
                    continue;

                // LUÔN prepend SPS/PPS cho mọi IDR, bất kể au đã có hay chưa
                if (hasIdr || hasIdrLocal)
                {
                    var sps = _lastSps; var pps = _lastPps;
                    if (sps != null && pps != null)
                    {
                        var fixedAu = new byte[sps.Length + pps.Length + au.Length];
                        int off = 0;
                        Buffer.BlockCopy(sps, 0, fixedAu, off, sps.Length); off += sps.Length;
                        Buffer.BlockCopy(pps, 0, fixedAu, off, pps.Length); off += pps.Length;
                        Buffer.BlockCopy(au, 0, fixedAu, off, au.Length);
                        au = fixedAu;
                    }
                }

                OnEncodedAccessUnit?.Invoke(_lastDurationMs, au);
            }

            // Giữ phần đuôi từ AUD cuối
            int keepFrom = nals[audIdxs[^1]].pos;
            int remain = total - keepFrom;
            var tail = new byte[remain];
            Buffer.BlockCopy(data, keepFrom, tail, 0, remain);
            _auBuf.SetLength(0);
            _auBuf.Write(tail, 0, remain);
            return;
        }

        // Fallback: không thấy ≥2 AUD → cắt theo VCL (giống trước đây nhưng rón rén hơn)
        // Kéo lùi header (AUD/SPS/PPS/SEI) trước VCL, emit khi gặp VCL kế tiếp.
        static int BackToHeaders(List<(int pos, int scLen, int type)> list, int vclIdx)
        {
            int start = list[vclIdx].pos;
            for (int k = vclIdx - 1; k >= 0; k--)
            {
                int t = list[k].type;
                if (t == 9 || t == 7 || t == 8 || t == 6) start = list[k].pos;
                else break;
            }
            return start;
        }

        int frameStart = -1; bool haveVcl = false;
        for (int idx2 = 0; idx2 < nals.Count; idx2++)
        {
            int ty = nals[idx2].type; bool isVcl = (ty == 1 || ty == 5);
            if (!isVcl) continue;

            if (haveVcl && frameStart >= 0)
            {
                int end = nals[idx2].pos;
                if (end > frameStart)
                {
                    int len = end - frameStart;
                    var au = new byte[len];
                    Buffer.BlockCopy(data, frameStart, au, 0, len);
                    OnEncodedAccessUnit?.Invoke(_lastDurationMs, au);
                }
            }
            frameStart = BackToHeaders(nals, idx2);
            haveVcl = true;
        }

        if (haveVcl && frameStart >= 0)
        {
            // giữ đuôi từ frameStart (AU đang dở) cho lần sau
            int remain = total - frameStart;
            var tail = new byte[remain];
            Buffer.BlockCopy(data, frameStart, tail, 0, remain);
            _auBuf.SetLength(0);
            _auBuf.Write(tail, 0, remain);
        }
        else
        {
            // Chưa thấy VCL → giữ nguyên; chỉ chặn phình buffer
            CapBufferIfTooLarge(nals);
        }

        // helper
        void CapBufferIfTooLarge(List<(int pos, int scLen, int type)>? list = null)
        {
            if (_auBuf.Length <= _maxBufferBytes) return;
            int keepFrom = 0;
            if (list != null && list.Count > 0) keepFrom = list[^1].pos;
            int remain = total - keepFrom;
            if (remain < 0) remain = 0;
            var tail = new byte[remain];
            if (remain > 0) Buffer.BlockCopy(data, keepFrom, tail, 0, remain);
            _auBuf.SetLength(0);
            if (remain > 0) _auBuf.Write(tail, 0, remain);
        }
    }
}
#endregion