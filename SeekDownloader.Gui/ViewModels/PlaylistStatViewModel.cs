namespace SeekDownloader.Gui.ViewModels;

/// <summary>An Apple Music playlist; click to queue downloads for its tracks.</summary>
public class PlaylistStatViewModel : ViewModelBase
{
    public string Name { get; }
    public int TrackCount { get; }
    public RelayCommand PickCommand { get; }

    public PlaylistStatViewModel(string name, int trackCount, Action<string> onPick)
    {
        Name = name;
        TrackCount = trackCount;
        PickCommand = new RelayCommand(() => onPick(name));
    }

    public string Summary => $"{TrackCount} nummers  ·  klik om naar wachtrij te sturen";
}
