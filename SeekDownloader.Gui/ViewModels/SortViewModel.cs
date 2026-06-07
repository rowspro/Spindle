using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>
/// Sorts a folder into Artiest / Album (Jaar) / "hoofdartiest - album - ## titel" based on tags (via ATL). Test mode shows
/// what would happen without moving. Compilations go under "Various Artists". Uses Album Artist when
/// present; never splits names. Tag reading runs on all cores; file moves are serialized for safety.
/// </summary>
public class SortViewModel : ViewModelBase
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    private static readonly HashSet<string> VaSet = new(StringComparer.OrdinalIgnoreCase)
        { "various artists", "various", "va", "v.a.", "v/a" };

    public ObservableCollection<SortArtistGroupViewModel> Tree { get; } = new();
    private readonly Dictionary<string, SortArtistGroupViewModel> _artists = new();

    public SortViewModel()
    {
        SortCommand = new RelayCommand(Run, () => !IsRunning && !string.IsNullOrWhiteSpace(SourceFolder));
    }

    private string _sourceFolder = string.Empty;
    public string SourceFolder
    {
        get => _sourceFolder;
        set { if (SetField(ref _sourceFolder, value)) SortCommand.RaiseCanExecuteChanged(); }
    }

    private string _destFolder = string.Empty;
    public string DestFolder { get => _destFolder; set => SetField(ref _destFolder, value); }

    private bool _testMode = true;
    public bool TestMode { get => _testMode; set => SetField(ref _testMode, value); }

    private bool _cleanEmpty = true;
    public bool CleanEmpty { get => _cleanEmpty; set => SetField(ref _cleanEmpty, value); }

    private bool _cleanGenre = true;
    public bool CleanGenre { get => _cleanGenre; set => SetField(ref _cleanGenre, value); }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (SetField(ref _isRunning, value)) SortCommand.RaiseCanExecuteChanged(); }
    }

    private string _status = "Kies een map. Zet 'Alleen tonen' uit om echt te verplaatsen.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _detail = string.Empty;
    public string Detail { get => _detail; private set => SetField(ref _detail, value); }
    private void ShowDetail(string d) => Detail = d;

    public RelayCommand SortCommand { get; }

    private void Run()
    {
        if (IsRunning) return;
        var src = SourceFolder;
        var dest = string.IsNullOrWhiteSpace(DestFolder) ? src : DestFolder;
        if (!Directory.Exists(src)) { Status = "Bronmap bestaat niet."; return; }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                .ToList();
        }
        catch (Exception e) { Status = "Kon map niet lezen: " + e.Message; return; }

        Tree.Clear();
        _artists.Clear();
        if (files.Count == 0) { Status = "Geen audiobestanden gevonden."; return; }

        IsRunning = true;
        var test = TestMode;
        var clean = CleanEmpty;
        var cleanGenre = CleanGenre;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        Task.Run(() =>
        {
            int moved = 0, notags = 0, failed = 0, done = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                string targetRel = "";
                try
                {
                    var t = new Track(file);
                    var aa = (t.AlbumArtist ?? "").Trim();
                    var ar = (t.Artist ?? "").Trim();
                    var album = (t.Album ?? "").Trim();
                    var title = (t.Title ?? "").Trim();
                    var folderArtist = !string.IsNullOrEmpty(aa) ? aa : ar;
                    bool va = VaSet.Contains(folderArtist);

                    if (string.IsNullOrEmpty(folderArtist) && string.IsNullOrEmpty(album) && string.IsNullOrEmpty(title))
                    {
                        Interlocked.Increment(ref notags);
                        AddItem(Path.GetFileName(file), file, "", "Geen tags", "");
                        return;
                    }

                    // Genre opschonen: één canonieke genre i.p.v. hoofdletter-varianten / lange reeksen.
                    if (!test && cleanGenre)
                    {
                        var g0 = t.Genre ?? "";
                        var ng = GenreFormat.Normalize(g0);
                        if (!string.IsNullOrEmpty(ng) && ng != g0) { t.Genre = ng; t.Save(); }
                    }

                    var ext = Path.GetExtension(file);
                    var artistDir = Clean(va ? "Various Artists" : (string.IsNullOrEmpty(folderArtist) ? "Unknown Artist" : folderArtist));
                    var albumDir = string.IsNullOrEmpty(album) ? "Singles" : Clean(t.Year > 0 ? $"{album} ({t.Year})" : album);

                    // Filename: hoofdartiest - album - ## titel (track-artiest bij verzamelalbums).
                    var fileArtist = va && !string.IsNullOrEmpty(ar) ? ar : folderArtist;
                    if (string.IsNullOrEmpty(fileArtist)) fileArtist = "Unknown Artist";
                    var trackNum = t.TrackNumber is > 0 ? $"{t.TrackNumber:00} " : "";
                    var baseName = string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(file) : title;
                    var nameParts = new List<string> { Clean(fileArtist) };
                    if (!string.IsNullOrEmpty(album)) nameParts.Add(Clean(album));
                    nameParts.Add(Clean($"{trackNum}{baseName}"));
                    var fileName = string.Join(" - ", nameParts) + ext;
                    var targetDir = Path.Combine(dest, artistDir, albumDir);

                    string status;
                    lock (gate)
                    {
                        var target = Unique(Path.Combine(targetDir, fileName), taken);
                        targetRel = Path.GetRelativePath(dest, target);
                        if (Path.GetFullPath(target) == Path.GetFullPath(file))
                        {
                            AddItem(Path.GetFileName(file), file, targetRel, "Al goed", "");
                            return;
                        }
                        if (test)
                        {
                            status = "Test";
                        }
                        else
                        {
                            Directory.CreateDirectory(targetDir);
                            File.Move(file, target);
                            Interlocked.Increment(ref moved);
                            status = "Verplaatst";
                        }
                    }
                    AddItem(Path.GetFileName(file), file, targetRel, status, "");
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref failed);
                    AddItem(Path.GetFileName(file), file, targetRel, "Mislukt", e.GetType().Name + ": " + e.Message);
                }
                var p = Interlocked.Increment(ref done);
                if (p % 25 == 0) Dispatcher.UIThread.Post(() => Status = $"Bezig... {p}/{files.Count}");
            });

            if (!test && clean) RemoveEmptyDirs(src);

            Dispatcher.UIThread.Post(() =>
            {
                IsRunning = false;
                Status = test
                    ? $"Test - {files.Count} bestanden bekeken, {notags} zonder tags. Zet 'Alleen tonen' uit om te verplaatsen."
                    : $"Klaar - {moved} verplaatst, {notags} zonder tags, {failed} mislukt.";
            });
        });
    }

    private void AddItem(string name, string sourcePath, string target, string status, string error)
        => Dispatcher.UIThread.Post(() =>
        {
            var item = new SortItemViewModel(name, sourcePath, target, status, error, ShowDetail);
            ParseTarget(target, out var artist, out var album);
            if (!_artists.TryGetValue(artist, out var ag))
            {
                ag = new SortArtistGroupViewModel(artist);
                _artists[artist] = ag;
                Tree.Add(ag);
            }
            if (!ag.Index.TryGetValue(album, out var alg))
            {
                alg = new SortAlbumGroupViewModel(album);
                ag.Index[album] = alg;
                ag.Albums.Add(alg);
            }
            alg.Add(item);
        });

    private static void ParseTarget(string target, out string artist, out string album)
    {
        artist = "⚠ Overig";
        album = "";
        if (string.IsNullOrEmpty(target)) return;
        var parts = target.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) { artist = parts[0]; album = parts[1]; }
    }

    private static string Clean(string s)
    {
        s = (s ?? "").Trim().Replace('/', '-').Replace('\\', '-');
        s = Regex.Replace(s, "[:*?\"<>|\\x00-\\x1f]", "");
        s = Regex.Replace(s, "\\s+", " ").Trim().TrimEnd('.', ' ');
        return s.Length > 0 ? s : "Unknown";
    }

    private static string Unique(string path, HashSet<string> taken)
    {
        if (!taken.Contains(path) && !File.Exists(path)) { taken.Add(path); return path; }
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var cand = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!taken.Contains(cand) && !File.Exists(cand)) { taken.Add(cand); return cand; }
        }
    }

    private static void RemoveEmptyDirs(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch { }
            }
        }
        catch { }
    }
}
