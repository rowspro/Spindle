using System.Linq;

namespace Spindle;

/// <summary>
/// Normalizes a genre tag to one clean, canonical value. Fixes case variants ("hip hop", "HipHop",
/// "Hip-Hop" -> "Hip-Hop/Rap") and reduces long multi-genre strings ("Hip-Hop/Rap/Pop/Soul") to their
/// primary genre. Shared by the sort tool and the metadata Apple-format.
/// </summary>
public static class GenreFormat
{
    private static readonly char[] Separators = { '/', ';', ',', '|', '\\' };

    // Multi-genre separators we unify (NOT a lone '/', which lives inside canonical names like Hip-Hop/Rap).
    private static readonly string[] MultiSeparators = { "//", "\\", ";", ",", "|" };

    /// <summary>The chosen separator rendered with its usual spacing, e.g. "Rock, Pop" / "Rock // Pop".</summary>
    public static string SepJoin => CleanupOptions.GenreSeparator switch
    {
        ";" => "; ",
        "//" => " // ",
        "\\" => " \\ ",
        _ => ", ",
    };

    /// <summary>
    /// Unify the multi-genre separators in a tag to the user's chosen one — without canonicalizing the
    /// names and without touching a lone '/' (part of names like Hip-Hop/Rap). Used on save so tagging
    /// stays consistent: "Rock; Pop" / "Rock\Pop" → "Rock, Pop" when the separator is a comma.
    /// </summary>
    public static string UnifySeparators(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;
        const char sentinel = '\u0001';
        var tmp = s;
        foreach (var o in MultiSeparators) tmp = tmp.Replace(o, sentinel.ToString());
        var tokens = tmp.Split(sentinel, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length <= 1) return s.Trim();
        var dedup = new List<string>();
        foreach (var t in tokens)
            if (!dedup.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) dedup.Add(t);
        return string.Join(SepJoin, dedup);
    }

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;

        var tokens = s.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (CleanupOptions.KeepMultipleGenres)
        {
            var canon = new List<string>();
            foreach (var tk in tokens)
            {
                var c = Canonical(tk);
                if (!canon.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) canon.Add(c);
            }
            return canon.Count == 0 ? Canonical(s.Trim()) : string.Join(SepJoin, canon);
        }

        // Take the primary genre when several are jammed together with a separator.
        var first = tokens.FirstOrDefault();
        var token = string.IsNullOrWhiteSpace(first) ? s.Trim() : first;

        return Canonical(token);
    }

    private static string Canonical(string token) => token.Trim().ToLowerInvariant() switch
    {
        "hip hop" or "hiphop" or "hip-hop" or "rap" or "trap" or "conscious hip hop" or "jazz rap"
            or "gangsta rap" or "southern hip hop" or "pop rap" => "Hip-Hop/Rap",
        "r&b" or "rnb" or "r and b" or "soul" or "rhythm and blues" or "r&b/soul" or "neo soul"
            or "contemporary r&b" => "R&B/Soul",
        "edm" or "electronica" or "electronic" or "house" or "techno" => "Electronic",
        "dnb" or "drum and bass" or "drum & bass" or "drum n bass" => "Drum & Bass",
        _ => TitleCase(token).Replace(" And ", " & ")
    };

    private static string TitleCase(string s)
    {
        var words = s.Trim().Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            words[i] = char.ToUpper(w[0]) + w.Substring(1).ToLowerInvariant();
        }
        return string.Join(" ", words);
    }
}
