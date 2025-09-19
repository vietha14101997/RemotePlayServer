#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

#region Minimal FFmpeg pipe encoder (control real bitrate/fps via libx264)
// Encoder chạy ffmpeg qua stdin/stdout để KHÓA thật fps/bitrate/CRF.
// Đầu vào: raw BGRA; Đầu ra: Annex-B H.264 (có AUD) -> tách theo AU và bắn qua WebRTC.
internal sealed class FfmpegPipeEncoder : IDisposable
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FPS { get; private set; }
    public int BitrateKbps { get; private set; }   // 0 => dùng CRF
    public int CRF { get; private set; }           // <0 => off
    public string Preset { get; private set; }
    public bool ZeroLatency { get; private set; }

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
    const int _maxBufferBytes = 4 * 1024 * 1024;
    private readonly string _exePath;

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

    public FfmpegPipeEncoder(int w, int h, int fps, int bitrateKbps, int crf, string preset, bool zerolatency, string? ffmpegExe = null)
    {
        Width = w; Height = h; FPS = Math.Max(5, fps);
        BitrateKbps = Math.Max(0, bitrateKbps);
        CRF = crf < 0 ? -1 : crf;
        Preset = string.IsNullOrWhiteSpace(preset) ? "veryfast" : preset;
        ZeroLatency = zerolatency;
        _exePath = ResolveFFmpeg(ffmpegExe);
    }

    string BuildArgs()
    {
        // -r: fps output (khóa fps encoder), -g: keyint ~ 1s
        // ép x264 chèn AUD mỗi frame + lặp SPS/PPS ở mỗi keyframe (IDR)
        var x264Params = $"keyint={Math.Max(FPS, 2)}:min-keyint={Math.Max(FPS, 2)}:scenecut=0:aud=1:repeat-headers=1:slices=1";
        if (BitrateKbps > 0) x264Params += ":nal-hrd=cbr"; // đẹp HRD cho CBR

        var common =
            $"-f rawvideo -pix_fmt bgra -s {Width}x{Height} -r {FPS} -i - " +
            "-an -c:v libx264 " +
            $"-preset {Preset} " +
            (ZeroLatency ? "-tune zerolatency " : "") +
            $"-x264-params {x264Params} " +
            "-pix_fmt yuv420p " +
            $"-g {Math.Max(FPS, 2)} " +
            // không cần bsf aud nữa vì x264 đã chèn sẵn
            "-f h264 -";

        if (BitrateKbps > 0)
        {
            var vbv = Math.Max(BitrateKbps * 2, 1000);
            return $"{common} -b:v {BitrateKbps}k -maxrate {BitrateKbps}k -bufsize {vbv}k";
        }
        else
        {
            var crf = CRF >= 0 ? CRF : 23;
            return $"{common} -crf {crf}";
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
                    Arguments = "-hide_banner -loglevel error " + BuildArgs(),
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

                // chỉ emit nếu đoạn này có VCL (1/5)
                bool hasVcl = false;
                for (int t = audIdxs[a]; t < audIdxs[a + 1]; t++)
                {
                    int ty = nals[t].type; if (ty == 1 || ty == 5) { hasVcl = true; break; }
                }
                if (!hasVcl) continue;

                int len = end - start;
                var au = new byte[len];
                Buffer.BlockCopy(data, start, au, 0, len);
                OnEncodedAccessUnit?.Invoke(_lastDurationMs, au);
            }

            // Giữ đuôi từ AUD cuối cùng
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