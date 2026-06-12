using System.Text.RegularExpressions;

namespace Spindle;

/// <summary>
/// A "single" is a standalone track, not part of a real multi-track album. We keep an artist's
/// singles together in one <c>Artist/Singles/</c> folder for tidiness (disk + iPod), but they are
/// NEVER treated as one shared album for any metadata operation — so an album-match can't homogenize
/// a whole bucket of unrelated singles. See docs/SPINDLE_GOALS.md and the "Bee Gees" failsafe.
/// </summary>
public static class Singles
{
    public const string Folder = "Singles";

    private static readonly Regex SingleWord = new(@"^\s*singles?\s*$", RegexOptions.IgnoreCase);

    /// <summary>True when the album tag does not denote a real album: empty, "Single"/"Singles",
    /// or identical to the track title.</summary>
    public static bool IsSingle(string? album, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(album)) return true;
        if (SingleWord.IsMatch(album)) return true;
        if (!string.IsNullOrWhiteSpace(title) && string.Equals(album.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
