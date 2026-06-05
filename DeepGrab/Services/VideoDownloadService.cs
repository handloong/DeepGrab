using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace DeepGrab.Services;

/// <summary>视频下载引擎（yt-dlp + ffmpeg 封装），与站点无关</summary>
public class VideoDownloadService
{
    string _downloadPath;
    string _dateFormat = "yyyyMMdd";
    string _ytdlPath = "yt-dlp";
    string _ffmpegPath = "ffmpeg";
    readonly string _appBasePath;

    public string VideosPath => _downloadPath;

    public VideoDownloadService(string appBasePath)
    {
        _appBasePath = appBasePath;
        _downloadPath = Path.Combine(appBasePath, "videos");
    }

    public void SetDownloadPath(string path) { if (!string.IsNullOrWhiteSpace(path)) _downloadPath = path; }
    public void SetDateFormat(string format) { _dateFormat = format ?? ""; }

    public async Task InitializeAsync()
    {
        string yexe = Path.Combine(_appBasePath, "yt-dlp.exe");
        string fexe = Path.Combine(_appBasePath, "ffmpeg.exe");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!File.Exists(yexe)) await YoutubeDLSharp.Utils.DownloadYtDlp(_appBasePath);
            _ytdlPath = yexe;
            if (!File.Exists(fexe)) await YoutubeDLSharp.Utils.DownloadFFmpeg(_appBasePath);
            _ffmpegPath = fexe;
        }
    }

    YoutubeDL CreateYtdl() => new() { YoutubeDLPath = _ytdlPath, FFmpegPath = _ffmpegPath };

    // ==================== yt-dlp 下载（Pinse 等） ====================

    public async Task<string?> DownloadAsync(
        string videoUrl, Dictionary<string, string> headers,
        string outputTitle, IProgress<DownloadProgress> progress, CancellationToken ct = default)
    {
        string outputDir = GetOutputDir();
        Directory.CreateDirectory(outputDir);
        string safeTitle = $"{outputTitle}_{Random.Shared.Next(1000, 9999)}";

        var opts = new OptionSet { NoPart = true, WindowsFilenames = true, Output = Path.Combine(outputDir, $"{safeTitle}.%(ext)s") };
        foreach (var (k, v) in headers)
            opts.AddCustomOption("--add-header", $"{k}: {v}");

        var ytdl = CreateYtdl();
        var result = await ytdl.RunVideoDownload(videoUrl, progress: progress, overrideOptions: opts, ct: ct);
        return result.Success ? result.Data : null;
    }

    // ==================== HttpClient 下载 m3u8 + ts 分片 + ffmpeg 本地拼接 ====================

    const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public async Task<string?> DownloadWithFfmpegAsync(
        string m3u8Url, string outputTitle, string refererUrl, HttpClient http,
        Action<int, int>? onProgress, CancellationToken ct = default)
    {
        string outputDir = GetOutputDir();
        Directory.CreateDirectory(outputDir);
        string fileName = $"{outputTitle}_{Random.Shared.Next(1000, 9999)}.mp4";
        string outputPath = Path.Combine(outputDir, fileName);

        // 构造 headers（参考浏览器插件：Origin + Referer[视频页URL] 是防盗链关键）
        string origin = "";
        try { var u = new Uri(refererUrl); origin = $"{u.Scheme}://{u.Host}"; } catch { }

        // 1. 下载 m3u8 文本
        var tsUrls = new List<string>();
        using (var req = new HttpRequestMessage(HttpMethod.Get, m3u8Url))
        {
            req.Headers.TryAddWithoutValidation("User-Agent", UA);
            if (!string.IsNullOrEmpty(origin)) req.Headers.TryAddWithoutValidation("Origin", origin);
            req.Headers.TryAddWithoutValidation("Referer", refererUrl);
            var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            string m3u8Text = await resp.Content.ReadAsStringAsync();

            // 2. 解析 ts 分片 URL
            string baseUri = m3u8Url[..(m3u8Url.LastIndexOf('/') + 1)];
            var lines = m3u8Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("#EXTINF:")) continue;
                // 下一行是 ts 文件名
                if (i + 1 >= lines.Length) break;
                string tsName = lines[i + 1].Trim();
                if (string.IsNullOrEmpty(tsName) || tsName.StartsWith("#")) continue;
                tsUrls.Add(tsName.StartsWith("http") ? tsName : baseUri + tsName);
            }
        }

        if (tsUrls.Count == 0)
            throw new Exception("m3u8 中未找到任何 TS 分片");

        // 3. 并发下载 ts 分片到临时目录
        string tempDir = Path.Combine(outputDir, $"_ts_{Random.Shared.Next(10000, 99999)}");
        Directory.CreateDirectory(tempDir);

        try
        {
            int done = 0;
            var sem = new SemaphoreSlim(6); // 对标插件 10 并发，保守用 6
            var tasks = new List<Task>();
            int total = tsUrls.Count;

            int failed = 0;
            for (int i = 0; i < tsUrls.Count; i++)
            {
                int idx = i;
                string tsUrl = tsUrls[i];
                string tsPath = Path.Combine(tempDir, $"segment_{idx:D5}.ts");

                var t = Task.Run(async () =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        using var tsReq = new HttpRequestMessage(HttpMethod.Get, tsUrl);
                        tsReq.Headers.TryAddWithoutValidation("User-Agent", UA);
                        if (!string.IsNullOrEmpty(origin)) tsReq.Headers.TryAddWithoutValidation("Origin", origin);
                        tsReq.Headers.TryAddWithoutValidation("Referer", refererUrl);

                        var tsResp = await http.SendAsync(tsReq, ct);
                        tsResp.EnsureSuccessStatusCode();
                        byte[] data = await tsResp.Content.ReadAsByteArrayAsync(ct);

                        await File.WriteAllBytesAsync(tsPath, data, ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref failed);
                        try { File.WriteAllText(tsPath + ".err", ex.Message); } catch { }
                    }
                    catch { }
                    finally { sem.Release(); }

                    int cur = Interlocked.Increment(ref done);
                    onProgress?.Invoke(cur, total);
                }, ct);

                tasks.Add(t);
            }

            await Task.WhenAll(tasks);

            // 4. 生成本地 concat 列表
            string concatPath = Path.Combine(tempDir, "concat.txt");
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < total; i++)
            {
                string tsFile = Path.Combine(tempDir, $"segment_{i:D5}.ts");
                if (File.Exists(tsFile))
                    sb.AppendLine($"file '{tsFile.Replace("\\", "/")}'");
            }
            await File.WriteAllTextAsync(concatPath, sb.ToString(), ct);

            // 5. ffmpeg 纯本地拼接（不联网，0 TLS 风险）
            string errLog = "";
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-y -f concat -safe 0 -i \"{concatPath}\" -c copy \"{outputPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi };
            var tcs = new TaskCompletionSource<bool>();
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                try { errLog = proc.StandardError.ReadToEnd(); } catch { }
                tcs.TrySetResult(true);
            };
            proc.Start();
            using var ctReg = ct.Register(() => { try { proc.Kill(); } catch { } });
            await tcs.Task;

            if (failed > 0)
                File.WriteAllText(outputPath + ".ts_errors.txt",
                    $"有 {failed}/{total} 个 TS 分片下载失败（已跳过）\nffmpeg stderr:\n{errLog}");
        }
        finally
        {
            // 6. 清理临时目录
            try { Directory.Delete(tempDir, true); } catch { }
        }

        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            return outputPath;

        return null;
    }

    string GetOutputDir()
    {
        string dir = _downloadPath;
        if (!string.IsNullOrEmpty(_dateFormat))
            dir = Path.Combine(dir, DateTime.Now.ToString(_dateFormat));
        return dir;
    }

    public void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(VideosPath);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Process.Start("explorer.exe", VideosPath);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Process.Start("open", VideosPath);
            else Process.Start("xdg-open", VideosPath);
        }
        catch { }
    }
}
