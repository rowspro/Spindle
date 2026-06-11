using System.Text.RegularExpressions;

namespace Spindle;

/// <summary>
/// Apple-style text formatting for artist/title fields, shared by the metadata editor and the
/// organize pipeline. Artist: "A, B & C"; primary artist = first credited; titles in title case.
/// </summary>
public static class TextFormat
{
    public const string ArtistSplitPattern =
        @"\s*(?:;|/|,|&|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b|\bvs\.?\b|\bx\b)\s*";

    // The primary (first) credited artist — used as Album-artiest so collabs land under one artist.
    public static string PrimaryArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist;
        foreach (var p in Regex.Split(artist, ArtistSplitPattern, RegexOptions.IgnoreCase))
            if (p.Trim().Length > 0) return p.Trim();
        return artist.Trim();
    }

    public static string AppleArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist;
        var parts = Regex.Split(artist, ArtistSplitPattern, RegexOptions.IgnoreCase)
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var dedup = new List<string>();
        foreach (var p in parts)
            if (!dedup.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) dedup.Add(p);
        if (dedup.Count == 0) return artist;
        if (dedup.Count == 1) return dedup[0];
        return string.Join(", ", dedup.Take(dedup.Count - 1)) + " & " + dedup[^1];
    }

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into", "nor", "of",
        "on", "onto", "or", "over", "the", "to", "up", "vs", "via", "with", "feat", "ft"
    };

    public static string Title(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var words = s.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            bool keepLower = i != 0 && i != words.Length - 1 && SmallWords.Contains(w);
            words[i] = keepLower ? w.ToLowerInvariant() : char.ToUpper(w[0]) + w.Substring(1);
        }
        return string.Join(" ", words);
    }
}
