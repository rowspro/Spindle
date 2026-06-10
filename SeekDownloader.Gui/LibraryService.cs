using Avalonia.Threading;

namespace SeekDownloader.Gui;

/// <summary>
/// App-wide library service (fase 0 van PLAN.md): owns the persistent LibraryIndex, keeps it fresh
/// with FileSystemWatchers on the library + Nieuw folders (debounced incremental rescans) and
/// broadcasts progress/changes so screens read instantly from the index.
/// </summary>
public sealed class LibraryService : IDisposable
{
    public LibraryIndex Index { get; } = new();

    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _dirtyLock = new();
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _w1, _w2;
    private string _library = "", _nieuw = "";

    /// <summary>root, done, total — raised on the UI thread while a scan reads files.</summary>
    public event Action<string, int, int>? ScanProgress;
    /// <summary>Raised on the UI thread after the index changed.</summary>
    public event Action? Changed;

    public LibraryService()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            string[] roots;
            lock (_dirtyLock) { roots = _dirty.ToArray(); _dirty.Clear(); }
            foreach (var r in roots) { var root = r; Task.Run(() => Refresh(root)); }
        };
    }

    /// <summary>(Re)point the watchers at the configured folders and kick off initial incremental scans.</summary>
    public void Configure(string libraryFolder, string nieuwFolder)
    {
        _library = libraryFolder ?? "";
        _nieuw = nieuwFolder ?? "";
        _w1?.Dispose(); _w1 = null;
        _w2?.Dispose(); _w2 = null;
        _w1 = TryWatch(libraryFolder);
        _w2 = TryWatch(nieuwFolder);
        foreach (var r in new[] { libraryFolder, nieuwFolder })
            if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            { var root = r; Task.Run(() => Refresh(root)); }
    }

    /// <summary>Refresh both configured roots in the background (used after tag/file writes).</summary>
    public void RefreshConfigured()
    {
        foreach (var r in new[] { _library, _nieuw })
        { var root = r; Task.Run(() => Refresh(root)); }
    }

    private FileSystemWatcher? TryWatch(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += (_, _) => MarkDirty(root);
            w.Deleted += (_, _) => MarkDirty(root);
            w.Renamed += (_, _) => MarkDirty(root);
            w.Changed += (_, _) => MarkDirty(root);
            w.Error += (_, _) => { try { w.EnableRaisingEvents = false; } catch { } }; // volume weg → watcher uit
            w.EnableRaisingEvents = true;
            return w;
        }
        catch { return null; }
    }

    private void MarkDirty(string root)
    {
        lock (_dirtyLock) _dirty.Add(root);
        Dispatcher.UIThread.Post(() => { _debounce.Stop(); _debounce.Start(); });
    }

    /// <summary>Incremental refresh of one root (serialized; safe from any thread). Returns null if the root is missing.</summary>
    public ScanStats? Refresh(string root, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        _scanGate.Wait();
        try
        {
            var stats = Index.ScanFolder(root, ct, (d, t) =>
            {
                if (d % 25 == 0 || d == t)
                    Dispatcher.UIThread.Post(() => ScanProgress?.Invoke(root, d, t));
            });
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
            return stats;
        }
        finally { _scanGate.Release(); }
    }

    public void Dispose()
    {
        _w1?.Dispose();
        _w2?.Dispose();
        Index.Dispose();
    }
}
