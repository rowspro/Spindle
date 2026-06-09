using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

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
    private string _deviceText = "Kies de gekoppelde iPod-map om de vrije ruimte te zien.";
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
                DeviceText = "Kies de gekoppelde iPod-map om de vrije ruimte te zien.";
                HasDevice = false; UsedPercent = 0; return;
            }
            var d = FindVolume(IpodFolder);
            if (d == null) { DeviceText = IpodFolder; HasDevice = true; UsedPercent = 0; return; }
            double freeGb = d.AvailableFreeSpace / 1073741824.0;
            double totGb = d.TotalSize / 1073741824.0;
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "iPod" : d.VolumeLabel;
            DeviceText = $"{label} — {freeGb:0.0} GB vrij van {totGb:0.0} GB";
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

    private string _status = "Scan je bibliotheek, filter op genre/zoekterm, vink albums aan en zet over naar de iPod.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand ScanCommand { get; }
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
        if (music.Length == 0 || !Directory.Exists(music)) { Status = "Geen Music-map op de iPod."; return; }
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
            Dispatcher.UIThread.Post(() => { IsBusy = false; Status = $"{made} album-playlists (.m3u) gemaakt op de iPod."; });
        });
    }

    private void Scan()
    {
        if (IsBusy) return;
        var lib = LibraryFolder;
        if (!Directory.Exists(lib)) { Status = "Bibliotheek-map bestaat niet."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Bibliotheek scannen...";

        Task.Run(() =>
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(lib, "*.*", SearchOption.AllDirectories)
                    .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                    .ToList();
            }
            catch (Exception e) { Dispatcher.UIThread.Post(() => { IsBusy = false; Status = "Kon bibliotheek niet lezen: " + e.Message; }); return; }

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
                    if (s % 100 == 0) Dispatcher.UIThread.Post(() => Status = $"Scannen... {s} bestanden");
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
                Status = $"{_all.Count} albums gevonden ({scanned} bestanden). Filter en vink aan wat je wilt overzetten.";
                MarkIpodPresence();
            });
        });
    }

    // Mark, per album, how many tracks are already on the iPod (checks both the original name and the
    // .m4a name in case it was converted). Runs off the UI thread; results are applied on it.
    private void MarkIpodPresence()
    {
        var ipod = IpodFolder;
        bool ok = !string.IsNullOrWhiteSpace(ipod) && Directory.Exists(ipod);
        var snapshot = _all.ToList();

        Task.Run(() =>
        {
            // Index op bestandsnaam-zonder-extensie van ALLES wat op de iPod staat (recursief),
            // zodat ook handmatig/anders gestructureerd overgezette mappen herkend worden — ongeacht waar ze staan.
            var onIpod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ok)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(ipod, "*.*", SearchOption.AllDirectories))
                        if (AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                            onIpod.Add(Path.GetFileNameWithoutExtension(f));
                }
                catch { }
            }
            var map = new Dictionary<AlbumEntryViewModel, int>();
            foreach (var a in snapshot)
            {
                if (!ok) { map[a] = -1; continue; }
                map[a] = a.Files.Count(f => onIpod.Contains(Path.GetFileNameWithoutExtension(f)));
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Pre-tick what's already on the iPod: ticked = should be on iPod. Untick to remove it.
                foreach (var kv in map)
                {
                    kv.Key.OnIpod = kv.Value;
                    if (ok) kv.Key.Selected = kv.Value > 0;
                }
                if (ok)
                {
                    int present = snapshot.Count(a => a.OnIpod > 0);
                    Status = $"{snapshot.Count} albums · {present} al op de iPod (voorgevinkt). Vink af om van de iPod te verwijderen, vink aan om over te zetten.";
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
        if (!Directory.Exists(ipod)) { Status = "iPod-map bestaat niet."; return; }
        var jobs = _all.Where(a => a.Selected)
            .SelectMany(a => a.Files.Select(f => (File: f, a.Artist, a.Album)))
            .ToList();
        // Unticked albums that are currently on the iPod get removed from it (iTunes-style sync).
        var deletes = _all.Where(a => !a.Selected && a.OnIpod > 0).ToList();
        if (jobs.Count == 0 && deletes.Count == 0) { Status = "Niets te doen — selectie komt al overeen met de iPod."; return; }

        Failures.Clear();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var convert = ConvertToAlac;
        var dop = System.Math.Max(1, (int)Concurrency);

        Task.Run(() =>
        {
            int copied = 0, skipped = 0, failed = 0, processed = 0, removed = 0;
            foreach (var a in deletes)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var dir = Path.Combine(ipod, "Music", Clean(a.Artist), Clean(a.Album));
                    if (Directory.Exists(dir)) { Directory.Delete(dir, true); removed++; }
                }
                catch { }
            }
            try
            {
                Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = token }, job =>
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var ext = Path.GetExtension(job.File).ToLowerInvariant();
                        var doConvert = convert && ConvertExt.Contains(ext);
                        var destDir = Path.Combine(ipod, "Music", Clean(job.Artist), Clean(job.Album));
                        var name = Path.GetFileName(job.File);
                        var dest = Path.Combine(destDir, doConvert ? Path.ChangeExtension(name, ".m4a") : name);
                        if (File.Exists(dest)) { Interlocked.Increment(ref skipped); }
                        else
                        {
                            Directory.CreateDirectory(destDir);
                            if (doConvert)
                            {
                                var part = dest + ".part";
                                try
                                {
                                    if (AudioConvert.Encode(job.File, part, true, token, out var encErr) && File.Exists(part))
                                    {
                                        AudioConvert.CopyTags(job.File, part);
                                        File.Move(part, dest, true);
                                        Interlocked.Increment(ref copied);
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref failed);
                                        if (!token.IsCancellationRequested) AddFailure(job.File, encErr ?? "geen uitvoerbestand");
                                    }
                                }
                                finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
                            }
                            else
                            {
                                File.Copy(job.File, dest);
                                Interlocked.Increment(ref copied);
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Interlocked.Increment(ref failed); AddFailure(job.File, ex.Message); }
                    var p = Interlocked.Increment(ref processed);
                    Dispatcher.UIThread.Post(() => Status = $"Overzetten... {p}/{jobs.Count}");
                });
            }
            catch (OperationCanceledException) { }

            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                Status = token.IsCancellationRequested
                    ? $"Gestopt - {copied} overgezet, {removed} verwijderd, {skipped} overgeslagen."
                    : $"Klaar - {copied} overgezet, {removed} van iPod verwijderd, {skipped} overgeslagen (al aanwezig), {failed} mislukt.";
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
