using SeekDownloader.Services;

namespace SeekDownloader.Gui;

/// <summary>
/// Plain configuration object mirroring the CLI options of SeekDownloader.Commands.RootCommand,
/// used to drive a download run from the GUI.
/// </summary>
public class SeekConfig
{
    public string DownloadFilePath { get; set; } = string.Empty;
    public int SoulseekListenPort { get; set; } = 12345;
    public string SoulseekUsername { get; set; } = string.Empty;
    public string SoulseekPassword { get; set; } = string.Empty;

    public string SearchDelimeter { get; set; } = "-";
    public string MusicLibrary { get; set; } = string.Empty;
    public string SearchTerm { get; set; } = string.Empty;
    public string SearchFilePath { get; set; } = string.Empty;

    public int ThreadCount { get; set; } = 10;
    public int Mode { get; set; } // 0 = automatisch, 1 = handmatig (per bestand), 2 = artiest (kies albums)
    public bool AlbumMode { get; set; }
    public bool GroupedDownloads { get; set; }
    public bool DownloadSingles { get; set; }
    public bool UpdateAlbumName { get; set; }
    public bool AllowNonTaggedFiles { get; set; }
    public bool CheckTags { get; set; }
    public bool CheckTagsDelete { get; set; }

    public List<string> SearchFileExtensions { get; set; } = FileSeekService.MediaFileExtensions.ToList();
    public List<string> FilterOutFileNames { get; set; } = new();

    public int MusicLibraryMatch { get; set; } = 50;
    public bool MusicLibraryQuickMatch { get; set; }
    public int MaxFileSize { get; set; } = 200;
    public bool InMemoryDownloads { get; set; }
    public int InMemoryDownloadMaxSize { get; set; } = 50;
    public bool SaveInUploaderSubfolder { get; set; }
    public string? DownloadArchiveFilePath { get; set; }

    public string AcoustIdKey { get; set; } = string.Empty;

    public int SearchMatchArtistPercentage { get; set; } = 50;
    public int SearchMatchAlbumPercentage { get; set; } = 50;
    public int SearchMatchTrackPercentage { get; set; } = 50;

    // Last-used folders per tool, so the app remembers them between launches.
    public string SortSource { get; set; } = string.Empty;
    public string SortDest { get; set; } = string.Empty;
    public string AlacSource { get; set; } = string.Empty;
    public string AlacOutput { get; set; } = string.Empty;
    public string DupFolder { get; set; } = string.Empty;
    public string SyncLibrary { get; set; } = string.Empty;
    public string SyncIpod { get; set; } = string.Empty;
    public string AppleLibrary { get; set; } = string.Empty;

    // Run the Organiseren-pijplijn automatically on the download folder after an auto-download finishes.
    public bool AutoOrganize { get; set; }

    // Filename template for sort/organize (tokens: {artist} {album} {title} {track} {year}).
    public string FilenameTemplate { get; set; } = "{artist} - {album} - {track} {title}";

    // Followed artists for the discography/watchlist tab.
    public List<string> Watchlist { get; set; } = new();
}
