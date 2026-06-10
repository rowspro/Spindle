using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One track row in the browser inspector.</summary>
public sealed class BrowserTrackViewModel
{
    public string Path { get; }
    public string Display { get; }
    public string Right { get; }

    public BrowserTrackViewModel(IndexedTrack t)
    {
        Path = t.Path;
        var title = string.IsNullOrWhiteSpace(t.Title) ? System.IO.Path.GetFileName(t.Path) : t.Title;
        Display = (t.TrackNo > 0 ? $"{t.TrackNo:00}  " : "") + title;
        Right = $"{t.Format} · {t.Duration / 60}:{t.Duration % 60:00}";
    }
}

/// <summary>One album card in the library browser (fase 1).</summary>
public class BrowserAlbumViewModel : ViewModelBase
{
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public int Year { get; init; }
    public string Genre { get; init; } = "";
    public List<IndexedTrack> Tracks { get; init; } = new();
    public int LossyCount { get; init; }
    public int UntaggedCount { get; init; }
    public bool HasCoverFlag { get; init; }
    public string Sub { get; init; } = "";
    public string FlagText { get; init; } = "";
    public bool HasIssues { get; init; }

    public string Key => (Artist + "|" + Album).ToLowerInvariant();
    public string Title => string.IsNullOrEmpty(Album) ? "(unknown album)" : Album;
    public string ArtistText => string.IsNullOrEmpty(Artist) ? "(unknown artist)" : Artist;

    private Bitmap? _cover;
    public Bitmap? Cover { get => _cover; set => SetField(ref _cover, value); }
}

/// <summary>
/// Fase 1: het hart van de app — albumgrid met covers uit de index, zoeken-als-je-typt,
/// vlag-filters, en een inspector met quick-edit (album-brede velden), tracklist en 10s-preview.
/// </summary>
public class BrowserViewModel : ViewModelBase
{
    private readonly LibraryService _lib;
    private readonly Func<string> _root;
    private readonly Action<IReadOnlyList<string>, string> _onEdit;
    private readonly List<BrowserAlbumViewModel> _all = new();
    private readonly ConcurrentDictionary<string, Bitmap> _coverCache = new();
    private CancellationTokenSource? _coverCts;
    private Process? _preview;
    private bool _loaded;
    private string _filter = "alles";
    private string? _pendingSelectKey;

    public ObservableCollection<BrowserAlbumViewModel> Albums { get; } = new();
    public ObservableCollection<BrowserTrackViewModel> SelectedTracks { get; } = new();

