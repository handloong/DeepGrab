using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeepGrab.Models;

namespace DeepGrab.Sites;

public class PHSite : IDeepGrabSite
{
    public string Name => "Pornhub";
    public string BaseUrl => "https://cn.pornhub.com";
    public string UserAgent => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public bool UseFfmpegDownload => true;

    public IReadOnlyList<(string Label, string Url)> Categories { get; } =
    [
        ("推荐视频", "https://cn.pornhub.com/video"),
        ("最热门",   "https://cn.pornhub.com/video?o=ht"),
        ("最高分",   "https://cn.pornhub.com/video?o=tr"),
        ("最新",     "https://cn.pornhub.com/video?o=cm"),
    ];

    public IReadOnlyList<(string Label, string Value)> DurationFilters { get; } =
    [
        ("全部",        ""),
        ("<10分钟",     "&min_duration=0&max_duration=600"),
        ("10-20分钟",   "&min_duration=600&max_duration=1200"),
        ("20-30分钟",   "&min_duration=1200&max_duration=1800"),
        ("30-60分钟",   "&min_duration=1800&max_duration=3600"),
        (">60分钟",     "&min_duration=3600"),
    ];

    public string BuildSearchUrl(string keyword) =>
        $"{BaseUrl}/video/search?search={Uri.EscapeDataString(keyword)}";

    public string BuildPageUrl(string baseUrl, int page) => $"{baseUrl}&page={page}";

    // ==================== 探索：解析视频列表 ====================

    public List<VideoItem> ParseVideoList(string html)
    {
        var items = new List<VideoItem>();
        var seen = new HashSet<string>();

        foreach (Match m in Regex.Matches(html,
            @"<a[^>]*href=""(/view_video\.php\?viewkey=[^""]+)""[^>]*>.*?<img[^>]*alt=""([^""]*)""",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string href = m.Groups[1].Value.Trim();
            string title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (string.IsNullOrEmpty(title) || !seen.Add(href)) continue;

            string dur = "";
            var dm = Regex.Match(m.Value, @"<var[^>]*>(\d+:\d+)</var>");
            if (dm.Success) dur = dm.Groups[1].Value;

            items.Add(new VideoItem { Title = title, Url = $"{BaseUrl}{href}", Duration = dur });
        }
        return items;
    }

    // ==================== 解析：提取 VIDEO_SHOW → mediaDefinitions → m3u8/mp4 直链 ====================

    static void AddBrowserHeaders(HttpRequestMessage req, string? referer = null, bool isNavigation = true)
    {
        const string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        if (isNavigation)
        {
            req.Headers.TryAddWithoutValidation("Cache-Control", "max-age=0");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        }
        if (!string.IsNullOrEmpty(referer))
            req.Headers.TryAddWithoutValidation("Referer", referer);
    }

    public async Task<(string VideoUrl, string RefererUrl, string Title)?> ResolveVideoAsync(string pageUrl, HttpClient http)
    {
        // 0. 预热：先访问首页获取 Cloudflare clearance cookie（模拟浏览器行为）
        try
        {
            using var warmReq = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/");
            AddBrowserHeaders(warmReq);
            using var warmResp = await http.SendAsync(warmReq);
            // 忽略结果，只为拿 cookie
        }
        catch { }

        // 1. 请求视频页面（完整浏览器 headers）
        using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        AddBrowserHeaders(req, BaseUrl + "/");

        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? pageUrl;
        string html = await resp.Content.ReadAsStringAsync();

        // DEBUG: 保存 HTML 方便和 Python 对比
        try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ph_csharp.html"), html); } catch { }

        // 2. 检查页面是否包含 VIDEO_SHOW
        if (!html.Contains("VIDEO_SHOW"))
            throw new Exception($"页面未包含 VIDEO_SHOW（可能被 Cloudflare 或验证拦截）。\n最终URL: {finalUrl}\n页面大小: {html.Length} 字节\n前200字: {html[..Math.Min(200,html.Length)]}");

        // 3. 提取 flashvars 或 VIDEO_SHOW JSON（兼容新旧格式）
        var videoShow = ExtractVideoShow(html, out string debug);
        if (videoShow == null)
            throw new Exception($"提取视频数据失败。\n{debug}\n\n页面大小: {html.Length}B");
        string title = "ph_video";
        if (videoShow.TryGetPropertyValue("videoTitleOriginal", out var t1) && t1 != null)
            title = t1.ToString();
        else if (videoShow.TryGetPropertyValue("videoTitle", out var t2) && t2 != null)
            title = t2.ToString();
        title = SanitizeFileName(title);

        // 4. 检查 mediaDefinitions
        if (!videoShow.TryGetPropertyValue("mediaDefinitions", out var defs) || defs is not JsonArray arr || arr.Count == 0)
        {
            // 输出完整的 JSON 以便调试
            string fullJson = videoShow.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            throw new Exception($"mediaDefinitions 为空或不存在。\nVIDEO_SHOW 包含的字段: {string.Join(", ", ((IDictionary<string, JsonNode?>)videoShow).Keys)}\n\n提取到的JSON前800字:\n{fullJson[..Math.Min(800, fullJson.Length)]}");
        }

        // 5. 提取视频链接
        var links = await ExtractVideoLinks(videoShow, http);

        if (links.Count == 0)
        {
            string samples = string.Join("\n", arr.Take(5).Select((item, i) =>
            {
                if (item is JsonObject obj)
                {
                    var keys = string.Join(", ", ((IDictionary<string, JsonNode?>)obj).Keys);
                    var vUrl = obj.TryGetPropertyValue("videoUrl", out var vu) && vu != null ? vu.ToString()[..Math.Min(80, vu.ToString().Length)] : "null";
                    return $"  [{i}] videoUrl={vUrl}  字段: {keys}";
                }
                return $"  [{i}] 非JSON对象";
            }));
            throw new Exception($"解析到 {arr.Count} 条 mediaDefinitions 但提取出 0 个链接:\n{samples}");
        }

        // 6. 选最佳链接：优先 master.m3u8 → 解析最高画质 → mp4 兜底
        string? bestUrl = await ChooseBestLink(links, http);

        if (bestUrl == null)
            throw new Exception($"找到 {links.Count} 个链接但无法解析出可用流:\n{string.Join("\n", links.Take(10).Select((l,i) => $"  [{i}] {l[..Math.Min(100,l.Length)]}"))}");

        return (bestUrl, pageUrl, title);
    }

    public Dictionary<string, string> GetDownloadHeaders(string refererUrl)
    {
        var h = new Dictionary<string, string>
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = $"{BaseUrl}/",
        };
        try
        {
            var u = new Uri(refererUrl);
            h["Origin"] = $"{u.Scheme}://{u.Host}";
        }
        catch { }
        return h;
    }

