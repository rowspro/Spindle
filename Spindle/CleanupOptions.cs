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

}
