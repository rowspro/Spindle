using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One file inside a staging album, with its tags shown so you can read what it actually is.</summary>
public class StagingFileViewModel : ViewModelBase
{
    public string Path { get; }
    public string FileName { get; }
    public string Format { get; }
    public string Meta { get; }
    public List<string> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
    public RelayCommand DeleteCommand { get; }

    public StagingFileViewModel(string path, string fileName, string format, string meta, List<string> issues, Action<StagingFileViewModel> onDelete)
    {
        Path = path; FileName = fileName; Format = format; Meta = meta; Issues = issues;
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }
}

/// <summary>A set of duplicate files within one staging folder (same title).</summary>
public class StagingDupGroup
{
    public string Title { get; }
    public List<StagingFileViewModel> Files { get; }
    public StagingDupGroup(string title, List<StagingFileViewModel> files) { Title = title; Files = files; }
}

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
    private readonly LibraryService _lib;
    private readonly UndoJournal _undo;
    private readonly List<StagingAlbumViewModel> _all = new();
    private CancellationTokenSource? _cts;

    public StagingViewModel(Action<IReadOnlyList<string>, string> onFix, Action onSortLibrary, LibraryService lib, UndoJournal undo)
    {
        _onFix = onFix;
        _onSortLibrary = onSortLibrary;
        _lib = lib;
        _undo = undo;
        ScanCommand = new RelayCommand(Scan, () => !IsBusy && !string.IsNullOrWhiteSpace(NieuwFolder));
        ApproveCommand = new RelayCommand(Approve, () => !IsBusy && _all.Any(a => a.IsSelected));
        SelectAllCommand = new RelayCommand(() => SetSelection(_ => true));
        SelectNoneCommand = new RelayCommand(() => SetSelection(_ => false));
        SelectCleanCommand = new RelayCommand(() => SetSelection(a => a.IsClean && !a.AlreadyInLibrary));
        BackCommand = new RelayCommand(() => ShowDetail = false);
        EditInMetadataCommand = new RelayCommand(
            () => { if (_detailAlbum != null) _onFix(_detailAlbum.Files, $"{_detailAlbum.Title} — tags/hoes bewerken."); },
            () => _detailAlbum != null);
    }

    // ---- Map-inspecteur (klik 'Fix' op een album) ----
    private StagingAlbumViewModel? _detailAlbum;

    private bool _showDetail;
    public bool ShowDetail
    {
        get => _showDetail;
        private set { if (SetField(ref _showDetail, value)) OnPropertyChanged(nameof(ShowAlbumList)); }
    }
    public bool ShowAlbumList => !_showDetail;

    private string _detailTitle = string.Empty;
    public string DetailTitle { get => _detailTitle; private set => SetField(ref _detailTitle, value); }

    public ObservableCollection<StagingFileViewModel> DetailFiles { get; } = new();
    public ObservableCollection<StagingDupGroup> DetailDuplicates { get; } = new();

    private bool _hasDuplicates;
    public bool HasDuplicates { get => _hasDuplicates; private set => SetField(ref _hasDuplicates, value); }

    public RelayCommand BackCommand { get; }
    public RelayCommand EditInMetadataCommand { get; }

    private void OpenDetail(StagingAlbumViewModel album)
    {
        _detailAlbum = album;
        DetailTitle = album.Title;
        EditInMetadataCommand.RaiseCanExecuteChanged();
        var files = album.Files.ToList();
        Status = "Map inlezen…";
        Task.Run(() =>
        {
            var rows = new List<StagingFileViewModel>();
            foreach (var f in files)
            {
                string fmt, meta; var issues = new List<string>(); string title = "";
                try
                {
                    var t = new Track(f);
                    title = t.Title ?? "";
                    var artist = !string.IsNullOrWhiteSpace(t.Artist) ? t.Artist : (t.AlbumArtist ?? "");
                    fmt = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(title)) parts.Add(title); else parts.Add("(geen titel)");
                    if (!string.IsNullOrWhiteSpace(artist)) parts.Add(artist);
                    var tn = t.TrackNumber ?? 0; if (tn > 0) parts.Add($"#{tn}");
                    if (t.Year > 0) parts.Add(t.Year.ToString());
                    if (!string.IsNullOrWhiteSpace(t.Genre)) parts.Add(t.Genre);
                    meta = string.Join("  ·  ", parts);
                    if (!Lossless.Contains(Path.GetExtension(f).ToLowerInvariant())) issues.Add("lossy");
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) issues.Add("geen tags");
                    if (t.EmbeddedPictures.Count == 0) issues.Add("geen hoes");
                }
                catch { fmt = Path.GetExtension(f).TrimStart('.').ToUpperInvariant(); meta = "(kon niet lezen)"; }
                rows.Add(new StagingFileViewModel(f, Path.GetFileName(f), fmt, meta, issues, DeleteDetailFile) { });
                // keep title for dup grouping via a parallel map
                _detailTitles[rows[^1]] = title;
            }
            Dispatcher.UIThread.Post(() =>
            {
                DetailFiles.Clear();
                foreach (var r in rows.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)) DetailFiles.Add(r);
                RebuildDuplicates();
                ShowDetail = true;
                Status = $"{DetailFiles.Count} nummers in '{album.Title}'.";
            });
        });
    }

    private readonly Dictionary<StagingFileViewModel, string> _detailTitles = new();

    private void RebuildDuplicates()
    {
        DetailDuplicates.Clear();
        foreach (var grp in DetailFiles
                     .Where(f => !string.IsNullOrWhiteSpace(_detailTitles.GetValueOrDefault(f)))
                     .GroupBy(f => Norm(_detailTitles[f]))
                     .Where(g => g.Count() > 1))
            DetailDuplicates.Add(new StagingDupGroup(_detailTitles[grp.First()], grp.ToList()));
        HasDuplicates = DetailDuplicates.Count > 0;
    }

    private void DeleteDetailFile(StagingFileViewModel file)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(NieuwFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var trash = parent != null ? Path.Combine(parent, "_Verwijderd (Spindle)") : Path.Combine(NieuwFolder, "_Verwijderd");
            var dest = MoveInto(file.Path, trash);
            _undo.Record($"Bestand verwijderd: {file.FileName}", new List<UndoJournal.MoveOp> { new(file.Path, dest) });
        }
        catch { try { File.Delete(file.Path); } catch { } }
        DetailFiles.Remove(file);
        _detailTitles.Remove(file);
        RebuildDuplicates();
        Status = $"'{file.FileName}' verwijderd (naar _Verwijderd, omkeerbaar).";
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
        ShowDetail = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Nieuw scannen…";
        var lib = LibraryFolder;

        Task.Run(() =>
        {
            // Fase 0: beide roots incrementeel verversen en uit de index lezen.
            _lib.Refresh(lib, token);
            _lib.Refresh(nieuw, token);
            var have = new HashSet<string>(_lib.Index.AllTracks(lib)
                .Select(r => Norm(!string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist) + "|" + Norm(r.Album)));
            var files = _lib.Index.AllTracks(nieuw);

            var groups = new Dictionary<string, (string Artist, string Album, string Year, List<string> Files,
                int Lossy, int Untagged, int NoCover, int MissingYg, HashSet<string> Dirs, HashSet<int> Tracks, bool DupTrack)>();

            foreach (var r in files)
            {
                if (token.IsCancellationRequested) break;
                var artist = !string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist;
                var key = Norm(artist) + "|" + Norm(r.Album);
                if (!groups.TryGetValue(key, out var g))
                    g = (artist, r.Album, r.Year > 0 ? r.Year.ToString() : "", new List<string>(), 0, 0, 0, 0, new HashSet<string>(), new HashSet<int>(), false);

                g.Files.Add(r.Path);
                var dir = Path.GetDirectoryName(r.Path);
                if (dir != null) g.Dirs.Add(dir);
                if (!r.Lossless) g.Lossy++;
                if (r.MissingTags) g.Untagged++;
                if (!r.HasCover) g.NoCover++;
                if (r.Year <= 0 || string.IsNullOrWhiteSpace(r.Genre)) g.MissingYg++;
                if (r.TrackNo > 0) { if (!g.Tracks.Add(r.TrackNo)) g.DupTrack = true; }
                if (string.IsNullOrEmpty(g.Year) && r.Year > 0) g.Year = r.Year.ToString();

                groups[key] = g;
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
                    flags, clean, already, sub, OpenDetail));
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
            var ops = new List<UndoJournal.MoveOp>();
            foreach (var album in selected)
            {
                foreach (var f in album.Files)
                {
                    try { var dest = MoveInto(f, lib); ops.Add(new UndoJournal.MoveOp(f, dest)); moved++; } catch { }
                }
                foreach (var d in album.SourceDirs) dirs.Add(d);
            }
            // opruimen: bronmappen die helemaal leeg zijn van audio → naar prullenbak (incl. hoezen/.nfo).
            foreach (var d in dirs) CleanConsumedSourceDir(d, ops);
            _undo.Record($"Goedkeuren: {selected.Count} album(s) naar bieb", ops);
            _lib.Refresh(NieuwFolder);

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
    private static string MoveInto(string file, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(file));
        if (File.Exists(dest)) dest = Unique(dest);
        try { File.Move(file, dest); }
        catch (IOException) { File.Copy(file, dest, false); File.Delete(file); }
        return dest;
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

    // After moving an album's audio out, a wholly-consumed source folder may still hold sidecars
    // (folder.jpg, .nfo, .cue, .log). Move that whole folder to the trash so 'Nieuw' is left clean.
    private void CleanConsumedSourceDir(string dir, List<UndoJournal.MoveOp>? ops = null)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var nieuwFull = Path.GetFullPath(NieuwFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(dirFull, nieuwFull, StringComparison.OrdinalIgnoreCase)) return; // nooit de Nieuw-hoofdmap

            bool audioLeft = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Any(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"));
            if (audioLeft) return; // gedeelde map met een ander, niet-goedgekeurd album → laat staan

            var parent = Path.GetDirectoryName(nieuwFull);
            var trash = parent != null ? Path.Combine(parent, "_Verwijderd (Spindle)") : Path.Combine(NieuwFolder, "_Verwijderd");
            Directory.CreateDirectory(trash);
            var dest = Path.Combine(trash, Path.GetFileName(dirFull));
            for (int i = 2; Directory.Exists(dest); i++) dest = Path.Combine(trash, $"{Path.GetFileName(dirFull)} ({i})");
            try { Directory.Move(dir, dest); }
            catch { CopyDir(dir, dest); try { Directory.Delete(dir, true); } catch { } }
            ops?.Add(new UndoJournal.MoveOp(dirFull, dest));

            CleanEmptyParents(Path.GetDirectoryName(dirFull), nieuwFull);
        }
        catch { }
    }

    // Verwijder lege oudermappen omhoog tot (maar niet incl.) de Nieuw-hoofdmap.
    private static void CleanEmptyParents(string? dir, string stopAt)
    {
        try
        {
            while (!string.IsNullOrEmpty(dir))
            {
                var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(full, stopAt, StringComparison.OrdinalIgnoreCase)) break;
                if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) break;
                var up = Path.GetDirectoryName(full);
                Directory.Delete(full);
                dir = up;
            }
        }
        catch { }
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }


    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
}
