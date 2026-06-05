namespace DeepGrab.Models;

public class DownloadRecord
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime DownloadedAt { get; set; }
    public bool FileExists => File.Exists(FilePath);
}
