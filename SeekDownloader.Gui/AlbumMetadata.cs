using System.Net.Http;
using System.Text.Json;

namespace SeekDownloader.Gui;

/// <summary>One album candidate from a metadata provider, with a canonical, ordered tracklist.</summary>
public class AlbumMetaMatch
{
    public string Source = "";          // "Apple", "Discogs", "MusicBrainz"
    public string Artist = "";
    public string Album = "";
    public string Year = "";
    public string Genre = "";
    public int TrackCount;
    public List<string> TrackTitles = new(); // index = track position - 1
    public string CoverUrl = "";

    // provider ids for the lazy tracklist fetch
    public string CollectionId = "";    // Apple
    public string DiscogsReleaseId = "";
    public string MbReleaseId = "";

    public string Display => $"{Artist} — {Album}" + (Year.Length > 0 ? $" ({Year})" : "");
    public string Sub => $"{Source}" + (TrackCount > 0 ? $" · {TrackCount} tracks" : "") + (Genre.Length > 0 ? $" · {Genre}" : "");
}

/// <summary>
/// Album-level metadata lookup across iTunes/Apple (keyless), Discogs (token) and MusicBrainz (fallback).
/// Matching a whole album to ONE release keeps artist/album/year/genre spelling consistent across tracks.
/// </summary>
public static class AlbumMetadata
{
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Spindle/1.0 (https://github.com/rowspro/Spindle)");
        return h;
    }

    public static async Task<List<AlbumMetaMatch>> SearchAsync(string artist, string album, int fileCount, string? discogsToken)
    {
        var results = new List<AlbumMetaMatch>();
        try { results.AddRange(await ITunesAsync(artist, album)); } catch { }
        if (!string.IsNullOrWhiteSpace(discogsToken))
            try { results.AddRange(await DiscogsAsync(artist, album, discogsToken!)); } catch { }
        try { var mb = await MusicBrainzAlbumAsync(artist, album, fileCount); if (mb != null) results.Add(mb); } catch { }
        return results;
    }

    /// <summary>Fill the tracklist (and download nothing) for the chosen candidate, if not already present.</summary>
    public static async Task EnsureTracksAsync(AlbumMetaMatch m, string? discogsToken)
    {
        if (m.TrackTitles.Count > 0) return;
        try
        {
            if (m.Source == "Apple" && m.CollectionId.Length > 0) await FillApple(m);
            else if (m.Source == "Discogs" && m.DiscogsReleaseId.Length > 0) await FillDiscogs(m, discogsToken);
        }
        catch { }
    }

