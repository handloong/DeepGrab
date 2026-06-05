using System.Text;
using System.Text.RegularExpressions;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace DeepGrab.Services;

/// <summary>通用视频直链解析器 - 严格按照 downloader.py 逻辑</summary>
public class GenericVideoResolver
{
    const string AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    const string LangHeader = "zh-CN,zh;q=0.9,en;q=0.8";

    public async Task<(string VideoUrl, string RefererUrl, string Title)?> ExtractVideoUrl(
        string html, string pageUrl, HttpClient http, string userAgent)
    {
        string title = ExtractPageTitle(html);

        // 第2步：直接从页面提取媒体链接
        var mediaUrls = ExtractMediaUrls(html, pageUrl);
        mediaUrls.AddRange(ExtractBase64MediaUrls(html, pageUrl));
        string? best = PickBestMediaUrl(mediaUrls);
        if (best != null) return (best, pageUrl, title);

        // 提取 iframe
        var iframeUrls = ExtractIframeUrls(html, pageUrl);
        if (iframeUrls.Count == 0)
            throw new Exception("找不到视频播放器(无iframe)");

        // 第3步：从 iframe 中提取（带上完整 headers）
        string lastYtError = "";
        foreach (string iframeUrl in iframeUrls)
        {
            try
            {
                using var r = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                r.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                r.Headers.TryAddWithoutValidation("Accept", AcceptHeader);
                r.Headers.TryAddWithoutValidation("Accept-Language", LangHeader);
                r.Headers.Referrer = new Uri(pageUrl);
                using var resp = await http.SendAsync(r);
                resp.EnsureSuccessStatusCode();
                string iframeHtml = await resp.Content.ReadAsStringAsync();
                var urls = ExtractMediaUrls(iframeHtml, iframeUrl);
                urls.AddRange(ExtractBase64MediaUrls(iframeHtml, iframeUrl));
                best = PickBestMediaUrl(urls);
                if (best != null) return (best, iframeUrl, title);
            }
            catch (Exception ex) { lastYtError = ex.Message; }
        }

        // yt-dlp 兜底
        string? ytdlpUrl = await ExtractWithYtDlp(pageUrl, userAgent, pageUrl);
        if (ytdlpUrl != null) return (ytdlpUrl, pageUrl, title);

        foreach (string iframeUrl in iframeUrls)
        {
            ytdlpUrl = await ExtractWithYtDlp(iframeUrl, userAgent, pageUrl);
            if (ytdlpUrl != null) return (ytdlpUrl, iframeUrl, title);
        }

        throw new Exception($"找到播放器但无法提取链接。{lastYtError}".Trim());
    }

    static string ExtractPageTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            string t = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(t)) return Sanitize(t);
        }
        return $"video_{Random.Shared.Next(10000,99999)}";
    }

    static List<string> ExtractIframeUrls(string html, string baseUrl)
    {
        var u = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<iframe[^>]+src=[""']?([^""'>\s]+)", RegexOptions.IgnoreCase))
        { string s = m.Groups[1].Value.Trim(); if (!string.IsNullOrEmpty(s)) u.Add(J(baseUrl, s)); }
        var m1 = Regex.Match(html, @"src=[""'](https?://fplayer\.cc/embed/[^""']+)", RegexOptions.IgnoreCase);
        if (m1.Success) u.Insert(0, m1.Groups[1].Value);
        var m2 = Regex.Match(html, @"src=(https?://fplayer\.cc/embed/[^\s>]+)", RegexOptions.IgnoreCase);
        if (m2.Success) u.Insert(0, m2.Groups[1].Value);
        return u.Distinct().ToList();
    }

    static List<string> ExtractMediaUrls(string text, string baseUrl)
    {
        var r = new List<string>();
        // 严格按照 Python 的正则
        var patterns = new (string pattern, int group)[]
        {
            (@"(https?:\\?/\\?/[^""'\s]+?\.(?:m3u8|mp4)(?:\?[^""'\s]*)?)", 1),
            (@"(//[^""'\s]+?\.(?:m3u8|mp4)(?:\?[^""'\s]*)?)", 1),
            (@"([""'])([^""']+?\.(?:m3u8|mp4)(?:\?[^""']*)?)\1", 2),
        };

        foreach (var (pattern, groupIndex) in patterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                string raw = m.Groups[groupIndex].Value;
                if (string.IsNullOrEmpty(raw)) continue;

                string url = raw.Replace("\\u002F", "/").Replace("\\/", "/").Replace("\\u0026", "&").Trim();
                if (url.StartsWith("//")) url = J(baseUrl, url);
                else if (url.StartsWith("/")) url = J(baseUrl, url);
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    r.Add(url);
            }
        }
        return r.Distinct().ToList();
    }

    static List<string> ExtractBase64MediaUrls(string text, string baseUrl)
    {
        var r = new List<string>();
        foreach (Match m in Regex.Matches(text, @"[""']([A-Za-z0-9+/\\=]{24,})[""']"))
        {
            string n = m.Groups[1].Value.Replace("\\u003D", "=").Replace("\\/", "/");
            try { r.AddRange(ExtractMediaUrls(Encoding.UTF8.GetString(Convert.FromBase64String(n)), baseUrl)); } catch { }
        }
        return r.Distinct().ToList();
    }

    static string? PickBestMediaUrl(List<string> urls)
    {
        if (urls.Count == 0) return null;
        var candidates = urls.Where(u => !IsPlaceholder(u)).ToList();
        if (candidates.Count == 0) candidates = urls;
        return candidates.MaxBy(url =>
        {
            string l = url.ToLower();
            int s = 0;
            if (l.Contains(".m3u8")) s += 300;
            else if (l.Contains(".mp4")) s += 200;
            if (l.Contains("master.m3u8")) s += 120;
            if (l.Contains("expires=")) s += 60;
            if (l.Contains("trailer")) s -= 400;
            if (l.Contains("preview") || l.Contains("thumb")) s -= 150;
            return s;
        });
    }

    static bool IsPlaceholder(string url) =>
        url.Contains('{') || url.Contains('}') ||
        url.ToLower().Contains("ping.m3u8") ||
        url.ToLower().StartsWith("blob:");

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
        if (res?.Success == true && res.Data != null)
        {
            if (!string.IsNullOrEmpty(res.Data.Url)) return res.Data.Url;
            if (res.Data.Formats != null)
                foreach (var f in res.Data.Formats)
                    if (!string.IsNullOrEmpty(f.Url)) return f.Url;
        }
        return null;
    }

    static string J(string b, string r) { try { return new Uri(new Uri(b), r).ToString(); } catch { return r; } }
    static string Sanitize(string n)
    {
        var inv = Path.GetInvalidFileNameChars(); var sb = new StringBuilder(n.Length);
        foreach (char c in n) sb.Append(inv.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().Trim('.', ' '); if (s.Length > 120) s = s[..120];
        return string.IsNullOrWhiteSpace(s) ? $"video_{DateTime.Now:HHmmss}" : s;
    }
}
