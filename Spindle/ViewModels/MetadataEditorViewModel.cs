using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ATL;

namespace Spindle.ViewModels;

/// <summary>
/// Edit tags + album art (ATL). Open a single file, or a folder (recursief) and step through it.
/// "Auto-fill" on a folder runs a batch via MusicBrainz (+ AcoustID fingerprint for untagged files),
/// then narrows the step-through to only the changed / not-found tracks. Goedkeuren slaat automatisch op.
/// </summary>
public class MetadataEditorViewModel : ViewModelBase
{
    private static readonly string[] AudioExts = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };

    public TagGridViewModel Grid { get; }

    /// <summary>Album-artist autocomplete suggestions (fed by MainViewModel), bound by the editor view.</summary>
    public ObservableCollection<string> ArtistSuggestions { get; } = new();

    /// <summary>Show the "Open file/folder" entry buttons (true in the Metadata tab, false when embedded in the Inbox).</summary>
    public bool ShowSourceButtons { get; init; } = true;

    // ---- Album "health" checks shown as chips in the editor (what's wrong) ----
    public ObservableCollection<CheckItem> Checks { get; } = new();
    // Inbox-only pruning: multiple downloaded versions / in-folder duplicates (populated by the host).
    public ObservableCollection<StagingVersionViewModel> Versions { get; } = new();
    public ObservableCollection<StagingDupGroup> Duplicates { get; } = new();
    public bool HasVersions => Versions.Count > 1;
    public bool HasDuplicates => Duplicates.Count > 0;

    private static readonly string[] LosslessExtns = { ".flac", ".wav", ".aiff", ".aif", ".alac" };

    private readonly UndoJournal? _undo;

    public MetadataEditorViewModel(LibraryService? lib = null, UndoJournal? undo = null)
    {
        _undo = undo;
        Grid = new TagGridViewModel(lib, undo);
        Versions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVersions));
        Duplicates.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDuplicates));
        ModeFormCommand = new RelayCommand(() => EditorMode = "form");
        ModeTableCommand = new RelayCommand(() => { if (!Grid.HasDirty) Grid.Reload(); EditorMode = "tabel"; });
        ModeConvCommand = new RelayCommand(() => { if (!Grid.HasDirty) Grid.Reload(); EditorMode = "conv"; });
        SaveCommand = new RelayCommand(Save, () => HasFile && !IsBusy);
        AutoFillCommand = new RelayCommand(AutoFill, () => HasFile && !IsBusy);
        FetchLyricsCommand = new RelayCommand(() => _ = FetchLyricsAsync(), () => HasFile && !IsBusy);
        ApplyGenreToAlbumCommand = new RelayCommand(ApplyGenreToAlbum, () => HasFile && !IsBusy);
        RemoveArtCommand = new RelayCommand(RemoveArt, () => HasFile && !IsBusy);
        ApplyArtNowCommand = new RelayCommand(ApplyArtNow, () => HasFile && !IsBusy);
        NextAlbumCommand = new RelayCommand(() => GoToAlbum(_albumIndex + 1), () => HasNextAlbum && !IsBusy);
        PrevAlbumCommand = new RelayCommand(() => GoToAlbum(_albumIndex - 1), () => HasPrevAlbum && !IsBusy);
        MatchAlbumCommand = new RelayCommand(MatchAlbum, () => HasFile && !IsBusy && AlbumMatchable);
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

    // ---- Album-match safety: only allow matching when the loaded set really is ONE album. ----
    // Stops "apply to the whole album" from homogenizing a heterogeneous bucket (the "Bee Gees" bug).
    private bool _albumMatchable = true;
    public bool AlbumMatchable
    {
        get => _albumMatchable;
        private set { if (SetField(ref _albumMatchable, value)) { MatchAlbumCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(AlbumMatchBlocked)); } }
    }
    public bool AlbumMatchBlocked => !_albumMatchable;

    private string _albumMatchBlockReason = "";
    public string AlbumMatchBlockReason { get => _albumMatchBlockReason; private set => SetField(ref _albumMatchBlockReason, value); }

    private static string NormKey(string? s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    // Read the loaded files and decide whether they form one coherent, matchable album.
    // Compute the album "health" checks from the loaded files (+ async MusicBrainz completeness).
    private void RecomputeChecks()
    {
        var files = _allFiles.ToList();
        Task.Run(async () =>
        {
            bool tags = true, cover = true, year = true, genre = true, lossless = true, single = true;
            var trackKeys = new HashSet<(int, int)>();
            string artist = "", album = ""; int n = 0;
            foreach (var f in files)
            {
                try
                {
                    var t = new Track(f);
                    var eff = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : (t.Artist ?? "");
                    if (string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(eff)) tags = false;
                    if (t.EmbeddedPictures.Count == 0 &&
                        !System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f) ?? "", "folder.jpg"))) cover = false;
                    if (((int?)t.Year ?? 0) <= 0) year = false;
                    if (string.IsNullOrWhiteSpace(t.Genre)) genre = false;
                    if (!LosslessExtns.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())) lossless = false;
                    int tn = t.TrackNumber ?? 0, dc = t.DiscNumber ?? 0;
                    if (tn > 0 && !trackKeys.Add((dc, tn))) single = false;
                    if (artist.Length == 0) { artist = eff; album = t.Album ?? ""; }
                    n++;
                }
                catch { }
            }
            Dispatcher.UIThread.Post(() =>
            {
                Checks.Clear();
                Checks.Add(new CheckItem("tags complete", tags));
                Checks.Add(new CheckItem("cover art", cover));
                Checks.Add(new CheckItem("year", year));
                Checks.Add(new CheckItem("genre", genre));
                Checks.Add(new CheckItem("single version", single));
                Checks.Add(new CheckItem("lossless", lossless));
                Checks.Add(new CheckItem("completeness check pending (MusicBrainz)…", null));
            });
            // Async completeness: compare the loaded track count against the matched release
            // (same source as "Album match"). Only show the chip when we actually know the count.
            int exp = 0;
            try
            {
                if (n > 0 && artist.Length > 0 && album.Length > 0)
                {
                    var cands = await AlbumMetadata.SearchAsync(artist, album, n, DiscogsToken);
                    var best = cands?.FirstOrDefault();
                    if (best != null) { await AlbumMetadata.EnsureTracksAsync(best, DiscogsToken); exp = best.TrackTitles.Count; }
                }
            }
            catch { }
            Dispatcher.UIThread.Post(() =>
            {
                if (Checks.Count == 0) return;
                if (exp > 0) Checks[Checks.Count - 1] = new CheckItem($"complete ({n}/{exp} tracks)", n >= exp);
                else Checks.RemoveAt(Checks.Count - 1);   // unknown → don't show a noisy chip
            });
        });
    }

    private void RecomputeAlbumMatchable()
    {
        var files = _allFiles.ToList();
        Task.Run(() =>
        {
            bool ok = true; string reason = "";
            if (files.Count > 1)
            {
                var artists = new HashSet<string>(); var albums = new HashSet<string>();
                string sampleAlbum = "", sampleTitle = "";
                foreach (var f in files)
                {
                    try
                    {
                        var t = new Track(f);
                        var eff = !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist : (t.Artist ?? "");
                        artists.Add(NormKey(eff)); albums.Add(NormKey(t.Album ?? ""));
                        if (sampleAlbum.Length == 0 && !string.IsNullOrWhiteSpace(t.Album)) { sampleAlbum = t.Album ?? ""; sampleTitle = t.Title ?? ""; }
                    }
                    catch { }
                }
                if (albums.Count > 1 || artists.Count > 1)
                {
                    ok = false;
                    reason = $"These {files.Count} tracks aren't one album ({artists.Count} artist(s), {albums.Count} album name(s)). Album match is off so it can't give them all the same identity — fix tracks individually.";
                }
                else if (Singles.IsSingle(sampleAlbum, sampleTitle))
                {
                    ok = false;
                    reason = "These look like singles (no real album). Album match is off so they don't all get the same album — fix them one track at a time.";
                }
            }
            Dispatcher.UIThread.Post(() => { AlbumMatchable = ok; AlbumMatchBlockReason = reason; });
        });
    }

    private static UndoJournal.TagOp Snapshot(Track t, string path)
    {
        int tn = (int?)t.TrackNumber ?? 0, dn = (int?)t.DiscNumber ?? 0, y = (int?)t.Year ?? 0;
        return new UndoJournal.TagOp(path, t.Title ?? "", t.Artist ?? "", t.AlbumArtist ?? "",
            t.Album ?? "", t.Genre ?? "", tn > 0 ? tn.ToString() : "", dn > 0 ? dn.ToString() : "", y.ToString());
    }

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

    // ---- album queue: edit several albums one by one (⌘-click multiple, then open) ----
    private List<(List<string> Files, string Label)> _albumQueue = new();
    private int _albumIndex;
    public bool HasAlbumQueue => _albumQueue.Count > 1;
    public bool HasNextAlbum => _albumIndex < _albumQueue.Count - 1;
    public bool HasPrevAlbum => _albumIndex > 0;
    public string AlbumQueuePosition => HasAlbumQueue ? $"Album {_albumIndex + 1} of {_albumQueue.Count}" : string.Empty;
    public RelayCommand NextAlbumCommand { get; }
    public RelayCommand PrevAlbumCommand { get; }

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

    private string _status = "Open a file or folder (album). Auto-fill completes missing tags via MusicBrainz.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand AutoFillCommand { get; }
    public RelayCommand FetchLyricsCommand { get; }
    public RelayCommand ApplyGenreToAlbumCommand { get; }

    /// <summary>Standard genres for the album-genre picker in the form.</summary>
    public IReadOnlyList<string> GenreOptions => Genres.Standard;
    public RelayCommand RemoveArtCommand { get; }
    public RelayCommand ApplyArtNowCommand { get; }

    // ---- weergavemodus: formulier / tabel / converters (fase 2) ----
    public RelayCommand ModeFormCommand { get; }
    public RelayCommand ModeTableCommand { get; }
    public RelayCommand ModeConvCommand { get; }

    private string _editorMode = "form";
    public string EditorMode
    {
        get => _editorMode;
        private set
        {
            if (SetField(ref _editorMode, value))
            {
                OnPropertyChanged(nameof(IsFormMode));
                OnPropertyChanged(nameof(IsTableMode));
                OnPropertyChanged(nameof(IsConvMode));
            }
        }
    }
    public bool IsFormMode => _editorMode == "form";
    public bool IsTableMode => _editorMode == "tabel";
    public bool IsConvMode => _editorMode == "conv";

    public void Open(string path)
    {
        _albumQueue = new(); _albumIndex = 0; RaiseAlbumQueue();
        _folderLoaded = false;
        _reviewNotes.Clear();
        _files = new List<string> { path };
        _allFiles = new List<string> { path };
        _index = 0;
        Load(path);
        Grid.Load(_allFiles);
        EditorMode = "form";
        RecomputeAlbumMatchable();
        RecomputeChecks();
    }

    public void LoadFolder(string folder)
    {
        _albumQueue = new(); _albumIndex = 0; RaiseAlbumQueue();
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
            if (_files.Count == 0) { Status = "No audio files in this folder (incl. subfolders)."; return; }
            _index = 0;
            Load(_files[0]);
            Status = $"{_files.Count} tracks loaded. Click Auto-fill to update the whole folder.";
            Grid.Load(_allFiles);
            EditorMode = _files.Count > 1 ? "tabel" : "form";
            RecomputeAlbumMatchable();
        RecomputeChecks();
        }
        catch (Exception e)
        {
            Status = "Couldn't read folder: " + e.Message;
        }
    }

    /// <summary>Load an explicit set of files (e.g. the "no tags" / "no cover" lists from Gezondheid) and step through them.</summary>
    public void LoadFiles(IReadOnlyList<string> files, string? context = null)
    {
        _albumQueue = new();
        _albumIndex = 0;
        RaiseAlbumQueue();
        LoadFilesCore(files, context);
    }

    /// <summary>Open several albums one after another. The editor shows a "next album" strip; each album stays a coherent unit.</summary>
    public void LoadAlbumQueue(IReadOnlyList<(IReadOnlyList<string> Files, string Label)> albums)
    {
        var list = albums.Where(a => a.Files.Count > 0).Select(a => (a.Files.ToList(), a.Label)).ToList();
        if (list.Count == 0) { LoadFiles(System.Array.Empty<string>()); return; }
        if (list.Count == 1) { LoadFiles(list[0].Item1, list[0].Label); return; }
        _albumQueue = list;
        _albumIndex = 0;
        RaiseAlbumQueue();
        LoadFilesCore(list[0].Item1, list[0].Label);
    }

    private void GoToAlbum(int i)
    {
        if (i < 0 || i >= _albumQueue.Count) return;
        _albumIndex = i;
        RaiseAlbumQueue();
        LoadFilesCore(_albumQueue[i].Files, _albumQueue[i].Label);
    }

    private void RaiseAlbumQueue()
    {
        OnPropertyChanged(nameof(HasAlbumQueue));
        OnPropertyChanged(nameof(HasNextAlbum));
        OnPropertyChanged(nameof(HasPrevAlbum));
        OnPropertyChanged(nameof(AlbumQueuePosition));
        NextAlbumCommand.RaiseCanExecuteChanged();
        PrevAlbumCommand.RaiseCanExecuteChanged();
    }

    private void LoadFilesCore(IReadOnlyList<string> files, string? context = null)
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
            Status = "No files to edit.";
            return;
        }
        _index = 0;
        Load(_files[0]);
        Status = context ?? $"{_files.Count} tracks loaded.";
        Grid.Load(_allFiles);
        EditorMode = _files.Count > 1 ? "tabel" : "form";
        RecomputeAlbumMatchable();
        RecomputeChecks();
    }

    private async void MatchAlbum()
    {
        if (IsBusy || !HasFile) return;
        var artist = (!string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist).Trim();
        var album = (Album ?? string.Empty).Trim();
        if (album.Length == 0) { Status = "Fill in the Album field first to match."; return; }
        IsBusy = true;
        Status = $"Searching album: {artist} – {album}…";
        try
        {
            var list = await AlbumMetadata.SearchAsync(artist, album, _allFiles.Count, DiscogsToken);
            AlbumCandidates.Clear();
            foreach (var c in list) AlbumCandidates.Add(c);
            SelectedCandidate = AlbumCandidates.FirstOrDefault();
            ShowCandidates = AlbumCandidates.Count > 0;
            Status = AlbumCandidates.Count > 0
                ? $"{AlbumCandidates.Count} editions found — pick the right one and apply to the whole album."
                : "No album match found (try adjusting artist/album).";
        }
        catch (Exception e) { Status = "Album match failed: " + e.Message; }
        finally { IsBusy = false; }
    }

    private async void ApplyAlbumMatch()
    {
        var m = SelectedCandidate;
        if (m == null || IsBusy) return;
        // Failsafe: never blanket-apply one release to a heterogeneous set (the "Bee Gees" bug).
        if (!AlbumMatchable) { Status = AlbumMatchBlockReason; ShowCandidates = false; return; }
        IsBusy = true;
        Status = $"Applying '{m.Album}' to the album…";
        try
        {
            await AlbumMetadata.EnsureTracksAsync(m, DiscogsToken);
            var cover = await AlbumMetadata.DownloadCoverAsync(m.CoverUrl);
            var files = _allFiles.ToList();
            int year = (m.Year.Length >= 4 && int.TryParse(m.Year.Substring(0, 4), out var yy)) ? yy : 0;
            var before = new List<UndoJournal.TagOp>();
            await Task.Run(() =>
            {
                foreach (var f in files)
                {
                    try
                    {
                        var t = new Track(f);
                        before.Add(Snapshot(t, f));
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
            _undo?.RecordTags($"Album match: {m.Album} → {before.Count} tracks", before);
            ShowCandidates = false;
            Load(_path);
            Status = $"Album '{m.Album}' applied to {files.Count} tracks (source: {m.Source})  ·  Cmd+Z to undo.";
        }
        catch (Exception e) { Status = "Apply failed: " + e.Message; }
        finally { IsBusy = false; }
    }

    private string _loadedAlbum = "", _loadedAlbumArtist = "", _loadedGenre = "", _loadedYear = "";

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
            _loadedAlbum = Album; _loadedAlbumArtist = AlbumArtist; _loadedGenre = Genre; _loadedYear = Year;

            _artChanged = false;
            _artData = t.EmbeddedPictures.Count > 0 ? t.EmbeddedPictures[0].PictureData : null;
            AlbumArt = _artData != null ? BitmapFrom(_artData) : null;

            OnPropertyChanged(nameof(HasFile));
            RaiseNav();
            var note = _reviewNotes.TryGetValue(path, out var nv) ? $"  ({nv})" : "";
            Status = FolderMode ? $"{Position}  —  {FileName}{note}" : $"Loaded: {FileName}";
        }
        catch (Exception e)
        {
            Status = "Couldn't load file: " + e.Message;
        }
    }

    /// <summary>Status helper for code-behind (e.g. clipboard paste feedback).</summary>
    public void Notify(string msg) => Status = msg;

    /// <summary>Set cover art from raw image bytes (clipboard paste, Mp3tag-style).</summary>
    public void SetArtBytes(byte[] data)
    {
        try
        {
            _artData = data;
            _artChanged = true;
            AlbumArt = BitmapFrom(data);
            Status = "Cover pasted — saved on Approve, or use 'Apply to whole album'.";
        }
        catch (Exception e) { Status = "Couldn't read pasted image: " + e.Message; }
    }

    /// <summary>Write the current cover to ALL loaded files immediately (no Approve needed).</summary>
    private void ApplyArtNow()
    {
        if (_artData == null) { Status = "No cover loaded — choose or paste one first."; return; }
        var files = _allFiles.ToList();
        var art = _artData;
        IsBusy = true;
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
                    t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(art));
                    t.Save();
                    n++;
                }
                catch { }
            }
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                _artChanged = false;
                Status = $"Cover applied to {n} files.";
            });
        });
    }

    public void SetArt(string imagePath)
    {
        try
        {
            _artData = System.IO.File.ReadAllBytes(imagePath);
            _artChanged = true;
            AlbumArt = BitmapFrom(_artData);
            Status = "New album art loaded — saved on Approve.";
        }
        catch (Exception e) { Status = "Couldn't load image: " + e.Message; }
    }

    private void RemoveArt()
    {
        _artData = null;
        _artChanged = true;
        AlbumArt = null;
        Status = "Album art will be removed on Approve.";
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
        Status = "Searching metadata…";
        try
        {
            var rec = await Identify(Artist, Title, Album, _path);
            if (rec == null) { Status = "No match found."; return; }
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
            Status = $"Filled in: {rec.Album} ({rec.Year}). Review and click Approve.";
        }
        catch (Exception e) { Status = "Auto-fill failed: " + e.Message; }
    }

    private void BatchAutoFill()
    {
        IsBusy = true;
        Status = "Auto-filling the folder…";
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
                    if (rec == null) { review.Add(f); notes[f] = "not found"; notFound++; continue; }

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
                    if (ch) { t.Save(); review.Add(f); notes[f] = "changed"; changed++; }
                }
                catch { review.Add(f); notes[f] = "error"; }
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
                    Status = $"Done — {changed} changed, {notFound} not found. Nothing left to review.";
                    OnPropertyChanged(nameof(Position));
                    RaiseNav();
                }
                else
                {
                    Load(_files[0]);
                    Status = $"{changed} changed, {notFound} not found. Step through the {_files.Count} tracks to review.";
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
        catch (Exception e) { Status = "Save failed: " + e.Message; }

        // Album-niveau-velden (album, album-artiest, genre, jaar) en de hoes gelden voor het HELE album:
        // propageer alleen wat veranderd is naar de andere tracks van dit album (zelfde map).
        var origAlbum = _loadedAlbum;
        bool cAlbum = !string.Equals((Album ?? "").Trim(), (_loadedAlbum ?? "").Trim());
        bool cAa = !string.Equals((AlbumArtist ?? "").Trim(), (_loadedAlbumArtist ?? "").Trim());
        bool cGenre = !string.Equals((Genre ?? "").Trim(), (_loadedGenre ?? "").Trim());
        bool cYear = !string.Equals((Year ?? "").Trim(), (_loadedYear ?? "").Trim());
        var path = _path; var art = _artData;
        var album = Album; var aa = AlbumArtist; var genre = Genre; var year = Year;
        if (cAlbum || cAa || cGenre || cYear)
            Task.Run(() => ApplyAlbumFieldsToSiblings(path, origAlbum, cAlbum, album, cAa, aa, cGenre, genre, cYear, year, artChangedNow, art));
        else if (artChangedNow)
            Task.Run(() => ApplyArtToAlbum(path, art));
        _loadedAlbum = Album; _loadedAlbumArtist = AlbumArtist; _loadedGenre = Genre; _loadedYear = Year;
    }

    // Propagate the changed album-level fields (and cover) to the other tracks of the same album.
    private void ApplyAlbumFieldsToSiblings(string currentPath, string originalAlbum,
        bool setAlbum, string album, bool setAa, string albumArtist, bool setGenre, string genre,
        bool setYear, string year, bool setArt, byte[]? art)
    {
        var dir = System.IO.Path.GetDirectoryName(currentPath);
        if (dir == null) return;
        var match = (originalAlbum ?? string.Empty).Trim();
        int.TryParse(year, out var yearVal);
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
                    if (match.Length > 0 && !string.Equals((t.Album ?? string.Empty).Trim(), match, StringComparison.OrdinalIgnoreCase))
                        continue; // ander album in dezelfde map
                    if (setAlbum) t.Album = album;
                    if (setAa) t.AlbumArtist = albumArtist;
                    if (setGenre) t.Genre = genre;
                    if (setYear) t.Year = yearVal;
                    if (setArt) { t.EmbeddedPictures.Clear(); if (art != null) t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(art)); }
                    t.Save();
                    n++;
                }
                catch { }
            }
        }
        catch { }
        if (n > 0) Dispatcher.UIThread.Post(() => Status = $"Album fields applied to {n + 1} tracks.");
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
            var msg = art != null ? $"Album art applied to {n + 1} tracks." : $"Album art removed from {n + 1} tracks.";
            Dispatcher.UIThread.Post(() => Status = msg);
        }
    }

    private void RaiseNav()
    {
        SaveCommand.RaiseCanExecuteChanged();
        AutoFillCommand.RaiseCanExecuteChanged();
        FetchLyricsCommand.RaiseCanExecuteChanged();
        ApplyGenreToAlbumCommand.RaiseCanExecuteChanged();
        RemoveArtCommand.RaiseCanExecuteChanged();
        ApplyArtNowCommand.RaiseCanExecuteChanged();
        MatchAlbumCommand.RaiseCanExecuteChanged();
        ApplyAlbumMatchCommand.RaiseCanExecuteChanged();
        NextAlbumCommand.RaiseCanExecuteChanged();
        PrevAlbumCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(FolderMode));
        OnPropertyChanged(nameof(HasPrev));
        OnPropertyChanged(nameof(Position));
    }

    // Online lyrics (LRCLIB): synced → sidecar .lrc next to the file, plain → embedded lyrics tag.
    private async Task FetchLyricsAsync()
    {
        if (IsBusy || _allFiles.Count == 0) return;
        IsBusy = true;
        var files = _allFiles.ToList();
        var counts = await Task.Run(async () =>
        {
            int g = 0, m = 0, i = 0;
            foreach (var f in files)
            {
                var snap = ++i;
                Dispatcher.UIThread.Post(() => Status = $"Fetching lyrics… {snap}/{files.Count}");
                if (await Lyrics.ApplyToFileAsync(f)) g++; else m++;
            }
            return (Got: g, Miss: m);
        });
        IsBusy = false;
        Status = $"Lyrics: {counts.Got} found, {counts.Miss} missing.";
    }

    // Write the current Genre to every loaded track of the album (one undo batch).
    private void ApplyGenreToAlbum()
    {
        if (IsBusy || _allFiles.Count == 0) return;
        var genre = (Genre ?? "").Trim();
        var files = _allFiles.ToList();
        IsBusy = true;
        Task.Run(() =>
        {
            var before = new List<UndoJournal.TagOp>();
            foreach (var f in files)
            {
                try { var t = new Track(f); before.Add(Snapshot(t, f)); t.Genre = genre; t.Save(); } catch { }
            }
            _undo?.RecordTags($"Genre → \"{genre}\": {files.Count} tracks", before);
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                Grid.Reload();
                Status = $"Genre '{genre}' applied to {files.Count} tracks (Cmd+Z = undo).";
            });
        });
    }

    private static Bitmap? BitmapFrom(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try { return new Bitmap(new System.IO.MemoryStream(data)); }
        catch { return null; }
    }
}
