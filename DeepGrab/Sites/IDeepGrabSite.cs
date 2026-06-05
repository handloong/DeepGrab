using DeepGrab.Models;

namespace DeepGrab.Sites;

public interface IDeepGrabSite
{
    string Name { get; }
    string BaseUrl { get; }
    string UserAgent { get; }
    IReadOnlyList<(string Label, string Url)> Categories { get; }
    string BuildSearchUrl(string keyword);
    string BuildPageUrl(string baseUrl, int page);
    List<VideoItem> ParseVideoList(string html);
    Task<(string VideoUrl, string RefererUrl, string Title)?> ResolveVideoAsync(string pageUrl, HttpClient http);
    Dictionary<string, string> GetDownloadHeaders(string refererUrl);
    IReadOnlyList<(string Label, string Value)> DurationFilters { get; }
}
