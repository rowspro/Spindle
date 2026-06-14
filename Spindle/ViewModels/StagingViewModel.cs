using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ATL;

namespace Spindle.ViewModels;

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

    public RelayCommand PlayCommand { get; }

    public StagingFileViewModel(string path, string fileName, string format, string meta, List<string> issues,
        Action<StagingFileViewModel> onDelete, Action<StagingFileViewModel> onPlay)
    {
        Path = path; FileName = fileName; Format = format; Meta = meta; Issues = issues;
        DeleteCommand = new RelayCommand(() => onDelete(this));
        PlayCommand = new RelayCommand(() => onPlay(this));
    }
}

/// <summary>A set of duplicate files within one staging folder (same title).</summary>
public class StagingDupGroup
{
    public string Title { get; }
    public List<StagingFileViewModel> Files { get; }
    public StagingDupGroup(string title, List<StagingFileViewModel> files) { Title = title; Files = files; }
}

/// <summary>One album found in the "Inbox" staging folder, with the quality flags that need attention.</summary>
public class StagingAlbumViewModel : ViewModelBase
{
    public string Artist { get; }
    public string Album { get; }
    public string Year { get; }
    public IReadOnlyList<string> Files { get; }
    public List<string> SourceDirs { get; }
    private List<string> _flags = new();
    public List<string> Flags { get => _flags; private set => SetField(ref _flags, value); }

    private bool _isCleanFlag;
    public bool IsClean { get => _isCleanFlag; private set => SetField(ref _isCleanFlag, value); }

    public bool AlreadyInLibrary { get; }
    public bool CanReplace { get; }

    public string Title => string.IsNullOrEmpty(Artist) ? Album : $"{Artist} — {Album}";

    private string _sub = "";
    public string Sub { get => _sub; private set => SetField(ref _sub, value); }
    public bool HasFlags => Flags.Count > 0;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (value && !CanSelect) return; if (SetField(ref _isSelected, value)) OnSelectedChanged?.Invoke(); } }
    public Action? OnSelectedChanged;

    // ---- Strict gate: kritieke vlaggen blokkeren het vinkje (fouten gaan het magazijn niet in) ----
    private bool _critical;
    public bool Critical { get => _critical; private set => SetField(ref _critical, value); }
    private bool _approveAnyway;
    public bool CanSelect => !_critical || _approveAnyway;
    public bool ShowOverride => _critical && !_approveAnyway;
    public RelayCommand OverrideCommand { get; }
    public bool Receiving { get; set; }

    // ---- MusicBrainz-tracklijstvalidatie (ASN-check) ----
    private string _validation = "";
    public string Validation { get => _validation; private set => SetField(ref _validation, value); }
    public bool HasValidation => _validation.Length > 0;
    public bool ValidationOk { get; private set; }
    public bool ValidationInfo => HasValidation && !ValidationOk;

    public void SetValidation(bool ok, string text, List<string> missingFlags)
    {
        ValidationOk = ok;
        Validation = text;
        OnPropertyChanged(nameof(HasValidation));
        OnPropertyChanged(nameof(ValidationOk));
        OnPropertyChanged(nameof(ValidationInfo));
        if (missingFlags.Count > 0)
        {
            var f = Flags.ToList();
            foreach (var m in missingFlags) if (!f.Contains(m)) f.Add(m);
            Flags = f;
            IsClean = false;
            OnPropertyChanged(nameof(HasFlags));
        }
        RecomputeCritical();
    }

    /// <summary>Critical = mag het magazijn niet in zonder bewuste override.</summary>
    public void RecomputeCritical()
    {
        Critical = Receiving || Flags.Any(f =>
            f.Contains("without tags") || f.StartsWith("missing track")
            || f.StartsWith("still receiving") || f == "already in library — better quality there");
        if (!CanSelect && _isSelected) { _isSelected = false; OnPropertyChanged(nameof(IsSelected)); OnSelectedChanged?.Invoke(); }
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(ShowOverride));
    }

    public RelayCommand FixCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ReplaceCommand { get; }

    public string? CoverPath { get; }

    private Bitmap? _cover;
    public Bitmap? Cover { get => _cover; set => SetField(ref _cover, value); }

    public StagingAlbumViewModel(string artist, string album, string year, IReadOnlyList<string> files,
        List<string> sourceDirs, List<string> flags, bool isClean, bool alreadyInLibrary, bool canReplace, string sub,
        string? coverPath, Action<StagingAlbumViewModel> onFix, Action<StagingAlbumViewModel> onDelete,
        Action<StagingAlbumViewModel> onReplace)
    {
        Artist = artist; Album = album; Year = year; Files = files; SourceDirs = sourceDirs;
        _flags = flags; _isCleanFlag = isClean; AlreadyInLibrary = alreadyInLibrary; CanReplace = canReplace; _sub = sub;
        CoverPath = coverPath;
        _isSelected = isClean && !alreadyInLibrary;   // pre-select what's ready to import
        FixCommand = new RelayCommand(() => onFix(this));
        DeleteCommand = new RelayCommand(() => onDelete(this));
        ReplaceCommand = new RelayCommand(() => onReplace(this));
        OverrideCommand = new RelayCommand(() =>
        {
            _approveAnyway = true;
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(ShowOverride));
            IsSelected = true;
        });
    }

    /// <summary>Recompute the card stats after files were deleted in the inspector.</summary>
    public void UpdateStats(List<string> flags, bool isClean, string sub)
    {
        Flags = flags;
        IsClean = isClean;
        Sub = sub;
        OnPropertyChanged(nameof(HasFlags));
        RecomputeCritical();
    }
}

