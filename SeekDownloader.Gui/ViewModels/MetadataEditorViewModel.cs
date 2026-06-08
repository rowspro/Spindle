using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>
/// Edit tags + album art (ATL). Open a single file, or a folder (recursief) and step through it.
/// "Auto-fill" on a folder runs a batch via MusicBrainz (+ AcoustID fingerprint for untagged files),
/// then narrows the step-through to only the changed / not-found tracks. Goedkeuren slaat automatisch op.
/// </summary>
public class MetadataEditorViewModel : ViewModelBase
{
    private static readonly string[] AudioExts = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };

    public MetadataEditorViewModel()
    {
        ApproveNextCommand = new RelayCommand(ApproveNext, () => HasFile && !IsBusy);
        BackCommand = new RelayCommand(Back, () => HasPrev && !IsBusy);
        ApplyAppleArtistCommand = new RelayCommand(ApplyAppleArtist, () => HasFile && !IsBusy);
        AutoFillCommand = new RelayCommand(AutoFill, () => HasFile && !IsBusy);
        RemoveArtCommand = new RelayCommand(RemoveArt, () => HasFile && !IsBusy);
        MatchAlbumCommand = new RelayCommand(MatchAlbum, () => HasFile && !IsBusy);
        ApplyAlbumMatchCommand = new RelayCommand(ApplyAlbumMatch, () => SelectedCandidate != null && !IsBusy);
        CancelCandidatesCommand = new RelayCommand(() => ShowCandidates = false);
        _acoustIdKey = Settings.Load().AcoustIdKey ?? string.Empty;
    }

    /// <summary>Discogs token (from Instellingen) — enables the Discogs provider for album matching.</summary>
    public string DiscogsToken { get; set; } = string.Empty;

    // ---- Album match (one release → all tracks, consistent spelling) ----
    public ObservableCollection<AlbumMetaMatch> AlbumCandidates { get; } = new();

    private AlbumMetaMatch? _selectedCandidate;
    public AlbumMetaMatch? SelectedCandidate
    {
        get => _selectedCandidate;
        set { if (SetField(ref _selectedCandidate, value)) ApplyAlbumMatchCommand.RaiseCanExecuteChanged(); }
    }

    private bool _showCandidates;
    public bool ShowCandidates { get => _showCandidates; private set => SetField(ref _showCandidates, value); }

    public RelayCommand MatchAlbumCommand { get; }
    public RelayCommand ApplyAlbumMatchCommand { get; }
    public RelayCommand CancelCandidatesCommand { get; }

    private string _path = string.Empty;
    public bool HasFile => !string.IsNullOrEmpty(_path);

    private List<string> _files = new();      // current navigation list (may be narrowed to review)
    private List<string> _allFiles = new();   // full folder list (batch actions run over this)
    private readonly Dictionary<string, string> _reviewNotes = new();
    private int _index;
    private bool _folderLoaded;

    public bool FolderMode => _files.Count > 1;
    public bool HasPrev => _index > 0;
    public string Position => _files.Count > 0 ? $"{_index + 1} / {_files.Count}" : string.Empty;

    private string _fileName = string.Empty;
    public string FileName { get => _fileName; private set => SetField(ref _fileName, value); }

    private string _title = "", _artist = "", _albumArtist = "", _album = "", _track = "", _disc = "", _year = "", _genre = "";
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Artist { get => _artist; set => SetField(ref _artist, value); }
    public string AlbumArtist { get => _albumArtist; set => SetField(ref _albumArtist, value); }
    public string Album { get => _album; set => SetField(ref _album, value); }
    public string Track { get => _track; set => SetField(ref _track, value); }
    public string Disc { get => _disc; set => SetField(ref _disc, value); }
    public string Year { get => _year; set => SetField(ref _year, value); }
    public string Genre { get => _genre; set => SetField(ref _genre, value); }

    private Bitmap? _albumArt;
    public Bitmap? AlbumArt { get => _albumArt; private set => SetField(ref _albumArt, value); }
    private byte[]? _artData;
    private bool _artChanged;

    private string _acoustIdKey;
    public string AcoustIdKey
    {
        get => _acoustIdKey;
        set { if (SetField(ref _acoustIdKey, value)) Settings.SaveAcoustIdKey(value ?? string.Empty); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) RaiseNav(); }
    }

    private string _status = "Open een bestand of map (album). Auto-fill vult ontbrekende tags via MusicBrainz.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand ApproveNextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ApplyAppleArtistCommand { get; }
    public RelayCommand AutoFillCommand { get; }
    public RelayCommand RemoveArtCommand { get; }

    public void Open(string path)
    {
        _folderLoaded = false;
        _reviewNotes.Clear();
        _files = new List<string> { path };
        _allFiles = new List<string> { path };
        _index = 0;
        Load(path);
    }

    public void LoadFolder(string folder)
    {
        try
        {
            _files = System.IO.Directory
                .EnumerateFiles(folder, "*.*", System.IO.SearchOption.AllDirectories)
                .Where(f => AudioExts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())
                            && !System.IO.Path.GetFileName(f).StartsWith("._"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _allFiles = _files.ToList();
            _folderLoaded = true;
            _reviewNotes.Clear();
            if (_files.Count == 0) { Status = "Geen audiobestanden in deze map (incl. submappen)."; return; }
            _index = 0;
            Load(_files[0]);
            Status = $"{_files.Count} nummers geladen. Klik Auto-fill om de hele map bij te werken.";
        }
        catch (Exception e)
        {
            Status = "Kon map niet lezen: " + e.Message;
        }
    }

    /// <summary>Load an explicit set of files (e.g. the "no tags" / "no cover" lists from Gezondheid) and step through them.</summary>
    public void LoadFiles(IReadOnlyList<string> files, string? context = null)
    {
        _files = files.ToList();
        _allFiles = _files.ToList();
        _folderLoaded = true;            // batch Auto-fill / Apple-format run over this set
        _reviewNotes.Clear();
        if (_files.Count == 0)
        {
            _path = string.Empty;
            OnPropertyChanged(nameof(HasFile));
            OnPropertyChanged(nameof(Position));
            RaiseNav();
            Status = "Geen bestanden om te bewerken.";
            return;
        }
        _index = 0;
        Load(_files[0]);
        Status = context ?? $"{_files.Count} nummers geladen.";
    }

    private async void MatchAlbum()
    {
        if (IsBusy || !HasFile) return;
        var artist = (!string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist).Trim();
        var album = (Album ?? string.Empty).Trim();
        if (album.Length == 0) { Status = "Vul eerst het Album-veld om op te matchen."; return; }
        IsBusy = true;
        Status = $"Album zoeken: {artist} – {album}…";
        try
        {
            var list = await AlbumMetadata.SearchAsync(artist, album, _allFiles.Count, DiscogsToken);
            AlbumCandidates.Clear();
            foreach (var c in list) AlbumCandidates.Add(c);
            SelectedCandidate = AlbumCandidates.FirstOrDefault();
            ShowCandidates = AlbumCandidates.Count > 0;
            Status = AlbumCandidates.Count > 0
                ? $"{AlbumCandidates.Count} edities gevonden — kies de juiste en pas toe op het hele album."
                : "Geen album-match gevonden (probeer artiest/album bij te stellen).";
        }
        catch (Exception e) { Status = "Album-match mislukt: " + e.Message; }
        finally { IsBusy = false; }
    }

    private async void ApplyAlbumMatch()
    {
        var m = SelectedCandidate;
        if (m == null || IsBusy) return;
        IsBusy = true;
        Status = $"'{m.Album}' toepassen op het album…";
        try
        {
            await AlbumMetadata.EnsureTracksAsync(m, DiscogsToken);
            var cover = await AlbumMetadata.DownloadCoverAsync(m.CoverUrl);
            var files = _allFiles.ToList();
            int year = (m.Year.Length >= 4 && int.TryParse(m.Year.Substring(0, 4), out var yy)) ? yy : 0;
            await Task.Run(() =>
            {
                foreach (var f in files)
                {
                    try
                    {
                        var t = new Track(f);
                        t.Album = m.Album;
                        if (m.Artist.Length > 0) t.AlbumArtist = m.Artist;
                        if (string.IsNullOrWhiteSpace(t.Artist) && m.Artist.Length > 0) t.Artist = m.Artist;
                        if (year > 0) t.Year = year;
                        if (m.Genre.Length > 0) t.Genre = m.Genre;
                        int tn = t.TrackNumber ?? 0;
                        if (tn >= 1 && tn <= m.TrackTitles.Count && m.TrackTitles[tn - 1].Length > 0)
                            t.Title = m.TrackTitles[tn - 1];
                        if (cover != null) { t.EmbeddedPictures.Clear(); t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(cover)); }
                        t.Save();
                    }
                    catch { }
                }
            });
            ShowCandidates = false;
            Load(_path);
            Status = $"Album '{m.Album}' toegepast op {files.Count} tracks (bron: {m.Source}).";
        }
        catch (Exception e) { Status = "Toepassen mislukt: " + e.Message; }
        finally { IsBusy = false; }
    }

    private void ApproveNext()
    {
        if (IsBusy) return;
        Save();
        if (_index < _files.Count - 1) { _index++; Load(_files[_index]); }
        else if (FolderMode) { Status = "Klaar — alle nummers gehad."; RaiseNav(); }
    }

    private void Back()
    {
        if (IsBusy || _index == 0) return;
        _index--;
        Load(_files[_index]);
    }

    private void Load(string path)
    {
        try
        {
            var t = new Track(path);
            _path = path;
            FileName = System.IO.Path.GetFileName(path);
            Title = t.Title ?? "";
            Artist = t.Artist ?? "";
            AlbumArtist = t.AlbumArtist ?? "";
            Album = t.Album ?? "";
            Genre = t.Genre ?? "";
            Track = t.TrackNumber?.ToString() ?? "";
            Disc = t.DiscNumber?.ToString() ?? "";
            Year = t.Year > 0 ? t.Year.ToString() : "";

            _artChanged = false;
            _artData = t.EmbeddedPictures.Count > 0 ? t.EmbeddedPictures[0].PictureData : null;
            AlbumArt = _artData != null ? BitmapFrom(_artData) : null;

            OnPropertyChanged(nameof(HasFile));
            RaiseNav();
            var note = _reviewNotes.TryGetValue(path, out var nv) ? $"  ({nv})" : "";
            Status = FolderMode ? $"{Position}  —  {FileName}{note}" : $"Geladen: {FileName}";
        }
        catch (Exception e)
        {
            Status = "Kon bestand niet laden: " + e.Message;
        }
    }

    public void SetArt(string imagePath)
    {
        try
        {
            _artData = System.IO.File.ReadAllBytes(imagePath);
            _artChanged = true;
            AlbumArt = BitmapFrom(_artData);
            Status = "Nieuwe albumhoes geladen — wordt bewaard bij Goedkeuren.";
        }
        catch (Exception e) { Status = "Kon afbeelding niet laden: " + e.Message; }
    }

    private void RemoveArt()
    {
        _artData = null;
        _artChanged = true;
        AlbumArt = null;
        Status = "Albumhoes wordt verwijderd bij Goedkeuren.";
    }

    private void ApplyAppleArtist()
    {
        if (IsBusy) return;
        if (_folderLoaded) BatchAppleFormat();
        else ApplyAppleFormatCurrent();
    }

    private void ApplyAppleFormatCurrent()
    {
        var primary = PrimaryArtist(string.IsNullOrWhiteSpace(Artist) ? AlbumArtist : Artist);
        Artist = AppleFormat(Artist);
        AlbumArtist = primary;
        Title = AppleTitle(Title);
        Album = AppleTitle(Album);
        Genre = AppleGenre(Genre);
        Status = "Apple-format toegepast (album-artiest = hoofdartiest).";
    }

    private void BatchAppleFormat()
    {
        IsBusy = true;
        Status = "Apple-format toepassen op de map…";
        var files = _allFiles.ToList();

        Task.Run(() =>
        {
            var review = new List<string>();
            var notes = new Dictionary<string, string>();
            int i = 0, changed = 0;
            foreach (var f in files)
            {
                i++;
                var snap = i;
                Dispatcher.UIThread.Post(() => Status = $"Apple-format {snap}/{files.Count}…");
                try
                {
                    var t = new Track(f);
                    var a0 = t.Artist ?? ""; var aa0 = t.AlbumArtist ?? ""; var ti0 = t.Title ?? "";
                    var al0 = t.Album ?? ""; var g0 = t.Genre ?? "";
                    var na = AppleFormat(a0); var naa = PrimaryArtist(a0.Length > 0 ? a0 : aa0); var nti = AppleTitle(ti0);
                    var nal = AppleTitle(al0); var ng = AppleGenre(g0);
                    if (na != a0 || naa != aa0 || nti != ti0 || nal != al0 || ng != g0)
                    {
                        t.Artist = na; t.AlbumArtist = naa; t.Title = nti; t.Album = nal; t.Genre = ng;
                        t.Save();
                        review.Add(f); notes[f] = "geformatteerd"; changed++;
                    }
                }
                catch { review.Add(f); notes[f] = "fout"; }
            }

            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                _reviewNotes.Clear();
                foreach (var kv in notes) _reviewNotes[kv.Key] = kv.Value;
                _files = review;
                _index = 0;
                if (_files.Count == 0)
                {
                    Status = $"Klaar — niets te wijzigen ({files.Count} nummers waren al goed.)";
                    OnPropertyChanged(nameof(Position));
                    RaiseNav();
                }
                else
                {
                    Load(_files[0]);
                    Status = $"{changed} geformatteerd. Loop de {_files.Count} wijzigingen langs en keur goed.";
                }
            });
        });
    }

    private void AutoFill()
    {
        if (IsBusy) return;
        if (_folderLoaded) BatchAutoFill();
        else AutoFillCurrent();
    }

    private async void AutoFillCurrent()
    {
        if (!HasFile) return;
        Status = "Metadata zoeken…";
        try
        {
            var rec = await Identify(Artist, Title, Album, _path);
            if (rec == null) { Status = "Geen match gevonden."; return; }
            if (string.IsNullOrWhiteSpace(Title) && rec.Title.Length > 0) Title = rec.Title;
            if (string.IsNullOrWhiteSpace(Artist) && rec.Artist.Length > 0) Artist = rec.Artist;
            if (string.IsNullOrWhiteSpace(AlbumArtist) && rec.Artist.Length > 0) AlbumArtist = rec.Artist;
            if (string.IsNullOrWhiteSpace(Album) && rec.Album.Length > 0) Album = rec.Album;
            if (string.IsNullOrWhiteSpace(Year) && rec.Year.Length >= 4) Year = rec.Year;
            if (string.IsNullOrWhiteSpace(Genre) && rec.Genre.Length > 0) Genre = rec.Genre;
            if (AlbumArt == null && rec.ReleaseId.Length > 0)
            {
                var art = await MusicBrainzClient.GetCoverArtAsync(rec.ReleaseId);
                if (art != null) { _artData = art; _artChanged = true; AlbumArt = BitmapFrom(art); }
            }
            Status = $"Aangevuld: {rec.Album} ({rec.Year}). Controleer en klik Goedkeuren.";
        }
        catch (Exception e) { Status = "Auto-fill mislukt: " + e.Message; }
    }

    private void BatchAutoFill()
    {
        IsBusy = true;
        Status = "Auto-fill van de map…";
        var files = _allFiles.ToList();

        Task.Run(async () =>
        {
            var review = new List<string>();
            var notes = new Dictionary<string, string>();
            int i = 0, changed = 0, notFound = 0;
            foreach (var f in files)
            {
                i++;
                var snap = i;
                Dispatcher.UIThread.Post(() => Status = $"Auto-fill {snap}/{files.Count}…");
                try
                {
                    var t = new Track(f);
                    var rec = await Identify(t.Artist ?? "", t.Title ?? "", t.Album ?? "", f);
                    if (rec == null) { review.Add(f); notes[f] = "niet gevonden"; notFound++; continue; }

                    bool ch = false;
                    if (string.IsNullOrWhiteSpace(t.Title) && rec.Title.Length > 0) { t.Title = rec.Title; ch = true; }
                    if (string.IsNullOrWhiteSpace(t.Artist) && rec.Artist.Length > 0) { t.Artist = rec.Artist; ch = true; }
                    if (string.IsNullOrWhiteSpace(t.AlbumArtist) && rec.Artist.Length > 0) { t.AlbumArtist = rec.Artist; ch = true; }
                    if (string.IsNullOrWhiteSpace(t.Album) && rec.Album.Length > 0) { t.Album = rec.Album; ch = true; }
                    if (t.Year <= 0 && rec.Year.Length >= 4 && int.TryParse(rec.Year, out var yy)) { t.Year = yy; ch = true; }
                    if (string.IsNullOrWhiteSpace(t.Genre) && rec.Genre.Length > 0) { t.Genre = rec.Genre; ch = true; }
                    if (t.EmbeddedPictures.Count == 0 && rec.ReleaseId.Length > 0)
                    {
                        var art = await MusicBrainzClient.GetCoverArtAsync(rec.ReleaseId);
                        if (art != null) { t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(art)); ch = true; }
                    }
                    if (ch) { t.Save(); review.Add(f); notes[f] = "gewijzigd"; changed++; }
                }
                catch { review.Add(f); notes[f] = "fout"; }
            }

            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                _reviewNotes.Clear();
                foreach (var kv in notes) _reviewNotes[kv.Key] = kv.Value;
                _files = review;
                _index = 0;
                if (_files.Count == 0)
                {
                    Status = $"Klaar — {changed} gewijzigd, {notFound} niet gevonden. Niets meer te reviewen.";
                    OnPropertyChanged(nameof(Position));
                    RaiseNav();
                }
                else
                {
                    Load(_files[0]);
                    Status = $"{changed} gewijzigd, {notFound} niet gevonden. Loop de {_files.Count} te controleren nummers langs.";
                }
            });
        });
    }

    // Tag-based MusicBrainz lookup, with an AcoustID fingerprint fallback for (near-)untagged files.
    private async Task<MbRecording?> Identify(string artist, string title, string album, string path)
    {
        var rec = await MusicBrainzClient.LookupRecordingAsync(artist, title, album);
        if (rec != null) return rec;
        if (!string.IsNullOrWhiteSpace(AcoustIdKey) && FingerprintService.Available)
        {
            var fp = await FingerprintService.IdentifyAsync(path, AcoustIdKey);
            if (fp != null)
            {
                rec = await MusicBrainzClient.LookupRecordingAsync(fp.Value.artist, fp.Value.title, "");
                if (rec == null && (fp.Value.title.Length > 0 || fp.Value.artist.Length > 0))
                    rec = new MbRecording { Title = fp.Value.title, Artist = fp.Value.artist };
            }
        }
        return rec;
    }

    private void Save()
    {
        if (!HasFile) return;
        bool artChangedNow = _artChanged;
        try
        {
            var t = new Track(_path)
            {
                Title = Title,
                Artist = Artist,
                AlbumArtist = AlbumArtist,
                Album = Album,
                Genre = Genre
            };
            if (int.TryParse(Track, out var tn)) t.TrackNumber = tn;
            if (int.TryParse(Disc, out var dn)) t.DiscNumber = dn;
            if (int.TryParse(Year, out var y)) t.Year = y;
            if (_artChanged)
            {
                t.EmbeddedPictures.Clear();
                if (_artData != null) t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(_artData));
                _artChanged = false;
            }
            t.Save();
        }
        catch (Exception e) { Status = "Opslaan mislukt: " + e.Message; }

        // Album art is set per album: when the cover changed, apply it to the rest of the album.
        // Run off the UI thread so approving stays instant on big albums.
        if (artChangedNow)
        {
            var path = _path;
            var art = _artData;
            Task.Run(() => ApplyArtToAlbum(path, art));
        }
    }

    // Apply (or clear) the current cover on every other track of the same album (same folder + same Album tag).
    private void ApplyArtToAlbum(string currentPath, byte[]? art)
    {
        var dir = System.IO.Path.GetDirectoryName(currentPath);
        if (dir == null) return;
        var wantAlbum = (Album ?? string.Empty).Trim();
        int n = 0;
        try
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*.*"))
            {
                if (string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!AudioExts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())) continue;
                if (System.IO.Path.GetFileName(f).StartsWith("._")) continue;
                try
                {
                    var t = new Track(f);
                    // Skip tracks from a different album that happen to sit in the same folder.
                    if (wantAlbum.Length > 0 && !string.Equals((t.Album ?? string.Empty).Trim(), wantAlbum, StringComparison.OrdinalIgnoreCase))
                        continue;
                    t.EmbeddedPictures.Clear();
                    if (art != null) t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(art));
                    t.Save();
                    n++;
                }
                catch { }
            }
        }
        catch { }
        if (n > 0)
        {
            var msg = art != null ? $"Albumhoes toegepast op {n + 1} tracks." : $"Albumhoes verwijderd van {n + 1} tracks.";
            Dispatcher.UIThread.Post(() => Status = msg);
        }
    }

    private void RaiseNav()
    {
        ApproveNextCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        ApplyAppleArtistCommand.RaiseCanExecuteChanged();
        AutoFillCommand.RaiseCanExecuteChanged();
        RemoveArtCommand.RaiseCanExecuteChanged();
        MatchAlbumCommand.RaiseCanExecuteChanged();
        ApplyAlbumMatchCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(FolderMode));
        OnPropertyChanged(nameof(HasPrev));
        OnPropertyChanged(nameof(Position));
    }

    // Shared formatting (see TextFormat / GenreFormat) so the editor and the organize pipeline agree.
    private static string PrimaryArtist(string artist) => TextFormat.PrimaryArtist(artist);
    private static string AppleFormat(string artist) => TextFormat.AppleArtist(artist);
    private static string AppleTitle(string s) => TextFormat.Title(s);
    private static string AppleGenre(string s) => GenreFormat.Normalize(s);

    private static Bitmap? BitmapFrom(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try { return new Bitmap(new System.IO.MemoryStream(data)); }
        catch { return null; }
    }
}
