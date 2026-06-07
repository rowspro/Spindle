using SeekDownloader;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One row in the active-downloads list, kept in sync with a DownloadProgress from the core.</summary>
public class DownloadRowViewModel : ViewModelBase
{
    private string _fileName = string.Empty;
    private string _username = string.Empty;
    private int _progress;
    private string _status = string.Empty;
    private string _speed = string.Empty;
    private int _threadIndex;

    public int ThreadIndex { get => _threadIndex; set => SetField(ref _threadIndex, value); }
    public string FileName { get => _fileName; set => SetField(ref _fileName, value); }
    public string Username { get => _username; set => SetField(ref _username, value); }
    public int Progress { get => _progress; set => SetField(ref _progress, value); }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public string Speed { get => _speed; set => SetField(ref _speed, value); }

    public void Update(DownloadProgress p)
    {
        ThreadIndex = p.ThreadIndex;
        Username = p.Username ?? string.Empty;
        Progress = p.Progress;
        Status = p.ThreadStatus ?? string.Empty;
        Speed = $"{(int)(p.AverageDownloadSpeed / 1000)} KB/s";

        var name = string.IsNullOrWhiteSpace(p.Filename)
            ? string.Empty
            : p.Filename.Split('\\', '/').Last();
        FileName = name;
    }
}
