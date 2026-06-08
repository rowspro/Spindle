using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One album found in the "Nieuw" staging folder, with the quality flags that need attention.</summary>
public class StagingAlbumViewModel : ViewModelBase
{
    public string Artist { get; }
    public string Album { get; }
    public string Year { get; }
    public IReadOnlyList<string> Files { get; }
    public List<string> SourceDirs { get; }
    public List<string> Flags { get; }
    public bool IsClean { get; }
    public bool AlreadyInLibrary { get; }

    public string Title => string.IsNullOrEmpty(Artist) ? Album : $"{Artist} — {Album}";
    public string Sub { get; }
    public bool HasFlags => Flags.Count > 0;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (SetField(ref _isSelected, value)) OnSelectedChanged?.Invoke(); } }
    public Action? OnSelectedChanged;

    public RelayCommand FixCommand { get; }

    public StagingAlbumViewModel(string artist, string album, string year, IReadOnlyList<string> files,
        List<string> sourceDirs, List<string> flags, bool isClean, bool alreadyInLibrary, string sub,
        Action<StagingAlbumViewModel> onFix)
    {
        Artist = artist; Album = album; Year = year; Files = files; SourceDirs = sourceDirs;
        Flags = flags; IsClean = isClean; AlreadyInLibrary = alreadyInLibrary; Sub = sub;
        _isSelected = isClean && !alreadyInLibrary;   // pre-select what's ready to import
        FixCommand = new RelayCommand(() => onFix(this));
    }
}

