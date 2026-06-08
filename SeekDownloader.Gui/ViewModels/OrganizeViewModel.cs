using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;
using FuzzySharp;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>
/// One-stop "Organiseren" pipeline: for a folder it matches each album against MusicBrainz (album-level),
/// fills/cleans the tags (Apple-format artist, primary album-artist, canonical genre, cover art) and then
/// sorts the files into Artiest / Album (Jaar) / "hoofdartiest - album - ## titel" — optionally adding
/// ReplayGain. Test mode previews everything without touching files.
/// </summary>
public class OrganizeViewModel : ViewModelBase
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };

    public ObservableCollection<SortItemViewModel> Items { get; } = new();

    private CancellationTokenSource? _cts;

    public OrganizeViewModel()
    {
        RunCommand = new RelayCommand(Run, () => !IsBusy && !string.IsNullOrWhiteSpace(SourceFolder));
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        UndoCommand = new RelayCommand(() => { var n = MoveLog.UndoLast(); Status = n > 0 ? $"{n} bestand(en) teruggezet." : "Niets om ongedaan te maken."; }, () => !IsBusy);
    }

    private string _sourceFolder = string.Empty;
    public string SourceFolder { get => _sourceFolder; set { if (SetField(ref _sourceFolder, value)) RunCommand.RaiseCanExecuteChanged(); } }

    private string _destFolder = string.Empty;
    public string DestFolder { get => _destFolder; set => SetField(ref _destFolder, value); }

    private bool _testMode = true;
    public bool TestMode { get => _testMode; set => SetField(ref _testMode, value); }

    private bool _albumMatch = true;
    public bool AlbumMatch { get => _albumMatch; set => SetField(ref _albumMatch, value); }

    private bool _fetchCover = true;
    public bool FetchCover { get => _fetchCover; set => SetField(ref _fetchCover, value); }

    private bool _cleanGenre = true;
    public bool CleanGenre { get => _cleanGenre; set => SetField(ref _cleanGenre, value); }

    private bool _replayGain;
    public bool ReplayGain { get => _replayGain; set => SetField(ref _replayGain, value); }

    private bool _lyrics;
    public bool Lyrics { get => _lyrics; set => SetField(ref _lyrics, value); }

    private string _template = NameTemplate.Default;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) { RunCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); } }
    }

    private string _status = "Kies een map. 'Alleen tonen' laat zien wat er zou gebeuren zonder iets te wijzigen.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _detail = string.Empty;
    public string Detail { get => _detail; private set => SetField(ref _detail, value); }
    private void ShowDetail(string d) => Detail = d;

    public RelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand UndoCommand { get; }

    private record FileRec(string Path, string Artist, string Album, string Title, int Track, bool HasArt);

    private void Run()
    {
        if (IsBusy) return;
        var src = SourceFolder;
        var dest = string.IsNullOrWhiteSpace(DestFolder) ? src : DestFolder;
        if (!Directory.Exists(src)) { Status = "Bronmap bestaat niet."; return; }

        Items.Clear();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var test = TestMode;
        var useMb = AlbumMatch;
        var cover = FetchCover;
        var genre = CleanGenre;
        var rg = ReplayGain && ReplayGainService.Available;
        _template = Settings.Load().FilenameTemplate;
        if (!test) MoveLog.StartBatch();

        Task.Run(async () =>
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                    .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("._"))
                    .ToList();
            }
            catch (Exception e) { Done(false, "Kon map niet lezen: " + e.Message); return; }
            if (files.Count == 0) { Done(false, "Geen audiobestanden gevonden."); return; }

            // Read tags across all cores, then group into albums.
            var bag = new ConcurrentBag<FileRec>();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
            {
                try
                {
                    var t = new Track(f);
                    var artist = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : (t.Artist ?? "");
                    bag.Add(new FileRec(f, artist, t.Album ?? "", t.Title ?? "",
                        t.TrackNumber is > 0 ? t.TrackNumber.Value : 0, t.EmbeddedPictures.Count > 0));
                }
                catch { }
            });

            var groups = bag.GroupBy(r =>
            {
                var key = Norm(r.Artist) + "|" + Norm(r.Album);
                return key == "|" ? "dir:" + (Path.GetDirectoryName(r.Path) ?? "") : key;
            }).ToList();

            int moved = 0, failed = 0, skipped = 0, albumsMatched = 0, done = 0;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var grp in groups)
            {
                if (token.IsCancellationRequested) break;
                var groupFiles = grp.OrderBy(r => r.Track).ToList();
                var grpArtist = MostCommon(groupFiles.Select(r => r.Artist)) ?? "";
                var grpAlbum = MostCommon(groupFiles.Select(r => r.Album).Where(a => a.Length > 0))
                               ?? Path.GetFileName(Path.GetDirectoryName(groupFiles[0].Path) ?? "");

                MbReleaseMatch? mb = null;
                byte[]? art = null;
                if (useMb)
                {
                    Dispatcher.UIThread.Post(() => Status = $"MusicBrainz: {grpArtist} — {grpAlbum}…");
                    mb = await MusicBrainzClient.MatchReleaseAsync(grpArtist, grpAlbum, groupFiles.Count);
                    if (mb != null) albumsMatched++;
                    if (cover && mb != null && !string.IsNullOrEmpty(mb.ReleaseId))
                        art = await MusicBrainzClient.GetCoverArtAsync(mb.ReleaseId);
                    await Task.Delay(300, token).ContinueWith(_ => { });
                }

                foreach (var rec in groupFiles)
                {
                    if (token.IsCancellationRequested) break;
                    var p = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() => Status = $"Verwerken… {p}/{files.Count}");
                    try
                    {
                        var result = ProcessFile(rec, mb, art, grpArtist, grpAlbum, genre, rg, cover, test, dest, taken, token);
                        if (result.Item1 == "Verplaatst") moved++;
                        else if (result.Item1 == "Mislukt") failed++;
                        else if (result.Item1 == "Al goed" || result.Item1 == "Overgeslagen") skipped++;
                        AddItem(Path.GetFileName(rec.Path), rec.Path, result.Item2, result.Item1, result.Item3);
                    }
                    catch (Exception e)
                    {
                        failed++;
                        AddItem(Path.GetFileName(rec.Path), rec.Path, "", "Mislukt", e.GetType().Name + ": " + e.Message);
                    }
                }
            }

            var mbInfo = useMb ? $" · {albumsMatched}/{groups.Count} albums via MusicBrainz" : "";
            Done(!test, test
                ? $"Test — {files.Count} bestanden in {groups.Count} albums bekeken{mbInfo}. Zet 'Alleen tonen' uit om te verwerken."
                : $"Klaar — {moved} verwerkt, {skipped} overgeslagen, {failed} mislukt{mbInfo}.");
        });
    }

    private (string, string, string) ProcessFile(FileRec rec, MbReleaseMatch? mb, byte[]? art,
        string grpArtist, string grpAlbum, bool genre, bool rg, bool cover, bool test,
        string dest, HashSet<string> taken, CancellationToken token)
    {
        var t = new Track(rec.Path);
        var existingTitle = (t.Title ?? "").Trim();
        var existingArtist = (t.Artist ?? "").Trim();
        var artistForAlbum = mb != null && mb.Artist.Length > 0 ? mb.Artist : grpArtist;

        // Title + track number: prefer the MusicBrainz track (by number, else fuzzy on title/filename).
        var newTitle = TextFormat.Title(existingTitle.Length > 0 ? existingTitle : Path.GetFileNameWithoutExtension(rec.Path));
        int newTrack = rec.Track;
        if (mb != null && mb.Tracks.Count > 0)
        {
            MbTrack? mt = rec.Track > 0 ? mb.Tracks.FirstOrDefault(x => x.Position == rec.Track) : null;
            if (mt == null)
            {
                var basis = (existingTitle.Length > 0 ? existingTitle : Path.GetFileNameWithoutExtension(rec.Path)).ToLowerInvariant();
                mt = mb.Tracks.Select(x => new { x, s = Fuzz.Ratio(basis, x.Title.ToLowerInvariant()) })
                              .OrderByDescending(z => z.s).FirstOrDefault(z => z.s >= 75)?.x;
            }
            if (mt != null) { newTitle = mt.Title; if (newTrack <= 0) newTrack = mt.Position; }
        }

        var newArtist = TextFormat.AppleArtist(existingArtist.Length > 0 ? existingArtist : artistForAlbum);
        var albumArtist = TextFormat.PrimaryArtist(artistForAlbum.Length > 0 ? artistForAlbum : newArtist);
        var newAlbum = mb != null && mb.Album.Length > 0 ? mb.Album : (t.Album ?? grpAlbum);
        var year = mb != null && mb.Year.Length > 0 ? mb.Year : (t.Year is > 0 ? t.Year.ToString() : "");
        var newGenre = genre ? GenreFormat.Normalize(mb != null && mb.Genre.Length > 0 ? mb.Genre : (t.Genre ?? "")) : (t.Genre ?? "");

        if (!test)
        {
            t.Title = newTitle;
            t.Artist = newArtist;
            t.AlbumArtist = albumArtist;
            t.Album = newAlbum;
            if (newGenre.Length > 0) t.Genre = newGenre;
            if (newTrack > 0) t.TrackNumber = newTrack;
            if (int.TryParse(year, out var y) && y > 0) t.Year = y;
            if (cover && art != null && t.EmbeddedPictures.Count == 0)
            {
                try { t.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(art)); } catch { }
            }
            t.Save();
            if (rg) ReplayGainService.AnalyzeAndTag(rec.Path, token);
            if (_lyrics)
            {
                try { LyricsService.FetchLrc(rec.Path, albumArtist, newTitle, newAlbum, (int)t.Duration, token).GetAwaiter().GetResult(); } catch { }
            }
        }

        // Destination path: Artiest / Album (Jaar) / <bestandsnaam-template>
        var ext = Path.GetExtension(rec.Path);
        var artistDir = Clean(string.IsNullOrEmpty(albumArtist) ? "Unknown Artist" : albumArtist);
        var albumDir = string.IsNullOrEmpty(newAlbum) ? "Singles" : Clean(year.Length > 0 ? $"{newAlbum} ({year})" : newAlbum);
        var fileArtist = string.IsNullOrEmpty(albumArtist) ? "Unknown Artist" : albumArtist;
        var fileName = NameTemplate.Build(_template, fileArtist, newAlbum, newTitle, newTrack, year, Clean) + ext;
        var targetDir = Path.Combine(dest, artistDir, albumDir);
        var target = Unique(Path.Combine(targetDir, fileName), taken);
        var targetRel = Path.GetRelativePath(dest, target);

        if (Path.GetFullPath(target) == Path.GetFullPath(rec.Path))
            return ("Al goed", targetRel, "");
        if (test)
            return ("Test", targetRel, "");

        Directory.CreateDirectory(targetDir);
        File.Move(rec.Path, target);
        MoveLog.Record(target, rec.Path);
        return ("Verplaatst", targetRel, "");
    }

    private void AddItem(string name, string source, string target, string status, string error)
        => Dispatcher.UIThread.Post(() => Items.Add(new SortItemViewModel(name, source, target, status, error, ShowDetail)));

    private void Done(bool _, string status)
        => Dispatcher.UIThread.Post(() => { IsBusy = false; Status = status; });

    private static string? MostCommon(IEnumerable<string> values)
        => values.Where(v => !string.IsNullOrWhiteSpace(v))
                 .GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

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
}
