using System.Net;
using System.Text.RegularExpressions;
using DeepGrab.Models;
using DeepGrab.Services;

namespace DeepGrab.Sites;

public class PinseSite : IDeepGrabSite
{
    public string Name => "91Pinse";
    public string BaseUrl => "https://91pinse.com";
    public string UserAgent => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0.0.0 Safari/537.36";

    public IReadOnlyList<(string Label, string Url)> Categories { get; } =
    [
        ("当前热门", "https://91pinse.com/rank/current-hot"),
        ("本周热门", "https://91pinse.com/rank/weekly-hot"),
        ("月度趋势", "https://91pinse.com/rank/month-hot"),
        ("精选视频", "https://91pinse.com/rank/recently-featured"),
        ("大家都在看", "https://91pinse.com/rank/popular-now"),
        ("最新视频", "https://91pinse.com/v/"),
        ("最热视频", "https://91pinse.com/v/hot/"),
    ];

    public IReadOnlyList<(string Label, string Value)> DurationFilters { get; } =
        [("全部",""), ("<5分钟","short"), ("5-20分钟","medium"), ("≥20分钟","long")];

    public string BuildSearchUrl(string keyword) =>
        $"https://91pinse.com/v/search?keyword={Uri.EscapeDataString(keyword)}";

    public string BuildPageUrl(string baseUrl, int page)
        => (baseUrl.TrimEnd('/').Contains('?') ? $"{baseUrl.TrimEnd('/')}&page={page}" : $"{baseUrl.TrimEnd('/')}?page={page}");

    public List<VideoItem> ParseVideoList(string html)
    {
        var items = new List<VideoItem>(); var seen = new HashSet<string>();
        var regex = new Regex(@"<div\s+class=""group"">\s*<a\s+href=""([^""]+)"".*?<img[^>]*alt=""([^""]*)""", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in regex.Matches(html))
        {
            string href = m.Groups[1].Value.Trim(), title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(title) || !seen.Add(href)) continue;
            string d="", v="";
            var dm = Regex.Match(m.Value, @"<span[^>]*>\s*(\d+:\d+:\d+|\d+:\d+)\s*</span>");
            if (dm.Success) d=dm.Groups[1].Value.Trim();
            var vm = Regex.Match(m.Value, @"icon-play[^<]*</i>\s*(\d[\d,]*)");
            if (vm.Success) v=vm.Groups[1].Value.Trim();
            items.Add(new VideoItem{Title=title, Url=$"{BaseUrl}{href}", Duration=d, Views=v});
        }
        return items;
    }

    public async Task<(string VideoUrl, string RefererUrl, string Title)?> ResolveVideoAsync(string pageUrl, HttpClient http)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        string html = await resp.Content.ReadAsStringAsync();
        string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? pageUrl;
        return await new GenericVideoResolver().ExtractVideoUrl(html, finalUrl, http, UserAgent);
    }

    public Dictionary<string, string> GetDownloadHeaders(string refererUrl)
    {
        var h = new Dictionary<string, string> { ["User-Agent"] = UserAgent, ["Referer"] = refererUrl };
        try { var u = new Uri(refererUrl); h["Origin"] = $"{u.Scheme}://{u.Host}"; } catch { }
        return h;
    }
}
