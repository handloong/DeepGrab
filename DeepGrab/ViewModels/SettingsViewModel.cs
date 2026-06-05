using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepGrab.Services;

namespace DeepGrab.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    readonly DatabaseService _db;
    [ObservableProperty] private string _downloadPath = "";
    [ObservableProperty] private string _dateFormat = "yyyyMMdd";
    [ObservableProperty] private int _maxConcurrent = 3;
    [ObservableProperty] private string _statusMessage = "";

    public int DateFormatIndex { get=> DateFormat switch { ""=>0, "yyyyMMdd"=>1, "yyyy-MM-dd"=>2, "yyyy-MM"=>3, _=>0 };
        set=> DateFormat = value switch {0=>"",1=>"yyyyMMdd",2=>"yyyy-MM-dd",3=>"yyyy-MM",_=>""}; }

    public SettingsViewModel(DatabaseService db)
    {
        _db = db;
        string bp = AppDomain.CurrentDomain.BaseDirectory;
        DownloadPath=_db.GetSetting("download_path", Path.Combine(bp,"videos"));
        DateFormat=_db.GetSetting("date_format","yyyyMMdd");
        if(int.TryParse(_db.GetSetting("max_concurrent","3"),out int m)) MaxConcurrent=m;
    }

    [RelayCommand] void Save() {
        _db.SetSetting("download_path",DownloadPath);
        _db.SetSetting("date_format",DateFormat);
        _db.SetSetting("max_concurrent",MaxConcurrent.ToString());
        StatusMessage="设置已保存"; SettingsChanged?.Invoke();
    }

    public event Action? SettingsChanged;
}
