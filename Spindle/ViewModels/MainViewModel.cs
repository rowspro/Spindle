using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Spindle.ViewModels;

public class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        AddGenreCommand = new RelayCommand(AddGenre, () => !string.IsNullOrWhiteSpace(NewGenre));
        PlayerViewModel.Current = Player;
        Meta = new MetadataEditorViewModel(Lib, Undo);
        Sync = new SyncViewModel(Lib);

        Library = new LibraryViewModel(
            onEdit: (files, status) =>
            {
                Meta.LoadFiles(files, status);
                SelectedTabIndex = 0; // Metadata
            }, lib: Lib, undo: Undo, template: () => FilenameTemplate,
            openLibrary: () => SelectedTabIndex = 6);

        Staging = new StagingViewModel(
            onFix: (files, status) => { Meta.LoadFiles(files, status); SelectedTabIndex = 0; }, // Metadata
            lib: Lib, undo: Undo,
            template: () => FilenameTemplate);

        Browser = new BrowserViewModel(Lib, () => MusicLibrary,
            (files, status) => { Meta.LoadFiles(files, status); SelectedTabIndex = 0; });

        Galaxy = new GalaxyViewModel(Lib, () => MusicLibrary,
            onOpenAlbum: (artist, album) => { SelectedTabIndex = 6; Browser.FocusAlbum(artist, album); },
            getAlbumLevel: () => GalaxyAlbumLevel,
            setAlbumLevel: v => GalaxyAlbumLevel = v);

        Wantlist = new WantlistViewModel(Lib, () => MusicLibrary, () => DownloadFilePath);
        _mirror.Status += msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => MirrorStatus = msg);

        LoadFromConfig(Settings.Load());

        // Pre-fill tool folders with the library path, but never overwrite a remembered value.
        var fallback = !string.IsNullOrWhiteSpace(MusicLibrary) ? MusicLibrary
            : (!string.IsNullOrWhiteSpace(DownloadFilePath) ? DownloadFilePath : string.Empty);
        if (string.IsNullOrWhiteSpace(Sync.LibraryFolder)) Sync.LibraryFolder = fallback;
        if (string.IsNullOrWhiteSpace(Library.LibraryFolder)) Library.LibraryFolder = fallback;
        Staging.NieuwFolder = DownloadFilePath;
        Staging.LibraryFolder = MusicLibrary;

        // Watchers + globale statusbalk — de index ververst zichzelf op bestandswijzigingen.
        Lib.ScanProgress += (sroot, d, t) =>
        {
            IndexBusy = true;
            IndexPercent = t > 0 ? 100.0 * d / t : 0;
            GlobalStatus = $"Indexing… {d}/{t} — {System.IO.Path.GetFileName(sroot.TrimEnd('/'))}";
        };
        Lib.Changed += () =>
        {
            IndexBusy = false;
            GlobalStatus = $"{Lib.Index.TrackCount():N0} tracks indexed";
            RefreshArtistSuggestions();
            Wantlist.RefreshOwned();
            MaybeKickMirror();   // iTunes-modus: spiegel bijwerken zodra de bieb verandert
        };
        Lib.Configure(MusicLibrary, DownloadFilePath);
        CheckFolderSanity();

        // Externe-SSD-wacht: meld als het bieb-volume weg is en herstel automatisch zodra het terugkomt.
        _volTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _volTimer.Tick += (_, _) =>
        {
            bool missing = !string.IsNullOrWhiteSpace(MusicLibrary) && !System.IO.Directory.Exists(MusicLibrary);
            if (missing && !_volMissing)
            {
                _volMissing = true;
                GlobalStatus = "⚠ Library volume not mounted — waiting for it to come back…";
            }
            else if (!missing && _volMissing)
            {
                _volMissing = false;
                GlobalStatus = "Library volume is back — re-indexing…";
                Lib.Configure(MusicLibrary, DownloadFilePath);
            }
            else if (missing)
                GlobalStatus = "⚠ Library volume not mounted — waiting for it to come back…";
        };
        _volTimer.Start();
    }

    // ---- Kern: index-service, undo-journal, speler ----
    public LibraryService Lib { get; } = new();
    public UndoJournal Undo { get; } = new();
    public PlayerViewModel Player { get; } = new();

    /// <summary>Known album artists from the library — feeds the autocomplete boxes.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> ArtistSuggestions { get; } = new();

    private void RefreshArtistSuggestions()
    {
        try
        {
            var arts = Lib.Index.AllArtists(MusicLibrary);
            ArtistSuggestions.Clear();
            foreach (var a in arts) ArtistSuggestions.Add(a);
        }
        catch { }
    }

    private string _globalStatus = "Ready.";
    public string GlobalStatus { get => _globalStatus; private set => SetField(ref _globalStatus, value); }

    private bool _indexBusy;
    public bool IndexBusy { get => _indexBusy; private set => SetField(ref _indexBusy, value); }

    private double _indexPercent;
    public double IndexPercent { get => _indexPercent; private set => SetField(ref _indexPercent, value); }

    /// <summary>Space in the browser: play the selected track's album (or toggle pause on the same track).</summary>
    public void PlayBrowserSelection()
    {
        var a = Browser.SelectedAlbum;
        if (a == null) { Player.PlayPause(); return; }
        var items = a.Tracks.Select(t => new PlayerItem
        {
            Path = t.Path,
            Title = string.IsNullOrWhiteSpace(t.Title) ? System.IO.Path.GetFileName(t.Path) : t.Title,
            Sub = a.ArtistText + " — " + a.Title,
            Duration = t.Duration,
        }).ToList();
        int start = 0;
        var selPath = Browser.SelectedTrack?.Path;
        if (selPath != null)
        {
            var i = items.FindIndex(x => x.Path == selPath);
            if (i >= 0) start = i;
        }
        if (Player.HasTrack && Player.CurrentPath == items[start].Path) { Player.PlayPause(); return; }
        Player.PlayQueue(items, start);
    }

    public void UndoLast()
    {
        var (done, total, label) = Undo.UndoLast();
        GlobalStatus = total == 0
            ? "Nothing to undo."
            : $"Undone: {label} — {done}/{total} restored.";
        if (total > 0)
            Task.Run(() => { Lib.Refresh(MusicLibrary); Lib.Refresh(DownloadFilePath); });
    }


    // ---- Top bar ----
    public string CurrentSection => SelectedTabIndex switch
    {
        0 => "Metadata", 1 => "Health", 2 => "Duplicates", 3 => "Transfer",
        4 => "Settings", 5 => "Inbox", 6 => "Library", 7 => "Galaxy", 8 => "Wantlist", _ => "Spindle"
    };

    public string UserInitial => "S";

    // ---- Cmd+F command palette ----
    private static readonly (string Name, int Idx, string Glyph)[] PaletteSections =
    {
        ("Galaxy", 7, ""),
        ("Library", 6, ""),
        ("Inbox", 5, ""),
        ("Wantlist", 8, "\uE03B"),
        ("Metadata", 0, ""),
        ("Duplicates", 2, ""),
        ("Health", 1, ""),
        ("Transfer", 3, ""),
        ("Settings", 4, ""),
    };

    private bool _isPaletteOpen;
    public bool IsPaletteOpen { get => _isPaletteOpen; set => SetField(ref _isPaletteOpen, value); }

    private string _paletteQuery = string.Empty;
    public string PaletteQuery { get => _paletteQuery; set { if (SetField(ref _paletteQuery, value)) RebuildPalette(); } }

    public ObservableCollection<PaletteItem> PaletteResults { get; } = new();

    private PaletteItem? _selectedPaletteItem;
    public PaletteItem? SelectedPaletteItem { get => _selectedPaletteItem; set => SetField(ref _selectedPaletteItem, value); }

    // ---- Activity register (top bar) ----
    public ObservableCollection<HistoryRow> HistoryItems { get; } = new();
    private bool _isHistoryOpen;
    public bool IsHistoryOpen { get => _isHistoryOpen; set => SetField(ref _isHistoryOpen, value); }
    public bool HistoryEmpty => HistoryItems.Count == 0;

    public void OpenHistory()
    {
        HistoryItems.Clear();
        var h = Undo.History();
        for (int i = 0; i < h.Count; i++)
        {
            var parts = new List<string>();
            if (h[i].Moves > 0) parts.Add($"{h[i].Moves} file move(s)");
            if (h[i].Tags > 0) parts.Add($"{h[i].Tags} tag snapshot(s)");
            HistoryItems.Add(new HistoryRow(h[i].Label, string.Join(" · ", parts), i == 0));
        }
        OnPropertyChanged(nameof(HistoryEmpty));
        IsHistoryOpen = true;
    }

    public void CloseHistory() => IsHistoryOpen = false;
    public void UndoTop() { UndoLast(); OpenHistory(); }

    public void OpenPalette() { PaletteQuery = string.Empty; RebuildPalette(); IsPaletteOpen = true; }
    public void ClosePalette() => IsPaletteOpen = false;

    private void RebuildPalette()
    {
        PaletteResults.Clear();
        var q = (PaletteQuery ?? string.Empty).Trim();
        foreach (var s in PaletteSections)
        {
            if (s.Idx == 3 && ItunesMode) continue;   // Transfer verbergen in iTunes-modus
            if (q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                PaletteResults.Add(new PaletteItem(s.Name, "Go to " + s.Name, s.Glyph,
                    () => { SelectedTabIndex = s.Idx; ClosePalette(); }));
        }
        if (q.Length >= 2 && !string.IsNullOrWhiteSpace(MusicLibrary))
        {
            foreach (var a in Lib.Index.Albums(MusicLibrary).Where(x =>
                         x.Album.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                         x.AlbumArtist.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(6))
            {
                var aa = a.AlbumArtist; var al = a.Album;
                PaletteResults.Add(new PaletteItem(
                    (aa.Length > 0 ? aa + " — " : "") + al, "Open in Library", "",
                    () => { SelectedTabIndex = 6; Browser.FocusAlbum(aa, al); ClosePalette(); }));
            }
        }
        SelectedPaletteItem = PaletteResults.Count > 0 ? PaletteResults[0] : null;
    }

    public void MovePaletteSelection(int delta)
    {
        if (PaletteResults.Count == 0) return;
        int i = SelectedPaletteItem != null ? PaletteResults.IndexOf(SelectedPaletteItem) : -1;
        i = Math.Clamp(i + delta, 0, PaletteResults.Count - 1);
        SelectedPaletteItem = PaletteResults[i];
    }

    public void RunSelectedPalette() => SelectedPaletteItem?.RunCommand.Execute(null);

    // ---- Paths ----
    private string _downloadFilePath = string.Empty;
    private string _musicLibrary = string.Empty;
    private string _searchFilePath = string.Empty;
    private string _downloadArchiveFilePath = string.Empty;
    public string DownloadFilePath { get => _downloadFilePath; set { if (SetField(ref _downloadFilePath, value)) { Staging.NieuwFolder = value; CheckFolderSanity(); } } }
    public string MusicLibrary { get => _musicLibrary; set { if (SetField(ref _musicLibrary, value)) { OnPropertyChanged(nameof(HasMusicLibrary)); Staging.LibraryFolder = value; CheckFolderSanity(); } } }
    public bool HasMusicLibrary => !string.IsNullOrWhiteSpace(MusicLibrary);

    // ---- Map-validatie: inbox en bieb mogen niet samenvallen of genest zijn ----
    private Avalonia.Threading.DispatcherTimer? _volTimer;
    private bool _volMissing;
    private string _folderWarning = "";
    public string FolderWarning { get => _folderWarning; private set => SetField(ref _folderWarning, value); }

    private void CheckFolderSanity()
    {
        var a = MusicLibrary; var b = DownloadFilePath;
        string w = "";
        if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
        {
            string fa = a, fb = b;
            try
            {
                fa = System.IO.Path.GetFullPath(a).TrimEnd('/');
                fb = System.IO.Path.GetFullPath(b).TrimEnd('/');
            }
            catch { }
            if (string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase))
                w = "⚠ 'New music' and 'Music library' point to the same folder — approving would import the library into itself.";
            else if ((fb + "/").StartsWith(fa + "/", StringComparison.OrdinalIgnoreCase))
                w = "⚠ The 'New music' folder sits inside your music library — incoming files would already count as library tracks.";
            else if ((fa + "/").StartsWith(fb + "/", StringComparison.OrdinalIgnoreCase))
                w = "⚠ Your music library sits inside the 'New music' folder — the whole library would show up in the inbox.";
        }
        FolderWarning = w;
    }

    private string _filenameTemplate = NameTemplate.Default;
    public string FilenameTemplate { get => _filenameTemplate; set => SetField(ref _filenameTemplate, value); }

    private string _discogsToken = string.Empty;
    public string DiscogsToken { get => _discogsToken; set { if (SetField(ref _discogsToken, value)) Meta.DiscogsToken = value ?? string.Empty; } }


    private bool _galaxyAlbumLevel;
    public bool GalaxyAlbumLevel
    {
        get => _galaxyAlbumLevel;
        set { if (SetField(ref _galaxyAlbumLevel, value)) Galaxy?.Refresh(); }
    }

    private bool _darkMode;
    public bool DarkMode
    {
        get => _darkMode;
        set { if (SetField(ref _darkMode, value)) OnPropertyChanged(nameof(ThemeIcon)); }
    }
    public string ThemeIcon => DarkMode ? "\ue518" : "\ue51c";

    // ---- Personalisations: Database (My Music) ---- (see docs/IPOD_BEHAVIOR.md)
    private bool _splitArtistOnComma = true;
    public bool SplitArtistOnComma
    {
        get => _splitArtistOnComma;
        set { if (SetField(ref _splitArtistOnComma, value)) CleanupOptions.SplitArtistOnComma = value; }
    }

    private bool _keepMultipleGenres;
    public bool KeepMultipleGenres
    {
        get => _keepMultipleGenres;
        set { if (SetField(ref _keepMultipleGenres, value)) CleanupOptions.KeepMultipleGenres = value; }
    }

    private bool _groupCollabsUnderPrimaryArtist = true;
    public bool GroupCollabsUnderPrimaryArtist
    {
        get => _groupCollabsUnderPrimaryArtist;
        set { if (SetField(ref _groupCollabsUnderPrimaryArtist, value)) CleanupOptions.GroupCollabsUnderPrimaryArtist = value; }
    }

    // Canonical multi-artist separator the cleanup standardizes to.
    public IReadOnlyList<string> ArtistJoinOptions { get; } = new[]
    {
        "Leave as-is", "Apple (A, B & C)", "Semicolon (A; B; C)", "Slash (A / B / C)", "Comma (A, B, C)"
    };

    private static string JoinKey(string? label) => label switch
    {
        "Leave as-is" => "asis",
        "Semicolon (A; B; C)" => "semicolon",
        "Slash (A / B / C)" => "slash",
        "Comma (A, B, C)" => "comma",
        _ => "apple",
    };

    private static string JoinLabel(string? key) => key switch
    {
        "asis" => "Leave as-is",
        "semicolon" => "Semicolon (A; B; C)",
        "slash" => "Slash (A / B / C)",
        "comma" => "Comma (A, B, C)",
        _ => "Apple (A, B & C)",
    };

    private string _selectedArtistJoin = "Apple (A, B & C)";
    public string SelectedArtistJoin
    {
        get => _selectedArtistJoin;
        set { if (SetField(ref _selectedArtistJoin, value)) CleanupOptions.ArtistJoin = JoinKey(value); }
    }

    private bool _titleCaseTitlesAndAlbums = true;
    public bool TitleCaseTitlesAndAlbums
    {
        get => _titleCaseTitlesAndAlbums;
        set { if (SetField(ref _titleCaseTitlesAndAlbums, value)) CleanupOptions.TitleCaseTitlesAndAlbums = value; }
    }

    private bool _autoCleanOnApprove;
    public bool AutoCleanOnApprove
    {
        get => _autoCleanOnApprove;
        set { if (SetField(ref _autoCleanOnApprove, value)) CleanupOptions.AutoCleanOnApprove = value; }
    }

    // Standard genres list (editable): base set + the user's own. The Doctor retags to these.
    public ObservableCollection<GenrePref> StandardGenres { get; } = new();
    public RelayCommand AddGenreCommand { get; }

    private string _newGenre = "";
    public string NewGenre { get => _newGenre; set { if (SetField(ref _newGenre, value)) AddGenreCommand.RaiseCanExecuteChanged(); } }

    private GenrePref MakeGenre(string name) => new(name, p => { StandardGenres.Remove(p); PushGenres(); });

    private void AddGenre()
    {
        var g = (NewGenre ?? "").Trim();
        if (g.Length == 0) return;
        if (!StandardGenres.Any(x => string.Equals(x.Name, g, StringComparison.OrdinalIgnoreCase)))
            StandardGenres.Add(MakeGenre(g));
        NewGenre = "";
        PushGenres();
    }

    private void PushGenres() => Genres.Standard = StandardGenres.Select(g => g.Name).ToList();

    private void LoadGenres(List<string> saved)
    {
        StandardGenres.Clear();
        var list = saved != null && saved.Count > 0 ? saved : Genres.Default.ToList();
        foreach (var g in list.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            StandardGenres.Add(MakeGenre(g));
        PushGenres();
    }

    // ---- Personalisations: iPod ----
    private bool _flattenArtistOnSync;
    public bool FlattenArtistOnSync
    {
        get => _flattenArtistOnSync;
        set { if (SetField(ref _flattenArtistOnSync, value)) Sync.FlattenArtistOnSync = value; }
    }

    private bool _convertToAlacDefault;
    public bool ConvertToAlacDefault
    {
        get => _convertToAlacDefault;
        set { if (SetField(ref _convertToAlacDefault, value)) Sync.ConvertToAlac = value; }
    }

    private bool _autoCreatePlaylists;
    public bool AutoCreatePlaylists
    {
        get => _autoCreatePlaylists;
        set { if (SetField(ref _autoCreatePlaylists, value)) Sync.AutoCreatePlaylists = value; }
    }

    private bool _removeDotUnderscoreAfterTransfer = true;
    public bool RemoveDotUnderscoreAfterTransfer
    {
        get => _removeDotUnderscoreAfterTransfer;
        set { if (SetField(ref _removeDotUnderscoreAfterTransfer, value)) Sync.RemoveDotUnderscoreAfterTransfer = value; }
    }

    private bool _setCompilationFlag;
    public bool SetCompilationFlag
    {
        get => _setCompilationFlag;
        set { if (SetField(ref _setCompilationFlag, value)) Sync.SetCompilationFlag = value; }
    }


    private int _selectedTabIndex = 5; // open op de Inbox (review-gate)
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set { if (SetField(ref _selectedTabIndex, value)) { OnPropertyChanged(nameof(CurrentSection)); MaybeAutoScan(value); } }
    }

    // Open a menu → start its scan once per session, but only the safe read-only library scans
    // (no folder picked / network-heavy MusicBrainz / destructive previews are skipped).
    private readonly HashSet<int> _autoScanned = new();
    private void MaybeAutoScan(int tab)
    {
        if (_autoScanned.Contains(tab)) return;
        bool ran = false;
        switch (tab)
        {
            case 1: if (Library.ScanCommand.CanExecute(null)) { Library.ScanCommand.Execute(null); ran = true; } break; // Health
            case 3: if (Sync.ScanCommand.CanExecute(null))    { Sync.ScanCommand.Execute(null); ran = true; } break;    // Transfer
            case 5: if (Staging.ScanCommand.CanExecute(null)) { Staging.ScanCommand.Execute(null); ran = true; } break; // Inbox
            case 6: Browser.Refresh(); ran = true; break; // Library (leest direct uit de index)
            case 7: Galaxy.Refresh(); ran = true; break; // Galaxy
            case 8: Wantlist.OpenedTab(); ran = true; break; // Wantlist (discografieën 1x per sessie)
        }
        if (ran) _autoScanned.Add(tab);
    }

    // ---- Tools ----
    public MetadataEditorViewModel Meta { get; }
    public DuplicatesViewModel Duplicates { get; } = new();
    public SyncViewModel Sync { get; }
    public LibraryViewModel Library { get; }
    public StagingViewModel Staging { get; }
    public BrowserViewModel Browser { get; }
    public GalaxyViewModel Galaxy { get; }
    public WantlistViewModel Wantlist { get; }

    // ---- iPod mode: Rockbox (direct uit de bieb) of iTunes (ALAC-spiegel op de achtergrond) ----
    private readonly AlacMirrorService _mirror = new();
    private bool _itunesMode;
    public bool ItunesMode { get => _itunesMode; set { if (SetField(ref _itunesMode, value)) { OnPropertyChanged(nameof(RockboxMode)); ApplyMirrorMode(); } } }
    public bool RockboxMode => !_itunesMode;
    private string _alacMirrorFolder = "";
    public string AlacMirrorFolder { get => _alacMirrorFolder; set { if (SetField(ref _alacMirrorFolder, value)) MaybeKickMirror(); } }
    private string _mirrorStatus = "";
    public string MirrorStatus { get => _mirrorStatus; private set => SetField(ref _mirrorStatus, value); }

    private void ApplyMirrorMode()
    {
        if (_itunesMode)
        {
            if (SelectedTabIndex == 3) SelectedTabIndex = 4;   // Transfer is Rockbox-only -> naar Settings
            if (string.IsNullOrWhiteSpace(AlacMirrorFolder) && !string.IsNullOrWhiteSpace(MusicLibrary))
                AlacMirrorFolder = MusicLibrary.TrimEnd('/', System.IO.Path.DirectorySeparatorChar) + " (ALAC)";
            MirrorStatus = "iTunes mode on — the mirror syncs in the background.";
            MaybeKickMirror();
        }
        else
        {
            _mirror.Stop();
            MirrorStatus = "";
        }
    }

    private void MaybeKickMirror()
    {
        if (_itunesMode && !string.IsNullOrWhiteSpace(AlacMirrorFolder)
            && !string.IsNullOrWhiteSpace(MusicLibrary) && System.IO.Directory.Exists(MusicLibrary))
            _mirror.Kick(MusicLibrary, AlacMirrorFolder);
    }

    private SpindleConfig BuildConfig() => new SpindleConfig
    {
        DownloadFilePath = DownloadFilePath.Trim(),
        MusicLibrary = MusicLibrary.Trim(),
        AcoustIdKey = Meta.AcoustIdKey,
        DiscogsToken = DiscogsToken.Trim(),
        DupFolder = Duplicates.Folder,
        SyncLibrary = Sync.LibraryFolder,
        SyncIpod = Sync.IpodFolder,
        Watchlist = Wantlist.WatchNames(),
        Wantlist = Wantlist.WantEntries(),
        ItunesMode = ItunesMode,
        AlacMirrorFolder = AlacMirrorFolder,
        TransferWanted = Sync.WantedSnapshot(),
        DuplicateIgnores = Duplicates.IgnoreKeys(),
        GalaxyAlbumLevel = GalaxyAlbumLevel,
        DarkMode = DarkMode,
        FilenameTemplate = string.IsNullOrWhiteSpace(FilenameTemplate) ? NameTemplate.Default : FilenameTemplate.Trim(),
        SplitArtistOnComma = SplitArtistOnComma,
        KeepMultipleGenres = KeepMultipleGenres,
        GroupCollabsUnderPrimaryArtist = GroupCollabsUnderPrimaryArtist,
        ArtistJoinStyle = JoinKey(SelectedArtistJoin),
        TitleCaseTitlesAndAlbums = TitleCaseTitlesAndAlbums,
        AutoCleanOnApprove = AutoCleanOnApprove,
        FlattenArtistOnSync = FlattenArtistOnSync,
        ConvertToAlacDefault = ConvertToAlacDefault,
        AutoCreatePlaylists = AutoCreatePlaylists,
        RemoveDotUnderscoreAfterTransfer = RemoveDotUnderscoreAfterTransfer,
        SetCompilationFlag = SetCompilationFlag,
        StandardGenres = StandardGenres.Select(g => g.Name).ToList(),
    };

    private void LoadFromConfig(SpindleConfig c)
    {
        DownloadFilePath = c.DownloadFilePath;
        MusicLibrary = c.MusicLibrary;
        Duplicates.Folder = c.DupFolder;
        Sync.LibraryFolder = c.SyncLibrary;
        Sync.IpodFolder = c.SyncIpod;
        Wantlist.Load(c.Watchlist, c.Wantlist);
        Sync.LoadWanted(c.TransferWanted);
        Duplicates.LoadIgnores(c.DuplicateIgnores);
        _itunesMode = c.ItunesMode;
        _alacMirrorFolder = c.AlacMirrorFolder;
        OnPropertyChanged(nameof(ItunesMode));
        OnPropertyChanged(nameof(RockboxMode));
        OnPropertyChanged(nameof(AlacMirrorFolder));
        if (_itunesMode) MaybeKickMirror();
        GalaxyAlbumLevel = c.GalaxyAlbumLevel;
        DarkMode = c.DarkMode;
        FilenameTemplate = string.IsNullOrWhiteSpace(c.FilenameTemplate) ? NameTemplate.Default : c.FilenameTemplate;
        DiscogsToken = c.DiscogsToken ?? string.Empty;
        SplitArtistOnComma = c.SplitArtistOnComma;
        KeepMultipleGenres = c.KeepMultipleGenres;
        GroupCollabsUnderPrimaryArtist = c.GroupCollabsUnderPrimaryArtist;
        SelectedArtistJoin = JoinLabel(c.ArtistJoinStyle);
        TitleCaseTitlesAndAlbums = c.TitleCaseTitlesAndAlbums;
        AutoCleanOnApprove = c.AutoCleanOnApprove;
        FlattenArtistOnSync = c.FlattenArtistOnSync;
        ConvertToAlacDefault = c.ConvertToAlacDefault;
        AutoCreatePlaylists = c.AutoCreatePlaylists;
        RemoveDotUnderscoreAfterTransfer = c.RemoveDotUnderscoreAfterTransfer;
        SetCompilationFlag = c.SetCompilationFlag;
        LoadGenres(c.StandardGenres);
    }

    // Called when the window closes so tool folders (and other settings) survive a restart.
    public void SaveSettings()
    {
        Settings.Save(BuildConfig());
        Lib.Configure(MusicLibrary, DownloadFilePath);
    }
}

public sealed record HistoryRow(string Label, string Detail, bool IsTop);

/// <summary>One entry in the editable standard-genres list (name + remove button).</summary>
public sealed class GenrePref
{
    public string Name { get; }
    public RelayCommand RemoveCommand { get; }
    public GenrePref(string name, Action<GenrePref> onRemove)
    {
        Name = name;
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }
}
