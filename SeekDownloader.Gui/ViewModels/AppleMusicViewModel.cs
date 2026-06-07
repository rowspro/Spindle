using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using FuzzySharp;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>
/// Overview from the local Music.app: most-played artists and playlists, with a marker for what
/// isn't in your download library yet. Click an artist to open artist mode, or a playlist to queue it.
/// </summary>
public class AppleMusicViewModel : ViewModelBase
{
    private readonly Action<string> _onArtist;
    private readonly Action<List<string>> _onPlaylistTracks;

    public ObservableCollection<ArtistStatViewModel> Artists { get; } = new();
    public ObservableCollection<PlaylistStatViewModel> Playlists { get; } = new();

    public AppleMusicViewModel(Action<string> onArtist, Action<List<string>> onPlaylistTracks)
    {
        _onArtist = onArtist;
        _onPlaylistTracks = onPlaylistTracks;
        RefreshCommand = new RelayCommand(Refresh, () => !IsLoading);
    }

    private string _libraryFolder = string.Empty;
    public string LibraryFolder { get => _libraryFolder; set => SetField(ref _libraryFolder, value); }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetField(ref _isLoading, value)) RefreshCommand.RaiseCanExecuteChanged(); }
    }

    private string _status = "Stel je bibliotheek-map in en klik Vernieuwen om je Apple Music-overzicht te laden.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand RefreshCommand { get; }

    private void Refresh()
    {
        if (IsLoading) return;
        IsLoading = true;
        Status = "Apple Music uitlezen… (eerste keer vraagt macOS om toestemming voor Muziek)";
        var lib = LibraryFolder;

        Task.Run(() =>
        {
            try
            {
                var artists = AppleMusicService.GetTopArtists(50);
                var playlists = AppleMusicService.GetPlaylists();
                var index = BuildLibraryIndex(lib);

                // Compute "in library" on the background thread (fuzzy matching can be heavy).
                var artistRows = artists
                    .Select(a => (a.Name, a.Plays, In: InLibrary(index, a.Name)))
                    .ToList();

                Dispatcher.UIThread.Post(() =>
                {
                    Artists.Clear();
                    foreach (var r in artistRows)
                        Artists.Add(new ArtistStatViewModel(r.Name, r.Plays, r.In, _onArtist));
                    Playlists.Clear();
                    foreach (var p in playlists)
                        Playlists.Add(new PlaylistStatViewModel(p.Name, p.TrackCount, PickPlaylist));

                    IsLoading = false;
                    int missing = Artists.Count(a => !a.InLibrary);
                    Status = index.Count == 0
                        ? $"{Artists.Count} artiesten · {Playlists.Count} playlists — let op: geen bibliotheek-map gevonden, dus alles toont als 'niet gedownload'."
                        : $"{Artists.Count} artiesten · {Playlists.Count} playlists · {missing} nog niet gedownload.";
                });
            }
            catch (Exception e)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsLoading = false;
                    Status = "Kon Apple Music niet lezen: " + e.Message;
                });
            }
        });
    }

    private void PickPlaylist(string name)
    {
        Status = $"Playlist '{name}' ophalen…";
        Task.Run(() =>
        {
            try
            {
                var lines = AppleMusicService.GetPlaylistTracks(name);
                Dispatcher.UIThread.Post(() =>
                {
                    Status = $"Playlist '{name}': {lines.Count} nummers naar de wachtrij.";
                    _onPlaylistTracks(lines);
                });
            }
            catch (Exception e)
            {
                Dispatcher.UIThread.Post(() => Status = "Playlist ophalen mislukt: " + e.Message);
            }
        });
    }

    // ---- Library matching ----
    private sealed class LibIndex
    {
        public readonly HashSet<string> Norm = new();   // normalized (alphanumeric) folder names
        public readonly List<string> Names = new();     // readable lowercased folder names (for fuzzy)
        public int Count => Norm.Count;
    }

    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };

    // Build the "what do I have" set from BOTH folder names (recursief) AND the actual file tags
    // (artist + album-artist). Tags are the reliable signal — folder names often differ from artists.
    private static LibIndex BuildLibraryIndex(string folder)
    {
        var idx = new LibIndex();
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return idx;

            foreach (var d in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(d);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".")) continue;
                Add(idx, name);
            }

            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"));
            var names = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
            {
                try
                {
                    var t = new ATL.Track(f);
                    if (!string.IsNullOrWhiteSpace(t.AlbumArtist)) names.Add(t.AlbumArtist);
                    if (!string.IsNullOrWhiteSpace(t.Artist)) names.Add(t.Artist);
                }
                catch { }
            });
            foreach (var n in names) Add(idx, n);
        }
        catch { }
        return idx;
    }

    private static void Add(LibIndex idx, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var n = Norm(name);
        if (n.Length == 0) return;
        if (idx.Norm.Add(n)) idx.Names.Add(name.ToLowerInvariant());
    }

    private static bool InLibrary(LibIndex idx, string artist)
    {
        var n = Norm(artist);
        if (n.Length < 2 || idx.Count == 0) return false;

        if (idx.Norm.Contains(n)) return true;

        // a folder that contains the artist (e.g. "kendricklamargoodkidmaadcity"), or vice versa
        foreach (var d in idx.Norm)
            if (d.Length >= 4 && (d.Contains(n) || (n.Length >= 5 && n.Contains(d))))
                return true;

        // fuzzy fallback on readable names (handles small spelling/format differences)
        var al = artist.ToLowerInvariant();
        foreach (var name in idx.Names)
            if (Fuzz.TokenSetRatio(al, name) >= 90)
                return true;

        return false;
    }

    private static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
}
