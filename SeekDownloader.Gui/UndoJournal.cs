namespace SeekDownloader.Gui;

/// <summary>
/// Global undo journal for file operations (fase 0 van PLAN.md). Tools record batches of moves;
/// Cmd+Z restores the most recent batch (best effort: ops whose target has since moved are skipped).
/// Tag-undo volgt in fase 2.
/// </summary>
public sealed class UndoJournal
{
    public sealed record MoveOp(string From, string To);

    private readonly List<(string Label, List<MoveOp> Ops)> _batches = new();
    private readonly object _lock = new();

    public void Record(string label, List<MoveOp> ops)
    {
        if (ops.Count == 0) return;
        lock (_lock)
        {
            _batches.Add((label, ops));
            while (_batches.Count > 50) _batches.RemoveAt(0);
        }
    }

    public string? LastLabel { get { lock (_lock) return _batches.Count > 0 ? _batches[^1].Label : null; } }

    /// <summary>Undo the most recent batch. Returns (restored, total, label); total 0 = nothing to undo.</summary>
    public (int Done, int Total, string Label) UndoLast()
    {
        (string Label, List<MoveOp> Ops) b;
        lock (_lock)
        {
            if (_batches.Count == 0) return (0, 0, "");
            b = _batches[^1];
            _batches.RemoveAt(_batches.Count - 1);
        }
        int done = 0;
        foreach (var op in Enumerable.Reverse(b.Ops))
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
        return (done, b.Ops.Count, b.Label);
    }
}
