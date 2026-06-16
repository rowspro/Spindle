using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ATL;

namespace Spindle;

/// <summary>One indexed audio file (a row in the tracks table).</summary>
public sealed class IndexedTrack
{
    public string Path = "";
    public long Mtime;
    public long Size;
    public string Title = "";
    public string Artist = "";
    public string AlbumArtist = "";
    public string Album = "";
    public string Genre = "";
    public int Year;
    public int TrackNo;
    public int Disc;
    public int Duration;        // seconds
    public string Format = "";  // "FLAC", "MP3", ...
    public bool Lossless;
    public int Bitrate;
    public bool HasCover;
    public bool MissingTags;    // no title or no (album)artist
}

public sealed class ScanStats
{
    public int Total, Added, Updated, Unchanged, Removed, Errors;
    public TimeSpan Elapsed;
    public override string ToString()
        => $"{Total} bestanden · {Added} nieuw · {Updated} gewijzigd · {Unchanged} ongewijzigd · {Removed} verwijderd · {Errors} fouten · {Elapsed.TotalSeconds:0.0}s";
}

public sealed record AlbumAgg(string AlbumArtist, string Album, int Tracks, int Year,
    int LossyTracks, int MissingTagTracks, bool HasCover);

/// <summary>
/// Persistent SQLite index of the music library (fase 0 van PLAN.md). Scans are incremental:
/// only files whose mtime/size changed are re-read with ATL; deleted files are pruned. After the
/// first scan every view (health, browser, galaxy) reads from here instantly. All db access is
/// serialized behind one lock so background scans and UI reads can interleave safely.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    private static readonly string[] LosslessExt = { ".flac", ".wav", ".aiff", ".aif" };
    private const string EffArtist = "CASE WHEN album_artist <> '' THEN album_artist ELSE artist END";

    private readonly SqliteConnection _db;
    private readonly object _dbLock = new();

    public static string DefaultDbPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Spindle", "library.db");

    public LibraryIndex(string? dbPath = null)
    {
        var path = dbPath ?? DefaultDbPath;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS tracks(
                 path TEXT PRIMARY KEY, root TEXT NOT NULL,
                 mtime INTEGER NOT NULL, size INTEGER NOT NULL,
                 title TEXT, artist TEXT, album_artist TEXT, album TEXT, genre TEXT,
                 year INTEGER, track INTEGER, disc INTEGER, duration INTEGER,
                 format TEXT, lossless INTEGER, bitrate INTEGER,
                 has_cover INTEGER, missing_tags INTEGER);");
        Exec("CREATE INDEX IF NOT EXISTS idx_tracks_album ON tracks(album_artist, album);");
        Exec("CREATE INDEX IF NOT EXISTS idx_tracks_root ON tracks(root);");
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);");
        // Spindle-managed listening stats (kept out of the source files; rating is also written to the
        // file tag separately so it travels). Survives rescans because the scan never touches this table.
        Exec(@"CREATE TABLE IF NOT EXISTS track_stats(
                 path TEXT PRIMARY KEY, rating INTEGER DEFAULT 0,
                 playcount INTEGER DEFAULT 0, lastplayed INTEGER DEFAULT 0);");
    }

    public int GetRating(string path)
    {
        lock (_dbLock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT rating FROM track_stats WHERE path = $p";
            c.Parameters.AddWithValue("$p", path);
            return c.ExecuteScalar() is long l ? (int)l : 0;
        }
    }

    public void SetRating(string path, int rating)
    {
        lock (_dbLock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO track_stats(path, rating) VALUES($p, $r) " +
                            "ON CONFLICT(path) DO UPDATE SET rating = $r";
            c.Parameters.AddWithValue("$p", path);
            c.Parameters.AddWithValue("$r", rating);
            c.ExecuteNonQuery();
        }
    }

    public (int Rating, int Play, long Last) GetStats(string path)
    {
        lock (_dbLock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT rating, playcount, lastplayed FROM track_stats WHERE path = $p";
            c.Parameters.AddWithValue("$p", path);
            using var r = c.ExecuteReader();
            return r.Read() ? ((int)r.GetInt64(0), (int)r.GetInt64(1), r.GetInt64(2)) : (0, 0, 0);
        }
    }

    public void BumpPlay(string path, long whenTicks)
    {
        lock (_dbLock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO track_stats(path, playcount, lastplayed) VALUES($p, 1, $t) " +
                            "ON CONFLICT(path) DO UPDATE SET playcount = playcount + 1, lastplayed = $t";
            c.Parameters.AddWithValue("$p", path);
            c.Parameters.AddWithValue("$t", whenTicks);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>All listening stats keyed by path (for smart playlists / sorting).</summary>
    public Dictionary<string, (int Rating, int Play, long Last)> AllStats()
    {
        var d = new Dictionary<string, (int, int, long)>(StringComparer.Ordinal);
        lock (_dbLock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT path, rating, playcount, lastplayed FROM track_stats";
            using var r = c.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = ((int)r.GetInt64(1), (int)r.GetInt64(2), r.GetInt64(3));
        }
        return d;
    }

    public void Dispose() { lock (_dbLock) _db.Dispose(); }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Incremental scan of one root folder. Reads only new/changed files (parallel, ATL), prunes
    /// rows for files that no longer exist under the root.
    /// </summary>
    public ScanStats ScanFolder(string root, CancellationToken ct = default, Action<int, int>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var stats = new ScanStats();
        var rootFull = System.IO.Path.GetFullPath(root);

        var existing = new Dictionary<string, (long Mtime, long Size)>(StringComparer.Ordinal);
        lock (_dbLock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, mtime, size FROM tracks WHERE root = $r";
            cmd.Parameters.AddWithValue("$r", rootFull);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) existing[rd.GetString(0)] = (rd.GetInt64(1), rd.GetInt64(2));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var toRead = new List<(string Path, long Mtime, long Size, bool IsNew)>();
        foreach (var f in System.IO.Directory.EnumerateFiles(rootFull, "*.*", System.IO.SearchOption.AllDirectories))
        {
            if (ct.IsCancellationRequested) break;
            var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            if (!AudioExt.Contains(ext) || System.IO.Path.GetFileName(f).StartsWith("._")) continue;
            long mtime, size;
            try { var fi = new System.IO.FileInfo(f); mtime = fi.LastWriteTimeUtc.Ticks; size = fi.Length; }
            catch { continue; }
            seen.Add(f);
            stats.Total++;
            if (existing.TryGetValue(f, out var e) && e.Mtime == mtime && e.Size == size) { stats.Unchanged++; continue; }
            toRead.Add((f, mtime, size, !existing.ContainsKey(f)));
        }

        var rows = new ConcurrentBag<IndexedTrack>();
        int done = 0;
        Parallel.ForEach(toRead,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            item =>
            {
                if (ct.IsCancellationRequested) return;
                try { rows.Add(ReadTrack(item.Path, item.Mtime, item.Size)); }
                catch { Interlocked.Increment(ref stats.Errors); }
                var d = Interlocked.Increment(ref done);
                progress?.Invoke(d, toRead.Count);
            });

        lock (_dbLock)
        {
            using var tx = _db.BeginTransaction();
            using (var up = _db.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = @"INSERT OR REPLACE INTO tracks
                    (path, root, mtime, size, title, artist, album_artist, album, genre,
                     year, track, disc, duration, format, lossless, bitrate, has_cover, missing_tags)
                    VALUES ($p,$r,$m,$s,$ti,$ar,$aa,$al,$g,$y,$tn,$dn,$du,$f,$lo,$br,$hc,$mt)";
                var ps = new[] { "$p","$r","$m","$s","$ti","$ar","$aa","$al","$g","$y","$tn","$dn","$du","$f","$lo","$br","$hc","$mt" }
                    .Select(n => { var p = up.CreateParameter(); p.ParameterName = n; up.Parameters.Add(p); return p; }).ToArray();
                foreach (var t in rows)
                {
                    if (ct.IsCancellationRequested) break;
                    ps[0].Value = t.Path; ps[1].Value = rootFull; ps[2].Value = t.Mtime; ps[3].Value = t.Size;
                    ps[4].Value = t.Title; ps[5].Value = t.Artist; ps[6].Value = t.AlbumArtist; ps[7].Value = t.Album;
                    ps[8].Value = t.Genre; ps[9].Value = t.Year; ps[10].Value = t.TrackNo; ps[11].Value = t.Disc;
                    ps[12].Value = t.Duration; ps[13].Value = t.Format; ps[14].Value = t.Lossless ? 1 : 0;
                    ps[15].Value = t.Bitrate; ps[16].Value = t.HasCover ? 1 : 0; ps[17].Value = t.MissingTags ? 1 : 0;
                    up.ExecuteNonQuery();
                }
            }
            if (!ct.IsCancellationRequested)
            {
                using var del = _db.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM tracks WHERE path = $p";
                var dp = del.CreateParameter(); dp.ParameterName = "$p"; del.Parameters.Add(dp);
                foreach (var gone in existing.Keys.Where(k => !seen.Contains(k)))
                {
                    dp.Value = gone;
                    del.ExecuteNonQuery();
                    stats.Removed++;
                }
            }
            tx.Commit();
        }

        stats.Added = toRead.Count(t => t.IsNew);
        stats.Updated = toRead.Count - stats.Added;
        stats.Elapsed = sw.Elapsed;
        return stats;
    }

    private static IndexedTrack ReadTrack(string path, long mtime, long size)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var row = new IndexedTrack
        {
            Path = path, Mtime = mtime, Size = size,
            Format = ext.TrimStart('.').ToUpperInvariant(),
            Lossless = LosslessExt.Contains(ext),
        };
        var t = new Track(path);
        row.Title = t.Title ?? "";
        row.Artist = t.Artist ?? "";
        row.AlbumArtist = t.AlbumArtist ?? "";
        row.Album = t.Album ?? "";
        row.Genre = t.Genre ?? "";
        row.Year = Math.Max(0, (int?)t.Year ?? 0);
        row.TrackNo = t.TrackNumber ?? 0;
        row.Disc = t.DiscNumber ?? 0;
        // Vangnet: bronnen die de disc niet taggen zetten 'm vaak in de album- of mapnaam ("CD2", "Disc 1").
        if (row.Disc == 0) row.Disc = DiscFromContext(row.Album, path);
        row.Duration = (int?)t.Duration ?? 0;
        row.Bitrate = (int?)t.Bitrate ?? 0;
        row.HasCover = t.EmbeddedPictures.Count > 0;
        var artist = !string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.AlbumArtist : row.Artist;
        row.MissingTags = string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(artist);
        return row;
    }

    private static readonly System.Text.RegularExpressions.Regex DiscRx =
        new(@"\b(?:cd|dis[ck])\s*\.?\s*(\d{1,2})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Read a disc number from the album name or the file's parent folder ("CD2", "Disc 1").
    private static int DiscFromContext(string album, string path)
    {
        foreach (var s in new[] { album, System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "") })
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var m = DiscRx.Match(s);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and < 100) return n;
        }
        return 0;
    }

    // ---------- queries (fase 1+ bouwt hierop voort) ----------

    public int TrackCount(string? root = null)
    {
        lock (_dbLock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = root == null ? "SELECT COUNT(*) FROM tracks" : "SELECT COUNT(*) FROM tracks WHERE root=$r";
            if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public List<IndexedTrack> AllTracks(string? root = null)
    {
        lock (_dbLock)
        {
            var list = new List<IndexedTrack>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path,mtime,size,title,artist,album_artist,album,genre,year,track,disc,duration,format,lossless,bitrate,has_cover,missing_tags FROM tracks"
                              + (root == null ? "" : " WHERE root=$r");
            if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new IndexedTrack
                {
                    Path = rd.GetString(0), Mtime = rd.GetInt64(1), Size = rd.GetInt64(2),
                    Title = rd.GetString(3), Artist = rd.GetString(4), AlbumArtist = rd.GetString(5),
                    Album = rd.GetString(6), Genre = rd.GetString(7), Year = rd.GetInt32(8),
                    TrackNo = rd.GetInt32(9), Disc = rd.GetInt32(10), Duration = rd.GetInt32(11),
                    Format = rd.GetString(12), Lossless = rd.GetInt32(13) == 1, Bitrate = rd.GetInt32(14),
                    HasCover = rd.GetInt32(15) == 1, MissingTags = rd.GetInt32(16) == 1,
                });
            return list;
        }
    }

    /// <summary>Distinct effective album artists (for autocomplete suggestions).</summary>
    public List<string> AllArtists(string? root = null)
    {
        lock (_dbLock)
        {
            var where = root == null ? "" : " WHERE root=$r";
            var list = new List<string>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT {EffArtist} AS aa FROM tracks{where}";
            if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var a = rd.GetString(0);
                if (a.Length > 0) list.Add(a);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }

    public (int Files, int Albums, int Lossy, int MissingTags, int AlbumsNoCover, int AllLossyAlbums) HealthCounts(string? root = null)
    {
        lock (_dbLock)
        {
            var where = root == null ? "" : " WHERE root=$r";
            int files, lossy, missing;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM(1-lossless),0), COALESCE(SUM(missing_tags),0) FROM tracks{where}";
                if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
                using var rd = cmd.ExecuteReader();
                rd.Read();
                files = rd.GetInt32(0); lossy = rd.GetInt32(1); missing = rd.GetInt32(2);
            }
            int albums, noCover, allLossy;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = $@"SELECT COUNT(*), COALESCE(SUM(nc),0), COALESCE(SUM(al),0) FROM (
                    SELECT MAX(has_cover)=0 AS nc, MIN(1-lossless)=1 AS al
                    FROM tracks{where} GROUP BY lower({EffArtist}), lower(album))";
                if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
                using var rd = cmd.ExecuteReader();
                rd.Read();
                albums = rd.GetInt32(0); noCover = rd.GetInt32(1); allLossy = rd.GetInt32(2);
            }
            return (files, albums, lossy, missing, noCover, allLossy);
        }
    }

    public List<AlbumAgg> Albums(string? root = null)
    {
        lock (_dbLock)
        {
            var where = root == null ? "" : " WHERE root=$r";
            var list = new List<AlbumAgg>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT {EffArtist} AS eff, album, COUNT(*), MAX(year),
                    COALESCE(SUM(1-lossless),0), COALESCE(SUM(missing_tags),0), MAX(has_cover)
                FROM tracks{where}
                GROUP BY lower(eff), lower(album)
                ORDER BY lower(eff), lower(album)";
            if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new AlbumAgg(rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3),
                    rd.GetInt32(4), rd.GetInt32(5), rd.GetInt32(6) == 1));
            return list;
        }
    }

    public List<string> TrackPaths(string albumArtist, string album)
    {
        lock (_dbLock)
        {
            var list = new List<string>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT path FROM tracks
                WHERE lower({EffArtist})=lower($aa) AND lower(album)=lower($al) ORDER BY disc, track, path";
            cmd.Parameters.AddWithValue("$aa", albumArtist);
            cmd.Parameters.AddWithValue("$al", album);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(rd.GetString(0));
            return list;
        }
    }
}
