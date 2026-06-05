using CommunityToolkit.Mvvm.ComponentModel;

namespace DeepGrab.Models;

public enum DownloadStatus
{
    Queued, Parsing, Downloading, PostProcessing, Completed, Failed, Deleted
}

public partial class DownloadTask : ObservableObject
{
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private DownloadStatus _status = DownloadStatus.Queued;
    [ObservableProperty] private string _statusText = "等待中";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _speed = "";
    [ObservableProperty] private string _eta = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _filePath = "";

    public string RowBackground => Status switch
    {
        DownloadStatus.Completed => "#1A3320",
        DownloadStatus.Downloading => "#332800",
        DownloadStatus.Parsing => "#332800",
        DownloadStatus.PostProcessing => "#332800",
        DownloadStatus.Failed => "#331111",
        DownloadStatus.Deleted => "#2A2015",
        _ => "Transparent"
    };

    public bool IsDownloading => Status == DownloadStatus.Downloading;

    partial void OnStatusChanged(DownloadStatus value)
    {
        UpdateStatusDisplay();
        if (value is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Deleted)
        { Speed = ""; Eta = ""; }
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(RowBackground));
    }

    partial void OnErrorMessageChanged(string value)
    {
        if (Status == DownloadStatus.Failed) UpdateStatusDisplay();
    }

    void UpdateStatusDisplay()
    {
        string baseText = Status switch
        {
            DownloadStatus.Queued => "等待中",
            DownloadStatus.Parsing => "解析...",
            DownloadStatus.Downloading => "下载中",
            DownloadStatus.PostProcessing => "处理中",
            DownloadStatus.Completed => "已完成",
            DownloadStatus.Failed => "失败",
            DownloadStatus.Deleted => "已删除",
            _ => "未知"
        };
        StatusText = baseText;
    }
}
