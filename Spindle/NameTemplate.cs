using System.Text.RegularExpressions;

namespace Spindle;

/// <summary>
/// Builds a file name (without extension) from a template with tokens {artist} {album} {title}
/// {track} {year}. Empty tokens collapse cleanly. Used by the sort and organize tools.
/// </summary>
public static class NameTemplate
{
    public const string Default = "{artist} - {album} - {track} {title}";

    public static string Build(string template, string artist, string album, string title, int track, string year, Func<string, string> clean)
    {
        if (string.IsNullOrWhiteSpace(template)) template = Default;
        var trackStr = track > 0 ? track.ToString("00") : "";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["artist"] = clean(artist ?? ""),
            ["album"] = clean(album ?? ""),
            ["title"] = clean(title ?? ""),
            ["track"] = trackStr,
            ["year"] = year ?? ""
        };

        var result = Regex.Replace(template, @"\{(\w+)\}", m =>
            map.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

        // Tidy leftover separators around empty tokens.
        result = Regex.Replace(result, @"\s+", " ").Trim();
        result = Regex.Replace(result, @"(^[\s\-]+)|([\s\-]+$)", "");
        result = Regex.Replace(result, @"\s*-\s*-\s*", " - ");
        return string.IsNullOrWhiteSpace(result) ? clean(title ?? "track") : result;
    }
}
