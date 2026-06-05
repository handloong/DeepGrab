using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepGrab.Models;
using DeepGrab.Services;
using DeepGrab.Sites;
using YoutubeDLSharp;

namespace DeepGrab.ViewModels;

public partial class MainViewModel : ObservableObject
{
    readonly DatabaseService _db;
    readonly VideoDownloadService _engine;
    readonly HttpClient _http = new(new HttpClientHandler {
        AllowAutoRedirect = true,
        UseCookies = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(30) };
    SemaphoreSlim _concurrency = new(3);
    CancellationTokenSource _cancelAll = new();

    [ObservableProperty] private string _inputUrl = "";
    public ObservableCollection<DownloadTask> Downloads { get; } = [];
    public ExploreViewModel Explore { get; }
    public ExploreViewModel PornhubExplore { get; }
    public SettingsViewModel Settings { get; }

    readonly IDeepGrabSite _pinse;
    readonly IDeepGrabSite _ph;

    public MainViewModel()
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        _db = new DatabaseService(basePath);
        _engine = new VideoDownloadService(basePath);

        _pinse = new PinseSite();
        Explore = new ExploreViewModel(_db, _pinse);
        Explore.VideoSelected += (url, title) => StartDownload(url, title);

        _ph = new PHSite();
        PornhubExplore = new ExploreViewModel(_db, _ph);
        PornhubExplore.VideoSelected += (url, title) => StartDownload(url, title);

        Settings = new SettingsViewModel(_db);
        Settings.SettingsChanged += ApplySettings;
        ApplySettings();
    }

    void ApplySettings()
    {
        _engine.SetDownloadPath(Settings.DownloadPath);
        _engine.SetDateFormat(Settings.DateFormat);
        var ns = new SemaphoreSlim(Settings.MaxConcurrent);
        var o = Interlocked.Exchange(ref _concurrency, ns);
        o?.Dispose();
    }

    public async Task InitializeAsync()
    {
        await _engine.InitializeAsync();
        _ = LoadHistoryAsync();
    }

    async Task LoadHistoryAsync()
    {
        foreach (var r in _db.GetAllRecords())
            Downloads.Add(new DownloadTask { Url=r.Url, Title=r.Title, Status=r.FileExists?DownloadStatus.Completed:DownloadStatus.Deleted, FilePath=r.FilePath });
    }

    [RelayCommand] async Task AddDownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(InputUrl)) return;
        var url = InputUrl.Trim(); InputUrl = "";
        StartDownload(url);
    }

    void StartDownload(string url, string? preTitle = null)
    {
        var active = Downloads.FirstOrDefault(d => d.Url == url && d.Status is DownloadStatus.Queued or DownloadStatus.Parsing or DownloadStatus.Downloading);
        if (active != null) return;
        var exist = Downloads.FirstOrDefault(d => d.Url == url);
        if (exist != null) Downloads.Remove(exist);

        var task = new DownloadTask { Url = url };
        if (!string.IsNullOrWhiteSpace(preTitle)) task.Title = preTitle;
        Downloads.Insert(0, task);
        _ = ProcessTaskAsync(task);
    }

    [RelayCommand] void RemoveDownload(DownloadTask? t) { if (t != null) { _db.DeleteRecord(t.Url); Downloads.Remove(t); } }
    [RelayCommand] void ClearCompleted() { foreach (var d in Downloads.Where(d=>d.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Deleted).ToList()) { _db.DeleteRecord(d.Url); Downloads.Remove(d); } }
    [RelayCommand] void OpenFolder() => _engine.OpenDownloadFolder();
    [RelayCommand] void CancelAll() { _cancelAll.Cancel(); _cancelAll.Dispose(); _cancelAll = new(); }
    [RelayCommand] void PlayFile(DownloadTask? t) { if (t!=null && File.Exists(t.FilePath)) Process.Start(new ProcessStartInfo(t.FilePath){UseShellExecute=true}); }

    async Task ProcessTaskAsync(DownloadTask task)
    {
        await _concurrency.WaitAsync();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancelAll.Token);
            task.Status = DownloadStatus.Parsing;

            string videoUrl, referer, title;

            // 根据 URL 匹配站点解析逻辑
            var site = task.Url.Contains("pornhub.com") ? _ph :
                       task.Url.Contains("91pinse.com") ? _pinse :
                       null;

            if (site != null)
            {
                var resolved = await site.ResolveVideoAsync(task.Url, _http);
                if (resolved == null) { task.ErrorMessage="解析失败"; task.Status=DownloadStatus.Failed; return; }
                (videoUrl, referer, title) = resolved.Value;
            }
            else
            {
                // 直接当视频直链处理
                videoUrl = task.Url;
                referer = task.Url;
                title = $"video_{Random.Shared.Next(10000,99999)}";
            }

            if (string.IsNullOrWhiteSpace(task.Title)) task.Title = title;

            string? path;

            // PH 等站点：HttpClient 下载 ts 分片 + ffmpeg 本地拼接
            if (site?.UseFfmpegDownload == true)
            {
                task.Status = DownloadStatus.Downloading;
                path = await _engine.DownloadWithFfmpegAsync(
                    videoUrl, task.Title, referer, _http,
                    (done, total) =>
                    {
                        task.Progress = total > 0 ? (double)done / total * 100 : 0;
                        task.Speed = $"ts {done}/{total}";
                        task.Eta = "下载分片";
                    },
                    cts.Token);
            }
            else
            {
                double lp = 0;
                var prog = new Progress<DownloadProgress>(p => {
                    double raw = (double)p.Progress, sm = raw;
                    if (raw > lp) { double ms = Math.Max(3, (100-lp)*0.1); sm = Math.Min(raw, lp+ms); }
                    lp = Math.Max(lp, sm);
                    task.Progress=sm; task.Speed=p.DownloadSpeed??""; task.Eta=p.ETA??"";
                    if (p.State==DownloadState.Downloading) task.Status=DownloadStatus.Downloading;
                    else if (p.State==DownloadState.PostProcessing) task.Status=DownloadStatus.PostProcessing;
                });

                var headers = new Dictionary<string, string> { ["User-Agent"] = (site ?? _pinse).UserAgent, ["Referer"] = referer };
                try { var u = new Uri(referer); headers["Origin"] = $"{u.Scheme}://{u.Host}"; } catch { }

                path = await _engine.DownloadAsync(videoUrl, headers, task.Title, prog, cts.Token);
            }

            if (path != null)
            {
                task.Status = DownloadStatus.Completed; task.Progress = 100; task.FilePath = path;
                long size = 0; try { size = new FileInfo(path).Length; } catch { }
                _db.InsertRecord(task.Url, task.Title, path, size);
            }
            else { task.ErrorMessage="下载失败"; task.Status=DownloadStatus.Failed; }
        }
        catch (OperationCanceledException) { task.ErrorMessage="已取消"; task.Status=DownloadStatus.Failed; }
        catch (Exception ex) { task.ErrorMessage=ex.Message; task.Status=DownloadStatus.Failed; }
        finally { _concurrency.Release(); }
    }
}