    public BrowserViewModel(LibraryService lib, Func<string> root, Action<IReadOnlyList<string>, string> onEdit)
    {
        _lib = lib;
        _root = root;
        _onEdit = onEdit;
        RefreshCommand = new RelayCommand(Refresh);
        EditInMetadataCommand = new RelayCommand(() =>
        {
            var a = SelectedAlbum;
            if (a != null) _onEdit(a.Tracks.Select(t => t.Path).ToList(), $"{a.ArtistText} — {a.Title} — edit tags.");
        }, () => SelectedAlbum != null);
        ShowInFinderCommand = new RelayCommand(() =>
        {
            var p = SelectedAlbum?.Tracks.FirstOrDefault()?.Path;
            if (p != null) try { Process.Start("open", new[] { "-R", p }); } catch { }
        }, () => SelectedAlbum != null);
        SaveAlbumEditCommand = new RelayCommand(SaveAlbumEdit, () => SelectedAlbum != null && !_busyEdit);
        FilterAllCommand = new RelayCommand(() => SetFilter("alles"));
        FilterLossyCommand = new RelayCommand(() => SetFilter("lossy"));
        FilterNoCoverCommand = new RelayCommand(() => SetFilter("hoes"));
        FilterNoTagsCommand = new RelayCommand(() => SetFilter("tags"));
        _lib.Changed += () => { if (_loaded) Refresh(); };
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand EditInMetadataCommand { get; }
    public RelayCommand ShowInFinderCommand { get; }
    public RelayCommand SaveAlbumEditCommand { get; }
    public RelayCommand FilterAllCommand { get; }
    public RelayCommand FilterLossyCommand { get; }
    public RelayCommand FilterNoCoverCommand { get; }
    public RelayCommand FilterNoTagsCommand { get; }

    public bool IsFilterAll => _filter == "alles";
    public bool IsFilterLossy => _filter == "lossy";
    public bool IsFilterNoCover => _filter == "hoes";
    public bool IsFilterNoTags => _filter == "tags";

    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) ApplyFilter(); } }

    private string _status = "The library loads when you arrive here.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private bool _busyEdit;

    private string _editAlbum = "", _editArtist = "", _editGenre = "", _editYear = "";
    public string EditAlbum { get => _editAlbum; set => SetField(ref _editAlbum, value); }
    public string EditArtist { get => _editArtist; set => SetField(ref _editArtist, value); }
    public string EditGenre { get => _editGenre; set => SetField(ref _editGenre, value); }
    public string EditYear { get => _editYear; set => SetField(ref _editYear, value); }

    private BrowserAlbumViewModel? _selectedAlbum;
    public BrowserAlbumViewModel? SelectedAlbum
    {
        get => _selectedAlbum;
        set
        {
            if (!SetField(ref _selectedAlbum, value)) return;
            StopPreview();
            SelectedTracks.Clear();
            if (value != null)
            {
                foreach (var t in value.Tracks) SelectedTracks.Add(new BrowserTrackViewModel(t));
                EditAlbum = value.Album;
                EditArtist = value.Artist;
                EditGenre = value.Genre;
                EditYear = value.Year > 0 ? value.Year.ToString() : "";
            }
            EditInMetadataCommand.RaiseCanExecuteChanged();
            ShowInFinderCommand.RaiseCanExecuteChanged();
            SaveAlbumEditCommand.RaiseCanExecuteChanged();
        }
    }

    private BrowserTrackViewModel? _selectedTrack;
    public BrowserTrackViewModel? SelectedTrack { get => _selectedTrack; set => SetField(ref _selectedTrack, value); }

    private void SetFilter(string f)
    {
        _filter = f;
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterLossy));
        OnPropertyChanged(nameof(IsFilterNoCover));
        OnPropertyChanged(nameof(IsFilterNoTags));
        ApplyFilter();
    }

    public void Refresh()
    {
        var root = _root();
        if (string.IsNullOrWhiteSpace(root)) { Status = "Set your music library (Settings)."; return; }
        var rows = _lib.Index.AllTracks(root);

        var groups = new Dictionary<string, List<IndexedTrack>>();
        foreach (var r in rows)
        {
            var eff = !string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist;
            var key = (eff + "|" + r.Album).ToLowerInvariant();
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<IndexedTrack>();
            list.Add(r);
        }

        _all.Clear();
        foreach (var kv in groups)
        {
            var ts = kv.Value.OrderBy(t => t.Disc).ThenBy(t => t.TrackNo).ThenBy(t => t.Path).ToList();
            var first = ts[0];
            var eff = !string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.AlbumArtist : first.Artist;
            int lossy = ts.Count(t => !t.Lossless);
            int unt = ts.Count(t => t.MissingTags);
            bool cover = ts.Any(t => t.HasCover);
            int year = ts.Max(t => t.Year);
            var genre = ts.Select(t => t.Genre).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? "";
            var dur = TimeSpan.FromSeconds(ts.Sum(t => (long)t.Duration));
            var durTxt = dur.TotalHours >= 1 ? $"{(int)dur.TotalHours}:{dur.Minutes:00} u" : $"{(int)dur.TotalMinutes} min";
            var flags = new List<string>();
            if (lossy > 0) flags.Add($"{lossy} lossy");
            if (unt > 0) flags.Add($"{unt} without tags");
            if (!cover) flags.Add("no cover");
            _all.Add(new BrowserAlbumViewModel
            {
                Artist = eff, Album = first.Album, Year = year, Genre = genre, Tracks = ts,
                LossyCount = lossy, UntaggedCount = unt, HasCoverFlag = cover,
                Sub = (year > 0 ? year + " · " : "") + $"{ts.Count} tracks · {durTxt}",
                FlagText = string.Join(" · ", flags),
                HasIssues = flags.Count > 0,
            });
        }
        _all.Sort((a, b) =>
        {
            int c = string.Compare(a.ArtistText, b.ArtistText, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        _loaded = true;
        ApplyFilter();
        Status = $"{_all.Count} albums · {rows.Count} tracks";
        LoadCovers();
    }

    private void ApplyFilter()
    {
        var q = (SearchText ?? "").Trim();
        var selKey = SelectedAlbum?.Key ?? _pendingSelectKey;
        Albums.Clear();
        foreach (var a in _all)
        {
            bool okF = _filter switch
            {
                "lossy" => a.LossyCount > 0,
                "hoes" => !a.HasCoverFlag,
                "tags" => a.UntaggedCount > 0,
                _ => true,
            };
            if (!okF) continue;
            if (q.Length > 0
                && a.Album.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && a.Artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Albums.Add(a);
        }
        _pendingSelectKey = null;
        if (selKey != null)
        {
            var hit = Albums.FirstOrDefault(x => x.Key == selKey);
            if (hit != null && !ReferenceEquals(hit, SelectedAlbum)) SelectedAlbum = hit;
        }
    }

    /// <summary>Open the browser focused on one album (used by the Cmd+F palette).</summary>
    public void FocusAlbum(string artist, string album)
    {
        if (!_loaded) Refresh();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        SetFilter("alles");
        var key = (artist + "|" + album).ToLowerInvariant();
        var hit = _all.FirstOrDefault(a => a.Key == key)
               ?? _all.FirstOrDefault(a => a.Album.Equals(album, StringComparison.OrdinalIgnoreCase));
        if (hit != null) SelectedAlbum = hit;
    }

    // ---- covers (lazy, in-memory cache; disk-thumbs volgen later) ----
    private void LoadCovers()
    {
        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        var targets = _all.ToList();
        Task.Run(() =>
        {
            foreach (var a in targets)
            {
                if (cts.IsCancellationRequested) return;
                if (a.Cover != null) continue;
                if (_coverCache.TryGetValue(a.Key, out var hit)) { Dispatcher.UIThread.Post(() => a.Cover = hit); continue; }
                var src = a.Tracks.FirstOrDefault(t => t.HasCover)?.Path;
                if (src == null) continue;
                try
                {
                    var t = new Track(src);
                    var data = t.EmbeddedPictures.Count > 0 ? t.EmbeddedPictures[0].PictureData : null;
                    if (data == null || data.Length == 0) continue;
                    using var ms = new MemoryStream(data);
                    var bmp = Bitmap.DecodeToWidth(ms, 256);
                    _coverCache[a.Key] = bmp;
                    Dispatcher.UIThread.Post(() => a.Cover = bmp);
                }
                catch { }
            }
        });
    }

    // ---- quick-edit: album-brede velden (album, album-artiest, genre, jaar) ----
    private void SaveAlbumEdit()
    {
        var a = SelectedAlbum;
        if (a == null || _busyEdit) return;
        var album = (EditAlbum ?? "").Trim();
        var artist = (EditArtist ?? "").Trim();
        var genre = (EditGenre ?? "").Trim();
        int.TryParse((EditYear ?? "").Trim(), out var year);
        bool cAlbum = album.Length > 0 && album != a.Album;
        bool cArtist = artist.Length > 0 && artist != a.Artist;
        bool cGenre = genre != a.Genre && genre.Length > 0;
        bool cYear = year > 0 && year != a.Year;
        if (!cAlbum && !cArtist && !cGenre && !cYear) { Status = "Nothing changed."; return; }

        _busyEdit = true;
        SaveAlbumEditCommand.RaiseCanExecuteChanged();
        Status = "Saving album fields…";
        var files = a.Tracks.Select(t => t.Path).ToList();
        _pendingSelectKey = ((cArtist ? artist : a.Artist) + "|" + (cAlbum ? album : a.Album)).ToLowerInvariant();
        var root = _root();

        Task.Run(() =>
        {
            int n = 0;
            foreach (var f in files)
            {
                try
                {
                    var t = new Track(f);
                    if (cAlbum) t.Album = album;
                    if (cArtist) { t.AlbumArtist = artist; if (string.IsNullOrWhiteSpace(t.Artist)) t.Artist = artist; }
                    if (cGenre) t.Genre = genre;
                    if (cYear) t.Year = year;
                    t.Save();
                    n++;
                }
                catch { }
            }
            _lib.Refresh(root); // → Changed → Refresh() → herselectie via _pendingSelectKey
            Dispatcher.UIThread.Post(() =>
            {
                _busyEdit = false;
                SaveAlbumEditCommand.RaiseCanExecuteChanged();
                Status = $"Album fields applied to {n} tracks.";
            });
        });
    }

    // ---- 10s audio-preview (spatie) via afplay ----
    public void TogglePreview()
    {
        if (_preview != null && !_preview.HasExited) { StopPreview(); Status = "Preview stopped."; return; }
        var path = SelectedTrack?.Path ?? SelectedAlbum?.Tracks.FirstOrDefault()?.Path;
        if (path == null) return;
        try
        {
            var psi = new ProcessStartInfo("afplay") { UseShellExecute = false };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("10");
            psi.ArgumentList.Add(path);
            _preview = Process.Start(psi);
            Status = $"▶ {System.IO.Path.GetFileName(path)}  (space = stop)";
        }
        catch { Status = "Preview unavailable (afplay)."; }
    }

    private void StopPreview()
    {
        try { if (_preview != null && !_preview.HasExited) _preview.Kill(); } catch { }
        _preview = null;
    }
}
