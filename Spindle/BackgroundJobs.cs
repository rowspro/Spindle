using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Spindle;

/// <summary>
/// Runs lyric fetches in the background (parallel, multi-core) so the user can keep editing the next
/// album. Tracks the work per source folder so a move-to-library (Inbox approve) can wait until that
/// folder's lyrics are fully written — otherwise the .lrc/embedded write would race with the move.
/// </summary>
public static class BackgroundJobs
{
    static readonly object _gate = new();
    static readonly Dictionary<string, List<Task>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    static int _total, _done, _running;

    /// <summary>Raised (on a background thread) whenever progress or the running state changes.</summary>
    public static event Action? Changed;

    private static int _saving;

    public static bool Busy { get { lock (_gate) return _running > 0 || _saving > 0; } }

    public static string Status
    {
        get
        {
            lock (_gate)
                return _running > 0 ? $"Fetching lyrics… {_done}/{_total}"
                     : _saving > 0 ? "Saving changes…"
                     : string.Empty;
        }
    }

    /// <summary>Run background work (e.g. propagating album-level tag edits to the rest of the album) tracked
    /// per folder, so the editor stays free and a later move-to-library waits for it. Non-blocking.</summary>
    public static void RunTracked(IReadOnlyList<string> folders, Action work)
    {
        var fs = (folders ?? new List<string>()).Select(Norm).Where(s => s.Length > 0).Distinct().ToList();
        lock (_gate) _saving++;
        Raise();
        var task = Task.Run(work);
        lock (_gate)
            foreach (var fo in fs)
            {
                if (!_byFolder.TryGetValue(fo, out var l)) _byFolder[fo] = l = new List<Task>();
                l.Add(task);
            }
        _ = task.ContinueWith(_ =>
        {
            lock (_gate)
            {
                foreach (var fo in fs)
                    if (_byFolder.TryGetValue(fo, out var l)) { l.Remove(task); if (l.Count == 0) _byFolder.Remove(fo); }
                _saving--;
            }
            Raise();
        });
    }

    /// <summary>Fetch + write lyrics for these files in the background, across multiple cores.</summary>
    public static void RunLyrics(IReadOnlyList<string> files)
    {
        var list = files?.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();
        if (list.Count == 0) return;
        var folders = list.Select(f => Norm(Path.GetDirectoryName(f) ?? "")).Where(s => s.Length > 0).Distinct().ToList();

        lock (_gate) { _total += list.Count; _running++; }
        Raise();

        var task = Task.Run(async () =>
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 8) };
            await Parallel.ForEachAsync(list, po, async (f, ct) =>
            {
                try { await Lyrics.ApplyToFileAsync(f, ct); } catch { }
                lock (_gate) _done++;
                Raise();
            });
        });

        lock (_gate)
            foreach (var fo in folders)
            {
                if (!_byFolder.TryGetValue(fo, out var l)) _byFolder[fo] = l = new List<Task>();
                l.Add(task);
            }

        _ = task.ContinueWith(_ =>
        {
            lock (_gate)
            {
                foreach (var fo in folders)
                    if (_byFolder.TryGetValue(fo, out var l)) { l.Remove(task); if (l.Count == 0) _byFolder.Remove(fo); }
                _running--;
                if (_running == 0) { _total = 0; _done = 0; }
            }
            Raise();
        });
    }

    /// <summary>Await any in-flight lyric work for these folders before the files are moved/deleted.</summary>
    public static async Task WaitForFolders(IEnumerable<string> folders)
    {
        var wait = new List<Task>();
        lock (_gate)
            foreach (var f in folders)
                if (f != null && _byFolder.TryGetValue(Norm(f), out var l)) wait.AddRange(l);
        if (wait.Count > 0) { try { await Task.WhenAll(wait); } catch { } }
    }

    static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return p; }
    }

    static void Raise() => Changed?.Invoke();
}
