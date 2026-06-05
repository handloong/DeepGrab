using CommunityToolkit.Mvvm.ComponentModel;

namespace DeepGrab.Models;

public partial class VideoItem : ObservableObject
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Views { get; set; } = "";
    public int Number { get; set; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isAdded;
    public string TipText => IsAdded ? "已下载过，可重新下载" : "";
    public string RowBg => IsAdded ? "#1A3320" : "Transparent";
    partial void OnIsAddedChanged(bool value) { OnPropertyChanged(nameof(TipText)); OnPropertyChanged(nameof(RowBg)); }
}
