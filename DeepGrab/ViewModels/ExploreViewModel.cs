using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepGrab.Models;
using DeepGrab.Services;
using DeepGrab.Sites;

namespace DeepGrab.ViewModels;

public partial class ExploreViewModel : ObservableObject
{
    readonly HttpClient _http = new(new HttpClientHandler{
        AllowAutoRedirect = true,
        UseCookies = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    }){ Timeout = TimeSpan.FromSeconds(15) };
    readonly DatabaseService _db;
    readonly HashSet<string> _downloadedUrls;
    readonly IDeepGrabSite _site;

    string _baseUrl = "";
    int _currentPage = 1;

    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private int _durationIndex;
    [ObservableProperty] private string _currentLabel;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasNextPage = true;

    public ObservableCollection<VideoItem> Videos { get; } = [];
    public IReadOnlyList<(string Label, string Url)> Categories => _site.Categories;
    public IReadOnlyList<(string Label, string Value)> DurationFilters => _site.DurationFilters;

    public event Action<string, string>? VideoSelected;
    public event Action? ScrollToEndRequested;

    public ExploreViewModel(DatabaseService db, IDeepGrabSite site)
    {
        _db = db; _site = site;
        _currentLabel = site.Categories[0].Label; // 默认第一个分类
        _downloadedUrls = new HashSet<string>(_db.GetAllRecords().Select(r=>r.Url));
    }

    [RelayCommand] async Task LoadCategory(string labelUrl)
    {
        try
        {
            var parts = labelUrl.Split('|');
            var label = parts[0]; var url = parts[1];
            await FirstPage(label, url);
        }
        catch { }
    }

    [RelayCommand] async Task LoadCategoryByIndex(int idx)
    {
        try
        {
            if (idx < 0 || idx >= Categories.Count) return;
            await FirstPage(Categories[idx].Label, Categories[idx].Url);
        }
        catch { }
    }

    [RelayCommand] async Task Search()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SearchKeyword)) return;
            string dv = "", dl = "全部";
            var fs = _site.DurationFilters;
            int di = DurationIndex;
            if (di >= 0 && di < fs.Count) { dl = fs[di].Label; dv = fs[di].Value; }
            CurrentLabel = $"搜索: {SearchKeyword} ({dl})";
            string url = _site.BuildSearchUrl(SearchKeyword.Trim());
            if (!string.IsNullOrEmpty(dv)) url += dv;
            await FirstPage(null, url);
        }
        catch { }
    }

    [RelayCommand]
    async Task LoadNextPage()
    {
        if (string.IsNullOrEmpty(_baseUrl) || IsLoading || !HasNextPage) return;
        _currentPage++;
        string url = _site.BuildPageUrl(_baseUrl, _currentPage);
        IsLoading=true; HasNextPage=false;
        try { int before=Videos.Count; await AppendPage(url); HasNextPage=Videos.Count>before; if(HasNextPage) ScrollToEndRequested?.Invoke(); }
        catch { HasNextPage=false; }
        finally { IsLoading=false; }
    }

    [RelayCommand] void DownloadSelected()
    {
        foreach(var v in Videos.Where(v=>v.IsSelected).ToList())
        { v.IsSelected=false; v.IsAdded=true; _downloadedUrls.Add(v.Url); VideoSelected?.Invoke(v.Url, v.Title); }
    }

    async Task FirstPage(string? label, string url)
    {
        if(label!=null) CurrentLabel=label;
        _baseUrl=url; _currentPage=1; HasNextPage=true; Videos.Clear(); IsLoading=true;
        try
        {
            // 预热：访问首页获取 Cloudflare cookie（对需要反爬的站点有效）
            try { using var w = new HttpRequestMessage(HttpMethod.Get, _site.BaseUrl+"/"); AddHeaders(w, null); await _http.SendAsync(w); } catch { }
            await AppendPage(url);
            HasNextPage = Videos.Count > 0;
        }
        catch { HasNextPage = false; }
        finally { IsLoading=false; }
    }

    void AddHeaders(HttpRequestMessage req, string? referer)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", _site.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("Cache-Control", "max-age=0");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        if (!string.IsNullOrEmpty(referer))
            req.Headers.TryAddWithoutValidation("Referer", referer);
    }

    async Task AppendPage(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req, _site.BaseUrl + "/");
        using var resp = await _http.SendAsync(req); resp.EnsureSuccessStatusCode();
        string html = await resp.Content.ReadAsStringAsync();
        foreach(var item in _site.ParseVideoList(html))
        { item.IsAdded=_downloadedUrls.Contains(item.Url); item.Number=Videos.Count+1; Videos.Add(item); }
    }

    public async Task InitAsync()
    {
        if (Videos.Count==0) await FirstPage(Categories[0].Label, Categories[0].Url);
    }
}
