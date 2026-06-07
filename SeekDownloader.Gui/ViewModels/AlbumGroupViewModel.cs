using SeekDownloader;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>
/// A row in artist mode: an official album (from MusicBrainz) matched against Soulseek results,
/// or a folder-derived album when MusicBrainz is unavailable.
/// </summary>
public class AlbumGroupViewModel : ViewModelBase
{
    public string Title { get; }
    public string Year { get; }
    public bool Found { get; }
    public int FoundTracks { get; }
    public int ExpectedTracks { get; }
    public string Username { get; }
    public IReadOnlyList<SearchResult> Tracks { get; }

    public AlbumGroupViewModel(string title, string year, bool found, int foundTracks, int expectedTracks,
        string username, IReadOnlyList<SearchResult> tracks)
    {
        Title = title;
        Year = year;
        Found = found;
        FoundTracks = foundTracks;
        ExpectedTracks = expectedTracks;
        Username = username;
        Tracks = tracks;
    }

    public string Name => string.IsNullOrWhiteSpace(Year) ? Title : $"{Title} ({Year})";
    public int TrackCount => Tracks.Count;
    public bool CanSelect => Found;

    public string Summary => !Found
        ? "niet gevonden op Soulseek"
        : ExpectedTracks > 0
            ? $"{FoundTracks}/{ExpectedTracks} tracks  ·  {Username}"
            : $"{FoundTracks} tracks  ·  {Username}";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Found) SetField(ref _isSelected, value); }
    }
}
