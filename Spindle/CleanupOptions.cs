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

    // Separator the user picks for multi-genre tags. All other multi-genre separators are unified to it
    // on save (a single "/" is left alone — it lives inside canonical names like Hip-Hop/Rap).
    public static string GenreSeparator { get; set; } = ",";

    // After saving tag edits in the editor, rename the file to match the filename template.
    public static bool RenameToMatchTags { get; set; } = true;

    // Collapse leading/trailing/double spaces in tag fields automatically on save. Opt-out in settings.
    public static bool TrimSpaces { get; set; } = true;

    // The active filename template (mirrors the Personalisations setting) used for that rename.
    public static string FilenameTemplate { get; set; } = NameTemplate.Default;

    // Fetch lyrics online (LRCLIB) for albums approved from the Inbox.
    public static bool FetchLyricsOnApprove { get; set; }

    // Opt-in: keep the whole library's lyrics filled in the background (LRCLIB). Transfers carry the .lrc.
    public static bool AutoFetchLyrics { get; set; }
}
