using System.Text.RegularExpressions;

namespace Spindle;

/// <summary>
/// The user's "standard genres" — a base set (Apple Music / iTunes primary genres) plus any the
/// user added. The Library Doctor flags tracks that are genre-less or whose genre doesn't conform
/// to this set, and lets them be retagged to one of these. Set from settings; see docs/SPINDLE_GOALS.md.
/// </summary>
public static class Genres
{
    // Apple Music primary genres (iTunes genre tree, id 34), trimmed to a practical default.
    // Users add/remove their own from Personalisations.
    public static readonly string[] Default =
    {
        "African", "Alternative", "Blues", "Children's Music", "Classical", "Comedy", "Country",
        "Dance", "Easy Listening", "Electronic", "Folk", "Hip-Hop/Rap", "Holiday", "Instrumental",
        "Jazz", "J-Pop", "K-Pop", "Latin", "Metal", "New Age", "Opera", "Pop", "R&B/Soul",
        "Reggae", "Rock", "Singer/Songwriter", "Soundtrack", "Spoken Word", "Vocal", "World",
    };

    // The active standard set, set from settings (defaults to Default).
    public static IReadOnlyList<string> Standard { get; set; } = Default;

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>The standard genre matching <paramref name="genre"/> (case/punctuation-insensitive), or null.</summary>
    public static string? Match(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return null;
        var n = Norm(genre);
        foreach (var g in Standard) if (Norm(g) == n) return g;
        return null;
    }

    public static bool IsStandard(string? genre) => Match(genre) != null;
}
