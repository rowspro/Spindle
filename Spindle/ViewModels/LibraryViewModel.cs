using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace Spindle.ViewModels;

/// <summary>One album with a quality issue, shown in the "Problematische albums" table (Gezondheid).</summary>
public class ProblemAlbumViewModel : ViewModelBase
{
    public string Artist { get; }
    public string Album { get; }
    public string Issue { get; }
    public string Title => string.IsNullOrEmpty(Artist) ? Album : $"{Artist} — {Album}";

    public IReadOnlyList<string> Files { get; }
    private readonly Action<IReadOnlyList<string>, string> _onEdit;
    public RelayCommand FixCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public ProblemAlbumViewModel(string artist, string album, string issue, IReadOnlyList<string> files,
        Action<IReadOnlyList<string>, string> onEdit, Action<ProblemAlbumViewModel> onDelete)
    {
        Artist = artist; Album = album; Issue = issue; Files = files; _onEdit = onEdit;
        FixCommand = new RelayCommand(() => _onEdit(Files, $"{Title} — {issue}. Complete / set cover and approve."));
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }
}

/// <summary>One album row in a Gezondheid detail list (FLAC-upgrades / Lossy) with a "replace with FLAC" action.</summary>
public class HealthAlbumViewModel : ViewModelBase
{
    public string Title { get; }
    public string Detail { get; }
    public RelayCommand UpgradeCommand { get; }
    public HealthAlbumViewModel(string title, string detail, Action onUpgrade)
    {
        Title = title; Detail = detail; UpgradeCommand = new RelayCommand(onUpgrade);
    }
}

/// <summary>Album node in the "Alle albums" tree (children are track filenames).</summary>
public class HealthAlbumNode
{
    public string Header { get; }
    public List<string> Tracks { get; }
    /// <summary>Full file paths for this album (for "open in Metadata editor"); Tracks holds display names only.</summary>
    public List<string> Paths { get; }
    public HealthAlbumNode(string header, List<string> tracks, List<string> paths) { Header = header; Tracks = tracks; Paths = paths; }
}

/// <summary>Artist node in the "Alle albums" tree (children are albums).</summary>
public class HealthArtistNode
{
    public string Name { get; }
    public List<HealthAlbumNode> Albums { get; }
    public HealthArtistNode(string name, List<HealthAlbumNode> albums) { Name = name; Albums = albums; }
}

