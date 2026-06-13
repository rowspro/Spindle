using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Spindle;

/// <summary>
/// Fetches lyrics online (LRCLIB) and applies them to a file: synced lyrics → a sidecar .lrc next to
/// the track (for Rockbox), plain lyrics → the embedded lyrics tag (for the stock iPod). Shared by the
/// editor button, the whole-library bulk action and the auto-on-approve toggle.
/// </summary>
public static class Lyrics
{
    /// <summary>Fetch + write lyrics for one file. Returns true if anything was written.</summary>
    public static async Task<bool> ApplyToFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var t = new ATL.Track(path);
            var artist = !string.IsNullOrWhiteSpace(t.Artist) ? t.Artist : (t.AlbumArtist ?? "");
            var res = await LyricsClient.FetchAsync(artist, t.Title ?? "", t.Album ?? "", t.Duration, ct);
            if (res == null || res.Instrumental || !res.HasAny) return false;

            if (!string.IsNullOrWhiteSpace(res.Synced))
                try { File.WriteAllText(Path.ChangeExtension(path, ".lrc"), res.Synced!); } catch { }

            var plain = !string.IsNullOrWhiteSpace(res.Plain) ? res.Plain : StripTimestamps(res.Synced);
            if (!string.IsNullOrWhiteSpace(plain))
                try
                {
                    var tt = new ATL.Track(path);
                    tt.Lyrics = new List<ATL.LyricsInfo> { new ATL.LyricsInfo { UnsynchronizedLyrics = plain } };
                    tt.Save();
                }
                catch { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Drop the [mm:ss.xx] timestamps from an LRC to get plain lyrics.</summary>
    public static string StripTimestamps(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return "";
        var lines = lrc.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l, @"^(\s*\[\d+:\d+(?:\.\d+)?\])+", "").Trim());
        return string.Join("\n", lines.Where(l => l.Length > 0));
    }
}