    // ==================== 私有方法 ====================

    /// <summary>从 HTML 提取 VIDEO_SHOW 或 flashvars（PH 新旧格式兼容）</summary>
    static JsonObject? ExtractVideoShow(string html, out string debug)
    {
        debug = "";

        // 方法1: 新版 flashvars - 包含 mediaDefinitions
        // flashvars_451106921 = {"mediaDefinitions":[...], ...};
        int fvIdx = html.IndexOf("var flashvars_");
        if (fvIdx >= 0)
        {
            var fvObj = ExtractJsonAt(html, fvIdx, out string dbg);
            debug = "flashvars: " + dbg;
            if (fvObj != null && fvObj.ContainsKey("mediaDefinitions"))
            {
                debug += ", 包含mediaDefinitions";
                return fvObj;
            }
        }

        // 方法2: 旧版 VIDEO_SHOW（可能仍有 mediaDefinitions 的旧页面）
        int vsIdx = html.IndexOf("var VIDEO_SHOW");
        if (vsIdx >= 0)
        {
            var vsObj = ExtractJsonAt(html, vsIdx, out string dbg);
            debug = "VIDEO_SHOW: " + dbg;
            if (vsObj != null && vsObj.ContainsKey("mediaDefinitions"))
            {
                debug += ", 包含mediaDefinitions";
                return vsObj;
            }
        }

        return null;
    }

    /// <summary>从指定位置提取 next JSON {...} 对象（括号计数）</summary>
    static JsonObject? ExtractJsonAt(string html, int startIdx, out string debug)
    {
        debug = "";
        int eq = html.IndexOf('=', startIdx);
        if (eq < 0) { debug = "未找到 ="; return null; }

        int braceStart = html.IndexOf('{', eq);
        if (braceStart < 0) { debug = "未找到 {"; return null; }

        int depth = 0;
        bool inString = false;
        bool escapeNext = false;
        int braceEnd = -1;

        for (int i = braceStart; i < html.Length; i++)
        {
            char c = html[i];
            if (escapeNext) { escapeNext = false; continue; }
            if (c == '\\') { escapeNext = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { braceEnd = i; break; }
            }
        }

        if (braceEnd < 0) { debug = $"depth={depth}"; return null; }

        string json = html[braceStart..(braceEnd + 1)];
        debug = $"长度={json.Length}";

        try
        {
            var node = JsonNode.Parse(json);
            return node as JsonObject;
        }
        catch (Exception ex)
        {
            debug += $", 解析失败: {ex.Message}";
            return null;
        }
    }

