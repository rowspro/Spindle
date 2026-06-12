using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace Spindle.ViewModels;

/// <summary>
/// iTunes-like selection to copy music to a Rockbox iPod (just files on disk). Scan a library, filter
/// by genre / search, tick artists/albums, and transfer to the iPod. Shows what's already on the iPod
/// so you don't transfer twice. Files are copied as-is by default (fast — Rockbox plays FLAC/MP3/AAC);
/// optional ALAC conversion only re-encodes FLAC/WAV/AIFF. Tag scan + transfer use all cores.
/// </summary>
public class SyncViewModel : ViewModelBase
{
    private const string AllGenres = "Alle genres";
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    // Only these need re-encoding for ALAC; everything else (mp3/aac/m4a) is copied untouched — like iTunes.
    private static readonly string[] ConvertExt = { ".flac", ".wav", ".aiff", ".aif" };

    public ObservableCollection<ArtistNodeViewModel> ArtistTree { get; } = new();
    public ObservableCollection<string> Genres { get; } = new() { AllGenres };
    public ObservableCollection<FailureViewModel> Failures { get; } = new();

    private readonly List<AlbumEntryViewModel> _all = new();
    private CancellationTokenSource? _cts;

    public SyncViewModel()
    {
        ScanCommand = new RelayCommand(Scan, () => !IsBusy && !string.IsNullOrWhiteSpace(LibraryFolder));
        AdoptCommand = new RelayCommand(AdoptIpod, () => !IsBusy);
        CleanupCommand = new RelayCommand(CleanupPreview, () => !IsBusy);
        ConfirmCleanupCommand = new RelayCommand(CleanupExecute, () => !IsBusy && ShowCleanup);
        CancelCleanupCommand = new RelayCommand(() => { ShowCleanup = false; _cleanupFiles.Clear(); _cleanupDirs.Clear(); CleanupItems.Clear(); });
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SelectAllCommand = new RelayCommand(() => { foreach (var n in ArtistTree) foreach (var a in n.Albums) a.Selected = true; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var a in _all) a.Selected = false; });
        TransferCommand = new RelayCommand(Transfer, () => !IsBusy && !string.IsNullOrWhiteSpace(IpodFolder));
        MakePlaylistsCommand = new RelayCommand(MakePlaylists, () => !IsBusy && !string.IsNullOrWhiteSpace(IpodFolder));
        Failures.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFailures));
    }

    public bool HasFailures => Failures.Count > 0;

    private string _libraryFolder = string.Empty;
    public string LibraryFolder { get => _libraryFolder; set { if (SetField(ref _libraryFolder, value)) ScanCommand.RaiseCanExecuteChanged(); } }

    private string _ipodFolder = string.Empty;
    public string IpodFolder
    {
        get => _ipodFolder;
        set
        {
            if (SetField(ref _ipodFolder, value))
            {
                TransferCommand.RaiseCanExecuteChanged();
                MakePlaylistsCommand.RaiseCanExecuteChanged();
                if (_all.Count > 0) MarkIpodPresence();
                UpdateDevice();
            }
        }
    }

    // ---- Device card (capacity of the chosen iPod folder's volume) ----
    private string _deviceText = "Pick the connected iPod folder to see free space.";
    public string DeviceText { get => _deviceText; private set => SetField(ref _deviceText, value); }

    private bool _hasDevice;
    public bool HasDevice { get => _hasDevice; private set => SetField(ref _hasDevice, value); }

    private int _usedPercent;
    public int UsedPercent { get => _usedPercent; private set => SetField(ref _usedPercent, value); }

    private void UpdateDevice()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(IpodFolder) || !Directory.Exists(IpodFolder))
            {
                DeviceText = "Pick the connected iPod folder to see free space.";
                HasDevice = false; UsedPercent = 0; return;
            }
            var d = FindVolume(IpodFolder);
            if (d == null) { DeviceText = IpodFolder; HasDevice = true; UsedPercent = 0; return; }
            double freeGb = d.AvailableFreeSpace / 1073741824.0;
            double totGb = d.TotalSize / 1073741824.0;
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "iPod" : d.VolumeLabel;
            DeviceText = $"{label} — {freeGb:0.0} GB free of {totGb:0.0} GB";
            UsedPercent = totGb > 0 ? (int)Math.Round((1 - freeGb / totGb) * 100) : 0;
            HasDevice = true;
        }
        catch { DeviceText = IpodFolder; HasDevice = true; UsedPercent = 0; }
    }

    // The volume that actually contains the iPod path. On macOS Path.GetPathRoot() returns "/", which
    // reports the internal disk; instead pick the deepest mounted volume whose root is a prefix of the path.
    private static DriveInfo? FindVolume(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return null; }
        DriveInfo? best = null; int bestLen = -1;
        foreach (var dr in DriveInfo.GetDrives())
        {
            try
            {
                if (!dr.IsReady) continue;
                var root = dr.RootDirectory.FullName;
                var rootSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                if ((full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                     full.StartsWith(rootSlash, StringComparison.OrdinalIgnoreCase)) && root.Length > bestLen)
                { best = dr; bestLen = root.Length; }
            }
            catch { }
        }
        return best;
    }

    // Rockbox plays FLAC/MP3/AAC natively, so copying as-is is the fast default. Conversion is only
    // useful to save battery on the old hardware (FLAC decode is heavier).
    private bool _convertToAlac;
    public bool ConvertToAlac { get => _convertToAlac; set => SetField(ref _convertToAlac, value); }

    private decimal _concurrency = 4;
    public decimal Concurrency { get => _concurrency; set => SetField(ref _concurrency, value); }

    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) ApplyFilter(); } }

    private string _selectedGenre = AllGenres;
    public string SelectedGenre { get => _selectedGenre; set { if (SetField(ref _selectedGenre, value)) ApplyFilter(); } }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                TransferCommand.RaiseCanExecuteChanged();
                MakePlaylistsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _isTransferring;
    public bool IsTransferring { get => _isTransferring; private set => SetField(ref _isTransferring, value); }

    private double _transferProgress;
    public double TransferProgress { get => _transferProgress; private set => SetField(ref _transferProgress, value); }

    private string _status = "Scan your library, filter by genre/search, tick albums and transfer to the iPod.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand ScanCommand { get; }
    public RelayCommand AdoptCommand { get; }
    public RelayCommand CleanupCommand { get; }
    public RelayCommand ConfirmCleanupCommand { get; }
    public RelayCommand CancelCleanupCommand { get; }

    // ==== Clean up iPod: wezen van hernoemingen opruimen (preview eerst, bron blijft heilig) ====
    public System.Collections.ObjectModel.ObservableCollection<string> CleanupItems { get; } = new();
    private bool _showCleanup;
    public bool ShowCleanup { get => _showCleanup; private set { if (SetField(ref _showCleanup, value)) ConfirmCleanupCommand.RaiseCanExecuteChanged(); } }
    private string _cleanupSummary = "";
    public string CleanupSummary { get => _cleanupSummary; private set => SetField(ref _cleanupSummary, value); }
    private readonly List<string> _cleanupFiles = new();
    private readonly List<string> _cleanupDirs = new();

    private static readonly string[] IpodSystemDirs = { ".rockbox", "ipod_control", ".spotlight-v100", ".trashes", ".fseventsd", "system volume information" };

    /// <summary>Audio op de iPod, mét overslaan van systeemmappen — die bevatten duizenden
    /// niet-muziekbestandjes en maakten elke scan over USB onnodig traag.</summary>
    private static IEnumerable<string> EnumerateIpodAudio(string ipod)
    {
        IEnumerable<string> tops;
        try { tops = Directory.EnumerateDirectories(ipod).ToList(); } catch { yield break; }
        foreach (var top in tops)
        {
            var name = Path.GetFileName(top).ToLowerInvariant();
            if (IpodSystemDirs.Contains(name)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(top, "*.*", SearchOption.AllDirectories); } catch { continue; }
            foreach (var f in files)
                if (AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                    yield return f;
        }
        IEnumerable<string> rootFiles;
        try { rootFiles = Directory.EnumerateFiles(ipod); } catch { yield break; }
        foreach (var f in rootFiles)
            if (AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                yield return f;
    }

    private static string NormName(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    private void CleanupPreview()
    {
        var ipod = IpodFolder;
        if (string.IsNullOrWhiteSpace(ipod) || !Directory.Exists(ipod)) { Status = "Connect the iPod (and set its folder) first."; return; }
        if (_all.Count == 0) { Status = "Scan your library first, then run the cleanup."; return; }
        var music = Path.Combine(ipod, "Music");
        if (!Directory.Exists(music)) { Status = "No Music folder on this iPod yet — nothing to clean."; return; }
        IsBusy = true;
        Status = "Looking for outdated copies on the iPod…";
        var albums = _all.ToList();
        Task.Run(() =>
        {
            var files = new List<string>();
            var dirs = new List<string>();
            var lines = new List<string>();
            long bytes = 0;

            // A) Verouderde artiestmappen: genormaliseerd gelijk aan een map die bij de
            //    huidige bieb-spelling hoort, en die canonieke map staat óók op de iPod.
            var libArtists = albums.Select(a => Clean(a.Artist)).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(NormName, v => v, StringComparer.Ordinal);
            try
            {
                foreach (var d in Directory.EnumerateDirectories(music))
                {
                    var name = Path.GetFileName(d);
                    if (name.StartsWith(".")) continue;
                    if (libArtists.Values.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;   // huidige spelling
                    if (!libArtists.TryGetValue(NormName(name), out var canonical)) continue;            // geen bieb-tegenhanger
                    if (!Directory.Exists(Path.Combine(music, canonical))) continue;                     // canonieke twin moet bestaan
                    long sz = 0; int n = 0;
                    try { foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) { n++; try { sz += new FileInfo(f).Length; } catch { } } } catch { }
                    dirs.Add(d);
                    bytes += sz;
                    lines.Add($"Folder '{name}' — superseded by '{canonical}' ({n} files, {FmtBytes(sz)})");
                }
            }
            catch { }

            // B) Verouderde tracks binnen bekende albummappen: naam komt niet meer voor in het
            //    huidige album, terwijl er wél minstens één actueel bestand naast staat.
            int checkedAlbums = 0;
            foreach (var a in albums)
            {
                if (++checkedAlbums % 40 == 0)
                {
                    var snap = checkedAlbums;
                    Dispatcher.UIThread.Post(() => Status = $"Looking for outdated copies… {snap}/{albums.Count} albums checked");
                }
                var dir = Path.Combine(music, Clean(a.Artist), Clean(a.Album));
                if (!Directory.Exists(dir)) continue;
                var expected = new HashSet<string>(a.Files.Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
                List<string> present;
                try
                {
                    present = Directory.EnumerateFiles(dir)
                        .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                        .ToList();
                }
                catch { continue; }
                var current = present.Where(f => expected.Contains(Path.GetFileNameWithoutExtension(f))).ToList();
                if (current.Count == 0) continue;   // album enkel onder oude namen → niet leegtrekken
                var stale = present.Where(f => !expected.Contains(Path.GetFileNameWithoutExtension(f))).ToList();
                if (stale.Count == 0) continue;
                foreach (var f in stale) { files.Add(f); try { bytes += new FileInfo(f).Length; } catch { } }
                lines.Add($"{a.Artist}/{a.Album}: {stale.Count} outdated file(s) next to {current.Count} current");
            }

            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                _cleanupFiles.Clear(); _cleanupFiles.AddRange(files);
                _cleanupDirs.Clear(); _cleanupDirs.AddRange(dirs);
                CleanupItems.Clear();
                foreach (var l in lines) CleanupItems.Add(l);
                if (files.Count == 0 && dirs.Count == 0)
                {
                    ShowCleanup = false;
                    Status = "✓ No outdated copies found — the iPod is clean.";
                }
                else
                {
                    CleanupSummary = $"{dirs.Count} superseded folder(s) and {files.Count} outdated file(s) — {FmtBytes(bytes)} to free. Your library is untouched; removed albums can always be re-synced.";
                    ShowCleanup = true;
                    Status = "Review the cleanup list below, then press Remove.";
                }
            });
        });
    }

    private void CleanupExecute()
    {
        if (IsBusy) return;
        var files = _cleanupFiles.ToList();
        var dirs = _cleanupDirs.ToList();
        var ipod = IpodFolder;
        IsBusy = true;
        ShowCleanup = false;
        Status = "Cleaning up the iPod…";
        Task.Run(() =>
        {
            using var awake = KeepAwake.Start();
            int nf = 0, nd = 0, total = files.Count + dirs.Count;
            foreach (var f in files)
            {
                try { File.Delete(f); nf++; } catch { }
                if (nf % 20 == 0)
                {
                    var snap = nf;
                    Dispatcher.UIThread.Post(() => Status = $"Cleaning up… {snap}/{total} removed (FAT over USB is slow, hang in there)");
                }
            }
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); nd++; } catch { }
                var snap2 = nf + nd;
                Dispatcher.UIThread.Post(() => Status = $"Cleaning up… {snap2}/{total} removed");
            }
            // Alleen de mappen opruimen die we echt hebben aangeraakt (ouders van verwijderde
            // bestanden) — geen volledige boom-scan over USB.
            try
            {
                var music = Path.GetFullPath(Path.Combine(ipod, "Music"));
                foreach (var start in files.Select(Path.GetDirectoryName).Where(x => x != null)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    var d = start!;
                    while (!string.IsNullOrEmpty(d)
                           && Path.GetFullPath(d).StartsWith(music, StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(Path.GetFullPath(d), music, StringComparison.OrdinalIgnoreCase)
                           && Directory.Exists(d)
                           && !Directory.EnumerateFileSystemEntries(d).Any())
                    {
                        try { Directory.Delete(d); } catch { break; }
                        d = Path.GetDirectoryName(d) ?? "";
                    }
                }
            }
            catch { }
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                _cleanupFiles.Clear(); _cleanupDirs.Clear(); CleanupItems.Clear();
                Status = $"✓ Cleanup done — {nf} file(s) and {nd} folder(s) removed from the iPod.";
                MarkIpodPresence();
            });
        });
    }

    // ==== Adopt this iPod: eenmalige tag-index van alles wat er al op staat ====
    // Cache op de iPod zelf (.spindle-adopt.json) zodat herkenning op artiest+titel-tags werkt,
    // volledig onafhankelijk van bestandsnamen of mappenstructuur van andere tools.
    private static string TagKey(string artist, string title) =>
        System.Text.RegularExpressions.Regex.Replace((artist + "|" + title).ToLowerInvariant(), "[^a-z0-9|]", "");

    private static HashSet<string> LoadAdoptCache(string ipod)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(ipod, ".spindle-adopt.json");
            if (!File.Exists(path)) return set;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("keys").EnumerateArray())
            {
                var k = e.GetString();
                if (!string.IsNullOrEmpty(k)) set.Add(k);
            }
        }
        catch { }
        return set;
    }

    private void AdoptIpod()
    {
        var ipod = IpodFolder;
        if (string.IsNullOrWhiteSpace(ipod) || !Directory.Exists(ipod)) { Status = "Connect the iPod (and set its folder) first."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Adopting iPod — reading the tags of everything on it (one-time)…";
        Task.Run(() =>
        {
            using var awake = KeepAwake.Start();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            List<string> files;
            try { files = EnumerateIpodAudio(ipod).ToList(); }
            catch { files = new List<string>(); }
            int done = 0;
            foreach (var f in files)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var t = new ATL.Track(f);
                    var k = TagKey(t.Artist ?? "", t.Title ?? "");
                    if (k.Length > 1) keys.Add(k);
                }
                catch { }
                int n = ++done;
                if (n % 25 == 0) Dispatcher.UIThread.Post(() => Status = $"Adopting iPod… {n}/{files.Count} tags read");
            }
            bool saved = false;
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(new { version = 1, keys = keys.ToList() });
                    File.WriteAllText(Path.Combine(ipod, ".spindle-adopt.json"), json);
                    saved = true;
                }
                catch { }
            }
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                Status = !saved
                    ? $"Adopt stopped — nothing saved ({keys.Count} tracks indexed so far)."
                    : $"✓ iPod adopted — {keys.Count} tracks are now recognized by their tags, regardless of file names.";
                if (saved) MarkIpodPresence();
            });
        });
    }
    public RelayCommand StopCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand TransferCommand { get; }
    public RelayCommand MakePlaylistsCommand { get; }

    // Write a Rockbox-friendly .m3u in each album folder on the iPod, listing its tracks.
    private void MakePlaylists()
    {
        if (IsBusy) return;
        var ipod = IpodFolder;
        var music = string.IsNullOrWhiteSpace(ipod) ? "" : Path.Combine(ipod, "Music");
        if (music.Length == 0 || !Directory.Exists(music)) { Status = "No Music folder on the iPod."; return; }
        IsBusy = true;
        Task.Run(() =>
        {
            int made = 0;
            try
            {
                foreach (var albumDir in Directory.EnumerateDirectories(music, "*", SearchOption.AllDirectories))
                {
                    var tracks = Directory.EnumerateFiles(albumDir)
                        .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .Select(Path.GetFileName).ToList();
                    if (tracks.Count == 0) continue;
                    File.WriteAllLines(Path.Combine(albumDir, Path.GetFileName(albumDir) + ".m3u"), tracks!);
                    made++;
                }
            }
            catch { }
            Dispatcher.UIThread.Post(() => { IsBusy = false; Status = $"{made} album playlists (.m3u) created on the iPod."; });
        });
    }

    private void Scan()
    {
        if (IsBusy) return;
        var lib = LibraryFolder;
        if (!Directory.Exists(lib)) { Status = "Library folder doesn't exist."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Scanning library...";

        Task.Run(() =>
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(lib, "*.*", SearchOption.AllDirectories)
                    .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                    .ToList();
            }
            catch (Exception e) { Dispatcher.UIThread.Post(() => { IsBusy = false; Status = "Couldn't read library: " + e.Message; }); return; }

            // Tag reading is the slow part -> read across all cores, then group in-memory.
            int scanned = 0;
            var bag = new ConcurrentBag<(string Artist, string Album, string Genre, string Year, string File)>();
            try
            {
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token }, f =>
                {
                    try
                    {
                        var t = new Track(f);
                        var artist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : (t.Artist ?? "");
                        var album = t.Album ?? "";
                        if (!(string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album)))
                            bag.Add((artist, album, (t.Genre ?? "").Trim(), t.Year > 0 ? t.Year.ToString() : "", f));
                    }
                    catch { }
                    var s = Interlocked.Increment(ref scanned);
                    if (s % 100 == 0) Dispatcher.UIThread.Post(() => Status = $"Scanning... {s} files");
                });
            }
            catch (OperationCanceledException) { }

            var groups = new Dictionary<string, (string Artist, string Album, Dictionary<string, int> Genres, string Year, List<string> Files)>();
            foreach (var rec in bag)
            {
                var key = rec.Artist.ToLowerInvariant() + " " + rec.Album.ToLowerInvariant();
                if (!groups.TryGetValue(key, out var g))
                {
                    g = (rec.Artist, rec.Album, new Dictionary<string, int>(), rec.Year, new List<string>());
                    groups[key] = g;
                }
                g.Files.Add(rec.File);
                if (rec.Genre.Length > 0) g.Genres[rec.Genre] = g.Genres.TryGetValue(rec.Genre, out var c) ? c + 1 : 1;
            }

            var albums = groups.Values
                .Select(g => new AlbumEntryViewModel(
                    g.Artist, g.Album,
                    g.Genres.Count == 0 ? "" : g.Genres.OrderByDescending(kv => kv.Value).First().Key,
                    g.Year, g.Files))
                .OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Year)
                .ToList();
            var genres = albums.Select(a => a.Genre).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

            Dispatcher.UIThread.Post(() =>
            {
                _all.Clear(); _all.AddRange(albums);
                Genres.Clear(); Genres.Add(AllGenres);
                foreach (var gn in genres) Genres.Add(gn);
                SelectedGenre = AllGenres;
                ApplyFilter();
                IsBusy = false;
                Status = $"{_all.Count} albums found ({scanned} files). Filter and tick what to transfer.";
                MarkIpodPresence();
            });
        });
    }

    // Mark, per album, how many tracks are already on the iPod (checks both the original name and the
    // .m4a name in case it was converted). Runs off the UI thread; results are applied on it.
    // ---- Transferintentie: overleeft stoppen, ontkoppelen en herstarten ----
    private HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private static string WKey(AlbumEntryViewModel a) =>
        System.Text.RegularExpressions.Regex.Replace((a.Artist + "|" + a.Album).ToLowerInvariant(), "[^a-z0-9|]", "");

    public void LoadWanted(List<string> keys) => _wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Huidige selectie als persisteerbare sleutels; zonder scan de bewaarde intentie.</summary>
    public List<string> WantedSnapshot()
    {
        if (_all.Count == 0) return _wanted.ToList();
        return _all.Where(a => a.Selected).Select(WKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FmtBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024.0:0} kB";

    private void MarkIpodPresence()
    {
        var ipod = IpodFolder;
        bool ok = !string.IsNullOrWhiteSpace(ipod) && Directory.Exists(ipod);
        var snapshot = _all.ToList();

        Task.Run(() =>
        {
            var adopt = ok ? LoadAdoptCache(ipod) : new HashSet<string>(StringComparer.Ordinal);
            // Index op bestandsnaam-zonder-extensie van ALLES wat op de iPod staat (recursief),
            // zodat ook handmatig/anders gestructureerd overgezette mappen herkend worden — ongeacht waar ze staan.
            var onIpod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ok)
            {
                try
                {
                    foreach (var f in EnumerateIpodAudio(ipod))
                        onIpod.Add(Path.GetFileNameWithoutExtension(f));
                }
                catch { }
            }
            var map = new Dictionary<AlbumEntryViewModel, int>();
            foreach (var a in snapshot)
            {
                if (!ok) { map[a] = -1; continue; }
                // Eerst de canonieke transfer-bestemming checken (Music/Artiest/Album): die komt uit
                // de TAGS en overleeft dus bestandshernoemingen in de bieb (doctor/template).
                int n = 0;
                try
                {
                    var dir = Path.Combine(ipod, "Music", Clean(a.Artist), Clean(a.Album));
                    if (Directory.Exists(dir))
                        n = Directory.EnumerateFiles(dir)
                            .Count(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant())
                                        && !Path.GetFileName(f).StartsWith("._"));
                }
                catch { }
                // Fallback: naam-matching over de hele iPod (vindt ook handmatig gekopieerde mappen).
                if (n == 0)
                    n = a.Files.Count(f => onIpod.Contains(Path.GetFileNameWithoutExtension(f)));
                // Laatste laag: tag-matching tegen de adopt-cache (naam- en structuur-onafhankelijk).
                if (n == 0 && adopt.Count > 0)
                {
                    int hits = 0;
                    foreach (var f in a.Files)
                    {
                        try
                        {
                            var t = new ATL.Track(f);
                            if (adopt.Contains(TagKey(t.Artist ?? "", t.Title ?? ""))) hits++;
                        }
                        catch { }
                    }
                    n = hits;
                }
                map[a] = n;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Pre-tick what's already on the iPod: ticked = should be on iPod. Untick to remove it.
                foreach (var kv in map)
                {
                    kv.Key.OnIpod = kv.Value;
                    if (ok) kv.Key.Selected = kv.Value > 0 || _wanted.Contains(WKey(kv.Key));
                }
                if (ok)
                {
                    int present = snapshot.Count(a => a.OnIpod > 0);
                    Status = $"{snapshot.Count} albums · {present} already on the iPod (pre-ticked). Untick to remove from the iPod, tick to transfer.";
                }
                ApplyFilter();
            });
        });
    }

    private void ApplyFilter()
    {
        foreach (var n in ArtistTree) n.Detach();
        ArtistTree.Clear();
        var g = SelectedGenre;
        var q = (SearchText ?? "").Trim();
        var visible = _all.Where(a =>
            (g == AllGenres || string.Equals(a.Genre, g, StringComparison.OrdinalIgnoreCase)) &&
            (q.Length == 0 || a.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
        foreach (var grp in visible.GroupBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(g2 => g2.Key, StringComparer.OrdinalIgnoreCase))
            ArtistTree.Add(new ArtistNodeViewModel(grp.Key, grp.OrderBy(a => a.Year)));
    }

    private void Transfer()
    {
        if (IsBusy) return;
        var ipod = IpodFolder;
        if (!Directory.Exists(ipod)) { Status = "iPod folder doesn't exist."; return; }
        var jobs = _all.Where(a => a.Selected)
            .SelectMany(a => a.Files.Select(f => (File: f, a.Artist, a.Album)))
            .ToList();
        // Unticked albums that are currently on the iPod get removed from it (iTunes-style sync).
        var deletes = _all.Where(a => !a.Selected && a.OnIpod > 0).ToList();
        if (jobs.Count == 0 && deletes.Count == 0) { Status = "Nothing to do — selection already matches the iPod."; return; }

        Failures.Clear();
        IsBusy = true;
        IsTransferring = true;
        TransferProgress = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _wanted = new HashSet<string>(_all.Where(a => a.Selected).Select(WKey), StringComparer.OrdinalIgnoreCase);
        var convert = ConvertToAlac;
        var dop = System.Math.Max(1, (int)Concurrency);

        Task.Run(() =>
        {
            using var awake = KeepAwake.Start();   // Mac niet laten slapen midden in een transfer
            int copied = 0, skipped = 0, failed = 0, processed = 0, removed = 0;
            bool deviceGone = false;
            try
            {
                var musicRoot = Path.Combine(ipod, "Music");
                if (Directory.Exists(musicRoot))
                    foreach (var pf in Directory.EnumerateFiles(musicRoot, "*.part", SearchOption.AllDirectories).ToList())
                        try { File.Delete(pf); } catch { }
            }
            catch { }
            foreach (var a in deletes)
            {
                if (token.IsCancellationRequested) break;
                if (!Directory.Exists(ipod)) { deviceGone = true; break; }
                try
                {
                    var dir = Path.Combine(ipod, "Music", Clean(a.Artist), Clean(a.Album));
                    if (Directory.Exists(dir)) { Directory.Delete(dir, true); removed++; }
                }
                catch { }
            }

            // Deterministisch plan: vaste volgorde, unieke doelnamen (geen stille botsingen na Clean())
            // en een vrije-ruimte-check vóór er één byte wordt gekopieerd.
            var takenDest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var planned = new List<(string File, string Dest, bool DoConvert)>();
            long need = 0;
            foreach (var job in jobs.OrderBy(j => j.File, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(job.File).ToLowerInvariant();
                var doConvert = convert && ConvertExt.Contains(ext);
                var destDir = Path.Combine(ipod, "Music", Clean(job.Artist), Clean(job.Album));
                var name = Path.GetFileName(job.File);
                var dest = Path.Combine(destDir, doConvert ? Path.ChangeExtension(name, ".m4a") : name);
                if (!takenDest.Add(dest))
                {
                    int n2 = 2;
                    var bare = Path.GetFileNameWithoutExtension(dest);
                    var ex2 = Path.GetExtension(dest);
                    while (!takenDest.Add(dest = Path.Combine(destDir, bare + $" ({n2++})" + ex2))) { }
                }
                planned.Add((job.File, dest, doConvert));
                if (!File.Exists(dest))
                    try { var len = new FileInfo(job.File).Length; need += doConvert ? (long)(len * 0.7) : len; } catch { }
            }
            long freeBytes = 0;
            try { freeBytes = new DriveInfo(ipod).AvailableFreeSpace; } catch { }
            if (!deviceGone && freeBytes > 0 && need > freeBytes)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsBusy = false; IsTransferring = false; TransferProgress = 0;
                    Status = $"⚠ Not enough free space on the iPod — this selection needs ~{FmtBytes(need)} and only {FmtBytes(freeBytes)} is free. Untick some albums.";
                });
                return;
            }
            try
            {
                Parallel.ForEach(planned, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = token }, job =>
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!Directory.Exists(ipod))
                        {
                            deviceGone = true;
                            try { _cts?.Cancel(); } catch { }
                            throw new OperationCanceledException();
                        }
                        var doConvert = job.DoConvert;
                        var dest = job.Dest;
                        var destDir = Path.GetDirectoryName(dest)!;
                        if (File.Exists(dest)) { Interlocked.Increment(ref skipped); }
                        else
                        {
                            Directory.CreateDirectory(destDir);
                            if (doConvert)
                            {
                                var part = dest + ".part";
                                var staged = Path.Combine(Path.GetTempPath(), "spindle-" + Guid.NewGuid().ToString("N") + Path.GetExtension(job.File));
                                try
                                {
                                    // Bron eerst naar lokale tijdelijke opslag (via RAM): de conversie leest
                                    // daarna niets meer van de SSD, dus ontkoppelen kan haar niet raken.
                                    File.WriteAllBytes(staged, File.ReadAllBytes(job.File));
                                    if (AudioConvert.Encode(staged, part, true, token, out var encErr) && File.Exists(part))
                                    {
                                        AudioConvert.CopyTags(staged, part, artistFromAlbumArtist: true);
                                        File.Move(part, dest, true);
                                        Interlocked.Increment(ref copied);
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref failed);
                                        if (!token.IsCancellationRequested) AddFailure(job.File, encErr ?? "no output file");
                                    }
                                }
                                finally
                                {
                                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                                    try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                                }
                            }
                            else
                            {
                                var part = dest + ".part";
                                try
                                {
                                    // Eerst volledig in RAM lezen: valt de SSD weg, dan faalt het lezen
                                    // vóór er ook maar één byte naar de iPod is geschreven.
                                    if (new FileInfo(job.File).Length <= 256L << 20)
                                    {
                                        var bytes = File.ReadAllBytes(job.File);
                                        File.WriteAllBytes(part, bytes);
                                    }
                                    else
                                        File.Copy(job.File, part, true);   // extreem groot bestand: stream
                                    File.Move(part, dest, true);
                                    Interlocked.Increment(ref copied);
                                }
                                finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Interlocked.Increment(ref failed); AddFailure(job.File, ex.Message); }
                    var p = Interlocked.Increment(ref processed);
                    Dispatcher.UIThread.Post(() => { Status = $"Transferring… {p}/{jobs.Count}"; TransferProgress = jobs.Count > 0 ? 100.0 * p / jobs.Count : 100; });
                });
            }
            catch (OperationCanceledException) { }

            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                IsTransferring = false;
                if (!token.IsCancellationRequested) TransferProgress = 100;
                Status = deviceGone
                    ? $"⚠ iPod disconnected — transfer stopped after {copied} track(s). Reconnect and press Transfer; it continues where it left off."
                    : token.IsCancellationRequested
                    ? $"Stopped — {copied} transferred, {removed} removed, {skipped} skipped."
                    : $"Done — {copied} transferred, {removed} removed from iPod, {skipped} skipped, {failed} failed.  ✓ Safe to disconnect the iPod.";
                MarkIpodPresence();
            });
        });
    }

    private void AddFailure(string path, string reason)
        => Dispatcher.UIThread.Post(() => Failures.Add(new FailureViewModel(path, reason)));

    private static string Clean(string s)
    {
        s = (s ?? "").Trim().Replace('/', '-').Replace('\\', '-');
        s = Regex.Replace(s, "[:*?\"<>|\\x00-\\x1f]", "");
        s = Regex.Replace(s, "\\s+", " ").Trim().TrimEnd('.', ' ');
        return s.Length > 0 ? s : "Unknown";
    }
}
