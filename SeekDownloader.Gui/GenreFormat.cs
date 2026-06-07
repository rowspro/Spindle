using System.Linq;

namespace SeekDownloader.Gui;

/// <summary>
/// Normalizes a genre tag to one clean, canonical value. Fixes case variants ("hip hop", "HipHop",
/// "Hip-Hop" -> "Hip-Hop/Rap") and reduces long multi-genre strings ("Hip-Hop/Rap/Pop/Soul") to their
/// primary genre. Shared by the sort tool and the metadata Apple-format.
/// </summary>
public static class GenreFormat
{
    private static readonly char[] Separators = { '/', ';', ',', '|', '\\' };

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;

        // Take the primary genre when several are jammed together with a separator.
        var first = s.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
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
