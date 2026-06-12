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

    // Personalisations — Database (My Music): tag-cleanup behavior. See docs/IPOD_BEHAVIOR.md.
    public bool SplitArtistOnComma { get; set; } = true;            // false keeps "Last, First" sortnames intact
    public bool KeepMultipleGenres { get; set; }                    // true keeps multi-genre tags instead of reducing to one
    public bool GroupCollabsUnderPrimaryArtist { get; set; } = true; // album artist = first credited artist
    public bool AppleStyleArtistNames { get; set; } = true;         // reformat to "A, B & C"
    public bool TitleCaseTitlesAndAlbums { get; set; } = true;      // smart title case
    public bool AutoCleanOnApprove { get; set; }                    // clean tags automatically on Inbox approve

    // Personalisations — iPod: how music is prepared for the device.
    public bool FlattenArtistOnSync { get; set; }                   // primary artist on the iPod copy (source untouched)
    public bool ConvertToAlacDefault { get; set; }                  // default the Transfer "Convert to ALAC" toggle on
    public bool AutoCreatePlaylists { get; set; }                   // write a .m3u per album after transfer
    public bool RemoveDotUnderscoreAfterTransfer { get; set; } = true; // delete macOS ._* junk after transfer
    public bool SetCompilationFlag { get; set; }                    // tag various-artist albums as compilations

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

    // Standard genres (empty = the built-in default set). The Doctor retags to these.
    public List<string> StandardGenres { get; set; } = new();
}
