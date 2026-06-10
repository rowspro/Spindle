using Avalonia.Media;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>A selectable album in the iPod-transfer tool.</summary>
public class AlbumEntryViewModel : ViewModelBase
{
    public string Artist { get; }
    public string Album { get; }
    public string Genre { get; }
    public string Year { get; }
    public IReadOnlyList<string> Files { get; }

    public AlbumEntryViewModel(string artist, string album, string genre, string year, IReadOnlyList<string> files)
    {
        Artist = artist;
        Album = album;
        Genre = genre;
        Year = year;
        Files = files;
    }

    public int TrackCount => Files.Count;
    public string Display => $"{Artist} — {Album}";
    public string Sub => $"{(string.IsNullOrWhiteSpace(Genre) ? "geen genre" : Genre)}  ·  {TrackCount} tracks{(string.IsNullOrWhiteSpace(Year) ? "" : "  ·  " + Year)}";

    private bool _selected;
    public bool Selected { get => _selected; set => SetField(ref _selected, value); }

    // How many of this album's tracks are already on the iPod. -1 = not checked (no iPod connected).
    private int _onIpod = -1;
    public int OnIpod
    {
        get => _onIpod;
        set
        {
            if (SetField(ref _onIpod, value))
            {
                OnPropertyChanged(nameof(IpodStatus));
                OnPropertyChanged(nameof(IpodBrush));
                OnPropertyChanged(nameof(IsComplete));
            }
        }
    }

    public bool IsComplete => _onIpod >= 0 && _onIpod >= TrackCount;
    public string IpodStatus =>
        _onIpod <= 0 ? "" : _onIpod >= TrackCount ? "✓ op iPod" : $"deels ({_onIpod}/{TrackCount})";
    public IBrush IpodBrush =>
        _onIpod >= TrackCount ? new SolidColorBrush(Color.Parse("#2E7D32")) : new SolidColorBrush(Color.Parse("#B26A00"));
}
