using System.Diagnostics;
using System.Runtime.InteropServices;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace DeepGrab.Services;

/// <summary>视频下载引擎（yt-dlp 封装），与站点无关</summary>
public class VideoDownloadService
{
    const string BasePath = "."; // 实际路径由 SetDownloadPath 设置
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
        // 确保 GenericVideoResolver 中的 yt-dlp 也能用
        string genYtPath = Path.Combine(_appBasePath, "yt-dlp.exe");
        if (!File.Exists(genYtPath) && File.Exists(yexe))
        {
            // 从 PinseDownloader 旧目录复制 yt-dlp
            try { File.Copy(yexe, genYtPath, true); } catch { }
        }
    }

    YoutubeDL CreateYtdl() => new() { YoutubeDLPath = _ytdlPath, FFmpegPath = _ffmpegPath };

    public async Task<string?> DownloadAsync(
        string videoUrl, Dictionary<string, string> headers,
        string outputTitle, IProgress<DownloadProgress> progress, CancellationToken ct = default)
    {
        string outputDir = _downloadPath;
        if (!string.IsNullOrEmpty(_dateFormat))
            outputDir = Path.Combine(outputDir, DateTime.Now.ToString(_dateFormat));
        Directory.CreateDirectory(outputDir);
        string safeTitle = $"{outputTitle}_{Random.Shared.Next(1000, 9999)}";

        var opts = new OptionSet { NoPart = true, WindowsFilenames = true, Output = Path.Combine(outputDir, $"{safeTitle}.%(ext)s") };
        foreach (var (k, v) in headers)
            opts.AddCustomOption("--add-header", $"{k}: {v}");

        var ytdl = CreateYtdl();
        var result = await ytdl.RunVideoDownload(videoUrl, progress: progress, overrideOptions: opts, ct: ct);
        return result.Success ? result.Data : null;
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
