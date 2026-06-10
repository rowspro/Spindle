using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ATL;

namespace SeekDownloader.Gui;

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
    public bool MissingTags;    // no title or no artist
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
/// first scan every view (health, browser, galaxy) reads from here instantly.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    private static readonly string[] LosslessExt = { ".flac", ".wav", ".aiff", ".aif" };

    private readonly SqliteConnection _db;
    private readonly object _writeLock = new();

    public static string DefaultDbPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SeekDownloader", "library.db");

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
    }

    public void Dispose() => _db.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Incremental scan of one root folder. Reads only new/changed files (parallel, ATL), prunes
    /// rows for files that no longer exist under the root. Thread-safe for one scan at a time.
    /// </summary>
    public ScanStats ScanFolder(string root, CancellationToken ct = default, Action<int, int>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var stats = new ScanStats();
        var rootFull = System.IO.Path.GetFullPath(root);

        // Snapshot of what the index already knows about this root.
        var existing = new Dictionary<string, (long Mtime, long Size)>(StringComparer.Ordinal);
        using (var cmd = _db.CreateCommand())
        {
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

        // Read the new/changed files in parallel (tag parsing is the slow part).
        var rows = new ConcurrentBag<IndexedTrack>();
        int done = 0;
        Parallel.ForEach(toRead,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = CancellationToken.None },
            item =>
            {
                if (ct.IsCancellationRequested) return;
                try { rows.Add(ReadTrack(item.Path, item.Mtime, item.Size)); }
                catch { Interlocked.Increment(ref stats.Errors); }
                var d = Interlocked.Increment(ref done);
                progress?.Invoke(d, toRead.Count);
            });

        // Single write transaction: upserts + prune of deleted files.
        lock (_writeLock)
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
        row.Duration = (int?)t.Duration ?? 0;
        row.Bitrate = (int?)t.Bitrate ?? 0;
        row.HasCover = t.EmbeddedPictures.Count > 0;
        var artist = !string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.AlbumArtist : row.Artist;
        row.MissingTags = string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(artist);
        return row;
    }

    // ---------- queries (fase 1+ bouwt hierop voort) ----------

    public int TrackCount(string? root = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = root == null ? "SELECT COUNT(*) FROM tracks" : "SELECT COUNT(*) FROM tracks WHERE root=$r";
        if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public (int Files, int Albums, int Lossy, int MissingTags, int AlbumsNoCover, int AllLossyAlbums) HealthCounts(string? root = null)
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
                FROM tracks{where} GROUP BY lower(CASE WHEN album_artist <> '' THEN album_artist ELSE artist END), lower(album))";
            if (root != null) cmd.Parameters.AddWithValue("$r", System.IO.Path.GetFullPath(root));
            using var rd = cmd.ExecuteReader();
            rd.Read();
            albums = rd.GetInt32(0); noCover = rd.GetInt32(1); allLossy = rd.GetInt32(2);
        }
        return (files, albums, lossy, missing, noCover, allLossy);
    }

    public List<AlbumAgg> Albums(string? root = null)
    {
        var where = root == null ? "" : " WHERE root=$r";
        var list = new List<AlbumAgg>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $@"SELECT CASE WHEN album_artist <> '' THEN album_artist ELSE artist END AS eff, album, COUNT(*), MAX(year),
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

    public List<string> TrackPaths(string albumArtist, string album)
    {
        var list = new List<string>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"SELECT path FROM tracks
            WHERE lower(CASE WHEN album_artist <> '' THEN album_artist ELSE artist END)=lower($aa) AND lower(album)=lower($al) ORDER BY disc, track, path";
        cmd.Parameters.AddWithValue("$aa", albumArtist);
        cmd.Parameters.AddWithValue("$al", album);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }
}