    /// <summary>从 VIDEO_SHOW.mediaDefinitions 提取视频链接</summary>
    static async Task<List<string>> ExtractVideoLinks(JsonObject videoShow, HttpClient http)
    {
        var links = new List<string>();

        if (!videoShow.TryGetPropertyValue("mediaDefinitions", out var defs) || defs is not JsonArray arr)
            return links;

        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;

            // 读取 videoUrl 字段
            if (!obj.TryGetPropertyValue("videoUrl", out var videoUrlNode) || videoUrlNode == null)
                continue;
            string videoUrl = videoUrlNode.ToString();

            // 如果是 .json 结尾 → 需要二次请求获取实际视频链接
            if (videoUrl.EndsWith(".json"))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                    AddBrowserHeaders(req, isNavigation: false);
                    using var r = await http.SendAsync(req);
                    r.EnsureSuccessStatusCode();
                    string jsonText = await r.Content.ReadAsStringAsync();
                    var data = JsonNode.Parse(jsonText);
                    if (data is JsonArray jsonArr)
                    {
                        foreach (var x in jsonArr)
                        {
                            if (x is JsonObject xo && xo.TryGetPropertyValue("videoUrl", out var u) && u != null)
                                links.Add(u.ToString());
                        }
                    }
                }
                catch { /* 忽略单条链接解析失败 */ }
            }
            // 直接 http 链接
            else if (videoUrl.StartsWith("http"))
            {
                links.Add(videoUrl);
            }
        }

        return links.Distinct().ToList();
    }

    /// <summary>从 master.m3u8 解析出最高码率的子流 URL</summary>
    static async Task<string?> ResolveMasterM3u8(string masterUrl, HttpClient http)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, masterUrl);
            AddBrowserHeaders(req, isNavigation: false);
            using var r = await http.SendAsync(req);
            r.EnsureSuccessStatusCode();
            string text = await r.Content.ReadAsStringAsync();
            var lines = text.Split('\n');

            var streams = new List<(int Bandwidth, string Uri)>();
            for (int i = 0; i < lines.Length - 1; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("#EXT-X-STREAM-INF")) continue;

                int bw = 0;
                var m = Regex.Match(line, @"BANDWIDTH=(\d+)");
                if (m.Success) bw = int.Parse(m.Groups[1].Value);

                string uri = lines[i + 1].Trim();
                if (!string.IsNullOrEmpty(uri) && !uri.StartsWith("#"))
                {
                    if (!uri.StartsWith("http"))
                    {
                        string baseUri = masterUrl[..masterUrl.LastIndexOf('/')];
                        uri = $"{baseUri}/{uri}";
                    }
                    streams.Add((bw, uri));
                }
            }

            if (streams.Count > 0)
            {
                streams.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
                return streams[0].Uri;
            }
        }
        catch { }
        return null;
    }

    /// <summary>从链接列表中选择最佳下载链接</summary>
    static async Task<string?> ChooseBestLink(List<string> links, HttpClient http)
    {
        string[] priorities = ["1080", "720", "480", "360"];

        var hlsMasters = links.Where(x => x.Contains("master.m3u8")).ToList();

        // 优先按画质匹配 master.m3u8
        foreach (var q in priorities)
        {
            foreach (var link in hlsMasters.Where(l => l.Contains(q)))
            {
                var resolved = await ResolveMasterM3u8(link, http);
                if (resolved != null) return resolved;
            }
        }

        // 任意 master.m3u8 兜底
        foreach (var link in hlsMasters)
        {
            var resolved = await ResolveMasterM3u8(link, http);
            if (resolved != null) return resolved;
        }

        // mp4 直接链接兜底
        var mp4Links = links.Where(x => x.Contains(".mp4") && !x.Contains(".m3u8")).ToList();
        foreach (var q in priorities)
        {
            var match = mp4Links.FirstOrDefault(l => l.Contains(q));
            if (match != null) return match;
        }

        return mp4Links.FirstOrDefault();
    }

    static string SanitizeFileName(string n)
    {
        var inv = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(n.Length);
        foreach (char c in n) sb.Append(inv.Contains(c) || c == '"' || c == '\'' ? '_' : c);
        var s = sb.ToString().Trim().Trim('.', ' ');
        if (s.Length > 120) s = s[..120];
        return string.IsNullOrWhiteSpace(s) ? $"ph_{DateTime.Now:HHmmss}" : s;
    }
}
