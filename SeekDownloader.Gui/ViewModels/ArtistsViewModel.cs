using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Media;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>A studio album of a watched artist, with whether you already own it.</summary>
public class DiscAlbumViewModel : ViewModelBase
{
    public string Artist { get; }
    public string Album { get; }
    public string Year { get; }
    public bool Have { get; }

    public DiscAlbumViewModel(string artist, string album, string year, bool have)
    {
        Artist = artist; Album = album; Year = year; Have = have;
        _selected = !have; // pre-select what you're missing
    }

    private bool _selected;
    public bool Selected { get => _selected; set => SetField(ref _selected, value); }

    public string Display => string.IsNullOrEmpty(Year) ? Album : $"{Album} ({Year})";
    public string Mark => Have ? "✓ in bieb" : "ontbreekt";
    public IBrush MarkBrush => new SolidColorBrush(Color.Parse(Have ? "#2E7D32" : "#B26A00"));
    public string Query => $"{Artist} - {Album}";
}

/// <summary>One watched artist with its discography completeness.</summary>
public class ArtistDiscViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<DiscAlbumViewModel> Albums { get; } = new();

    public ArtistDiscViewModel(string name, IEnumerable<DiscAlbumViewModel> albums)
    {
        Name = name;
        foreach (var a in albums) Albums.Add(a);
    }

    public int Total => Albums.Count;
    public int Owned => Albums.Count(a => a.Have);
    public string Summary => $"{Owned}/{Total} albums in bezit";
}

/// <summary>
/// "Artiesten": follow artists and see their full studio discography from MusicBrainz against your
/// library — which albums you're missing — and queue the missing ones for download. The watchlist
/// persists; you can seed it from your most-played Apple Music artists.
/// </summary>
public class ArtistsViewModel : ViewModelBase
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };

    private readonly Action<List<string>> _onDownload;
    private readonly Func<List<string>> _topArtists;

    public ObservableCollection<string> Watchlist { get; } = new();
    public ObservableCollection<ArtistDiscViewModel> Results { get; } = new();

    private CancellationTokenSource? _cts;

    public ArtistsViewModel(Action<List<string>> onDownload, Func<List<string>> topArtists)
    {
        _onDownload = onDownload;
        _topArtists = topArtists;
        AddCommand = new RelayCommand(AddArtist, () => !string.IsNullOrWhiteSpace(ArtistInput));
        RemoveCommand = new RelayCommand(() => { if (SelectedArtist != null) { Watchlist.Remove(SelectedArtist); Persist(); } });
        CheckCommand = new RelayCommand(Check, () => !IsBusy && Watchlist.Count > 0);
        ImportTopCommand = new RelayCommand(ImportTop, () => !IsBusy);
        DownloadMissingCommand = new RelayCommand(DownloadMissing, () => !IsBusy);

        foreach (var a in Settings.Load().Watchlist ?? new List<string>()) Watchlist.Add(a);
    }

    private string _artistInput = string.Empty;
    public string ArtistInput { get => _artistInput; set { if (SetField(ref _artistInput, value)) AddCommand.RaiseCanExecuteChanged(); } }

    private string? _selectedArtist;
    public string? SelectedArtist { get => _selectedArtist; set => SetField(ref _selectedArtist, value); }

    private string _libraryFolder = string.Empty;
    public string LibraryFolder { get => _libraryFolder; set => SetField(ref _libraryFolder, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                CheckCommand.RaiseCanExecuteChanged();
                ImportTopCommand.RaiseCanExecuteChanged();
                DownloadMissingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _status = "Volg artiesten en zie welke albums je nog mist. Vul je muziekbieb in bij Instellingen.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand CheckCommand { get; }
    public RelayCommand ImportTopCommand { get; }
    public RelayCommand DownloadMissingCommand { get; }

    private void AddArtist()
    {
        var a = ArtistInput.Trim();
        if (a.Length == 0) return;
        if (!Watchlist.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
        {
            Watchlist.Add(a);
            Persist();
            CheckCommand.RaiseCanExecuteChanged();
        }
        ArtistInput = string.Empty;
    }

    private void ImportTop()
    {
        try
        {
            var top = _topArtists();
            int added = 0;
            foreach (var a in top.Take(25))
                if (!Watchlist.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
                { Watchlist.Add(a); added++; }
            Persist();
            CheckCommand.RaiseCanExecuteChanged();
            Status = $"{added} artiest(en) toegevoegd vanuit Apple Music.";
        }
        catch (Exception e) { Status = "Kon Apple Music niet lezen: " + e.Message; }
    }

    private void Persist() => Settings.SaveWatchlist(Watchlist.ToList());

    private void Check()
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var lib = LibraryFolder;
        var artists = Watchlist.ToList();
        Results.Clear();
        Status = "Bibliotheek indexeren…";

        Task.Run(async () =>
        {
            var have = BuildLibraryIndex(lib);
            for (int i = 0; i < artists.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var artist = artists[i];
                var snap = i + 1;
                Dispatcher.UIThread.Post(() => Status = $"MusicBrainz… {artist} ({snap}/{artists.Count})");
                var albums = await MusicBrainzClient.GetOfficialAlbumsAsync(artist);
                var rows = albums.Select(al => new DiscAlbumViewModel(
                    artist, al.Title, al.Year, have.Contains(Key(artist, al.Title)))).ToList();
                if (rows.Count > 0)
                    Dispatcher.UIThread.Post(() => Results.Add(new ArtistDiscViewModel(artist, rows)));
                try { await Task.Delay(400, token); } catch { break; }
            }

            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                int missing = Results.SelectMany(r => r.Albums).Count(a => !a.Have);
                Status = token.IsCancellationRequested
                    ? $"Gestopt — {Results.Count} artiesten."
                    : $"{Results.Count} artiesten · {missing} ontbrekende albums (voorgevinkt). Klik 'Download ontbrekende'.";
            });
        });
    }

    private void DownloadMissing()
    {
        var queries = Results.SelectMany(r => r.Albums).Where(a => a.Selected && !a.Have)
            .Select(a => a.Query).Distinct().ToList();
        if (queries.Count == 0) { Status = "Niets geselecteerd om te downloaden."; return; }
        Status = $"{queries.Count} ontbrekende albums naar downloaden…";
        _onDownload(queries);
    }

    // "What do I own" album index from the library (album-artist/artist + album tags).
    private static HashSet<string> BuildLibraryIndex(string folder)
    {
        var set = new HashSet<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return set;
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"));
            var bag = new ConcurrentBag<string>();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
            {
                try
                {
                    var t = new Track(f);
                    var artist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : (t.Artist ?? "");
                    if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(t.Album))
                        bag.Add(Key(artist, t.Album));
                }
                catch { }
            });
            foreach (var k in bag) set.Add(k);
        }
        catch { }
        return set;
    }

    private static string Key(string artist, string album) => Norm(artist) + "|" + Norm(album);
    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
}
