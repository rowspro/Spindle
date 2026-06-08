using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

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
        FixCommand = new RelayCommand(() => _onEdit(Files, $"{Title} — {issue}. Vul aan / zet hoes en keur goed."));
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }
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
        public string Query => $"{Artist} - {Name}";
    }

    private readonly Action<List<string>> _onDownload;
    private readonly Action<IReadOnlyList<string>, string> _onEdit;
    private readonly List<Album> _albums = new();
    private List<string> _noTagFiles = new();
    private List<string> _noCoverFiles = new();
    private CancellationTokenSource? _cts;

    public LibraryViewModel(Action<List<string>> onDownload, Action<IReadOnlyList<string>, string> onEdit)
    {
        _onDownload = onDownload;
        _onEdit = onEdit;
        ScanCommand = new RelayCommand(Scan, () => !IsBusy && !string.IsNullOrWhiteSpace(LibraryFolder));
        RepairCoversCommand = new RelayCommand(RepairCovers, () => !IsBusy && _albums.Count > 0);
        FindUpgradesCommand = new RelayCommand(FindUpgrades, () => !IsBusy && _albums.Count > 0);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        EditNoTagsCommand = new RelayCommand(
            () => _onEdit(_noTagFiles, $"{_noTagFiles.Count} nummers zonder (volledige) tags — vul aan en keur goed."),
            () => _noTagFiles.Count > 0);
        EditNoCoverCommand = new RelayCommand(
            () => _onEdit(_noCoverFiles, $"{_noCoverFiles.Count} nummers uit albums zonder hoes — zet een hoes (of Auto-fill) en keur goed."),
            () => _noCoverFiles.Count > 0);
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
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _status = "Stel je muziekbieb in (Instellingen) en scan op problemen.";
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
    public RelayCommand FindUpgradesCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand EditNoTagsCommand { get; }
    public RelayCommand EditNoCoverCommand { get; }

    private void Scan()
    {
        if (IsBusy) return;
        var lib = LibraryFolder;
        if (!Directory.Exists(lib)) { Status = "Bibliotheek-map bestaat niet."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = "Bibliotheek scannen…";

        Task.Run(() =>
        {
            var files = Directory.EnumerateFiles(lib, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                .ToList();

            int noTags = 0, lossyFiles = 0;
            var noTagBag = new ConcurrentBag<string>();
            var groups = new ConcurrentDictionary<string, Album>();
            int scanned = 0;

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token }, f =>
            {
                try
                {
                    var t = new Track(f);
                    var artist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : (t.Artist ?? "");
                    var album = t.Album ?? "";
                    bool untagged = string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(artist);
                    if (untagged) { Interlocked.Increment(ref noTags); noTagBag.Add(f); }
                    bool lossy = !Lossless.Contains(Path.GetExtension(f).ToLowerInvariant());
                    if (lossy) Interlocked.Increment(ref lossyFiles);
                    bool hasCover = t.EmbeddedPictures.Count > 0;

                    var key = Norm(artist) + "|" + Norm(album);
                    var a = groups.GetOrAdd(key, _ => new Album { Artist = artist, Name = album, AllLossy = true });
                    lock (a)
                    {
                        a.Files.Add(f);
                        if (hasCover) a.HasCover = true;
                        if (!lossy) a.AllLossy = false;
                        if (untagged) a.Untagged++;
                    }
                }
                catch { }
                var s = Interlocked.Increment(ref scanned);
                if (s % 200 == 0) Dispatcher.UIThread.Post(() => Status = $"Scannen… {s}");
            });

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
                                  a.HasCover ? null : "geen hoes",
                                  a.AllLossy ? "volledig lossy" : null,
                                  a.Untagged > 0 ? $"{a.Untagged} zonder tags" : null
                              }.Where(s => s != null))))
                .Where(x => x.sev > 0)
                .OrderByDescending(x => x.sev).ThenByDescending(x => x.Files.Count)
                .Take(40).ToList();

            double tagFrac = files.Count > 0 ? (double)noTags / files.Count : 0;
            double lossyFrac = files.Count > 0 ? (double)lossyFiles / files.Count : 0;
            double coverFrac = albums.Count > 0 ? (double)noCover / albums.Count : 0;
            int score = albums.Count == 0 ? 0 : Math.Clamp((int)Math.Round(100 * (1 - 0.4 * tagFrac - 0.3 * coverFrac - 0.3 * lossyFrac)), 0, 100);

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
                ProblemAlbums.Clear();
                foreach (var p in problemData)
                    ProblemAlbums.Add(new ProblemAlbumViewModel(p.Artist, p.Name, p.issue, p.IReadOnlyList, _onEdit, DeleteAlbum));
                HasScanned = true;
                IsBusy = false;
                RepairCoversCommand.RaiseCanExecuteChanged();
                FindUpgradesCommand.RaiseCanExecuteChanged();
                EditNoTagsCommand.RaiseCanExecuteChanged();
                EditNoCoverCommand.RaiseCanExecuteChanged();
                Status = token.IsCancellationRequested ? "Scan gestopt." : $"Scan klaar — gezondheid {score}%. Klik een kaart of album om te herstellen.";
            });
        });
    }

    // Safe delete: move the album's files to <library>/_Verwijderd (reversible), and drop it from the list.
    private void DeleteAlbum(ProblemAlbumViewModel album)
    {
        var lib = LibraryFolder;
        if (string.IsNullOrWhiteSpace(lib) || !Directory.Exists(lib)) { Status = "Geen geldige bibliotheek-map."; return; }
        int moved = 0;
        var trashRoot = Path.Combine(lib, "_Verwijderd");
        foreach (var f in album.Files)
        {
            try
            {
                var rel = Path.GetRelativePath(lib, f);
                if (rel.StartsWith("..")) rel = Path.GetFileName(f);
                var dest = Path.Combine(trashRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(f, dest);
                moved++;
            }
            catch { }
        }
        ProblemAlbums.Remove(album);
        FileCount = Math.Max(0, FileCount - album.Files.Count);
        AlbumCount = Math.Max(0, AlbumCount - 1);
        Status = $"'{album.Title}' verplaatst naar _Verwijderd ({moved} bestanden) — omkeerbaar.";
    }

    private void RepairCovers()
    {
        if (IsBusy) return;
        var todo = _albums.Where(a => !a.HasCover).ToList();
        if (todo.Count == 0) { Status = "Alle albums hebben al een hoes."; return; }
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
                Dispatcher.UIThread.Post(() => Status = $"Hoezen… {snap}/{todo.Count}  ({a.Artist} — {a.Name})");
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
                Status = $"{fixedCount} album(s) van een hoes voorzien.";
            });
        });
    }

    private void FindUpgrades()
    {
        var queries = _albums.Where(a => a.AllLossy).Select(a => a.Query).Distinct().ToList();
        if (queries.Count == 0) { Status = "Geen volledig-lossy albums gevonden."; return; }
        Status = $"{queries.Count} albums naar downloaden (FLAC-voorkeur via album-modus)…";
        _onDownload(queries);
    }

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
}
