namespace SeekDownloader.Gui;

/// <summary>
/// Global undo journal (fase 0/2 van PLAN.md). Tools record batches of file moves and/or tag
/// snapshots; Cmd+Z restores the most recent batch (best effort: ops whose target has since
/// moved are skipped).
/// </summary>
public sealed class UndoJournal
{
    public sealed record MoveOp(string From, string To);
    /// <summary>Snapshot of a file's tag fields BEFORE a change (all stored as strings).</summary>
    public sealed record TagOp(string Path, string Title, string Artist, string AlbumArtist,
        string Album, string Genre, string Track, string Disc, string Year);

    private readonly List<(string Label, List<MoveOp> Moves, List<TagOp> Tags)> _batches = new();
    private readonly object _lock = new();

    public void Record(string label, List<MoveOp> ops)
    {
        if (ops.Count == 0) return;
        Push((label, ops, new List<TagOp>()));
    }

    public void RecordTags(string label, List<TagOp> before)
    {
        if (before.Count == 0) return;
        Push((label, new List<MoveOp>(), before));
    }

    /// <summary>One batch with both moves and tag snapshots — Cmd+Z restores everything at once.</summary>
    public void RecordBatch(string label, List<MoveOp> moves, List<TagOp> tags)
    {
        if (moves.Count == 0 && tags.Count == 0) return;
        Push((label, moves, tags));
    }

    private void Push((string, List<MoveOp>, List<TagOp>) batch)
    {
        lock (_lock)
        {
            _batches.Add(batch);
            while (_batches.Count > 50) _batches.RemoveAt(0);
        }
    }

    public string? LastLabel { get { lock (_lock) return _batches.Count > 0 ? _batches[^1].Label : null; } }

    /// <summary>Undo the most recent batch. Returns (restored, total, label); total 0 = nothing to undo.</summary>
    public (int Done, int Total, string Label) UndoLast()
    {
        (string Label, List<MoveOp> Moves, List<TagOp> Tags) b;
        lock (_lock)
        {
            if (_batches.Count == 0) return (0, 0, "");
            b = _batches[^1];
            _batches.RemoveAt(_batches.Count - 1);
        }
        int done = 0;
        foreach (var op in Enumerable.Reverse(b.Moves))
        {
            try
            {
                var fromDir = Path.GetDirectoryName(op.From);
                if (File.Exists(op.To))
                {
                    if (!string.IsNullOrEmpty(fromDir)) Directory.CreateDirectory(fromDir);
                    if (!File.Exists(op.From)) { File.Move(op.To, op.From); done++; }
                }
                else if (Directory.Exists(op.To) && !Directory.Exists(op.From))
                {
                    if (!string.IsNullOrEmpty(fromDir)) Directory.CreateDirectory(fromDir);
                    Directory.Move(op.To, op.From); done++;
                }
            }
            catch { }
        }
        foreach (var op in b.Tags)
        {
            try
            {
                if (!File.Exists(op.Path)) continue;
                var t = new ATL.Track(op.Path);
                t.Title = op.Title;
                t.Artist = op.Artist;
                t.AlbumArtist = op.AlbumArtist;
                t.Album = op.Album;
                t.Genre = op.Genre;
                t.TrackNumber = int.TryParse(op.Track, out var tn) && tn > 0 ? tn : null;
                t.DiscNumber = int.TryParse(op.Disc, out var dn) && dn > 0 ? dn : null;
                t.Year = int.TryParse(op.Year, out var y) ? y : 0;
                t.Save();
                done++;
            }
            catch { }
        }
        return (done, b.Moves.Count + b.Tags.Count, b.Label);
    }
}
