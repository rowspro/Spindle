using Avalonia.Media;
using SeekDownloader;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One entry in the download queue (manual selections and auto-discovered downloads).</summary>
public class QueueItemViewModel : ViewModelBase
{
    public string Username { get; }
    public string RemoteFilename { get; }
    public string BaseFileName { get; }   // last path segment, used to match completion/failure
    public long Size { get; }

    public string Key => $"{Username}|{RemoteFilename}";

    private readonly Action<QueueItemViewModel>? _onRemove;
    public RelayCommand RemoveCommand { get; }

    public QueueItemViewModel(SearchResult source, Action<QueueItemViewModel>? onRemove = null)
        : this(source.Username, source.Filename, source.Size, onRemove) { }

    public QueueItemViewModel(string username, string remoteFilename, long size, Action<QueueItemViewModel>? onRemove = null)
    {
        Username = username ?? string.Empty;
        RemoteFilename = remoteFilename ?? string.Empty;
        Size = size;
        BaseFileName = RemoteFilename.Split('\\', '/').Last();
        _status = "Wachtrij";
        SizeText = Format.Size(size);
        _onRemove = onRemove;
        RemoveCommand = new RelayCommand(() => _onRemove?.Invoke(this));
    }

    public string FileName => BaseFileName;
    public string SizeText { get; }

    private int _progress;
    public int Progress { get => _progress; set => SetField(ref _progress, value); }

    private string _speed = string.Empty;
    public string Speed { get => _speed; set => SetField(ref _speed, value); }

    private string _status;
    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
                OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public bool IsTerminal => Status is "Klaar" or "Mislukt";

    public IBrush StatusBrush => Status switch
    {
        "Klaar" => new SolidColorBrush(Color.Parse("#2E7D43")),
        "Mislukt" => new SolidColorBrush(Color.Parse("#B23A2E")),
        "Bezig" => new SolidColorBrush(Color.Parse("#3568C4")),
        _ => new SolidColorBrush(Color.Parse("#8A8370")),
    };

    public void UpdateProgress(DownloadProgress p)
    {
        Progress = p.Progress;
        Speed = $"{(int)(p.AverageDownloadSpeed / 1000)} KB/s";
        if (!IsTerminal) Status = "Bezig";
    }

    public void MarkDone()
    {
        Progress = 100;
        Speed = string.Empty;
        Status = "Klaar";
    }

    public void MarkFailed()
    {
        Status = "Mislukt";
        Speed = string.Empty;
    }
}
