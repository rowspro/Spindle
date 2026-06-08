using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace SeekDownloader.Gui;

/// <summary>
/// Fetches lyrics from lrclib.net (free, no key) and writes a .lrc sidecar next to the track
/// (synced when available) — read by Rockbox and most players.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<bool> FetchLrc(string path, string artist, string title, string album, int durationSec, CancellationToken token)
    {
        try
        {
            var (synced, plain) = await Get(artist, title, album, durationSec, token);
            var text = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lrc = System.IO.Path.ChangeExtension(path, ".lrc");
            await System.IO.File.WriteAllTextAsync(lrc, text, token);
            return true;
        }
        catch { return false; }
    }

    private static async Task<(string? synced, string? plain)> Get(string artist, string title, string album, int durationSec, CancellationToken token)
    {
        string Q(string s) => Uri.EscapeDataString(s ?? "");
        var url = $"https://lrclib.net/api/get?artist_name={Q(artist)}&track_name={Q(title)}&album_name={Q(album)}";
        if (durationSec > 0) url += $"&duration={durationSec}";
        var r = await TryParse(url, token);
        if (r != null) return r.Value;

        // Fall back to search if the exact-match endpoint misses.
        var sUrl = $"https://lrclib.net/api/search?q={Q(artist + " " + title)}";
        try
        {
            var json = await Http.GetStringAsync(sUrl, token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                return Extract(doc.RootElement[0]);
        }
        catch { }
        return (null, null);
    }

    private static async Task<(string?, string?)?> TryParse(string url, CancellationToken token)
    {
        try
        {
            var json = await Http.GetStringAsync(url, token);
            using var doc = JsonDocument.Parse(json);
            return Extract(doc.RootElement);
        }
        catch { return null; }
    }

    private static (string?, string?) Extract(JsonElement el)
    {
        string? synced = el.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
        string? plain = el.TryGetProperty("plainLyrics", out var p) ? p.GetString() : null;
        return (synced, plain);
    }
}
