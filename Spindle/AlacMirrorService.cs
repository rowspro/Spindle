namespace Spindle;

/// <summary>
/// iTunes mode: keeps an ALAC mirror of the music library in sync, in the background.
/// Lossless (flac/wav/aiff) is converted to iPod-compatible ALAC (Artist = Album artist),
/// lossy (mp3/m4a/…) is copied as-is; orphans in the mirror are removed. Passes are
/// serialized and incremental (mtime check), so repeated kicks are cheap.
/// </summary>
public sealed class AlacMirrorService
{
    private static readonly string[] ConvertExt = { ".flac", ".wav", ".aiff", ".aif" };
    private static readonly string[] CopyExt = { ".mp3", ".m4a", ".aac", ".ogg", ".opus" };

    private readonly object _lock = new();
    private bool _running, _again;
    private CancellationTokenSource? _cts;

    public event Action<string>? Status;

    public void Stop() { lock (_lock) { _cts?.Cancel(); _again = false; } }

    /// <summary>Start (or queue) a sync pass. Safe to call often.</summary>
    public void Kick(string source, string mirror)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(mirror) || !Directory.Exists(source)) return;
        CancellationToken token;
        lock (_lock)
        {
            if (_running) { _again = true; return; }
            _running = true;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        Task.Run(() =>
        {
            try { RunPass(source, mirror, token); }
            catch (Exception e) { Status?.Invoke("ALAC mirror error: " + e.Message); }
            bool again;
            lock (_lock) { _running = false; again = _again; _again = false; }
            if (again && !token.IsCancellationRequested) Kick(source, mirror);
        });
    }

    private void RunPass(string source, string mirror, CancellationToken token)
    {
        var srcFull = Path.GetFullPath(source);
        var mirFull = Path.GetFullPath(mirror);
        if ((mirFull + Path.DirectorySeparatorChar).StartsWith(srcFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { Status?.Invoke("ALAC mirror must live outside the music library."); return; }
        // Vangrail: als het doel-volume niet gemount is, zou CreateDirectory een map op de
        // boot-schijf maken (/Volumes/...). Alleen verder als de oudermap echt bestaat.
        var mirParent = Path.GetDirectoryName(mirFull.TrimEnd(Path.DirectorySeparatorChar));
        if (mirParent == null || !Directory.Exists(mirParent))
        { Status?.Invoke("ALAC mirror skipped — target volume/folder isn't available."); return; }
        Directory.CreateDirectory(mirFull);

        // 1) werk verzamelen (incrementeel op mtime)
        var jobs = new List<(string Src, string Dst, bool Convert)>();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories))
        {
            if (token.IsCancellationRequested) return;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            bool conv = ConvertExt.Contains(ext);
            if (!conv && !CopyExt.Contains(ext)) continue;
            var rel = Path.GetRelativePath(srcFull, f);
            var dst = Path.Combine(mirFull, conv ? Path.ChangeExtension(rel, ".m4a") : rel);
            expected.Add(dst);
            try { if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(f)) continue; }
            catch { }
            jobs.Add((f, dst, conv));
        }

        // 2) wezen opruimen — vangrail: een lege/halfgemounte bron mag nooit de spiegel leegtrekken
        int removed = 0;
        if (expected.Count == 0)
        {
            Status?.Invoke("ALAC mirror skipped — source library looks empty (volume not mounted?).");
            return;
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(mirFull, "*", SearchOption.AllDirectories).ToList())
            {
                if (token.IsCancellationRequested) return;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".part") { try { File.Delete(f); } catch { } continue; }
                if (!CopyExt.Contains(ext) && !ConvertExt.Contains(ext)) continue;
                if (!expected.Contains(f)) { try { File.Delete(f); removed++; } catch { } }
            }
            foreach (var d in Directory.EnumerateDirectories(mirFull, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length).ToList())
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        catch { }

        if (jobs.Count == 0)
        {
            Status?.Invoke(removed > 0 ? $"ALAC mirror up to date ({removed} orphan(s) removed)." : "ALAC mirror up to date.");
            return;
        }

        using var awake = KeepAwake.Start();   // eerste volledige sync kan lang duren
        int done = 0, converted = 0, copied = 0, failed = 0;
        Status?.Invoke($"ALAC mirror: 0/{jobs.Count}…");
        var po = new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token };
        try
        {
            Parallel.ForEach(jobs, po, job =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.Dst)!);
                    var part = job.Dst + ".part";
                    if (job.Convert)
                    {
                        if (AudioConvert.Encode(job.Src, part, true, token, out _) && File.Exists(part))
                        {
                            AudioConvert.CopyTags(job.Src, part, artistFromAlbumArtist: true);
                            if (File.Exists(job.Dst)) File.Delete(job.Dst);
                            File.Move(part, job.Dst);
                            Interlocked.Increment(ref converted);
                        }
                        else { try { File.Delete(part); } catch { } Interlocked.Increment(ref failed); }
                    }
                    else
                    {
                        File.Copy(job.Src, part, true);
                        if (File.Exists(job.Dst)) File.Delete(job.Dst);
                        File.Move(part, job.Dst);
                        Interlocked.Increment(ref copied);
                    }
                }
                catch { Interlocked.Increment(ref failed); }
                int n = Interlocked.Increment(ref done);
                if (n % 5 == 0 || n == jobs.Count) Status?.Invoke($"ALAC mirror: {n}/{jobs.Count}…");
            });
        }
        catch (OperationCanceledException) { return; }
        Status?.Invoke($"ALAC mirror synced — {converted} converted, {copied} copied"
                       + (removed > 0 ? $", {removed} removed" : "") + (failed > 0 ? $", {failed} failed" : "") + ".");
    }
}
