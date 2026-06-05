using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DeepGrab.Models;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace DeepGrab.Sites;

public class PinseSite : IDeepGrabSite
{
    const string AcceptHdr = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    const string LangHdr = "zh-CN,zh;q=0.9,en;q=0.8";

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
        [("全部",""), ("<5分钟","&dur=short"), ("5-20分钟","&dur=medium"), ("≥20分钟","&dur=long")];

    public string BuildSearchUrl(string keyword) =>
        $"https://91pinse.com/v/search?keyword={Uri.EscapeDataString(keyword)}";

    public string BuildPageUrl(string baseUrl, int page)
        => (baseUrl.TrimEnd('/').Contains('?') ? $"{baseUrl.TrimEnd('/')}&page={page}" : $"{baseUrl.TrimEnd('/')}?page={page}");

    // ==================== 探索：解析视频列表 ====================

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
            var vm = Regex.Match(m.Value, @"icon-play[^<]*</i>\s*(\d[\d,]*)" );
            if (vm.Success) v=vm.Groups[1].Value.Trim();
            items.Add(new VideoItem{Title=title, Url=$"{BaseUrl}{href}", Duration=d, Views=v});
        }
        return items;
    }

    // ==================== 解析：提取视频直链 + 下载头 ====================

    public async Task<(string VideoUrl, string RefererUrl, string Title)?> ResolveVideoAsync(string pageUrl, HttpClient http)
    {
        // 第1步：获取主页面（带完整 headers）
        using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", AcceptHdr);
        req.Headers.TryAddWithoutValidation("Accept-Language", LangHdr);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? pageUrl;
        string html = await resp.Content.ReadAsStringAsync();

        // 第2步：从 HTML 提取标题 + 媒体URL
        string title = ExtractPageTitle(html);

        var mediaUrls = ExtractMediaUrls(html, finalUrl);
        mediaUrls.AddRange(ExtractBase64MediaUrls(html, finalUrl));
        string? best = PickBestMediaUrl(mediaUrls);
        if (best != null) return (best, finalUrl, title);

        // 第3步：从 iframe 提取
        var iframeUrls = ExtractIframeUrls(html, finalUrl);
        if (iframeUrls.Count == 0)
            throw new Exception("找不到视频播放器(无iframe)");

        foreach (string iframeUrl in iframeUrls)
        {
            try
            {
                using var r2 = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                r2.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                r2.Headers.TryAddWithoutValidation("Accept", AcceptHdr);
                r2.Headers.TryAddWithoutValidation("Accept-Language", LangHdr);
                r2.Headers.Referrer = new Uri(finalUrl);
                using var resp2 = await http.SendAsync(r2);
                resp2.EnsureSuccessStatusCode();
                var urls = ExtractMediaUrls(await resp2.Content.ReadAsStringAsync(), iframeUrl);
                urls.AddRange(ExtractBase64MediaUrls(await resp2.Content.ReadAsStringAsync(), iframeUrl));
                best = PickBestMediaUrl(urls);
                if (best != null) return (best, iframeUrl, title);
            }
            catch { }
        }

        // yt-dlp 兜底
        string? ytdlp = await ExtractWithYtDlp(finalUrl, UserAgent, finalUrl);
        if (ytdlp != null) return (ytdlp, finalUrl, title);
        foreach (string iframeUrl in iframeUrls)
        {
            ytdlp = await ExtractWithYtDlp(iframeUrl, UserAgent, finalUrl);
            if (ytdlp != null) return (ytdlp, iframeUrl, title);
        }

        throw new Exception("找到播放器但无法提取视频直链");
    }

    public Dictionary<string, string> GetDownloadHeaders(string refererUrl)
    {
        var h = new Dictionary<string, string> { ["User-Agent"] = UserAgent, ["Referer"] = refererUrl };
        try { var u = new Uri(refererUrl); h["Origin"] = $"{u.Scheme}://{u.Host}"; } catch { }
        return h;
    }

    // ==================== 私有：页面标题提取 ====================

    static string ExtractPageTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            string t = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(t)) return SanitizeFileName(t);
        }
        return $"video_{Random.Shared.Next(10000,99999)}";
    }

    // ==================== 私有：iframe URL 提取 ====================

    static List<string> ExtractIframeUrls(string html, string baseUrl)
    {
        var u = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<iframe[^>]+src=[""']?([^""'>\s]+)", RegexOptions.IgnoreCase))
        { string s = m.Groups[1].Value.Trim(); if (!string.IsNullOrEmpty(s)) u.Add(UrlJoin(baseUrl, s)); }
        var m1 = Regex.Match(html, @"src=[""'](https?://fplayer\.cc/embed/[^""']+)", RegexOptions.IgnoreCase);
        if (m1.Success) u.Insert(0, m1.Groups[1].Value);
        var m2 = Regex.Match(html, @"src=(https?://fplayer\.cc/embed/[^\s>]+)", RegexOptions.IgnoreCase);
        if (m2.Success) u.Insert(0, m2.Groups[1].Value);
        return u.Distinct().ToList();
    }

    // ==================== 私有：媒体 URL 提取 ====================

    static List<string> ExtractMediaUrls(string text, string baseUrl)
    {
        var r = new List<string>();
        (string p, int g)[] ps = [
            (@"(https?:\\?/\\?/[^""'\s]+?\.(?:m3u8|mp4)(?:\?[^""'\s]*)?)", 1),
            (@"(//[^""'\s]+?\.(?:m3u8|mp4)(?:\?[^""'\s]*)?)", 1),
            (@"([""'])([^""']+?\.(?:m3u8|mp4)(?:\?[^""']*)?)\1", 2),
        ];
        foreach (var (p,g) in ps)
            foreach (Match m in Regex.Matches(text, p, RegexOptions.IgnoreCase))
            {
                string raw = m.Groups[g].Value;
                if (string.IsNullOrEmpty(raw)) continue;
                string url = raw.Replace("\\u002F","/").Replace("\\/","/").Replace("\\u0026","&").Trim();
                if (url.StartsWith("//")) url = UrlJoin(baseUrl, url);
                else if (url.StartsWith("/")) url = UrlJoin(baseUrl, url);
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)||url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) r.Add(url);
            }
        return r.Distinct().ToList();
    }

    static List<string> ExtractBase64MediaUrls(string text, string baseUrl)
    {
        var r = new List<string>();
        foreach (Match m in Regex.Matches(text, @"[""']([A-Za-z0-9+/\\=]{24,})[""']"))
        {
            string n = m.Groups[1].Value.Replace("\\u003D","=").Replace("\\/","/");
            try { r.AddRange(ExtractMediaUrls(Encoding.UTF8.GetString(Convert.FromBase64String(n)), baseUrl)); } catch { }
        }
        return r.Distinct().ToList();
    }

    static string? PickBestMediaUrl(List<string> urls)
    {
        if (urls.Count==0) return null;
        var c = urls.Where(u => !u.Contains('{') && !u.Contains('}') && !u.ToLower().Contains("ping.m3u8") && !u.ToLower().StartsWith("blob:")).ToList();
        if (c.Count==0) c=urls;
        return c.MaxBy(u => { string l=u.ToLower(); int s=0; if(l.Contains(".m3u8")) s+=300; else if(l.Contains(".mp4")) s+=200; if(l.Contains("master.m3u8")) s+=120; if(l.Contains("expires=")) s+=60; if(l.Contains("trailer")) s-=400; if(l.Contains("preview")||l.Contains("thumb")) s-=150; return s; });
    }

    // ==================== 私有：yt-dlp 兜底 ====================

    static async Task<string?> ExtractWithYtDlp(string url, string ua, string referer)
    {
        var o = new OptionSet { SkipDownload = true };
        o.AddCustomOption("--add-header", $"User-Agent: {ua}");
        o.AddCustomOption("--add-header", $"Referer: {referer}");
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string ytPath = Path.Combine(basePath, "yt-dlp.exe");
        if (!File.Exists(ytPath)) ytPath = "yt-dlp";
        var y = new YoutubeDL { YoutubeDLPath = ytPath };
        var res = await y.RunVideoDataFetch(url, overrideOptions: o);
        if (res?.Success==true && res.Data != null)
        {
            if (!string.IsNullOrEmpty(res.Data.Url)) return res.Data.Url;
            if (res.Data.Formats != null)
                foreach (var f in res.Data.Formats)
                    if (!string.IsNullOrEmpty(f.Url)) return f.Url;
        }
        return null;
    }

    // ==================== 工具 ====================

    static string UrlJoin(string b, string r) { try { return new Uri(new Uri(b), r).ToString(); } catch { return r; } }

    static string SanitizeFileName(string n)
    {
        var inv = Path.GetInvalidFileNameChars(); var sb=new StringBuilder(n.Length);
        foreach(char c in n) sb.Append(inv.Contains(c)?'_':c);
        var s=sb.ToString().Trim().Trim('.',' '); if(s.Length>120) s=s[..120];
        return string.IsNullOrWhiteSpace(s)?$"video_{DateTime.Now:HHmmss}":s;
    }
}