/// <summary>
/// "Inbox": the review gate. Scans the staging/download folder (where Nicotine+ drops artist folders),
/// groups by album, and flags anything that deviates (no FLAC, missing tags/cover, already in the library,
/// duplicate versions). Approve = move the selected albums into the main library and re-sort everything.
/// </summary>
public class StagingViewModel : ViewModelBase
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    private static readonly string[] Lossless = { ".flac", ".wav", ".aiff", ".aif" };

    private readonly Action<IReadOnlyList<string>, string> _onFix;
    private readonly LibraryService _lib;
    private readonly UndoJournal _undo;
    private readonly Func<string> _template;
    public TagGridViewModel FixGrid { get; }
    /// <summary>The full metadata editor, embedded inline in the album detail (same component as the Metadata tab).</summary>
    public MetadataEditorViewModel DetailEditor { get; }
    private readonly List<StagingAlbumViewModel> _all = new();
    private CancellationTokenSource? _cts;

    public StagingViewModel(Action<IReadOnlyList<string>, string> onFix, LibraryService lib, UndoJournal undo, Func<string> template)
    {
        _onFix = onFix;
        _lib = lib;
        _undo = undo;
        _template = template;
        FixGrid = new TagGridViewModel(lib, undo);
        DetailEditor = new MetadataEditorViewModel(lib, undo) { ShowSourceButtons = false };
        ScanCommand = new RelayCommand(Scan, () => !IsBusy && !string.IsNullOrWhiteSpace(NieuwFolder));
        ApproveCommand = new RelayCommand(Approve, () => !IsBusy && _all.Any(a => a.IsSelected));
        SelectAllCommand = new RelayCommand(() => SetSelection(_ => true));
        SelectNoneCommand = new RelayCommand(() => SetSelection(_ => false));
        SelectCleanCommand = new RelayCommand(() => SetSelection(a => a.IsClean && !a.AlreadyInLibrary));
        DeleteRedundantCommand = new RelayCommand(DeleteRedundant, () => !IsBusy);
        ConfirmPlanCommand = new RelayCommand(ExecutePlan, () => PlanItems.Count > 0 && !IsBusy);
        CancelPlanCommand = new RelayCommand(() => ShowPlan = false);
        BackCommand = new RelayCommand(() => ShowDetail = false);
        EditInMetadataCommand = new RelayCommand(
            () => { if (_detailAlbum != null) _onFix(_detailAlbum.Files, $"{_detailAlbum.Title} — edit tags/cover."); },
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
        FixGrid.Load(files);
        DetailEditor.LoadFiles(files, $"{album.Title} — edit tags & cover");
        Status = "Reading folder…";
        var nieuw = NieuwFolder;
        Task.Run(() =>
        {
            var map = new Dictionary<string, IndexedTrack>(StringComparer.Ordinal);
            try { foreach (var r in _lib.Index.AllTracks(nieuw)) map[r.Path] = r; } catch { }
            var rows = new List<StagingFileViewModel>();
            foreach (var f in files)
            {
                var fmt = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
                string meta; var issues = new List<string>(); string title = "";
                if (map.TryGetValue(f, out var r))
                {
                    title = r.Title;
                    var artist = !string.IsNullOrWhiteSpace(r.Artist) ? r.Artist : r.AlbumArtist;
                    var parts = new List<string> { string.IsNullOrWhiteSpace(title) ? "(no title)" : title };
                    if (!string.IsNullOrWhiteSpace(artist)) parts.Add(artist);
                    if (r.TrackNo > 0) parts.Add($"#{r.TrackNo}");
                    if (r.Year > 0) parts.Add(r.Year.ToString());
                    if (r.Bitrate > 0) parts.Add($"{r.Bitrate} kbps");
                    if (r.Duration > 0) parts.Add($"{r.Duration / 60}:{r.Duration % 60:00}");
                    meta = string.Join("  ·  ", parts);
                    if (!r.Lossless) issues.Add("lossy");
                    if (r.MissingTags) issues.Add("no tags");
                    if (!r.HasCover) issues.Add("no cover");
                }
                else meta = "(not indexed yet)";
                rows.Add(new StagingFileViewModel(f, Path.GetFileName(f), fmt, meta, issues, DeleteDetailFile, PlayDetailFile));
                _detailTitles[rows[^1]] = title;
            }
            var versions = new List<(double LosslessFrac, int AvgBr, StagingVersionViewModel Vm)>();
            if (album.SourceDirs.Count > 1)
            {
                foreach (var d in album.SourceDirs)
                {
                    var inDir = files.Where(f => string.Equals(Path.GetDirectoryName(f), d, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (inDir.Count == 0) continue;
                    long sz = 0;
                    foreach (var f in inDir) { try { sz += new FileInfo(f).Length; } catch { } }
                    var rsIn = inDir.Where(map.ContainsKey).Select(f => map[f]).ToList();
                    var fmts = string.Join("/", inDir.Select(f => Path.GetExtension(f).TrimStart('.').ToUpperInvariant()).Distinct());
                    int avg = rsIn.Count > 0 ? (int)rsIn.Average(r => (double)r.Bitrate) : 0;
                    double lfr = rsIn.Count > 0 ? rsIn.Count(r => r.Lossless) / (double)rsIn.Count : 0;
                    var stats = $"{inDir.Count} tracks · {fmts}" + (avg > 0 ? $" · ~{avg} kbps" : "") + $" · {FmtBytes(sz)}";
                    versions.Add((lfr, avg, new StagingVersionViewModel(d, stats, KeepOnly)));
                }
                versions = versions.OrderByDescending(v => v.LosslessFrac).ThenByDescending(v => v.AvgBr).ToList();
                if (versions.Count > 0) versions[0].Vm.IsBest = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                DetailVersions.Clear();
                foreach (var v in versions) DetailVersions.Add(v.Vm);
                OnPropertyChanged(nameof(HasVersions));
                DetailEditor.Versions.Clear();
                foreach (var v in versions) DetailEditor.Versions.Add(v.Vm);
                DetailFiles.Clear();
                foreach (var r in rows.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)) DetailFiles.Add(r);
                RebuildDuplicates();
                DetailEditor.Duplicates.Clear();
                foreach (var g in DetailDuplicates) DetailEditor.Duplicates.Add(g);
                ShowDetail = true;
                RebuildChecks(album);
                Status = $"{DetailFiles.Count} tracks in '{album.Title}'.";
            });
        });
    }

    private void PlayDetailFile(StagingFileViewModel file)
    {
        var player = PlayerViewModel.Current;
        if (player == null) return;
        var items = DetailFiles.Select(f => new PlayerItem
        {
            Path = f.Path,
            Title = f.FileName,
            Sub = _detailAlbum?.Title ?? "Inbox",
        }).ToList();
        var idx = Math.Max(0, items.FindIndex(i => i.Path == file.Path));
        if (player.HasTrack && player.CurrentPath == file.Path) { player.PlayPause(); return; }
        player.PlayQueue(items, idx);
        Status = $"▶ {file.FileName}";
    }

    /// <summary>Status helper for code-behind (clipboard feedback).</summary>
    public void Notify(string msg) => Status = msg;

    /// <summary>Apply cover art to every file of the album that is open in the inspector (Mp3tag-style).</summary>
    public void ApplyCoverToDetailAlbum(byte[] data)
    {
        var album = _detailAlbum;
        if (album == null) return;
        var files = album.Files.ToList();
        Status = "Applying cover to the whole album…";
        Task.Run(() =>
        {
            int n = 0;
            foreach (var f in files)
            {
                try
                {
                    var t = new Track(f);
                    t.EmbeddedPictures.Clear();
                    t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(data));
                    t.Save();
                    n++;
                }
                catch { }
            }
            _lib.Refresh(NieuwFolder);
            Dispatcher.UIThread.Post(() =>
            {
                Status = $"Cover applied to {n} files.";
                RefreshAlbumStats(album);
            });
        });
    }

    /// <summary>Recompute one album card's flags/counters from the index (after deletes in the inspector).</summary>
    private void RefreshAlbumStats(StagingAlbumViewModel album)
    {
        var nieuw = NieuwFolder;
        Task.Run(() =>
        {
            _lib.Refresh(nieuw);
            var map = new Dictionary<string, IndexedTrack>(StringComparer.Ordinal);
            try { foreach (var r in _lib.Index.AllTracks(nieuw)) map[r.Path] = r; } catch { }
            var rows = album.Files.Where(map.ContainsKey).Select(f => map[f]).ToList();
            int lossy = rows.Count(r => !r.Lossless);
            int unt = rows.Count(r => r.MissingTags);
            int noCover = rows.Count(r => !r.HasCover);
            int missingYear = rows.Count(r => r.Year <= 0);
            int missingGenre = rows.Count(r => string.IsNullOrWhiteSpace(r.Genre));
            var trackNos = new HashSet<(int, int)>();
            var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool dup = false;
            foreach (var r in rows)
            {
                if (r.TrackNo > 0 && !trackNos.Add((r.Disc, r.TrackNo))) dup = true;   // disc-aware
                var d0 = Path.GetDirectoryName(r.Path);
                if (d0 != null) dirSet.Add(d0);
            }
            dup = dup || dirSet.Count > 1;
            var flags = new List<string>();
            if (lossy > 0) flags.Add($"{lossy} lossy");
            if (unt > 0) flags.Add($"{unt} without tags");
            if (noCover > 0) flags.Add("no cover");
            if (missingYear > 0) flags.Add("missing year");
            if (missingGenre > 0) flags.Add("missing genre");
            if (dup) flags.Add("duplicate versions");
            bool clean = flags.Count == 0;
            if (album.AlreadyInLibrary) flags.Add("already in library");
            int yr = rows.Count > 0 ? rows.Max(r => r.Year) : 0;
            var sub = $"{rows.Count} tracks" + (yr > 0 ? $"  ·  {yr}" : "");
            Dispatcher.UIThread.Post(() =>
            {
                album.UpdateStats(flags, clean, sub);
                if (ReferenceEquals(_detailAlbum, album)) RebuildChecks(album);
                Summary = $"{_all.Count} albums · {_all.Sum(a => a.Files.Count)} tracks · {_all.Count(a => !a.IsClean)} need attention";
                UpdatePipeline();
            });
        });
    }

    /// <summary>Remove a whole album from the inbox (e.g. it's already in the library): to the trash, reversible.</summary>
    private void DeleteInboxAlbum(StagingAlbumViewModel album)
    {
        if (IsBusy) return;
        var nieuw = NieuwFolder;
        IsBusy = true;
        Status = $"Removing '{album.Title}' from inbox…";
        Task.Run(() =>
        {
            string trash;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(nieuw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                trash = parent != null ? Path.Combine(parent, "_Verwijderd (Spindle)") : Path.Combine(nieuw, "_Verwijderd");
            }
            catch { trash = Path.Combine(nieuw, "_Verwijderd"); }
            var ops = new List<UndoJournal.MoveOp>();
            int moved = 0;
            foreach (var f in album.Files.ToList())
            {
                try { var dest = MoveInto(f, trash); ops.Add(new UndoJournal.MoveOp(f, dest)); moved++; } catch { }
            }
            foreach (var d in album.SourceDirs) CleanConsumedSourceDir(d, ops);
            _undo.Record($"Inbox album removed: {album.Title}", ops);
            _lib.Refresh(nieuw);
            Dispatcher.UIThread.Post(() =>
            {
                _all.Remove(album);
                RemoveAlbumFromList(album);
                IsBusy = false;
                ApproveCommand.RaiseCanExecuteChanged();
                Summary = $"{_all.Count} albums · {_all.Sum(a => a.Files.Count)} tracks · {_all.Count(a => !a.IsClean)} need attention";
                UpdatePipeline();
                Status = $"'{album.Title}' removed from inbox ({moved} files, Cmd+Z = undo).";
            });
        });
    }

    /// <summary>Replace the (lower-quality) library version of this album with the inbox version, in one undoable batch.</summary>
    private void ReplaceInLibrary(StagingAlbumViewModel album)
    {
        if (IsBusy) return;
        var lib = LibraryFolder;
        if (string.IsNullOrWhiteSpace(lib) || !Directory.Exists(lib)) { Status = "Set your music library first (Settings)."; return; }
        var nieuw = NieuwFolder;
        var template = _template();
        IsBusy = true;
        Status = $"Replacing '{album.Title}' in the library…";
        Task.Run(() =>
        {
            var ops = new List<UndoJournal.MoveOp>();
            // 1) oude bieb-versie naar de prullenbak naast de bieb
            var key = Norm(album.Artist) + "|" + Norm(album.Album);
            var oldRows = _lib.Index.AllTracks(lib).Where(r =>
                Norm(!string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist) + "|" + Norm(r.Album) == key).ToList();
            string trash;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(lib).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                trash = parent != null ? Path.Combine(parent, "_Verwijderd (Spindle)") : Path.Combine(lib, "_Verwijderd");
            }
            catch { trash = Path.Combine(lib, "_Verwijderd"); }
            int removedOld = 0;
            var oldDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in oldRows)
            {
                try
                {
                    var dest = MoveInto(r.Path, trash);
                    ops.Add(new UndoJournal.MoveOp(r.Path, dest));
                    removedOld++;
                    var d0 = Path.GetDirectoryName(r.Path);
                    if (d0 != null) oldDirs.Add(d0);
                }
                catch { }
            }
            foreach (var d in oldDirs)
            {
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            // 2) inbox-versie volgens template op de juiste plek zetten (zelfde logica als goedkeuren)
            var map = new Dictionary<string, IndexedTrack>(StringComparer.Ordinal);
            try { foreach (var r in _lib.Index.AllTracks(nieuw)) map[r.Path] = r; } catch { }
            var artistDir = Clean(string.IsNullOrWhiteSpace(album.Artist) ? "Unknown Artist" : album.Artist);
            int.TryParse(album.Year, out var yr);
            var albumDir = Singles.IsSingle(album.Album) ? Singles.Folder : Clean(yr > 0 ? $"{album.Album} ({yr})" : album.Album);
            bool multiDisc = album.Files.Select(f => map.TryGetValue(f, out var rr) ? rr.Disc : 0).DefaultIfEmpty(0).Max() > 1;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int moved = 0;
            foreach (var f in album.Files.ToList())
            {
                try
                {
                    var ext = Path.GetExtension(f);
                    string name = map.TryGetValue(f, out var r) && !string.IsNullOrWhiteSpace(r.Title)
                        ? NameTemplate.Build(template, string.IsNullOrWhiteSpace(r.Artist) ? album.Artist : r.Artist,
                            album.Album, r.Title, r.TrackNo, r.Year > 0 ? r.Year.ToString() : "", Clean, r.Disc, multiDisc) + ext
                        : Path.GetFileName(f);
                    var dest = Path.Combine(lib, artistDir, albumDir, name);
                    int n2 = 2;
                    while (!taken.Add(dest) || File.Exists(dest))
                        dest = Path.Combine(lib, artistDir, albumDir, Path.GetFileNameWithoutExtension(name) + $" ({n2++})" + ext);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    try { File.Move(f, dest); }
                    catch (IOException) { File.Copy(f, dest, false); File.Delete(f); }
                    ops.Add(new UndoJournal.MoveOp(f, dest));
                    moved++;
                }
                catch { }
            }
            foreach (var d in album.SourceDirs) CleanConsumedSourceDir(d, ops);
            _undo.Record($"Replaced in library: {album.Title}", ops);
            _lib.Refresh(nieuw);
            _lib.Refresh(lib);
            Dispatcher.UIThread.Post(() =>
            {
                _all.Remove(album);
                RemoveAlbumFromList(album);
                IsBusy = false;
                ApproveCommand.RaiseCanExecuteChanged();
                Summary = $"{_all.Count} albums · {_all.Sum(a => a.Files.Count)} tracks · {_all.Count(a => !a.IsClean)} need attention";
                UpdatePipeline();
                Status = $"✓ '{album.Title}' replaced: {removedOld} old files → trash, {moved} new files placed (Cmd+Z = undo).";
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
            _undo.Record($"File deleted: {file.FileName}", new List<UndoJournal.MoveOp> { new(file.Path, dest) });
        }
        catch { try { File.Delete(file.Path); } catch { } }
        if (File.Exists(file.Path)) { Status = $"Couldn't delete '{file.FileName}'."; return; }
        DetailFiles.Remove(file);
        _detailTitles.Remove(file);
        FixGrid.RemoveByPath(file.Path);
        if (_detailAlbum?.Files is List<string> albumFiles) albumFiles.Remove(file.Path);
        RebuildDuplicates();
        Status = $"'{file.FileName}' deleted (to _Verwijderd, reversible).";
        if (_detailAlbum != null) RefreshAlbumStats(_detailAlbum);
    }

    private string _nieuwFolder = string.Empty;
    public string NieuwFolder { get => _nieuwFolder; set { if (SetField(ref _nieuwFolder, value)) ScanCommand.RaiseCanExecuteChanged(); } }

    private string _libraryFolder = string.Empty;
    public string LibraryFolder { get => _libraryFolder; set => SetField(ref _libraryFolder, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) { ScanCommand.RaiseCanExecuteChanged(); ApproveCommand.RaiseCanExecuteChanged(); ConfirmPlanCommand?.RaiseCanExecuteChanged(); } }
    }

    private bool _showOnlyIssues;
    public bool ShowOnlyIssues { get => _showOnlyIssues; set { if (SetField(ref _showOnlyIssues, value)) ApplyFilter(); } }

    private string _status = "Set the 'New music' folder (Settings) and scan what Nicotine+ brought in.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _summary = string.Empty;
    public string Summary { get => _summary; private set => SetField(ref _summary, value); }

    public ObservableCollection<StagingAlbumViewModel> Albums { get; } = new();

    // ---- Toetsenbord-cursor (J/K/Enter/A/X in de inbox-lijst) ----
    private StagingAlbumViewModel? _cursorAlbum;
    public StagingAlbumViewModel? CursorAlbum { get => _cursorAlbum; set => SetField(ref _cursorAlbum, value); }

    public int PendingCount => _all.Count;
    public bool HasPending => _all.Count > 0;
    private void RaisePending() { OnPropertyChanged(nameof(PendingCount)); OnPropertyChanged(nameof(HasPending)); }

    public void CursorMove(int delta)
    {
        if (Albums.Count == 0) return;
        int i = _cursorAlbum != null ? Albums.IndexOf(_cursorAlbum) : -1;
        CursorAlbum = Albums[Math.Clamp(i + delta, 0, Albums.Count - 1)];
    }

    public void CursorFix() => CursorAlbum?.FixCommand.Execute(null);
    public void CursorToggle()
    {
        if (CursorAlbum is not { } a) return;
        if (!a.IsSelected && !a.CanSelect) { Status = "Blocked by critical flags — use 'Approve anyway' on the card to override."; return; }
        a.IsSelected = !a.IsSelected;
    }
    public void CursorDelete() => CursorAlbum?.DeleteCommand.Execute(null);

    /// <summary>Remove a card and keep the keyboard cursor on a sensible neighbour.</summary>
    private void RemoveAlbumFromList(StagingAlbumViewModel album)
    {
        int i = Albums.IndexOf(album);
        Albums.Remove(album);
        if (ReferenceEquals(_cursorAlbum, album))
            CursorAlbum = Albums.Count > 0 ? Albums[Math.Clamp(i, 0, Albums.Count - 1)] : null;
        RaisePending();
    }

    private string _pipelineText = "";
    public string PipelineText { get => _pipelineText; private set => SetField(ref _pipelineText, value); }

    public RelayCommand ScanCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectCleanCommand { get; }
    public RelayCommand ConfirmPlanCommand { get; }
    public RelayCommand CancelPlanCommand { get; }

    private void SetSelection(Func<StagingAlbumViewModel, bool> pick)
    {
        foreach (var a in _all) a.IsSelected = pick(a);
        ApproveCommand.RaiseCanExecuteChanged();
    }

    private void Scan()
    {
        if (IsBusy) return;
        var nieuw = NieuwFolder;
        if (!Directory.Exists(nieuw)) { Status = "The 'New music' folder doesn't exist."; return; }
        IsBusy = true;
        ShowDetail = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Scanning inbox…";
        var lib = LibraryFolder;

        Task.Run(() =>
        {
            // Fase 0: beide roots incrementeel verversen en uit de index lezen.
            _lib.Refresh(lib, token);
            _lib.Refresh(nieuw, token);
            var libRows = _lib.Index.AllTracks(lib);
            var have = new HashSet<string>(libRows
                .Select(r => Norm(!string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist) + "|" + Norm(r.Album)));
            var libQ = new Dictionary<string, (double LosslessFrac, double AvgBitrate)>();
            foreach (var grp in libRows.GroupBy(r => Norm(!string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist) + "|" + Norm(r.Album)))
                libQ[grp.Key] = (grp.Count(t => t.Lossless) / (double)grp.Count(), grp.Average(t => (double)t.Bitrate));
            var files = _lib.Index.AllTracks(nieuw);

            var groups = new Dictionary<string, (string Artist, string Album, string Year, List<string> Files,
                int Lossy, int Untagged, int NoCover, int MissingYear, int MissingGenre, HashSet<string> Dirs, HashSet<(int Disc, int Track)> Tracks, bool DupTrack, bool NonStdGenre, string Cover, long BrSum)>();

            foreach (var r in files)
            {
                if (token.IsCancellationRequested) break;
                var artist = !string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist;
                var key = Norm(artist) + "|" + Norm(r.Album);
                if (!groups.TryGetValue(key, out var g))
                    g = (artist, r.Album, r.Year > 0 ? r.Year.ToString() : "", new List<string>(), 0, 0, 0, 0, 0, new HashSet<string>(), new HashSet<(int, int)>(), false, false, "", 0L);

                g.Files.Add(r.Path);
                var dir = Path.GetDirectoryName(r.Path);
                if (dir != null) g.Dirs.Add(dir);
                if (!r.Lossless) g.Lossy++;
                if (r.MissingTags) g.Untagged++;
                if (!r.HasCover) g.NoCover++;
                else if (g.Cover.Length == 0) g.Cover = r.Path;
                if (r.Year <= 0) g.MissingYear++;
                if (string.IsNullOrWhiteSpace(r.Genre)) g.MissingGenre++;
                else if (!Genres.IsStandard(r.Genre)) g.NonStdGenre = true;   // genre niet conform de standaardlijst
                if (r.TrackNo > 0) { if (!g.Tracks.Add((r.Disc, r.TrackNo))) g.DupTrack = true; }   // disc-aware
                if (string.IsNullOrEmpty(g.Year) && r.Year > 0) g.Year = r.Year.ToString();
                g.BrSum += r.Bitrate;

                groups[key] = g;
            }

            var albums = new List<StagingAlbumViewModel>();
            foreach (var g in groups.Values.OrderBy(g => g.Artist).ThenBy(g => g.Album))
            {
                // Singles share a bucket (one folder per download, repeated track #s) — that's not a duplicate album.
                bool dup = !Singles.IsSingle(g.Album) && (g.DupTrack || g.Dirs.Count > 1);
                var key = Norm(g.Artist) + "|" + Norm(g.Album);
                bool already = have.Contains(key);
                bool inboxBetter = false;
                if (already && g.Files.Count > 0 && libQ.TryGetValue(key, out var lq))
                {
                    double inFrac = (g.Files.Count - g.Lossy) / (double)g.Files.Count;
                    double inAvg = g.BrSum / (double)g.Files.Count;
                    inboxBetter = inFrac > lq.LosslessFrac + 0.001
                        || (Math.Abs(inFrac - lq.LosslessFrac) < 0.001 && inAvg > lq.AvgBitrate * 1.1);
                }
                var flags = new List<string>();
                if (g.Lossy > 0) flags.Add($"{g.Lossy} lossy");
                if (g.Untagged > 0) flags.Add($"{g.Untagged} without tags");
                if (g.NoCover > 0) flags.Add("no cover");
                if (g.MissingYear > 0) flags.Add("missing year");
                if (g.MissingGenre > 0) flags.Add("missing genre");
                if (g.NonStdGenre) flags.Add("non-standard genre");
                if (dup) flags.Add("duplicate versions");
                bool clean = g.Lossy == 0 && g.Untagged == 0 && g.NoCover == 0 && g.MissingYear == 0 && g.MissingGenre == 0 && !g.NonStdGenre && !dup;
                if (already) flags.Add(inboxBetter ? "better quality than library — replace?" : "already in library — better quality there");
                var sub = $"{g.Files.Count} tracks" + (string.IsNullOrEmpty(g.Year) ? "" : $"  ·  {g.Year}");
                var vm = new StagingAlbumViewModel(g.Artist, g.Album, g.Year, g.Files, g.Dirs.ToList(),
                    flags, clean, already, inboxBetter, sub, g.Cover.Length > 0 ? g.Cover : null,
                    OpenDetail, DeleteInboxAlbum, ReplaceInLibrary);
                if (DirReceiving(g.Dirs))
                {
                    vm.Receiving = true;
                    var rf = vm.Flags.ToList();
                    rf.Insert(0, "still receiving — download in progress");
                    vm.UpdateStats(rf, false, sub);
                }
                vm.RecomputeCritical();
                albums.Add(vm);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _all.Clear();
                foreach (var a in albums) { a.OnSelectedChanged = () => ApproveCommand.RaiseCanExecuteChanged(); _all.Add(a); }
                ApplyFilter();
                int issues = albums.Count(a => !a.IsClean);
                int ready = albums.Count(a => a.IsSelected);
                Summary = $"{albums.Count} albums · {files.Count} tracks · {issues} need attention";
                Status = token.IsCancellationRequested ? "Scan stopped."
                    : albums.Count == 0 ? "Inbox is empty." : $"{ready} clean and ready to approve; {issues} need attention.";
                IsBusy = false;
                ApproveCommand.RaiseCanExecuteChanged();
                UpdatePipeline();
                LoadCovers();
                ValidateAlbums(albums);                                   // ASN-check tegen MusicBrainz
                if (albums.Any(x => x.Receiving)) ScheduleSettleRescan(); // dok laten settelen
            });
        });
    }

    // ==== Fase B: dock-stabiliteit (niets scannen dat nog binnenkomt) ====
    private static readonly string[] PartialExt = { ".part", ".tmp", ".incomplete", ".filepart", ".crdownload", ".!sk", ".aria2", ".download" };

    private static bool DirReceiving(IEnumerable<string> dirs)
    {
        foreach (var d in dirs)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (PartialExt.Any(e => name.EndsWith(e))) return true;
                    // A brand-new *empty* file is a download placeholder. A complete file with a recent
                    // mtime is NOT — Nicotine+ moves finished downloads in, so they always look recent.
                    try { var fi = new FileInfo(f); if (fi.Length == 0 && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < 45) return true; } catch { }
                }
            }
            catch { }
        }
        return false;
    }

    private DispatcherTimer? _settleTimer;
    private void ScheduleSettleRescan()
    {
        _settleTimer?.Stop();
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _settleTimer.Tick += (_, _) => { _settleTimer?.Stop(); if (!IsBusy && !ShowDetail) Scan(); };
        _settleTimer.Start();
    }

    // ==== Fase A: MusicBrainz-tracklijstvalidatie ====
    private readonly Dictionary<string, MbReleaseMatch?> _mbCache = new();
    private CancellationTokenSource? _valCts;

    private void ValidateAlbums(List<StagingAlbumViewModel> albums)
    {
        _valCts?.Cancel();
        _valCts = new CancellationTokenSource();
        var token = _valCts.Token;
        var work = albums.Where(a => !a.AlreadyInLibrary && a.Artist.Length > 0 && a.Album.Length > 0).ToList();
        if (work.Count == 0) return;
        var nieuw = NieuwFolder;
        Task.Run(async () =>
        {
            var trackNo = new Dictionary<string, int>(StringComparer.Ordinal);
            try { foreach (var r in _lib.Index.AllTracks(nieuw)) trackNo[r.Path] = r.TrackNo; } catch { }
            foreach (var a in work)
            {
                if (token.IsCancellationRequested) return;
                var key = Norm(a.Artist) + "|" + Norm(a.Album);
                MbReleaseMatch? m;
                bool cached;
                lock (_mbCache) cached = _mbCache.TryGetValue(key, out m);
                if (!cached)
                {
                    // fileCount 0: geen bias naar de editie met precies ons aantal tracks — anders maskeer je gaten
                    try { m = await MusicBrainzClient.MatchReleaseAsync(a.Artist, a.Album, 0); }
                    catch { m = null; }
                    lock (_mbCache) _mbCache[key] = m;
                    try { await Task.Delay(700, token); } catch { return; }   // MB-rate-limit
                }
                var album = a; var match = m;
                Dispatcher.UIThread.Post(() => ApplyValidation(album, match, trackNo));
            }
        });
    }

    private void ApplyValidation(StagingAlbumViewModel a, MbReleaseMatch? m, Dictionary<string, int> trackNo)
    {
        if (m == null || m.Tracks.Count == 0)
            a.SetValidation(false, "no MusicBrainz match — verify by ear", new List<string>());
        else
        {
            var present = new HashSet<int>();
            int withNo = 0;
            foreach (var f in a.Files)
                if (trackNo.TryGetValue(f, out var n) && n > 0) { present.Add(n); withNo++; }
            if (withNo == 0)
                a.SetValidation(false, $"MusicBrainz expects {m.Tracks.Count} tracks — can't verify (no track numbers)", new List<string>());
            else
            {
                var missing = m.Tracks.Where(t => t.Position > 0 && !present.Contains(t.Position)).ToList();
                if (missing.Count == 0)
                    a.SetValidation(true, $"complete · {m.Tracks.Count}/{m.Tracks.Count} tracks (MusicBrainz)", new List<string>());
                else if (missing.Count > Math.Max(3, m.Tracks.Count / 2))
                    a.SetValidation(false, $"MusicBrainz edition has {m.Tracks.Count} tracks, you have {present.Count} — possibly another edition", new List<string>());
                else
                {
                    var names = missing.Take(3).Select(t => $"#{t.Position} '{t.Title}'");
                    var flag = "missing track " + string.Join(", ", names) + (missing.Count > 3 ? $" +{missing.Count - 3} more" : "");
                    a.SetValidation(false, $"{m.Tracks.Count - missing.Count}/{m.Tracks.Count} tracks (MusicBrainz)", new List<string> { flag });
                }
            }
        }
        if (ReferenceEquals(_detailAlbum, a)) RebuildChecks(a);
    }

    // ==== Fase A: inbound-checklist in de inspecteur ====
    public ObservableCollection<CheckItem> DetailChecks { get; } = new();

    // ==== Fase D: beste-versie-kiezer (meerdere bronmappen voor één album) ====
    public ObservableCollection<StagingVersionViewModel> DetailVersions { get; } = new();
    public bool HasVersions => DetailVersions.Count > 1;

    private void KeepOnly(StagingVersionViewModel keep)
    {
        var album = _detailAlbum;
        if (album == null || IsBusy) return;
        var others = album.Files.Where(f => !string.Equals(Path.GetDirectoryName(f), keep.FullDir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count == 0) { Status = "This is already the only version."; return; }
        var nieuw = NieuwFolder;
        IsBusy = true;
        Status = $"Keeping '{keep.DirName}' — moving {others.Count} files to the trash…";
        Task.Run(() =>
        {
            string trash;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(nieuw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                trash = parent != null ? Path.Combine(parent, "_Verwijderd (Spindle)") : Path.Combine(nieuw, "_Verwijderd");
            }
            catch { trash = Path.Combine(nieuw, "_Verwijderd"); }
            var ops = new List<UndoJournal.MoveOp>();
            int moved = 0;
            foreach (var f in others)
            {
                try { var dest = MoveInto(f, trash); ops.Add(new UndoJournal.MoveOp(f, dest)); moved++; } catch { }
            }
            foreach (var d in others.Select(Path.GetDirectoryName).Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase))
                CleanConsumedSourceDir(d!, ops);
            _undo.Record($"Kept best version: {album.Title}", ops);
            _lib.Refresh(nieuw);
            Dispatcher.UIThread.Post(() =>
            {
                if (album.Files is List<string> lf) lf.RemoveAll(others.Contains);
                foreach (var f in others) FixGrid.RemoveByPath(f);
                album.SourceDirs.RemoveAll(d => !string.Equals(d, keep.FullDir, StringComparison.OrdinalIgnoreCase));
                IsBusy = false;
                Status = $"\u2713 Kept '{keep.DirName}' — {moved} files to the trash (Cmd+Z = undo).";
                RefreshAlbumStats(album);
                OpenDetail(album);   // detail opnieuw opbouwen uit de verse stand
            });
        });
    }


    private void RebuildChecks(StagingAlbumViewModel a)
    {
        DetailChecks.Clear();
        DetailChecks.Add(new CheckItem("tags complete", !a.Flags.Any(f => f.Contains("without tags"))));
        DetailChecks.Add(new CheckItem("cover art", !a.Flags.Contains("no cover")));
        DetailChecks.Add(new CheckItem("year", !a.Flags.Contains("missing year")));
        DetailChecks.Add(new CheckItem("genre", !a.Flags.Contains("missing genre")));
        DetailChecks.Add(new CheckItem("no duplicate tracks", !a.Flags.Contains("duplicate versions")));
        DetailChecks.Add(new CheckItem("lossless", !a.Flags.Any(f => f.EndsWith(" lossy"))));
        if (a.HasValidation)
        {
            bool? state = null;
            if (a.ValidationOk) state = true;
            else if (a.Flags.Any(f => f.StartsWith("missing track"))) state = false;
            DetailChecks.Add(new CheckItem(a.Validation, state));
        }
        else
            DetailChecks.Add(new CheckItem("completeness check pending (MusicBrainz)…", null));
    }

    // ==== Overbodige inbox-albums (bieb heeft al een betere versie) in één klap weg ====
    public RelayCommand DeleteRedundantCommand { get; }

    private void DeleteRedundant()
    {
        if (IsBusy) return;
        var targets = _all.Where(a => a.AlreadyInLibrary && !a.CanReplace).ToList();
        if (targets.Count == 0) { Status = "No albums flagged 'better quality there' in the inbox."; return; }
        var nieuw = NieuwFolder;
        IsBusy = true;
        Status = $"Removing {targets.Count} redundant album(s) from the inbox…";
        Task.Run(() =>
        {
            string trash;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(nieuw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                trash = parent != null ? Path.Combine(parent, "_Verwijderd (Spindle)") : Path.Combine(nieuw, "_Verwijderd");
            }
            catch { trash = Path.Combine(nieuw, "_Verwijderd"); }
            var ops = new List<UndoJournal.MoveOp>();
            int moved = 0, albums = 0;
            foreach (var album in targets)
            {
                int before = moved;
                foreach (var f in album.Files.ToList())
                {
                    try { var dest = MoveInto(f, trash); ops.Add(new UndoJournal.MoveOp(f, dest)); moved++; } catch { }
                }
                if (moved > before) albums++;
                foreach (var d in album.SourceDirs) CleanConsumedSourceDir(d, ops);
            }
            _undo.Record($"Inbox: removed {albums} album(s) already better in library", ops);
            _lib.Refresh(nieuw);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var a in targets) { _all.Remove(a); RemoveAlbumFromList(a); }
                IsBusy = false;
                ApproveCommand.RaiseCanExecuteChanged();
                UpdatePipeline();
                Summary = $"{_all.Count} albums · {_all.Sum(x => x.Files.Count)} tracks · {_all.Count(x => !x.IsClean)} need attention";
                Status = $"✓ {albums} album(s) ({moved} files) moved to the trash — your library versions are better. Cmd+Z = undo.";
            });
        });
    }

    // ==== Fase C: ontvangstbon ====
    private string _lastReceipt = "";
    public string LastReceipt { get => _lastReceipt; private set { if (SetField(ref _lastReceipt, value)) OnPropertyChanged(nameof(HasReceipt)); } }
    public bool HasReceipt => _lastReceipt.Length > 0;
    private RelayCommand? _dismissReceipt;
    public RelayCommand DismissReceiptCommand => _dismissReceipt ??= new RelayCommand(() => LastReceipt = "");

    private static string VolRoot(string p)
    {
        try
        {
            var full = Path.GetFullPath(p);
            if (full.StartsWith("/Volumes/", StringComparison.Ordinal))
            {
                var ix = full.IndexOf('/', 9);
                return ix > 0 ? full[..ix] : full;
            }
        }
        catch { }
        return "/";
    }

    private static string FmtBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024.0:0} kB";

    private void ApplyFilter()
    {
        Albums.Clear();
        foreach (var a in _all)
            if (!ShowOnlyIssues || !a.IsClean)
                Albums.Add(a);
        if (_cursorAlbum != null && !Albums.Contains(_cursorAlbum)) CursorAlbum = null;
        RaisePending();
    }

    private void UpdatePipeline()
    {
        try
        {
            var lib = LibraryFolder;
            if (string.IsNullOrWhiteSpace(lib) || !Directory.Exists(lib)) { PipelineText = ""; return; }
            var h = _lib.Index.HealthCounts(lib);
            int ready = _all.Count(a => a.IsSelected), att = _all.Count(a => !a.IsClean);
            PipelineText = $"Inbox {_all.Count} albums ({ready} clean · {att} attention)    →    Library {h.Albums} albums · {h.Files:N0} tracks";
        }
        catch { PipelineText = ""; }
    }

    private readonly ConcurrentDictionary<string, Bitmap> _coverCache = new();
    private CancellationTokenSource? _coverCts;

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
                if (a.Cover != null || a.CoverPath == null) continue;
                var key = Norm(a.Artist) + "|" + Norm(a.Album);
                if (_coverCache.TryGetValue(key, out var hit)) { Dispatcher.UIThread.Post(() => a.Cover = hit); continue; }
                try
                {
                    var t = new Track(a.CoverPath);
                    var data = t.EmbeddedPictures.Count > 0 ? t.EmbeddedPictures[0].PictureData : null;
                    if (data == null || data.Length == 0) continue;
                    using var ms = new MemoryStream(data);
                    var bmp = Bitmap.DecodeToWidth(ms, 160);
                    _coverCache[key] = bmp;
                    Dispatcher.UIThread.Post(() => a.Cover = bmp);
                }
                catch { }
            }
        });
    }

    // ---- Goedkeuren 2.0 (fase 3): diff-preview → albums landen direct volgens template in de bieb ----
    public sealed class PlanItemViewModel
    {
        public string FromPath { get; }
        public string ToPath { get; }
        public string FromText { get; }
        public string ToText { get; }
        public PlanItemViewModel(string fromPath, string toPath, string fromText, string toText)
        { FromPath = fromPath; ToPath = toPath; FromText = fromText; ToText = toText; }
    }

    public ObservableCollection<PlanItemViewModel> PlanItems { get; } = new();
    private List<StagingAlbumViewModel> _planAlbums = new();

    private bool _showPlan;
    public bool ShowPlan { get => _showPlan; private set => SetField(ref _showPlan, value); }

    private string _planSummary = "";
    public string PlanSummary { get => _planSummary; private set => SetField(ref _planSummary, value); }

    // ApproveCommand → bouwt het verplaatsplan en toont de diff-preview.
    private void Approve()
    {
        if (IsBusy) return;
        var lib = LibraryFolder;
        if (string.IsNullOrWhiteSpace(lib) || !Directory.Exists(lib)) { Status = "Set your music library first (Settings)."; return; }
        var selected = _all.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) { Status = "Nothing selected."; return; }

        var map = new Dictionary<string, IndexedTrack>(StringComparer.Ordinal);
        try { foreach (var r in _lib.Index.AllTracks(NieuwFolder)) map[r.Path] = r; } catch { }
        var template = _template();
        var nieuwRoot = NieuwFolder;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        PlanItems.Clear();
        _planAlbums = selected;
        foreach (var album in selected)
        {
            var artistDir = Clean(string.IsNullOrWhiteSpace(album.Artist) ? "Unknown Artist" : album.Artist);
            int.TryParse(album.Year, out var yr);
            var albumDir = Singles.IsSingle(album.Album) ? Singles.Folder : Clean(yr > 0 ? $"{album.Album} ({yr})" : album.Album);
            bool multiDisc = album.Files.Select(f => map.TryGetValue(f, out var rr) ? rr.Disc : 0).DefaultIfEmpty(0).Max() > 1;
            foreach (var f in album.Files)
            {
                var ext = Path.GetExtension(f);
                string name;
                if (map.TryGetValue(f, out var r) && !string.IsNullOrWhiteSpace(r.Title))
                    name = NameTemplate.Build(template, string.IsNullOrWhiteSpace(r.Artist) ? album.Artist : r.Artist,
                        album.Album, r.Title, r.TrackNo, r.Year > 0 ? r.Year.ToString() : "", Clean, r.Disc, multiDisc) + ext;
                else
                    name = Path.GetFileName(f);
                var dest = Path.Combine(lib, artistDir, albumDir, name);
                int n2 = 2;
                while (!taken.Add(dest) || File.Exists(dest))
                    dest = Path.Combine(lib, artistDir, albumDir, Path.GetFileNameWithoutExtension(name) + $" ({n2++})" + ext);

                string relFrom;
                try { relFrom = Path.GetRelativePath(nieuwRoot, f); } catch { relFrom = Path.GetFileName(f); }
                if (relFrom.StartsWith("..")) relFrom = Path.GetFileName(f);
                var relTo = Path.Combine(artistDir, albumDir, Path.GetFileName(dest));
                PlanItems.Add(new PlanItemViewModel(f, dest, relFrom, relTo));
            }
        }
        PlanSummary = $"{selected.Count} album(s) · {PlanItems.Count} files  →  {lib}";
        ConfirmPlanCommand.RaiseCanExecuteChanged();
        ShowPlan = true;
    }

    private void ExecutePlan()
    {
        if (IsBusy || PlanItems.Count == 0) return;
        var plan = PlanItems.ToList();
        var selected = _planAlbums;
        IsBusy = true;
        ShowPlan = false;
        Status = $"Moving {plan.Count} files…";

        Task.Run(async () =>
        {
            // Vrije-ruimte-check bij cross-volume approve (zelfde volume = rename, kost geen ruimte).
            try
            {
                if (!string.Equals(VolRoot(plan[0].FromPath), VolRoot(LibraryFolder), StringComparison.OrdinalIgnoreCase))
                {
                    long need = 0;
                    foreach (var it in plan) { try { need += new FileInfo(it.FromPath).Length; } catch { } }
                    long free = 0;
                    try { free = new DriveInfo(VolRoot(LibraryFolder)).AvailableFreeSpace; } catch { }
                    if (free > 0 && need > free)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            IsBusy = false;
                            Status = $"⚠ Not enough free space on the library volume — needs ~{FmtBytes(need)}, only {FmtBytes(free)} free.";
                        });
                        return;
                    }
                }
            }
            catch { }
            int moved = 0;
            var ops = new List<UndoJournal.MoveOp>();
            var dirs = new HashSet<string>();
            foreach (var item in plan)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.ToPath)!);
                    try { File.Move(item.FromPath, item.ToPath); }
                    catch (IOException) { File.Copy(item.FromPath, item.ToPath, false); File.Delete(item.FromPath); }
                    ops.Add(new UndoJournal.MoveOp(item.FromPath, item.ToPath));
                    moved++;
                }
                catch { }
            }
            foreach (var a in selected) foreach (var d in a.SourceDirs) dirs.Add(d);
            // opruimen: bronmappen die helemaal leeg zijn van audio → naar prullenbak (incl. hoezen/.nfo).
            foreach (var d in dirs) CleanConsumedSourceDir(d, ops);
            // Putaway-verificatie: staat alles echt op zijn plek?
            long bytes = 0; int verified = 0;
            foreach (var op in ops) { try { var fi = new FileInfo(op.To); if (fi.Exists) { verified++; bytes += fi.Length; } } catch { } }
            int conflicts = ops.Count(o =>
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(o.To), @" \(\d+\)$")
                && !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(o.From), @" \(\d+\)$"));
            int failed = plan.Count - moved;
            _undo.Record($"Approve: {selected.Count} album(s) to library", ops);
            _lib.Refresh(NieuwFolder);
            _lib.Refresh(LibraryFolder);

            // Optioneel: lyrics ophalen voor wat zojuist de bibliotheek in ging (online, LRCLIB).
            if (CleanupOptions.FetchLyricsOnApprove)
            {
                int li = 0;
                foreach (var op in ops)
                {
                    var snap = ++li;
                    Dispatcher.UIThread.Post(() => Status = $"Fetching lyrics… {snap}/{ops.Count}");
                    try { await Lyrics.ApplyToFileAsync(op.To); } catch { }
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var a in selected) { _all.Remove(a); RemoveAlbumFromList(a); }
                PlanItems.Clear();
                IsBusy = false;
                ApproveCommand.RaiseCanExecuteChanged();
                UpdatePipeline();
                Status = $"✓ {moved} tracks placed neatly in the library (Cmd+Z = undo).";
                LastReceipt = failed == 0 && verified == moved
                    ? $"✓ Received: {selected.Count} album(s) · {verified} tracks · {FmtBytes(bytes)} placed{(conflicts > 0 ? $" · {conflicts} name conflict(s) auto-suffixed" : "")} — Cmd+Z undoes everything"
                    : $"⚠ Receipt check: {verified}/{plan.Count} tracks verified in place{(failed > 0 ? $", {failed} failed to move" : "")} — inspect the library before continuing";
                CursorAlbum = Albums.FirstOrDefault();   // doorwerk-modus: cursor meteen op de volgende
            });
        });
    }

    private static string Clean(string s)
    {
        s = (s ?? "").Trim().Replace('/', '-').Replace('\\', '-');
        s = Regex.Replace(s, "[:*?\"<>|\\x00-\\x1f]", "");
        s = Regex.Replace(s, "\\s+", " ").Trim().TrimEnd('.', ' ');
        return s.Length > 0 ? s : "Unknown";
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

/// <summary>One inbound-checklist chip in the inbox inspector (ok / warn / pending).</summary>
public sealed class CheckItem
{
    public string Text { get; }
    public bool IsOk { get; }
    public bool IsWarn { get; }

    public CheckItem(string label, bool? ok)
    {
        IsOk = ok == true;
        IsWarn = ok == false;
        Text = (ok == true ? "✓  " : ok == false ? "✕  " : "…  ") + label;
    }
}

/// <summary>One received version (source folder) of an album in the inbox inspector.</summary>
public sealed class StagingVersionViewModel
{
    public string FullDir { get; }
    public string DirName { get; }
    public string Stats { get; }
    public bool IsBest { get; set; }
    public RelayCommand KeepCommand { get; }

    public StagingVersionViewModel(string dir, string stats, Action<StagingVersionViewModel> onKeep)
    {
        FullDir = dir;
        DirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Stats = stats;
        KeepCommand = new RelayCommand(() => onKeep(this));
    }
}