/// <summary>
/// Library health: scans your music for issues (missing tags, no cover art, lossy vs lossless) and
/// offers repair actions — fetch missing album covers (MusicBrainz/Cover Art Archive) and queue FLAC
/// upgrades for MP3 albums via Soulseek.
/// </summary>
public class LibraryViewModel : ViewModelBase
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    private static readonly string[] Lossless = { ".flac", ".wav", ".aiff", ".aif" };

    private sealed class Album
    {
        public string Artist = "";
        public string Name = "";
        public List<string> Files = new();
        public bool HasCover;
        public bool AllLossy;
        public int Untagged;
        public int LossyCount;
        public string Query => $"{Artist} - {Name}";
    }

    private readonly Action<IReadOnlyList<string>, string> _onEdit;
    private readonly List<Album> _albums = new();
    private List<string> _noTagFiles = new();
    private List<string> _noCoverFiles = new();
    private CancellationTokenSource? _cts;

    private readonly LibraryService _lib;
    private readonly UndoJournal _undo;

    public LibraryViewModel(Action<IReadOnlyList<string>, string> onEdit, LibraryService lib, UndoJournal undo, Func<string>? template = null, Action? openLibrary = null)
    {
        _onEdit = onEdit;
        _lib = lib;
        _undo = undo;
        ScanCommand = new RelayCommand(Scan, () => !IsBusy && !string.IsNullOrWhiteSpace(LibraryFolder));
        RepairCoversCommand = new RelayCommand(RepairCovers, () => !IsBusy && _albums.Count > 0);
        FetchLyricsCommand = new RelayCommand(FetchLyrics, () => !IsBusy && _albums.Count > 0);
        FindUpgradesCommand = new RelayCommand(FindUpgrades, () => !IsBusy && _albums.Count > 0);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        EditNoTagsCommand = new RelayCommand(
            () => _onEdit(_noTagFiles, $"{_noTagFiles.Count} tracks without (complete) tags — fill in and approve."),
            () => _noTagFiles.Count > 0);
        EditNoCoverCommand = new RelayCommand(
            () => _onEdit(_noCoverFiles, $"{_noCoverFiles.Count} tracks from albums without art — set a cover (or Auto-fill) and approve."),
            () => _noCoverFiles.Count > 0);
        ShowFilesCommand = new RelayCommand(() => openLibrary?.Invoke());   // Files-statknop → Library-tab
        ShowAlbumsCommand = new RelayCommand(ShowAlbums);
        ShowUpgradesCommand = new RelayCommand(ShowUpgrades);
        ShowLossyCommand = new RelayCommand(ShowLossy);
        CloseDetailCommand = new RelayCommand(() => DetailKind = string.Empty);
        Doctor = new DoctorViewModel(lib, undo, () => LibraryFolder,
            template ?? (() => "{artist} - {album} - {track} {title}"), onEdit);
        OpenDoctorCommand = new RelayCommand(() =>
        {
            DetailTitle = "Library Doctor";
            DetailKind = "doctor";
            if (!Doctor.HasRun) Doctor.RunCommand.Execute(null);
        });
    }

    public DoctorViewModel Doctor { get; }
    public RelayCommand OpenDoctorCommand { get; }

    // ---- Detail drill-down (clicking a stat box) ----
    public ObservableCollection<HealthArtistNode> AlbumTree { get; } = new();
    public ObservableCollection<FormatSegment> Formats { get; } = new();
    public ObservableCollection<HealthAlbumViewModel> DetailAlbums { get; } = new();

    private string _detailKind = string.Empty;     // "", "files", "albums", "list"
    public string DetailKind
    {
        get => _detailKind;
        private set
        {
            if (SetField(ref _detailKind, value))
            {
                OnPropertyChanged(nameof(ShowDetail));
                OnPropertyChanged(nameof(ShowDashboard));
                OnPropertyChanged(nameof(ShowAlbumsPanel));
                OnPropertyChanged(nameof(ShowListPanel));
                OnPropertyChanged(nameof(ShowDoctorPanel));
            }
        }
    }
    public bool ShowDetail => _detailKind.Length > 0;
    public bool ShowDashboard => _detailKind.Length == 0;
    public bool ShowAlbumsPanel => _detailKind == "albums";
    public bool ShowListPanel => _detailKind == "list";
    public bool ShowDoctorPanel => _detailKind == "doctor";

    private string _detailTitle = string.Empty;
    public string DetailTitle { get => _detailTitle; private set => SetField(ref _detailTitle, value); }

    public RelayCommand ShowFilesCommand { get; }
    public RelayCommand ShowAlbumsCommand { get; }
    public RelayCommand ShowUpgradesCommand { get; }
    public RelayCommand ShowLossyCommand { get; }
    public RelayCommand CloseDetailCommand { get; }

    private void ShowAlbums()
    {
        AlbumTree.Clear();
        foreach (var g in _albums.GroupBy(a => a.Artist).OrderBy(g => g.Key))
        {
            var albums = g.OrderBy(a => a.Name)
                .Select(a => new HealthAlbumNode($"{a.Name}  ·  {a.Files.Count} tracks",
                    a.Files.Select(x => Path.GetFileName(x) ?? x).OrderBy(x => x).ToList(),
                    a.Files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
            AlbumTree.Add(new HealthArtistNode(string.IsNullOrWhiteSpace(g.Key) ? "(unknown)" : g.Key, albums));
        }
        DetailTitle = $"All albums ({_albums.Count})";
        DetailKind = "albums";
    }

    private void ShowUpgrades()
    {
        DetailAlbums.Clear();
        foreach (var a in _albums.Where(a => a.AllLossy).OrderBy(a => a.Artist).ThenBy(a => a.Name))
        {
            var q = a.Query; var name = a.Name;
            DetailAlbums.Add(new HealthAlbumViewModel($"{a.Artist} — {a.Name}", $"{a.Files.Count} tracks · fully lossy",
                () => Status = $"'{name}': put it on your Wantlist and grab a FLAC version with your download client."));
        }
        DetailTitle = $"FLAC upgrades possible ({DetailAlbums.Count})";
        DetailKind = "list";
    }

    private void ShowLossy()
    {
        DetailAlbums.Clear();
        foreach (var a in _albums.Where(a => a.LossyCount > 0).OrderByDescending(a => a.LossyCount).ThenBy(a => a.Artist))
        {
            var q = a.Query; var name = a.Name;
            DetailAlbums.Add(new HealthAlbumViewModel($"{a.Artist} — {a.Name}", $"{a.LossyCount} lossy file(s)",
                () => Status = $"'{name}': put it on your Wantlist and grab a FLAC version with your download client."));
        }
        DetailTitle = $"Lossy files ({LossyFileCount})";
        DetailKind = "list";
    }

    private string _libraryFolder = string.Empty;
    public string LibraryFolder { get => _libraryFolder; set { if (SetField(ref _libraryFolder, value)) ScanCommand.RaiseCanExecuteChanged(); } }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                RepairCoversCommand.RaiseCanExecuteChanged();
                FindUpgradesCommand.RaiseCanExecuteChanged();
                FetchLyricsCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _status = "Set your music library (Settings) and scan for issues.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    // Individual figures for the stat cards (Gezondheid).
    private int _fileCount, _albumCount, _noTagCount, _noCoverCount, _lossyFileCount, _upgradeCount;
    public int FileCount { get => _fileCount; private set => SetField(ref _fileCount, value); }
    public int AlbumCount { get => _albumCount; private set => SetField(ref _albumCount, value); }
    public int NoTagCount { get => _noTagCount; private set => SetField(ref _noTagCount, value); }
    public int NoCoverCount { get => _noCoverCount; private set => SetField(ref _noCoverCount, value); }
    public int LossyFileCount { get => _lossyFileCount; private set => SetField(ref _lossyFileCount, value); }
    public int UpgradeAlbumCount { get => _upgradeCount; private set => SetField(ref _upgradeCount, value); }

    private bool _hasScanned;
    public bool HasScanned { get => _hasScanned; private set => SetField(ref _hasScanned, value); }

    private int _healthScore;
    public int HealthScore { get => _healthScore; private set => SetField(ref _healthScore, value); }

    public ObservableCollection<ProblemAlbumViewModel> ProblemAlbums { get; } = new();

    public RelayCommand ScanCommand { get; }
    public RelayCommand RepairCoversCommand { get; }
    public RelayCommand FetchLyricsCommand { get; }
    public RelayCommand FindUpgradesCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand EditNoTagsCommand { get; }
    public RelayCommand EditNoCoverCommand { get; }

    private void Scan()
    {
        if (IsBusy) return;
        var lib = LibraryFolder;
        if (!Directory.Exists(lib)) { Status = "Library folder doesn't exist."; return; }
        IsBusy = true;
        DetailKind = string.Empty; // terug naar het overzicht bij (her)scannen
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Scanning library…";

        Task.Run(() =>
        {
            // Fase 0: lees uit de persistente index (incrementele refresh — alleen gewijzigde bestanden).
            _lib.Refresh(lib, token);
            var files = _lib.Index.AllTracks(lib);

            int noTags = files.Count(r => r.MissingTags);
            int lossyFiles = files.Count(r => !r.Lossless);
            var noTagBag = new ConcurrentBag<string>(files.Where(r => r.MissingTags).Select(r => r.Path));
            var groups = new ConcurrentDictionary<string, Album>();

            foreach (var r in files)
            {
                if (token.IsCancellationRequested) break;
                var artist = !string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist;
                var a = groups.GetOrAdd(Norm(artist) + "|" + Norm(r.Album),
                    _ => new Album { Artist = artist, Name = r.Album, AllLossy = true });
                a.Files.Add(r.Path);
                if (r.HasCover) a.HasCover = true;
                if (r.Lossless) a.AllLossy = false; else a.LossyCount++;
                if (r.MissingTags) a.Untagged++;
            }

            var albums = groups.Values.Where(a => a.Files.Count > 0).ToList();
            int noCover = albums.Count(a => !a.HasCover);
            int lossyAlbums = albums.Count(a => a.AllLossy);
            var noCoverFiles = albums.Where(a => !a.HasCover).SelectMany(a => a.Files).ToList();
            var noTagFiles = noTagBag.ToList();

            var problemData = albums
                .Select(a => (a.Artist, a.Name, a.Files, IReadOnlyList: (IReadOnlyList<string>)a.Files,
                              sev: (a.HasCover ? 0 : 1) + (a.AllLossy ? 1 : 0) + (a.Untagged > 0 ? 1 : 0),
                              issue: string.Join(" · ", new[]
                              {
                                  a.HasCover ? null : "no cover",
                                  a.AllLossy ? "fully lossy" : null,
                                  a.Untagged > 0 ? $"{a.Untagged} without tags" : null
                              }.Where(s => s != null))))
                .Where(x => x.sev > 0)
                .OrderByDescending(x => x.sev).ThenByDescending(x => x.Files.Count)
                .Take(40).ToList();

            double tagFrac = files.Count > 0 ? (double)noTags / files.Count : 0;
            double lossyFrac = files.Count > 0 ? (double)lossyFiles / files.Count : 0;
            double coverFrac = albums.Count > 0 ? (double)noCover / albums.Count : 0;
            int score = albums.Count == 0 ? 0 : Math.Clamp((int)Math.Round(100 * (1 - 0.4 * tagFrac - 0.3 * coverFrac - 0.3 * lossyFrac)), 0, 100);

            // Formaatverdeling voor de mix-balk (ALAC = m4a + lossless; m4a lossy = AAC -> Other).
            int cFlac = files.Count(t => t.Format == "FLAC");
            int cMp3 = files.Count(t => t.Format == "MP3");
            int cWav = files.Count(t => t.Format == "WAV");
            int cAlac = files.Count(t => t.Format == "M4A" && t.Lossless);
            int cOther = files.Count - cFlac - cMp3 - cWav - cAlac;

            Dispatcher.UIThread.Post(() =>
            {
                _albums.Clear(); _albums.AddRange(albums);
                _noTagFiles = noTagFiles;
                _noCoverFiles = noCoverFiles;
                FileCount = files.Count;
                AlbumCount = albums.Count;
                NoTagCount = noTags;
                NoCoverCount = noCover;
                LossyFileCount = lossyFiles;
                UpgradeAlbumCount = lossyAlbums;
                HealthScore = score;
                Formats.Clear();
                if (files.Count > 0)
                {
                    void AddSeg(string n, int c, string hex) { if (c > 0) Formats.Add(new FormatSegment(n, c, 100.0 * c / files.Count, hex)); }
                    AddSeg("FLAC", cFlac, "#3568C4");
                    AddSeg("MP3", cMp3, "#FFB570");
                    AddSeg("WAV", cWav, "#2EC4B6");
                    AddSeg("ALAC", cAlac, "#9D7AFF");
                    AddSeg("Other", cOther, "#8A8FA0");
                }
                ProblemAlbums.Clear();
                foreach (var p in problemData)
                    ProblemAlbums.Add(new ProblemAlbumViewModel(p.Artist, p.Name, p.issue, p.IReadOnlyList, _onEdit, DeleteAlbum));
                HasScanned = true;
                IsBusy = false;
                RepairCoversCommand.RaiseCanExecuteChanged();
                FindUpgradesCommand.RaiseCanExecuteChanged();
                FetchLyricsCommand.RaiseCanExecuteChanged();
                EditNoTagsCommand.RaiseCanExecuteChanged();
                EditNoCoverCommand.RaiseCanExecuteChanged();
                Status = token.IsCancellationRequested ? "Scan stopped." : $"Scan done — health {score}%. Click a card or album to fix.";
            });
        });
    }

    // Safe delete: move the album's files to a trash folder OUTSIDE the library (a sibling on the same
    // volume), so re-organising/sorting the library never picks them back up. Reversible: move them back.
    private void DeleteAlbum(ProblemAlbumViewModel album)
    {
        var lib = LibraryFolder;
        if (string.IsNullOrWhiteSpace(lib) || !Directory.Exists(lib)) { Status = "No valid library folder."; return; }

        var libFull = Path.GetFullPath(lib).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(libFull);
        // Sibling of the library (same volume → File.Move works, and it's outside any library scan).
        var trashRoot = parent != null
            ? Path.Combine(parent, "_Verwijderd (Spindle)")
            : Path.Combine(libFull, "_Verwijderd");

        int moved = 0;
        var ops = new List<UndoJournal.MoveOp>();
        foreach (var f in album.Files)
        {
            try
            {
                var rel = Path.GetRelativePath(libFull, f);
                if (rel.StartsWith("..") || Path.IsPathRooted(rel)) rel = Path.GetFileName(f);
                var dest = Path.Combine(trashRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(f, dest);
                ops.Add(new UndoJournal.MoveOp(f, dest));
                moved++;
            }
            catch { }
        }
        _undo.Record($"Album deleted: {album.Title}", ops);
        ProblemAlbums.Remove(album);
        FileCount = Math.Max(0, FileCount - album.Files.Count);
        AlbumCount = Math.Max(0, AlbumCount - 1);
        Status = $"'{album.Title}' moved to '{trashRoot}' ({moved} files) — outside the library, reversible.";
    }

    // Bulk: fetch lyrics for every track in the library (online LRCLIB) — sidecar .lrc + embedded plain.
    private void FetchLyrics()
    {
        if (IsBusy) return;
        var files = _albums.SelectMany(a => a.Files).ToList();
        if (files.Count == 0) { Status = "Scan the library first."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            int got = 0, done = 0;
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 8),
                CancellationToken = token
            };
            try
            {
                await Parallel.ForEachAsync(files, po, async (f, ct) =>
                {
                    bool ok = false;
                    try { ok = await Lyrics.ApplyToFileAsync(f, ct); } catch { }
                    if (ok) Interlocked.Increment(ref got);
                    var snap = Interlocked.Increment(ref done);
                    if (snap % 5 == 0 || snap == 1)
                    {
                        var g0 = got;
                        Dispatcher.UIThread.Post(() => Status = $"Fetching lyrics… {snap}/{files.Count} ({g0} found)");
                    }
                });
            }
            catch (OperationCanceledException) { }
            var g = got;
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                Status = token.IsCancellationRequested
                    ? $"Lyrics stopped — {g} found so far."
                    : $"✓ Lyrics: {g} of {files.Count} tracks now have lyrics.";
            });
        });
    }

    private void RepairCovers()
    {
        if (IsBusy) return;
        var todo = _albums.Where(a => !a.HasCover).ToList();
        if (todo.Count == 0) { Status = "All albums already have a cover."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            int fixedCount = 0;
            for (int i = 0; i < todo.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var a = todo[i];
                var snap = i + 1;
                Dispatcher.UIThread.Post(() => Status = $"Covers… {snap}/{todo.Count}  ({a.Artist} — {a.Name})");
                try
                {
                    var mb = await MusicBrainzClient.MatchReleaseAsync(a.Artist, a.Name, a.Files.Count);
                    if (mb != null && !string.IsNullOrEmpty(mb.ReleaseId))
                    {
                        var art = await MusicBrainzClient.GetCoverArtAsync(mb.ReleaseId);
                        if (art != null)
                        {
                            try
                            {
                                var dir = Path.GetDirectoryName(a.Files[0]);
                                if (dir != null) await File.WriteAllBytesAsync(Path.Combine(dir, "folder.jpg"), art, token);
                            }
                            catch { }
                            foreach (var f in a.Files)
                            {
                                if (token.IsCancellationRequested) break;
                                try
                                {
                                    var t = new Track(f);
                                    if (t.EmbeddedPictures.Count == 0)
                                    {
                                        t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(art));
                                        t.Save();
                                    }
                                }
                                catch { }
                            }
                            a.HasCover = true;
                            fixedCount++;
                        }
                    }
                }
                catch { }
                try { await Task.Delay(350, token); } catch { break; }
            }
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                Status = $"{fixedCount} album(s) got a cover.";
            });
        });
    }

    private void FindUpgrades()
    {
        var n = _albums.Count(a => a.AllLossy);
        Status = n == 0
            ? "No fully-lossy albums found."
            : $"{n} fully-lossy album(s) — add them to your Wantlist and re-download in FLAC.";
    }

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
}

/// <summary>One slice of the Health "format mix" bar (FLAC/MP3/WAV/ALAC/Other).</summary>
public sealed class FormatSegment
{
    public string Name { get; }
    public int Count { get; }
    public double Pct { get; }
    public Avalonia.Media.IBrush Brush { get; }
    public string Label => $"{Name}  {Pct:0.#}%  ·  {Count:N0}";

    public FormatSegment(string name, int count, double pct, string hex)
    {
        Name = name; Count = count; Pct = pct;
        Brush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex));
    }
}