/// <summary>
/// "Nieuw": the review gate. Scans the staging/download folder (where Nicotine+ drops artist folders),
/// groups by album, and flags anything that deviates (no FLAC, missing tags/cover, already in the library,
/// duplicate versions). Approve = move the selected albums into the main library and re-sort everything.
/// </summary>
public class StagingViewModel : ViewModelBase
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    private static readonly string[] Lossless = { ".flac", ".wav", ".aiff", ".aif" };

    private readonly Action<IReadOnlyList<string>, string> _onFix;
    private readonly Action _onSortLibrary;
    private readonly List<StagingAlbumViewModel> _all = new();
    private CancellationTokenSource? _cts;

    public StagingViewModel(Action<IReadOnlyList<string>, string> onFix, Action onSortLibrary)
    {
        _onFix = onFix;
        _onSortLibrary = onSortLibrary;
        ScanCommand = new RelayCommand(Scan, () => !IsBusy && !string.IsNullOrWhiteSpace(NieuwFolder));
        ApproveCommand = new RelayCommand(Approve, () => !IsBusy && _all.Any(a => a.IsSelected));
        SelectAllCommand = new RelayCommand(() => SetSelection(_ => true));
        SelectNoneCommand = new RelayCommand(() => SetSelection(_ => false));
        SelectCleanCommand = new RelayCommand(() => SetSelection(a => a.IsClean && !a.AlreadyInLibrary));
    }

    private string _nieuwFolder = string.Empty;
    public string NieuwFolder { get => _nieuwFolder; set { if (SetField(ref _nieuwFolder, value)) ScanCommand.RaiseCanExecuteChanged(); } }

    private string _libraryFolder = string.Empty;
    public string LibraryFolder { get => _libraryFolder; set => SetField(ref _libraryFolder, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) { ScanCommand.RaiseCanExecuteChanged(); ApproveCommand.RaiseCanExecuteChanged(); } }
    }

    private bool _showOnlyIssues;
    public bool ShowOnlyIssues { get => _showOnlyIssues; set { if (SetField(ref _showOnlyIssues, value)) ApplyFilter(); } }

    private string _status = "Stel de map 'Nieuwe muziek' in (Instellingen) en scan wat Nicotine+ heeft binnengehaald.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _summary = string.Empty;
    public string Summary { get => _summary; private set => SetField(ref _summary, value); }

    public ObservableCollection<StagingAlbumViewModel> Albums { get; } = new();

    public RelayCommand ScanCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectCleanCommand { get; }

    private void SetSelection(Func<StagingAlbumViewModel, bool> pick)
    {
        foreach (var a in _all) a.IsSelected = pick(a);
        ApproveCommand.RaiseCanExecuteChanged();
    }

    private void Scan()
    {
        if (IsBusy) return;
        var nieuw = NieuwFolder;
        if (!Directory.Exists(nieuw)) { Status = "De map 'Nieuwe muziek' bestaat niet."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Nieuw scannen…";
        var lib = LibraryFolder;

        Task.Run(() =>
        {
            var have = BuildLibraryIndex(lib);
            var files = Directory.EnumerateFiles(nieuw, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                .ToList();

            var groups = new Dictionary<string, (string Artist, string Album, string Year, List<string> Files,
                int Lossy, int Untagged, int NoCover, int MissingYg, HashSet<string> Dirs, HashSet<int> Tracks, bool DupTrack)>();

            foreach (var f in files)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var t = new Track(f);
                    var artist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : (t.Artist ?? "");
                    var album = t.Album ?? "";
                    var key = Norm(artist) + "|" + Norm(album);
                    if (!groups.TryGetValue(key, out var g))
                        g = (artist, album, t.Year > 0 ? t.Year.ToString() : "", new List<string>(), 0, 0, 0, 0, new HashSet<string>(), new HashSet<int>(), false);

                    g.Files.Add(f);
                    var dir = Path.GetDirectoryName(f);
                    if (dir != null) g.Dirs.Add(dir);
                    if (!Lossless.Contains(Path.GetExtension(f).ToLowerInvariant())) g.Lossy++;
                    if (string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(artist)) g.Untagged++;
                    if (t.EmbeddedPictures.Count == 0) g.NoCover++;
                    if (t.Year <= 0 || string.IsNullOrWhiteSpace(t.Genre)) g.MissingYg++;
                    var tn = t.TrackNumber ?? 0;
                    if (tn > 0) { if (!g.Tracks.Add(tn)) g.DupTrack = true; }
                    if (string.IsNullOrEmpty(g.Year) && t.Year > 0) g.Year = t.Year.ToString();

                    groups[key] = g;
                }
                catch { }
            }

            var albums = new List<StagingAlbumViewModel>();
            foreach (var g in groups.Values.OrderBy(g => g.Artist).ThenBy(g => g.Album))
            {
                bool dup = g.DupTrack || g.Dirs.Count > 1;
                bool already = have.Contains(Norm(g.Artist) + "|" + Norm(g.Album));
                var flags = new List<string>();
                if (g.Lossy > 0) flags.Add($"{g.Lossy} lossy");
                if (g.Untagged > 0) flags.Add($"{g.Untagged} zonder tags");
                if (g.NoCover > 0) flags.Add("geen hoes");
                if (g.MissingYg > 0) flags.Add("mist jaar/genre");
                if (dup) flags.Add("dubbele versies");
                bool clean = g.Lossy == 0 && g.Untagged == 0 && g.NoCover == 0 && g.MissingYg == 0 && !dup;
                if (already) flags.Add("al in bieb");
                var sub = $"{g.Files.Count} nummers" + (string.IsNullOrEmpty(g.Year) ? "" : $"  ·  {g.Year}");
                albums.Add(new StagingAlbumViewModel(g.Artist, g.Album, g.Year, g.Files, g.Dirs.ToList(),
                    flags, clean, already, sub, a => _onFix(a.Files, $"{a.Title} — controleer/fix en keur goed.")));
            }

            Dispatcher.UIThread.Post(() =>
            {
                _all.Clear();
                foreach (var a in albums) { a.OnSelectedChanged = () => ApproveCommand.RaiseCanExecuteChanged(); _all.Add(a); }
                ApplyFilter();
                int issues = albums.Count(a => !a.IsClean);
                int ready = albums.Count(a => a.IsSelected);
                Summary = $"{albums.Count} albums · {files.Count} nummers · {issues} met aandachtspunten";
                Status = token.IsCancellationRequested ? "Scan gestopt."
                    : albums.Count == 0 ? "Niets in 'Nieuw'." : $"{ready} schoon en klaar om goed te keuren; {issues} hebben aandacht nodig.";
                IsBusy = false;
                ApproveCommand.RaiseCanExecuteChanged();
            });
        });
    }

    private void ApplyFilter()
    {
        Albums.Clear();
        foreach (var a in _all)
            if (!ShowOnlyIssues || !a.IsClean)
                Albums.Add(a);
    }

    private void Approve()
    {
        if (IsBusy) return;
        var lib = LibraryFolder;
        if (string.IsNullOrWhiteSpace(lib) || !Directory.Exists(lib)) { Status = "Stel eerst je muziekbieb in (Instellingen)."; return; }
        var selected = _all.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) { Status = "Niets geselecteerd."; return; }

        IsBusy = true;
        Status = $"{selected.Count} albums naar de bibliotheek verplaatsen…";

        Task.Run(() =>
        {
            int moved = 0;
            var dirs = new HashSet<string>();
            foreach (var album in selected)
            {
                foreach (var f in album.Files)
                {
                    try { MoveInto(f, lib); moved++; } catch { }
                }
                foreach (var d in album.SourceDirs) dirs.Add(d);
            }
            // opruimen: lege bronmappen in 'Nieuw'
            foreach (var d in dirs) TryRemoveIfEmpty(d);

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var a in selected) { _all.Remove(a); Albums.Remove(a); }
                IsBusy = false;
                Status = $"{moved} nummers verplaatst. Collectie wordt nu opnieuw gesorteerd…";
                _onSortLibrary();
            });
        });
    }

    // Move a file into a folder, handling cross-volume moves and name clashes.
    private static void MoveInto(string file, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(file));
        if (File.Exists(dest)) dest = Unique(dest);
        try { File.Move(file, dest); }
        catch (IOException) { File.Copy(file, dest, false); File.Delete(file); }
    }

    private static string Unique(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryRemoveIfEmpty(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

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
                        bag.Add(Norm(artist) + "|" + Norm(t.Album));
                }
                catch { }
            });
            foreach (var k in bag) set.Add(k);
        }
        catch { }
        return set;
    }

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
}
