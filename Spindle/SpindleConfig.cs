namespace Spindle;

/// <summary>One saved playlist: a name and the ordered library paths of its tracks.</summary>
public sealed class PlaylistDto
{
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = new();
}

/// <summary>One rule of a smart playlist (e.g. Rating ≥ 4).</summary>
public sealed class SmartRuleDto
{
    public string Field { get; set; } = "";
    public string Op { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A rule-based playlist that's evaluated live against the index.</summary>
public sealed class SmartPlaylistDto
{
    public string Name { get; set; } = "";
    public bool MatchAll { get; set; } = true;
    public List<SmartRuleDto> Rules { get; set; } = new();
}

/// <summary>Persisted app settings (folders, tokens, modes), stored as settings.json in App Support.</summary>
public class SpindleConfig
{
    public List<PlaylistDto> Playlists { get; set; } = new();
    public List<SmartPlaylistDto> SmartPlaylists { get; set; } = new();

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

    // Personalisations — Database (My Music). See docs/IPOD_BEHAVIOR.md.
    public bool SplitArtistOnComma { get; set; } = true;            // false keeps "Last, First" sortnames intact
    public bool KeepMultipleGenres { get; set; }                    // true keeps multi-genre tags instead of reducing to one
    public string GenreSeparator { get; set; } = ",";              // chosen multi-genre separator (",", ";", "//", "\\")
    public bool RenameToMatchTags { get; set; } = true;            // rename files to the template after a tag edit
    public bool TrimSpaces { get; set; } = true;                    // collapse stray spaces in tags automatically on save
    public bool FetchLyricsOnApprove { get; set; }                  // fetch lyrics online when approving from the Inbox
    public bool AutoFetchLyrics { get; set; }                       // background-fill the whole library's lyrics (LRCLIB)

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