    public static async Task<byte[]?> DownloadCoverAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try { return await Http.GetByteArrayAsync(url); } catch { return null; }
    }

    // ---------- iTunes / Apple (no key) ----------
    private static async Task<List<AlbumMetaMatch>> ITunesAsync(string artist, string album)
    {
        var list = new List<AlbumMetaMatch>();
        var term = Uri.EscapeDataString($"{artist} {album}".Trim());
        var json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=album&limit=6");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var arr)) return list;
        foreach (var r in arr.EnumerateArray())
        {
            var m = new AlbumMetaMatch
            {
                Source = "Apple",
                Album = Str(r, "collectionName"),
                Artist = Str(r, "artistName"),
                Genre = Str(r, "primaryGenreName"),
                TrackCount = r.TryGetProperty("trackCount", out var tc) ? tc.GetInt32() : 0,
                CollectionId = r.TryGetProperty("collectionId", out var ci) ? ci.GetInt64().ToString() : "",
                CoverUrl = Str(r, "artworkUrl100").Replace("100x100bb", "600x600bb"),
            };
            var rd = Str(r, "releaseDate");
            m.Year = rd.Length >= 4 ? rd.Substring(0, 4) : "";
            if (m.Album.Length > 0) list.Add(m);
        }
        return list;
    }

    private static async Task FillApple(AlbumMetaMatch m)
    {
        var json = await Http.GetStringAsync($"https://itunes.apple.com/lookup?id={m.CollectionId}&entity=song&limit=300");
        using var doc = JsonDocument.Parse(json);
        var songs = new SortedDictionary<int, string>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (Str(r, "wrapperType") != "track" || Str(r, "kind") != "song") continue;
            int n = r.TryGetProperty("trackNumber", out var tn) ? tn.GetInt32() : 0;
            var t = Str(r, "trackName");
            if (n > 0 && t.Length > 0) songs[n] = t;
        }
        FillFromPositions(m, songs);
    }

    // ---------- Discogs (token) ----------
    private static async Task<List<AlbumMetaMatch>> DiscogsAsync(string artist, string album, string token)
    {
        var list = new List<AlbumMetaMatch>();
        var q = Uri.EscapeDataString($"{artist} {album}".Trim());
        var json = await Http.GetStringAsync($"https://api.discogs.com/database/search?q={q}&type=release&per_page=6&token={token}");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var arr)) return list;
        foreach (var r in arr.EnumerateArray())
        {
            var title = Str(r, "title");          // "Artist - Album"
            var parts = title.Split(" - ", 2, StringSplitOptions.TrimEntries);
            var m = new AlbumMetaMatch
            {
                Source = "Discogs",
                Artist = parts.Length == 2 ? parts[0] : artist,
                Album = parts.Length == 2 ? parts[1] : title,
                Year = Str(r, "year"),
                CoverUrl = Str(r, "cover_image"),
                DiscogsReleaseId = r.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : "",
            };
            if (r.TryGetProperty("genre", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0)
                m.Genre = g[0].GetString() ?? "";
            if (m.Album.Length > 0) list.Add(m);
        }
        return list;
    }

    private static async Task FillDiscogs(AlbumMetaMatch m, string? token)
    {
        var url = $"https://api.discogs.com/releases/{m.DiscogsReleaseId}" + (string.IsNullOrWhiteSpace(token) ? "" : $"?token={token}");
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var titles = new List<string>();
        if (doc.RootElement.TryGetProperty("tracklist", out var tl))
            foreach (var t in tl.EnumerateArray())
                if (Str(t, "type_") is "track" or "")     // skip headings
                    titles.Add(Str(t, "title"));
        m.TrackTitles = titles.Where(s => s.Length > 0).ToList();
        if (m.TrackCount == 0) m.TrackCount = m.TrackTitles.Count;
    }

    // ---------- MusicBrainz (fallback, reuses existing matcher) ----------
    private static async Task<AlbumMetaMatch?> MusicBrainzAlbumAsync(string artist, string album, int fileCount)
    {
        var rel = await MusicBrainzClient.MatchReleaseAsync(artist, album, fileCount);
        if (rel == null) return null;
        var m = new AlbumMetaMatch
        {
            Source = "MusicBrainz",
            Artist = rel.Artist, Album = rel.Album, Year = rel.Year, Genre = rel.Genre,
            MbReleaseId = rel.ReleaseId, TrackCount = rel.Tracks.Count,
            CoverUrl = string.IsNullOrEmpty(rel.ReleaseId) ? "" : $"https://coverartarchive.org/release/{rel.ReleaseId}/front-500",
        };
        var byPos = new SortedDictionary<int, string>();
        foreach (var t in rel.Tracks) byPos[t.Position] = t.Title;
        FillFromPositions(m, byPos);
        return m;
    }

    // ---------- helpers ----------
    private static void FillFromPositions(AlbumMetaMatch m, SortedDictionary<int, string> byPos)
    {
        if (byPos.Count == 0) return;
        int max = byPos.Keys.Max();
        m.TrackTitles = Enumerable.Range(1, max).Select(i => byPos.TryGetValue(i, out var v) ? v : "").ToList();
        if (m.TrackCount == 0) m.TrackCount = byPos.Count;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}
