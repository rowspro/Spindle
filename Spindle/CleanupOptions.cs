namespace Spindle;

/// <summary>
/// User personalisation preferences for tag cleanup (the "Database / My Music" side), set from
/// settings and read by the tag-format helpers and the metadata actions. Defaults reproduce the
/// original behavior, so an unconfigured/old install behaves exactly as before. See docs/IPOD_BEHAVIOR.md.
/// </summary>
public static class CleanupOptions
{
    // Treat a comma as an artist separator. Off keeps "Last, First" sortnames intact.
    public static bool SplitArtistOnComma { get; set; } = true;

    // Keep every genre (each canonicalized, deduped) instead of reducing to the primary one.
    public static bool KeepMultipleGenres { get; set; }

    // Cleanup sets Album Artist to the first credited artist, so collabs/feats group under one artist.
    public static bool GroupCollabsUnderPrimaryArtist { get; set; } = true;

    // How multi-artist tags are written back (the canonical separator). One of:
    // "asis" (leave untouched), "apple" ("A, B & C"), "semicolon" ("A; B; C"),
    // "slash" ("A / B / C"), "comma" ("A, B, C").
    public static string ArtistJoin { get; set; } = "apple";

    // Apply smart title case to titles and album names.
    public static bool TitleCaseTitlesAndAlbums { get; set; } = true;

    // Run the cleanup automatically when approving albums from the Inbox.
    public static bool AutoCleanOnApprove { get; set; }
}
