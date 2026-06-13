using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace Spindle;

public sealed class LyricsResult
{
    public string? Synced;     // raw LRC text with [mm:ss.xx] timestamps
    public string? Plain;      // unsynchronized lyrics
    public bool Instrumental;
    public bool HasAny => Instrumental || !string.IsNullOrWhiteSpace(Synced) || !string.IsNullOrWhiteSpace(Plain);
}

/// <summary>
/// Online lyrics lookup against LRCLIB (https://lrclib.net) — no key, no rate limit. Matches on
/// artist + title + album + duration, with a /api/search fallback. See docs/LYRICS (online approach).
/// </summary>
public static class LyricsClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // LRCLIB etiquette: identify the app.
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Spindle/1.0 (https://github.com/rowspro/Spindle)");
        return h;
    }

    public static async Task<LyricsResult?> FetchAsync(string artist, string title, string album, int durationSec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;

        // 1) Exact signature (artist + title + album + duration). The server allows a small duration tolerance.
        var getUrl = "https://lrclib.net/api/get"
            + "?artist_name=" + Uri.EscapeDataString(artist)
            + "&track_name=" + Uri.EscapeDataString(title)
            + "&album_name=" + Uri.EscapeDataString(album ?? "")
            + "&duration=" + durationSec;
        var exact = await TryOneAsync(getUrl, ct);
        if (exact != null) return exact;

        // 2) Fallback: search by title + artist, pick the best candidate (prefer synced, closest duration).
        var searchUrl = "https://lrclib.net/api/search"
            + "?track_name=" + Uri.EscapeDataString(title)
            + "&artist_name=" + Uri.EscapeDataString(artist);
        return await TrySearchAsync(searchUrl, durationSec, ct);
    }

    private static async Task<LyricsResult?> TryOneAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return FromElement(doc.RootElement);
        }
        catch { return null; }
    }

    private static async Task<LyricsResult?> TrySearchAsync(string url, int durationSec, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            LyricsResult? best = null; double bestScore = double.MaxValue;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var r = FromElement(el);
                if (r == null || !r.HasAny) continue;
                double dur = el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                // prefer synced lyrics and the closest duration
                double score = Math.Abs(dur - durationSec) + (string.IsNullOrWhiteSpace(r.Synced) ? 1000 : 0);
                if (score < bestScore) { bestScore = score; best = r; }
            }
            return best;
        }
        catch { return null; }
    }

    private static LyricsResult? FromElement(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool instr = e.TryGetProperty("instrumental", out var iv) && iv.ValueKind == JsonValueKind.True;
        return new LyricsResult { Synced = S("syncedLyrics"), Plain = S("plainLyrics"), Instrumental = instr };
    }
}
