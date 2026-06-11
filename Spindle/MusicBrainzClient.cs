using System.Net.Http;
using System.Text.Json;

namespace Spindle;

public class MbAlbum
{
    public string Title { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public int ExpectedTracks { get; set; } // 0 = unknown
}

public class MbRecording
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string ReleaseId { get; set; } = string.Empty;
}

public class MbTrack
{
    public int Position { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class MbReleaseMatch
{
    public string Album { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string ReleaseId { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public List<MbTrack> Tracks { get; set; } = new();
}

/// <summary>
/// Fetches an artist's official studio albums from MusicBrainz (free, no API key).
/// Filters out bootlegs/edits (non-official releases) and non-album types
/// (compilations, live, mixtapes, demos, remixes).
/// </summary>
public static class MusicBrainzClient
{
    private const string Base = "https://musicbrainz.org/ws/2";
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Reason the last call returned no albums (shown in the UI for diagnosis).</summary>
    public static string? LastError { get; private set; }

    private static string? _lastHttpError;

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        // MusicBrainz requires a descriptive User-Agent; bypass .NET's strict validation so it is always sent.
        h.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Spindle/1.0 ( https://github.com/rowspro/Spindle )");
        return h;
    }

    public static async Task<List<MbAlbum>> GetOfficialAlbumsAsync(string artist)
    {
        LastError = null;
        _lastHttpError = null;
        try
        {
            var mbid = await ResolveArtistAsync(artist);
            if (mbid == null)
            {
                LastError = _lastHttpError != null
                    ? $"verbinding mislukt ({_lastHttpError})"
                    : "artiest niet gevonden";
                return new List<MbAlbum>();
            }

            await Task.Delay(300); // be gentle with the MusicBrainz rate limit
            var albums = await GetAlbumsAsync(mbid);
            if (albums.Count == 0 && LastError == null)
                LastError = _lastHttpError != null ? $"verbinding mislukt ({_lastHttpError})" : "geen officiële albums";
            return albums;
        }
        catch (Exception e)
        {
            LastError = $"{e.GetType().Name}: {e.Message}";
            return new List<MbAlbum>();
        }
    }

    private static async Task<string?> ResolveArtistAsync(string artist)
    {
        foreach (var q in new[] { $"artist:\"{artist}\"", artist })
        {
            var url = $"{Base}/artist?query={Uri.EscapeDataString(q)}&fmt=json&limit=5";
            var json = await TryGetAsync(url);
            if (json == null) continue;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("artists", out var arr) && arr.GetArrayLength() > 0)
                return arr[0].GetProperty("id").GetString();
        }
        return null;
    }

    private static async Task<List<MbAlbum>> GetAlbumsAsync(string mbid)
    {
        // Use the search API with a Lucene filter: official-status studio albums only
        // (no compilations/live/mixtapes/etc.). The release-group *browse* does not accept inc=releases,
        // and the bare release-group object has no status, so search is the reliable way to filter.
        var query = $"arid:{mbid} AND primarytype:album AND status:official AND -secondarytype:*";
        var url = $"{Base}/release-group?query={Uri.EscapeDataString(query)}&fmt=json&limit=100";
        var json = await TryGetAsync(url);
        if (json == null) return new List<MbAlbum>();

        using var doc = JsonDocument.Parse(json);
        var list = new List<MbAlbum>();
        if (!doc.RootElement.TryGetProperty("release-groups", out var rgs))
            return list;

        foreach (var rg in rgs.EnumerateArray())
        {
            var title = rg.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(title)) continue;
            var date = rg.TryGetProperty("first-release-date", out var d) ? d.GetString() ?? string.Empty : string.Empty;

            list.Add(new MbAlbum
            {
                Title = title,
                Year = date.Length >= 4 ? date.Substring(0, 4) : string.Empty,
                ExpectedTracks = 0 // search API doesn't return track counts; "X tracks found" is shown instead
            });
        }

        return list.OrderBy(a => a.Year).ToList();
    }

    /// <summary>Look up a recording by existing tags to auto-fill missing metadata.</summary>
    public static async Task<MbRecording?> LookupRecordingAsync(string artist, string title, string album)
    {
        try
        {
            var terms = new List<string>();
            if (!string.IsNullOrWhiteSpace(title)) terms.Add($"recording:\"{Lucene(title)}\"");
            if (!string.IsNullOrWhiteSpace(artist)) terms.Add($"artist:\"{Lucene(artist)}\"");
            if (!string.IsNullOrWhiteSpace(album)) terms.Add($"release:\"{Lucene(album)}\"");
            if (terms.Count == 0) return null;

            var url = $"{Base}/recording?query={Uri.EscapeDataString(string.Join(" AND ", terms))}&fmt=json&limit=5";
            var json = await TryGetAsync(url);
            if (json == null) return null;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recordings", out var recs) || recs.GetArrayLength() == 0)
                return null;

            var r0 = recs[0];
            var rec = new MbRecording
            {
                Title = r0.TryGetProperty("title", out var t0) ? t0.GetString() ?? "" : ""
            };

            if (r0.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var c in ac.EnumerateArray())
                    if (c.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } s)
                        names.Add(s);
                rec.Artist = string.Join(", ", names);
            }

            // Pick the most canonical release across the top matches: prefer an official studio
            // album (no compilation/live) with a date, instead of a random single/megamix/bootleg.
            int bestScore = -1, count = 0;
            foreach (var rr in recs.EnumerateArray())
            {
                if (count++ >= 5) break;
                if (!rr.TryGetProperty("releases", out var rels) || rels.ValueKind != JsonValueKind.Array) continue;
                foreach (var rel in rels.EnumerateArray())
                {
                    int score = 0;
                    if (rel.TryGetProperty("status", out var st) && st.GetString() == "Official") score += 3;
                    if (rel.TryGetProperty("release-group", out var rg))
                    {
                        if (rg.TryGetProperty("primary-type", out var pt) && pt.GetString() == "Album") score += 2;
                        if (!rg.TryGetProperty("secondary-types", out var sec) || sec.GetArrayLength() == 0) score += 2;
                    }
                    var date = rel.TryGetProperty("date", out var dt) ? dt.GetString() ?? "" : "";
                    if (date.Length >= 4) score += 1;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        rec.Album = rel.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
                        rec.ReleaseId = rel.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
                        rec.Year = date.Length >= 4 ? date.Substring(0, 4) : "";
                    }
                }
            }

            // Genre (community-voted; coverage is partial) needs a separate inc=genres lookup.
            // Recording genres are sparse, so fall back to the primary artist's top genre.
            var recId = r0.TryGetProperty("id", out var idp) ? idp.GetString() : null;
            if (!string.IsNullOrEmpty(recId))
                rec.Genre = await GetTopGenreAsync("recording", recId);
            if (string.IsNullOrEmpty(rec.Genre))
            {
                var artistId = ArtistId(r0);
                if (!string.IsNullOrEmpty(artistId))
                    rec.Genre = await GetTopGenreAsync("artist", artistId);
            }

            return rec;
        }
        catch { return null; }
    }

    /// <summary>
    /// Match a folder of files to one official MusicBrainz release (album-level), returning the
    /// canonical album/year/artist/genre and the full tracklist so tags can be filled consistently.
    /// </summary>
    public static async Task<MbReleaseMatch?> MatchReleaseAsync(string artist, string album, int fileCount)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(album)) return null;
            var terms = new List<string> { $"release:\"{Lucene(album)}\"", "status:official" };
            if (!string.IsNullOrWhiteSpace(artist)) terms.Add($"artist:\"{Lucene(artist)}\"");
            var url = $"{Base}/release?query={Uri.EscapeDataString(string.Join(" AND ", terms))}&fmt=json&limit=15";
            var json = await TryGetAsync(url);
            if (json == null) return null;

            string? bestId = null;
            int bestScore = int.MinValue;
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("releases", out var rels) || rels.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var rel in rels.EnumerateArray())
                {
                    int score = 0;
                    if (rel.TryGetProperty("status", out var st) && st.GetString() == "Official") score += 2;
                    if (rel.TryGetProperty("release-group", out var rg))
                    {
                        if (rg.TryGetProperty("primary-type", out var pt) && pt.GetString() == "Album") score += 2;
                        if (!rg.TryGetProperty("secondary-types", out var sec) || sec.GetArrayLength() == 0) score += 2;
                    }
                    if (fileCount > 0 && rel.TryGetProperty("track-count", out var tcp) && tcp.TryGetInt32(out var tc) && tc > 0)
                        score -= System.Math.Abs(tc - fileCount);
                    if (rel.TryGetProperty("date", out var dt) && (dt.GetString() ?? "").Length >= 4) score += 1;
                    if (score > bestScore) { bestScore = score; bestId = rel.TryGetProperty("id", out var idp) ? idp.GetString() : null; }
                }
            }
            if (string.IsNullOrEmpty(bestId)) return null;

            await Task.Delay(300); // rate limit
            var lj = await TryGetAsync($"{Base}/release/{bestId}?fmt=json&inc=recordings+artist-credits+genres+release-groups");
            if (lj == null) return null;

            using var ldoc = JsonDocument.Parse(lj);
            var root = ldoc.RootElement;
            var m = new MbReleaseMatch { ReleaseId = bestId! };
            m.Album = root.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";

            var date = root.TryGetProperty("date", out var dd) ? dd.GetString() ?? "" : "";
            if (date.Length < 4 && root.TryGetProperty("release-group", out var rgg) && rgg.TryGetProperty("first-release-date", out var frd))
                date = frd.GetString() ?? "";
            m.Year = date.Length >= 4 ? date.Substring(0, 4) : "";

            if (root.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var c in ac.EnumerateArray())
                    if (c.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } s) names.Add(s);
                m.Artist = string.Join(", ", names);
            }

            m.Genre = TopGenreFromElement(root);
            if (string.IsNullOrEmpty(m.Genre))
            {
                var aid = ArtistId(root);
                if (!string.IsNullOrEmpty(aid)) m.Genre = await GetTopGenreAsync("artist", aid);
            }

            if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            {
                foreach (var med in media.EnumerateArray())
                {
                    if (!med.TryGetProperty("tracks", out var trks) || trks.ValueKind != JsonValueKind.Array) continue;
                    foreach (var tr in trks.EnumerateArray())
                    {
                        int pos = tr.TryGetProperty("position", out var pp) && pp.TryGetInt32(out var pv) ? pv : 0;
                        var ttl = tr.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        if (ttl.Length > 0) m.Tracks.Add(new MbTrack { Position = pos, Title = ttl });
                    }
                }
            }

            return m;
        }
        catch { return null; }
    }

    private static string TopGenreFromElement(JsonElement el)
    {
        if (!el.TryGetProperty("genres", out var g) || g.ValueKind != JsonValueKind.Array || g.GetArrayLength() == 0)
            return string.Empty;
        string best = ""; int bestCount = -1;
        foreach (var ge in g.EnumerateArray())
        {
            int c = ge.TryGetProperty("count", out var cc) ? cc.GetInt32() : 0;
            var nm = ge.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
            if (nm.Length > 0 && c > bestCount) { bestCount = c; best = nm; }
        }
        return TitleCase(best);
    }

    /// <summary>Fetch front cover art for a release from the Cover Art Archive (no key needed).</summary>
    public static async Task<byte[]?> GetCoverArtAsync(string releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId)) return null;
        try { return await Http.GetByteArrayAsync($"https://coverartarchive.org/release/{releaseId}/front-500"); }
        catch { return null; }
    }

    private static string? ArtistId(JsonElement rec)
    {
        if (rec.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == JsonValueKind.Array && ac.GetArrayLength() > 0)
        {
            var first = ac[0];
            if (first.TryGetProperty("artist", out var a) && a.TryGetProperty("id", out var id))
                return id.GetString();
        }
        return null;
    }

    private static async Task<string> GetTopGenreAsync(string entity, string id)
    {
        try
        {
            var json = await TryGetAsync($"{Base}/{entity}/{id}?fmt=json&inc=genres");
            if (json == null) return string.Empty;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("genres", out var g) || g.GetArrayLength() == 0)
                return string.Empty;
            string best = ""; int bestCount = -1;
            foreach (var ge in g.EnumerateArray())
            {
                int c = ge.TryGetProperty("count", out var cc) ? cc.GetInt32() : 0;
                var nm = ge.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                if (nm.Length > 0 && c > bestCount) { bestCount = c; best = nm; }
            }
            return TitleCase(best);
        }
        catch { return string.Empty; }
    }

    private static string TitleCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        return string.Join(" ", s.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w.Substring(1)));
    }

    private static string Lucene(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<string?> TryGetAsync(string url)
    {
        try
        {
            return await Http.GetStringAsync(url);
        }
        catch (Exception e)
        {
            _lastHttpError = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }
}
