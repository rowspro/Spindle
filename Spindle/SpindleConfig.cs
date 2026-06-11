namespace Spindle;

/// <summary>Persisted app settings (folders, tokens, modes), stored as settings.json in App Support.</summary>
public class SpindleConfig
{
    public string DownloadFilePath { get; set; } = string.Empty;   // de "New music"-inbox-map
    public string MusicLibrary { get; set; } = string.Empty;

    public string AcoustIdKey { get; set; } = string.Empty;
    public string DiscogsToken { get; set; } = string.Empty;

    // Last-used folders per tool, so the app remembers them between launches.
    public string DupFolder { get; set; } = string.Empty;
    public string SyncLibrary { get; set; } = string.Empty;
    public string SyncIpod { get; set; } = string.Empty;

    public bool GalaxyAlbumLevel { get; set; }
    public bool DarkMode { get; set; }

    // Filename template for inbox approve (tokens: {artist} {album} {title} {track} {year}).
    public string FilenameTemplate { get; set; } = "{artist} - {album} - {track} {title}";

    // Wantlist: followed artists + wanted albums ("artist|year|album").
    public List<string> Watchlist { get; set; } = new();
    public List<string> Wantlist { get; set; } = new();

    // iPod mode: Rockbox (direct transfer) or iTunes (background ALAC mirror).
    public bool ItunesMode { get; set; }
    public string AlacMirrorFolder { get; set; } = string.Empty;
    public List<string> TransferWanted { get; set; } = new();

    // Duplicate sets the user marked as "not a duplicate" (normalized artist+title keys).
    public List<string> DuplicateIgnores { get; set; } = new();
}
