using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using SeekDownloader.Models;
using SeekDownloader.Services;

namespace SeekDownloader.Gui.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _connTimer;   // keeps the sidebar connection pill in sync
    private readonly DispatcherTimer _errorClearTimer; // auto-hides the queue error panel after 10s
    private string _lastErrorSnapshot = string.Empty;
    private bool _isConnecting;
    private readonly Dictionary<string, QueueItemViewModel> _queueByKey = new();
    private readonly HashSet<string> _removedQueueKeys = new(); // queue rows the user removed; Poll won't re-add them

    private SeekRunner? _runner;            // auto mode pipeline
    private CancellationTokenSource? _cts;  // auto mode cancellation
    private readonly DownloadService _download = new(); // one shared Soulseek client for auto + manual
    private FileSeekService? _manualSeeker;
    private DownloadService? _activeDownload; // whichever service to poll

    public MainViewModel()
    {
        Meta = new MetadataEditorViewModel(Lib, Undo);
        StartCommand = new RelayCommand(StartAuto, () => !IsRunning && IsAutoMode);
        StopCommand = new RelayCommand(StopAll);
        SearchCommand = new RelayCommand(Search, () => IsPickMode && !IsSearching);
        AddToQueueCommand = new RelayCommand(AddSelectedToQueue, () => (IsManualMode && HasResults) || (IsArtistMode && HasAlbums));
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false));
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
        ResumeCommand = new RelayCommand(ResumeQueue, () => !IsRunning);
        RetryFailedCommand = new RelayCommand(RetryFailed);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Poll();

        _connTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connTimer.Tick += (_, _) => UpdateConnection();
        // Geen Soulseek-verbinding meer: downloaden gaat via Nicotine+. (_connTimer niet gestart.)

        _errorClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _errorClearTimer.Tick += (_, _) => { _errorClearTimer.Stop(); ErrorLog = string.Empty; };

        AppleMusic = new AppleMusicViewModel(
            onArtist: a => { Mode = 2; SearchTerm = a; SelectedTabIndex = 0; Search(); },
            onPlaylistTracks: QueuePlaylist);

        Artists = new ArtistsViewModel(
            onDownload: QueuePlaylist,
            topArtists: () => AppleMusicService.GetTopArtists(50).Select(a => a.Name).ToList());

        Library = new LibraryViewModel(
            onDownload: QueuePlaylist,
            onEdit: (files, status) =>
            {
                Meta.LoadFiles(files, status);
                SelectedTabIndex = 3; // Metadata
            }, lib: Lib, undo: Undo);

        Staging = new StagingViewModel(
            onFix: (files, status) => { Meta.LoadFiles(files, status); SelectedTabIndex = 3; }, // Metadata
            lib: Lib, undo: Undo,
            template: () => FilenameTemplate);

        Browser = new BrowserViewModel(Lib, () => MusicLibrary,
            (files, status) => { Meta.LoadFiles(files, status); SelectedTabIndex = 3; });

        Galaxy = new GalaxyViewModel(Lib, () => MusicLibrary,
            onOpenAlbum: (artist, album) => { SelectedTabIndex = 9; Browser.FocusAlbum(artist, album); },
            getAlbumLevel: () => GalaxyAlbumLevel,
            setAlbumLevel: v => GalaxyAlbumLevel = v);

        LoadFromConfig(Settings.Load());

        // Pre-fill comparison/library folders with your library/download path, but only when the
        // saved value is empty (so a remembered folder is never overwritten).
        var fallback = !string.IsNullOrWhiteSpace(MusicLibrary) ? MusicLibrary
            : (!string.IsNullOrWhiteSpace(DownloadFilePath) ? DownloadFilePath : string.Empty);
        if (string.IsNullOrWhiteSpace(AppleMusic.LibraryFolder)) AppleMusic.LibraryFolder = fallback;
        if (string.IsNullOrWhiteSpace(Sync.LibraryFolder)) Sync.LibraryFolder = fallback;
        if (string.IsNullOrWhiteSpace(Organize.SourceFolder)) Organize.SourceFolder = DownloadFilePath;
        if (string.IsNullOrWhiteSpace(Organize.DestFolder)) Organize.DestFolder = MusicLibrary;
        if (string.IsNullOrWhiteSpace(Artists.LibraryFolder)) Artists.LibraryFolder = fallback;
        if (string.IsNullOrWhiteSpace(Library.LibraryFolder)) Library.LibraryFolder = fallback;
        Staging.NieuwFolder = DownloadFilePath;
        Staging.LibraryFolder = MusicLibrary;

        // Fase 0: watchers + globale statusbalk — index ververst zichzelf op bestandswijzigingen.
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
        };
        Lib.Configure(MusicLibrary, DownloadFilePath);

        // Restore unfinished downloads from the previous session so they can be resumed (no re-search).
        foreach (var e in QueueStore.Load())
        {
            var item = new QueueItemViewModel(e.Username, e.Filename, e.Size, RemoveQueueItem, RetryItem);
            if (_queueByKey.ContainsKey(item.Key)) continue;
            _queueByKey[item.Key] = item;
            Queue.Add(item);
        }
    }

    // ---- Fase 0: index-service, globale statusbalk en undo-journal ----
    public LibraryService Lib { get; } = new();
    public UndoJournal Undo { get; } = new();

    private string _globalStatus = "Ready.";
    public string GlobalStatus { get => _globalStatus; private set => SetField(ref _globalStatus, value); }

    private bool _indexBusy;
    public bool IndexBusy { get => _indexBusy; private set => SetField(ref _indexBusy, value); }

    private double _indexPercent;
    public double IndexPercent { get => _indexPercent; private set => SetField(ref _indexPercent, value); }

    public void UndoLast()
    {
        var (done, total, label) = Undo.UndoLast();
        GlobalStatus = total == 0
            ? "Nothing to undo."
            : $"Undone: {label} — {done}/{total} restored.";
        if (total > 0)
            Task.Run(() => { Lib.Refresh(MusicLibrary); Lib.Refresh(DownloadFilePath); });
    }

    // ---- Connection pill (sidebar) ----
    private static readonly IBrush ConnGreen = new SolidColorBrush(Color.Parse("#2E7D43"));
    private static readonly IBrush ConnAmber = new SolidColorBrush(Color.Parse("#A55600"));
    private static readonly IBrush ConnGrey = new SolidColorBrush(Color.Parse("#737783"));

    private string _connectionText = "Niet verbonden";
    public string ConnectionText { get => _connectionText; private set => SetField(ref _connectionText, value); }

    private IBrush _connectionBrush = ConnGrey;
    public IBrush ConnectionBrush { get => _connectionBrush; private set => SetField(ref _connectionBrush, value); }

    private void UpdateConnection()
    {
        if (_download.IsLoggedIn)
        {
            _isConnecting = false;
            ConnectionText = "Verbonden met Soulseek";
            ConnectionBrush = ConnGreen;
        }
        else if (_isConnecting)
        {
            ConnectionText = "Verbinden…";
            ConnectionBrush = ConnAmber;
        }
        else
        {
            ConnectionText = "Niet verbonden";
            ConnectionBrush = ConnGrey;
        }
    }

    public RelayCommand ReconnectCommand => _reconnectCommand ??= new RelayCommand(() => { if (!_isConnecting) TryAutoConnect(); });
    private RelayCommand? _reconnectCommand;

    // Connect at startup when credentials are saved, so the pill is meaningful and the first search is faster.
    private void TryAutoConnect()
    {
        if (string.IsNullOrWhiteSpace(SoulseekUsername) || string.IsNullOrWhiteSpace(SoulseekPassword))
        {
            ConnectionText = "Geen inloggegevens";
            return;
        }
        _isConnecting = true;
        UpdateConnection();
        _download.SoulSeekUsername = SoulseekUsername.Trim();
        _download.SoulSeekPassword = SoulseekPassword;
        _download.NicotineListenPort = (int)SoulseekListenPort;
        Task.Run(async () =>
        {
            try { await _download.ConnectAsync(); } catch { }
            Dispatcher.UIThread.Post(() => { _isConnecting = false; UpdateConnection(); });
        });
    }

    // ---- Top bar ----
    public string CurrentSection => SelectedTabIndex switch
    {
        0 => "Sort", 1 => "Organize", 2 => "ALAC Converter", 3 => "Metadata",
        4 => "Health", 5 => "Duplicates", 6 => "Transfer", 7 => "Settings", 8 => "Inbox", 9 => "Library", 10 => "Galaxy", _ => "Spindle"
    };

    public string UserInitial => "S";

    // ---- Cmd+F command palette ----
    private static readonly (string Name, int Idx, string Glyph)[] PaletteSections =
    {
        ("Galaxy", 10, ""),
        ("Library", 9, ""),
        ("Inbox", 8, ""),
        ("Organize", 1, ""), ("Sort", 0, ""), ("Metadata", 3, ""),
        ("Duplicates", 5, ""), ("Health", 4, ""), ("ALAC Converter", 2, ""),
        ("Transfer", 6, ""), ("Settings", 7, ""),
    };

    private bool _isPaletteOpen;
    public bool IsPaletteOpen { get => _isPaletteOpen; set => SetField(ref _isPaletteOpen, value); }

    private string _paletteQuery = string.Empty;
    public string PaletteQuery { get => _paletteQuery; set { if (SetField(ref _paletteQuery, value)) RebuildPalette(); } }

    public ObservableCollection<PaletteItem> PaletteResults { get; } = new();

    private PaletteItem? _selectedPaletteItem;
    public PaletteItem? SelectedPaletteItem { get => _selectedPaletteItem; set => SetField(ref _selectedPaletteItem, value); }

    public void OpenPalette() { PaletteQuery = string.Empty; RebuildPalette(); IsPaletteOpen = true; }
    public void ClosePalette() => IsPaletteOpen = false;

    private void RebuildPalette()
    {
        PaletteResults.Clear();
        var q = (PaletteQuery ?? string.Empty).Trim();
        foreach (var s in PaletteSections)
            if (q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                PaletteResults.Add(new PaletteItem(s.Name, "Go to " + s.Name, s.Glyph,
                    () => { SelectedTabIndex = s.Idx; ClosePalette(); }));
        if (q.Length >= 2 && !string.IsNullOrWhiteSpace(MusicLibrary))
        {
            foreach (var a in Lib.Index.Albums(MusicLibrary).Where(x =>
                         x.Album.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                         x.AlbumArtist.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(6))
            {
                var aa = a.AlbumArtist; var al = a.Album;
                PaletteResults.Add(new PaletteItem(
                    (aa.Length > 0 ? aa + " — " : "") + al, "Open in Library", "",
                    () => { SelectedTabIndex = 9; Browser.FocusAlbum(aa, al); ClosePalette(); }));
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

    // Apple Music playlist -> write its tracks to a temp search file and run the auto pipeline.
    private void QueuePlaylist(List<string> lines)
    {
        if (lines == null || lines.Count == 0) { StatusMessage = "Lege playlist."; return; }
        try
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "seek_playlist_" + Guid.NewGuid().ToString("N") + ".txt");
            System.IO.File.WriteAllLines(tmp, lines);
            SearchFilePath = tmp;
            SearchTerm = string.Empty;
            Mode = 0;
            SelectedTabIndex = 0;
            StartAuto();
        }
        catch (Exception e)
        {
            StatusMessage = "Playlist toevoegen mislukt: " + e.Message;
        }
    }

    // ---- Connection ----
    private string _username = string.Empty;
    private string _password = string.Empty;
    private decimal _listenPort = 12345;
    public string SoulseekUsername { get => _username; set { if (SetField(ref _username, value)) OnPropertyChanged(nameof(UserInitial)); } }
    public string SoulseekPassword { get => _password; set => SetField(ref _password, value); }
    public decimal SoulseekListenPort { get => _listenPort; set => SetField(ref _listenPort, value); }

    // ---- Paths ----
    private string _downloadFilePath = string.Empty;
    private string _musicLibrary = string.Empty;
    private string _searchFilePath = string.Empty;
    private string _downloadArchiveFilePath = string.Empty;
    public string DownloadFilePath { get => _downloadFilePath; set { if (SetField(ref _downloadFilePath, value)) Staging.NieuwFolder = value; } }
    public string MusicLibrary { get => _musicLibrary; set { if (SetField(ref _musicLibrary, value)) { OnPropertyChanged(nameof(HasMusicLibrary)); Staging.LibraryFolder = value; } } }
    public bool HasMusicLibrary => !string.IsNullOrWhiteSpace(MusicLibrary);
    public string SearchFilePath { get => _searchFilePath; set => SetField(ref _searchFilePath, value); }
    public string DownloadArchiveFilePath { get => _downloadArchiveFilePath; set => SetField(ref _downloadArchiveFilePath, value); }

    private string _filenameTemplate = NameTemplate.Default;
    public string FilenameTemplate { get => _filenameTemplate; set => SetField(ref _filenameTemplate, value); }

    private string _discogsToken = string.Empty;
    public string DiscogsToken { get => _discogsToken; set { if (SetField(ref _discogsToken, value)) Meta.DiscogsToken = value ?? string.Empty; } }

    // ---- Search ----
    private string _searchTerm = string.Empty;
    private string _searchDelimeter = "-";
    private string _filterOutFileNames = string.Empty;
    private string _searchFileExtensions = string.Empty;
    public string SearchTerm { get => _searchTerm; set => SetField(ref _searchTerm, value); }
    public string SearchDelimeter { get => _searchDelimeter; set => SetField(ref _searchDelimeter, value); }
    public string FilterOutFileNames { get => _filterOutFileNames; set => SetField(ref _filterOutFileNames, value); }
    public string SearchFileExtensions { get => _searchFileExtensions; set => SetField(ref _searchFileExtensions, value); }

    // ---- Numeric options ----
    private decimal _threadCount = 10;
    private decimal _maxFileSize = 200;
    private decimal _inMemoryMaxSize = 50;
    private decimal _musicLibraryMatch = 50;
    private decimal _matchArtist = 50;
    private decimal _matchAlbum = 50;
    private decimal _matchTrack = 50;
    public decimal ThreadCount { get => _threadCount; set => SetField(ref _threadCount, value); }
    public decimal MaxFileSize { get => _maxFileSize; set => SetField(ref _maxFileSize, value); }
    public decimal InMemoryDownloadMaxSize { get => _inMemoryMaxSize; set => SetField(ref _inMemoryMaxSize, value); }
    public decimal MusicLibraryMatch { get => _musicLibraryMatch; set => SetField(ref _musicLibraryMatch, value); }
    public decimal SearchMatchArtistPercentage { get => _matchArtist; set => SetField(ref _matchArtist, value); }
    public decimal SearchMatchAlbumPercentage { get => _matchAlbum; set => SetField(ref _matchAlbum, value); }
    public decimal SearchMatchTrackPercentage { get => _matchTrack; set => SetField(ref _matchTrack, value); }

    // ---- Flags ----
    private bool _albumMode;
    private bool _groupedDownloads;
    private bool _downloadSingles;
    private bool _updateAlbumName;
    private bool _allowNonTaggedFiles;
    private bool _checkTags;
    private bool _checkTagsDelete;
    private bool _musicLibraryQuickMatch;
    private bool _inMemoryDownloads;
    private bool _saveInUploaderSubfolder;
    public bool AlbumMode { get => _albumMode; set => SetField(ref _albumMode, value); }
    public bool SaveInUploaderSubfolder { get => _saveInUploaderSubfolder; set => SetField(ref _saveInUploaderSubfolder, value); }
    public bool GroupedDownloads { get => _groupedDownloads; set => SetField(ref _groupedDownloads, value); }
    public bool DownloadSingles { get => _downloadSingles; set => SetField(ref _downloadSingles, value); }
    public bool UpdateAlbumName { get => _updateAlbumName; set => SetField(ref _updateAlbumName, value); }
    public bool AllowNonTaggedFiles { get => _allowNonTaggedFiles; set => SetField(ref _allowNonTaggedFiles, value); }
    public bool CheckTags { get => _checkTags; set => SetField(ref _checkTags, value); }
    public bool CheckTagsDelete { get => _checkTagsDelete; set => SetField(ref _checkTagsDelete, value); }
    public bool MusicLibraryQuickMatch { get => _musicLibraryQuickMatch; set => SetField(ref _musicLibraryQuickMatch, value); }
    public bool InMemoryDownloads { get => _inMemoryDownloads; set => SetField(ref _inMemoryDownloads, value); }

    private bool _autoOrganize;
    public bool AutoOrganize { get => _autoOrganize; set => SetField(ref _autoOrganize, value); }

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

    // ---- Mode (0 = automatisch, 1 = handmatig per bestand, 2 = artiest → kies albums) ----
    private int _mode;
    public int Mode
    {
        get => _mode;
        set
        {
            if (SetField(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsAutoMode));
                OnPropertyChanged(nameof(IsManualMode));
                OnPropertyChanged(nameof(IsArtistMode));
                OnPropertyChanged(nameof(IsPickMode));
                StartCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
                AddToQueueCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsAutoMode => Mode == 0;
    public bool IsManualMode => Mode == 1;
    public bool IsArtistMode => Mode == 2;
    public bool IsPickMode => Mode == 1 || Mode == 2; // modes with a Zoek/results step

    private int _selectedTabIndex = 8; // open op 'Nieuw' (de inbox/review-gate)
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
            case 4: if (Library.ScanCommand.CanExecute(null)) { Library.ScanCommand.Execute(null); ran = true; } break; // Gezondheid
            case 6: if (Sync.ScanCommand.CanExecute(null))    { Sync.ScanCommand.Execute(null); ran = true; } break;    // Overzetten
            case 8: if (Staging.ScanCommand.CanExecute(null)) { Staging.ScanCommand.Execute(null); ran = true; } break; // Nieuw
            case 9: Browser.Refresh(); ran = true; break; // Bibliotheek (leest direct uit de index)
            case 10: Galaxy.Refresh(); ran = true; break; // Galaxy
        }
        if (ran) _autoScanned.Add(tab);
    }

    // ---- Manual results (per file) ----
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();
    public bool HasResults => Results.Count > 0;

    // ---- Artist results (per album) ----
    public ObservableCollection<AlbumGroupViewModel> Albums { get; } = new();
    public bool HasAlbums => Albums.Count > 0;

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        private set { if (SetField(ref _isSearching, value)) SearchCommand.RaiseCanExecuteChanged(); }
    }

    private string _resultsSummary = string.Empty;
    public string ResultsSummary { get => _resultsSummary; private set => SetField(ref _resultsSummary, value); }

    // ---- Queue ----
    public ObservableCollection<QueueItemViewModel> Queue { get; } = new();

    // ---- Extra tools (own tabs) ----
    public AlacConverterViewModel Alac { get; } = new();
    public MetadataEditorViewModel Meta { get; }
    public SortViewModel Sort { get; } = new();
    public OrganizeViewModel Organize { get; } = new();
    public DuplicatesViewModel Duplicates { get; } = new();
    public SyncViewModel Sync { get; } = new();
    public AppleMusicViewModel AppleMusic { get; }
    public ArtistsViewModel Artists { get; }
    public LibraryViewModel Library { get; }
    public StagingViewModel Staging { get; }
    public BrowserViewModel Browser { get; }
    public GalaxyViewModel Galaxy { get; }

    // ---- Runtime state ----
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsNotRunning));
                StartCommand.RaiseCanExecuteChanged();
                ResumeCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsNotRunning => !IsRunning;

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }

    private string _seekedText = "0 / 0";
    private string _currentlySeeking = string.Empty;
    private int _queueCount;
    private int _skippedCount;
    private int _incorrectTagsCount;
    private int _successfulDownloadsCount;
    private int _activeDownloadsCount;
    public string SeekedText { get => _seekedText; private set => SetField(ref _seekedText, value); }
    public string CurrentlySeeking { get => _currentlySeeking; private set => SetField(ref _currentlySeeking, value); }
    public int QueueCount { get => _queueCount; private set => SetField(ref _queueCount, value); }
    public int SkippedCount { get => _skippedCount; private set => SetField(ref _skippedCount, value); }
    public int IncorrectTagsCount { get => _incorrectTagsCount; private set => SetField(ref _incorrectTagsCount, value); }
    public int SuccessfulDownloadsCount { get => _successfulDownloadsCount; private set => SetField(ref _successfulDownloadsCount, value); }
    public int ActiveDownloadsCount { get => _activeDownloadsCount; private set => SetField(ref _activeDownloadsCount, value); }

    private string _totalSpeedText = "0 KB/s";
    public string TotalSpeedText { get => _totalSpeedText; private set => SetField(ref _totalSpeedText, value); }

    private string _errorLog = string.Empty;
    public string ErrorLog { get => _errorLog; private set => SetField(ref _errorLog, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand RetryFailedCommand { get; }

    // ===================== AUTO MODE =====================
    private void StartAuto()
    {
        if (IsRunning) return;
        if (!ValidateConnection()) return;
        if (string.IsNullOrWhiteSpace(SearchTerm) && string.IsNullOrWhiteSpace(SearchFilePath))
        {
            StatusMessage = "Geef een zoekterm of een zoekbestand op.";
            return;
        }

        var config = BuildConfig();
        Settings.Save(config);

        _runner = new SeekRunner(_download);
        _cts = new CancellationTokenSource();
        _activeDownload = _runner.Download;
        IsRunning = true;
        StatusMessage = "Verbinden met Soulseek…";
        SelectedTabIndex = 1;
        StartPolling();

        var token = _cts.Token;
        var runner = _runner;
        Task.Run(() => runner.RunAsync(config, token))
            .ContinueWith(t =>
            {
                Poll();
                IsRunning = false;
                if (t.IsCanceled || token.IsCancellationRequested)
                    StatusMessage = "Gestopt.";
                else if (t.IsFaulted)
                    StatusMessage = "Fout: " + (t.Exception?.GetBaseException().Message ?? "unknown");
                else
                {
                    StatusMessage = $"Klaar — {SuccessfulDownloadsCount} gedownload, {SkippedCount} overgeslagen.";
                    if (AutoOrganize && !string.IsNullOrWhiteSpace(DownloadFilePath) && System.IO.Directory.Exists(DownloadFilePath))
                    {
                        Organize.SourceFolder = DownloadFilePath;
                        Organize.DestFolder = string.IsNullOrWhiteSpace(MusicLibrary) ? DownloadFilePath : MusicLibrary;
                        Organize.TestMode = false;
                        SelectedTabIndex = 3; // Organiseren-tab
                        StatusMessage = "Klaar met downloaden — automatisch organiseren…";
                        if (Organize.RunCommand.CanExecute(null)) Organize.RunCommand.Execute(null);
                    }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StopAll()
    {
        StatusMessage = "Stoppen…";
        _cts?.Cancel();
        _activeDownload?.StopThreads();
    }

    // ===================== MANUAL / ARTIST MODE =====================
    private void Search()
    {
        if (IsArtistMode) SearchArtist();
        else SearchFiles();
    }

    private void AddSelectedToQueue()
    {
        if (IsArtistMode) AddSelectedAlbums();
        else AddSelectedFiles();
    }

    private void SearchFiles()
    {
        if (IsSearching) return;
        if (!ValidateConnection()) return;
        if (string.IsNullOrWhiteSpace(SearchTerm))
        {
            StatusMessage = "Geef een zoekterm op.";
            return;
        }

        var cfg = BuildConfig();
        Settings.Save(cfg);
        IsSearching = true;
        StatusMessage = "Zoeken…";

        Task.Run(async () =>
        {
            EnsureManualSession(cfg);
            var term = new SearchTermModel(cfg.SearchTerm, cfg.SearchDelimeter);
            return await _manualSeeker!.SearchAsync(
                new List<SearchTermModel> { term },
                _download!.SoulClient!,
                cfg.FilterOutFileNames,
                cfg.SearchFileExtensions,
                cfg.MusicLibraryMatch,
                cfg.MaxFileSize,
                _download.DownloadArchiveList,
                cfg.SearchMatchArtistPercentage,
                cfg.SearchMatchAlbumPercentage,
                cfg.SearchMatchTrackPercentage);
        }).ContinueWith(t =>
        {
            IsSearching = false;
            if (t.IsFaulted)
            {
                StatusMessage = "Zoekfout: " + (t.Exception?.GetBaseException().Message ?? "unknown");
                return;
            }
            var results = t.Result ?? new List<SearchResult>();
            Results.Clear();
            foreach (var r in results)
                Results.Add(new ResultRowViewModel(r));
            OnPropertyChanged(nameof(HasResults));
            AddToQueueCommand.RaiseCanExecuteChanged();
            ResultsSummary = $"{results.Count} resultaten — vink aan wat je wilt en klik 'Naar wachtrij'.";
            StatusMessage = results.Count == 0 ? "Geen resultaten." : $"{results.Count} resultaten gevonden.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SearchArtist()
    {
        if (IsSearching) return;
        if (!ValidateConnection()) return;
        var artist = SearchTerm.Trim();
        if (string.IsNullOrWhiteSpace(artist))
        {
            StatusMessage = "Geef een artiestnaam op.";
            return;
        }

        var cfg = BuildConfig();
        Settings.Save(cfg);
        IsSearching = true;
        StatusMessage = "Albums van artiest zoeken…";

        Task.Run(async () =>
        {
            EnsureManualSession(cfg);
            var official = await MusicBrainzClient.GetOfficialAlbumsAsync(artist);
            var albums = new List<AlbumGroupViewModel>();

            if (official.Count > 0)
            {
                int i = 0;
                foreach (var mb in official)
                {
                    i++; var snap = i;
                    Dispatcher.UIThread.Post(() => StatusMessage = $"Zoeken op Soulseek: {mb.Title} ({snap}/{official.Count})…");
                    List<SearchResult> raw;
                    try { raw = await SoulseekSearch.SearchAsync(_download!.SoulClient!, artist, mb.Title, cfg); }
                    catch { raw = new List<SearchResult>(); }
                    var best = AlbumGrouper.BestFolderForAlbum(raw, artist, mb.Title, fn => _manualSeeker!.GetSeekTrackName(fn));
                    albums.Add(best != null
                        ? new AlbumGroupViewModel(mb.Title, mb.Year, true, best.Value.Tracks.Count, mb.ExpectedTracks, best.Value.User, best.Value.Tracks) { IsSelected = true }
                        : new AlbumGroupViewModel(mb.Title, mb.Year, false, 0, mb.ExpectedTracks, "", new List<SearchResult>()));
                }
            }
            else
            {
                // MusicBrainz unavailable: fall back to one artist-only search + folder grouping.
                var raw = await SoulseekSearch.SearchAsync(_download!.SoulClient!, artist, string.Empty, cfg);
                albums = AlbumGrouper.Group(raw, artist, fn => _manualSeeker!.GetSeekTrackName(fn), out _);
            }
            return (official.Count, albums);
        }).ContinueWith(t =>
        {
            IsSearching = false;
            if (t.IsFaulted)
            {
                StatusMessage = "Zoekfout: " + (t.Exception?.GetBaseException().Message ?? "unknown");
                return;
            }
            var (officialCount, albums) = t.Result;
            Albums.Clear();
            foreach (var a in albums) Albums.Add(a);
            OnPropertyChanged(nameof(HasAlbums));
            AddToQueueCommand.RaiseCanExecuteChanged();
            int found = albums.Count(a => a.CanSelect);

            if (officialCount > 0)
            {
                ResultsSummary = $"{officialCount} officiële albums (MusicBrainz)  ·  {found} gevonden op Soulseek — vink aan en klik 'Naar wachtrij'.";
                StatusMessage = $"{officialCount} officiële albums · {found} gevonden op Soulseek.";
            }
            else
            {
                var why = MusicBrainzClient.LastError ?? "unknown";
                ResultsSummary = albums.Count == 0
                    ? $"Geen albums. MusicBrainz: {why}."
                    : $"{albums.Count} albums (op mapnaam — MusicBrainz: {why}) — vink aan en klik 'Naar wachtrij'.";
                StatusMessage = $"MusicBrainz: {why}. {albums.Count} albums (mapnaam-modus).";
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AddSelectedAlbums()
    {
        var selectedAlbums = Albums.Where(a => a.IsSelected).ToList();
        if (selectedAlbums.Count == 0)
        {
            StatusMessage = "Geen albums geselecteerd.";
            return;
        }

        var toEnqueue = new List<SearchResult>();
        foreach (var album in selectedAlbums)
        {
            foreach (var r in album.Tracks)
            {
                var item = new QueueItemViewModel(r, RemoveQueueItem, RetryItem);
                if (_queueByKey.ContainsKey(item.Key)) continue;
                _queueByKey[item.Key] = item;
                Queue.Add(item);
                toEnqueue.Add(r);
            }
            Albums.Remove(album);
        }
        OnPropertyChanged(nameof(HasAlbums));
        AddToQueueCommand.RaiseCanExecuteChanged();

        _activeDownload = _download;
        StartPolling();
        SelectedTabIndex = 1;
        StatusMessage = $"{selectedAlbums.Count} albums ({toEnqueue.Count} tracks) toegevoegd aan de wachtrij.";

        EnqueueAll(toEnqueue);
        SaveQueue();
    }

    private void AddSelectedFiles()
    {
        var selected = Results.Where(r => r.IsSelected).Select(r => r.Source).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Nothing selected.";
            return;
        }

        var toEnqueue = new List<SearchResult>();
        foreach (var r in selected)
        {
            var item = new QueueItemViewModel(r, RemoveQueueItem, RetryItem);
            if (_queueByKey.ContainsKey(item.Key)) continue;
            _queueByKey[item.Key] = item;
            Queue.Add(item);
            toEnqueue.Add(r);
        }

        // Remove the queued rows from the result list so it stays tidy.
        foreach (var row in Results.Where(r => r.IsSelected).ToList())
            Results.Remove(row);
        OnPropertyChanged(nameof(HasResults));
        AddToQueueCommand.RaiseCanExecuteChanged();

        _activeDownload = _download;
        StartPolling();
        SelectedTabIndex = 1;
        StatusMessage = $"{toEnqueue.Count} toegevoegd aan de wachtrij.";

        EnqueueAll(toEnqueue);
        SaveQueue();
    }

    // EnqueueDownload blocks while the thread pool is full, so run it off the UI thread.
    private void EnqueueAll(List<SearchResult> items)
    {
        var download = _download!;
        Task.Run(() =>
        {
            foreach (var r in items)
            {
                download.EnqueueDownload(new SearchGroup
                {
                    SearchResults = new List<SearchResult> { r },
                    TargetArtistName = string.Empty,
                    TargetAlbumName = string.Empty,
                    SongNames = new List<string>()
                });
            }
        });
    }

    private void SetAllSelected(bool value)
    {
        foreach (var r in Results) r.IsSelected = value;
        foreach (var a in Albums) a.IsSelected = value;
    }

    private void ClearCompleted()
    {
        foreach (var item in Queue.Where(q => q.IsTerminal).ToList())
        {
            Queue.Remove(item);
            _queueByKey.Remove(item.Key);
        }
        SaveQueue();
    }

    // Remove a single item: pending is skipped before it starts, an active download is cancelled.
    private void RemoveQueueItem(QueueItemViewModel item)
    {
        if (!item.IsTerminal)
        {
            _removedQueueKeys.Add(item.Key);
            _download.RemoveFromQueue(item.Username, item.RemoteFilename);
        }
        Queue.Remove(item);
        _queueByKey.Remove(item.Key);
        SaveQueue();
    }

    // Retry one failed (or any) item: reset it and re-enqueue for download.
    private void RetryItem(QueueItemViewModel item)
    {
        if (!ValidateConnection()) return;
        var cfg = BuildConfig();
        Settings.Save(cfg);
        _removedQueueKeys.Remove(item.Key);
        item.Requeue();
        _activeDownload = _download;
        StartPolling();
        SelectedTabIndex = 1;
        StatusMessage = $"Opnieuw proberen: {item.FileName}";

        var r = ToSearchResult(item);
        Task.Run(() =>
        {
            try { EnsureManualSession(cfg); }
            catch (Exception e) { Dispatcher.UIThread.Post(() => StatusMessage = "Opnieuw proberen mislukt: " + e.Message); return; }
            _download.EnqueueDownload(new SearchGroup
            {
                SearchResults = new List<SearchResult> { r },
                TargetArtistName = string.Empty,
                TargetAlbumName = string.Empty,
                SongNames = new List<string>()
            });
        });
        SaveQueue();
    }

    private void RetryFailed()
    {
        var failed = Queue.Where(q => q.IsFailed).ToList();
        if (failed.Count == 0) { StatusMessage = "Geen mislukte downloads om opnieuw te proberen."; return; }
        if (!ValidateConnection()) return;
        var cfg = BuildConfig();
        Settings.Save(cfg);
        foreach (var item in failed) { _removedQueueKeys.Remove(item.Key); item.Requeue(); }
        _activeDownload = _download;
        StartPolling();
        SelectedTabIndex = 1;
        StatusMessage = $"Opnieuw proberen: {failed.Count} mislukte downloads.";

        var results = failed.Select(ToSearchResult).ToList();
        Task.Run(() =>
        {
            try { EnsureManualSession(cfg); }
            catch (Exception e) { Dispatcher.UIThread.Post(() => StatusMessage = "Opnieuw proberen mislukt: " + e.Message); return; }
            foreach (var r in results)
                _download.EnqueueDownload(new SearchGroup
                {
                    SearchResults = new List<SearchResult> { r },
                    TargetArtistName = string.Empty,
                    TargetAlbumName = string.Empty,
                    SongNames = new List<string>()
                });
        });
        SaveQueue();
    }

    // Persist the unfinished queue so it survives a restart.
    private void SaveQueue()
        => QueueStore.Save(Queue.Where(q => !q.IsTerminal)
            .Select(q => new QueueEntry { Username = q.Username, Filename = q.RemoteFilename, Size = q.Size }));

    private static SearchResult ToSearchResult(QueueItemViewModel q) => new SearchResult
    {
        Username = q.Username,
        Filename = q.RemoteFilename,
        Size = q.Size,
        HasFreeUploadSlot = true,
        UploadSpeed = 0,
        PotentialArtistMatch = 0,
        PotentialAlbumMatch = 0,
        PotentialTrackMatch = 0,
        PotentialTrackWithoutVersionMatch = 0
    };

    // Re-queue every unfinished item (e.g. after a restart). Already-present files are skipped by the core.
    private void ResumeQueue()
    {
        if (IsRunning) return;
        if (!ValidateConnection()) return;
        var pending = Queue.Where(q => !q.IsTerminal).ToList();
        if (pending.Count == 0) { StatusMessage = "Niets te hervatten."; return; }

        var cfg = BuildConfig();
        Settings.Save(cfg);
        var results = pending.Select(ToSearchResult).ToList();
        foreach (var item in pending) item.Status = "Queued";

        _activeDownload = _download;
        StartPolling();
        SelectedTabIndex = 1;
        StatusMessage = $"Hervatten… {pending.Count} downloads.";

        Task.Run(() =>
        {
            try
            {
                EnsureManualSession(cfg);
            }
            catch (Exception e)
            {
                Dispatcher.UIThread.Post(() => StatusMessage = "Hervatten mislukt: " + e.Message);
                return;
            }
            foreach (var r in results)
            {
                _download.EnqueueDownload(new SearchGroup
                {
                    SearchResults = new List<SearchResult> { r },
                    TargetArtistName = string.Empty,
                    TargetAlbumName = string.Empty,
                    SongNames = new List<string>()
                });
            }
        });
    }

    private void EnsureManualSession(SeekConfig cfg)
    {
        _manualSeeker ??= new FileSeekService();
        if (!string.IsNullOrWhiteSpace(cfg.MusicLibrary) && !_manualSeeker.MusicLibraries.Contains(cfg.MusicLibrary))
            _manualSeeker.MusicLibraries.Add(cfg.MusicLibrary);

        var d = _download;
        d.SoulSeekUsername = cfg.SoulseekUsername;
        d.SoulSeekPassword = cfg.SoulseekPassword;
        d.ThreadCount = cfg.ThreadCount;
        d.NicotineListenPort = cfg.SoulseekListenPort;
        d.DownloadFolderNicotine = cfg.DownloadFilePath;
        d.DownloadSingles = false;
        d.UpdateAlbumName = cfg.UpdateAlbumName;
        d.CheckTags = cfg.CheckTags;
        d.CheckTagsDelete = cfg.CheckTagsDelete;
        d.AllowNonTaggedFiles = cfg.AllowNonTaggedFiles;
        d.InMemoryDownloads = cfg.InMemoryDownloads;
        d.InMemoryDownloadMaxSize = cfg.InMemoryDownloadMaxSize;
        d.IncludeUsernameInPath = cfg.SaveInUploaderSubfolder;
        d.OutputStatus = false;

        if (d.SoulClient == null || !d.IsLoggedIn)
        {
            d.ConnectAsync().GetAwaiter().GetResult();
            if (!d.IsLoggedIn)
                throw new Exception(SeekRunner.ConnectErrorMessage(d));
        }
    }

    // ===================== POLLING =====================
    private void StartPolling()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void Poll()
    {
        var dl = _activeDownload;
        if (dl == null) return;

        SeekedText = $"{dl.SeekCount} (succes: {dl.SeekSuccessCount}) / {dl.MissingNames.Count}";
        CurrentlySeeking = dl.CurrentlySeeking;
        QueueCount = dl.InQueueCount;
        SkippedCount = dl.AlreadyDownloadedSkipCount;
        IncorrectTagsCount = dl.IncorrectTags;
        SuccessfulDownloadsCount = dl.SuccesfulDownloads;

        var active = dl.ActiveDownloads.Where(p => p.Progress < 100 && !string.IsNullOrEmpty(p.Filename)).ToList();
        ActiveDownloadsCount = active.Count;

        double totalKb = active.Sum(p => p.AverageDownloadSpeed) / 1000.0;
        TotalSpeedText = totalKb >= 1000 ? $"{totalKb / 1000.0:0.0} MB/s" : $"{(int)totalKb} KB/s";

        foreach (var p in active)
        {
            var key = $"{p.Username}|{p.Filename}";
            if (_removedQueueKeys.Contains(key)) continue;
            if (!_queueByKey.TryGetValue(key, out var item))
            {
                item = new QueueItemViewModel(p.Username ?? string.Empty, p.Filename, 0, RemoveQueueItem, RetryItem);
                _queueByKey[key] = item;
                Queue.Add(item);
            }
            item.UpdateProgress(p);
        }

        var completed = new HashSet<string>(dl.CompletedFileNames, StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(dl.FailedFileNames, StringComparer.OrdinalIgnoreCase);
        bool queueChanged = false;
        foreach (var item in Queue)
        {
            if (item.IsTerminal) continue;
            if (completed.Contains(item.BaseFileName)) { item.MarkDone(); queueChanged = true; }
            else if (failed.Contains(item.BaseFileName)) { item.MarkFailed(); queueChanged = true; }
        }
        if (queueChanged) SaveQueue();

        var errorText = string.Join(Environment.NewLine, dl.RecentErrors
            .OrderByDescending(e => e.Value)
            .Take(5)
            .Select(e => $"[{e.Value}x] {e.Key}"));

        // Show the error panel only when the error set changes, and auto-dismiss it after 10s
        // (so Poll doesn't immediately re-show the same errors every 0.5s).
        if (errorText != _lastErrorSnapshot)
        {
            _lastErrorSnapshot = errorText;
            ErrorLog = errorText;
            _errorClearTimer.Stop();
            if (!string.IsNullOrEmpty(errorText)) _errorClearTimer.Start();
        }
    }

    // ===================== HELPERS =====================
    private bool ValidateConnection()
    {
        if (string.IsNullOrWhiteSpace(SoulseekUsername) || string.IsNullOrWhiteSpace(SoulseekPassword))
        {
            StatusMessage = "Vul een Soulseek-gebruikersnaam en wachtwoord in.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(DownloadFilePath))
        {
            StatusMessage = "Kies een downloadmap.";
            return false;
        }
        return true;
    }

    private static List<string> SplitList(string value) =>
        value.Split(new[] { ' ', ',', ':' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .ToList();

    private SeekConfig BuildConfig()
    {
        var config = new SeekConfig
        {
            SoulseekUsername = SoulseekUsername.Trim(),
            SoulseekPassword = SoulseekPassword,
            SoulseekListenPort = (int)SoulseekListenPort,
            DownloadFilePath = DownloadFilePath.Trim(),
            MusicLibrary = MusicLibrary.Trim(),
            SearchTerm = SearchTerm.Trim(),
            SearchFilePath = SearchFilePath.Trim(),
            DownloadArchiveFilePath = string.IsNullOrWhiteSpace(DownloadArchiveFilePath) ? null : DownloadArchiveFilePath.Trim(),
            SearchDelimeter = string.IsNullOrEmpty(SearchDelimeter) ? "-" : SearchDelimeter,
            ThreadCount = (int)ThreadCount,
            MaxFileSize = (int)MaxFileSize,
            InMemoryDownloadMaxSize = (int)InMemoryDownloadMaxSize,
            MusicLibraryMatch = (int)MusicLibraryMatch,
            SearchMatchArtistPercentage = (int)SearchMatchArtistPercentage,
            SearchMatchAlbumPercentage = (int)SearchMatchAlbumPercentage,
            SearchMatchTrackPercentage = (int)SearchMatchTrackPercentage,
            Mode = Mode,
            AlbumMode = AlbumMode,
            GroupedDownloads = GroupedDownloads,
            DownloadSingles = DownloadSingles,
            UpdateAlbumName = UpdateAlbumName,
            AllowNonTaggedFiles = AllowNonTaggedFiles,
            CheckTags = CheckTags,
            CheckTagsDelete = CheckTagsDelete,
            MusicLibraryQuickMatch = MusicLibraryQuickMatch,
            InMemoryDownloads = InMemoryDownloads,
            SaveInUploaderSubfolder = SaveInUploaderSubfolder,
            AcoustIdKey = Meta.AcoustIdKey,
            DiscogsToken = DiscogsToken.Trim(),
            FilterOutFileNames = SplitList(FilterOutFileNames),
            SortSource = Sort.SourceFolder,
            SortDest = Sort.DestFolder,
            AlacSource = Alac.SourceFolder,
            AlacOutput = Alac.OutputFolder,
            DupFolder = Duplicates.Folder,
            SyncLibrary = Sync.LibraryFolder,
            SyncIpod = Sync.IpodFolder,
            AppleLibrary = AppleMusic.LibraryFolder,
            AutoOrganize = AutoOrganize,
            GalaxyAlbumLevel = GalaxyAlbumLevel,
            DarkMode = DarkMode,
            FilenameTemplate = string.IsNullOrWhiteSpace(FilenameTemplate) ? NameTemplate.Default : FilenameTemplate.Trim(),
        };

        var extensions = SplitList(SearchFileExtensions);
        if (extensions.Count > 0)
            config.SearchFileExtensions = extensions;

        return config;
    }

    private void LoadFromConfig(SeekConfig c)
    {
        SoulseekUsername = c.SoulseekUsername;
        SoulseekPassword = c.SoulseekPassword;
        SoulseekListenPort = c.SoulseekListenPort;
        DownloadFilePath = c.DownloadFilePath;
        MusicLibrary = c.MusicLibrary;
        SearchTerm = c.SearchTerm;
        SearchFilePath = c.SearchFilePath;
        DownloadArchiveFilePath = c.DownloadArchiveFilePath ?? string.Empty;
        SearchDelimeter = c.SearchDelimeter;
        ThreadCount = c.ThreadCount;
        MaxFileSize = c.MaxFileSize;
        InMemoryDownloadMaxSize = c.InMemoryDownloadMaxSize;
        MusicLibraryMatch = c.MusicLibraryMatch;
        SearchMatchArtistPercentage = c.SearchMatchArtistPercentage;
        SearchMatchAlbumPercentage = c.SearchMatchAlbumPercentage;
        SearchMatchTrackPercentage = c.SearchMatchTrackPercentage;
        Mode = c.Mode;
        AlbumMode = c.AlbumMode;
        GroupedDownloads = c.GroupedDownloads;
        DownloadSingles = c.DownloadSingles;
        UpdateAlbumName = c.UpdateAlbumName;
        AllowNonTaggedFiles = c.AllowNonTaggedFiles;
        CheckTags = c.CheckTags;
        CheckTagsDelete = c.CheckTagsDelete;
        MusicLibraryQuickMatch = c.MusicLibraryQuickMatch;
        InMemoryDownloads = c.InMemoryDownloads;
        SaveInUploaderSubfolder = c.SaveInUploaderSubfolder;
        FilterOutFileNames = string.Join(" ", c.FilterOutFileNames ?? new List<string>());
        SearchFileExtensions = string.Join(" ", c.SearchFileExtensions ?? new List<string>());

        Sort.SourceFolder = c.SortSource;
        Sort.DestFolder = c.SortDest;
        Alac.SourceFolder = c.AlacSource;
        Alac.OutputFolder = c.AlacOutput;
        Duplicates.Folder = c.DupFolder;
        Sync.LibraryFolder = c.SyncLibrary;
        Sync.IpodFolder = c.SyncIpod;
        AppleMusic.LibraryFolder = c.AppleLibrary;
        AutoOrganize = c.AutoOrganize;
        GalaxyAlbumLevel = c.GalaxyAlbumLevel;
        DarkMode = c.DarkMode;
        FilenameTemplate = string.IsNullOrWhiteSpace(c.FilenameTemplate) ? NameTemplate.Default : c.FilenameTemplate;
        DiscogsToken = c.DiscogsToken ?? string.Empty;
    }

    // Called when the window closes so tool folders (and other settings) survive a restart.
    public void SaveSettings()
    {
        Settings.Save(BuildConfig());
        Lib.Configure(MusicLibrary, DownloadFilePath);
    }
}
