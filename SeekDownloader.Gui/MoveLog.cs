using System.Text.Json;

namespace SeekDownloader.Gui;

/// <summary>
/// Records the last batch of file moves (sort/organize) so it can be undone — even after a restart.
/// Stored next to settings.json.
/// </summary>
public static class MoveLog
{
    public class Move { public string From { get; set; } = ""; public string To { get; set; } = ""; }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeekDownloader");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "move-undo.json");
        }
    }

    private static List<Move> _current = new();

    public static void StartBatch()
    {
        lock (Gate) { _current = new List<Move>(); Persist(); }
    }

    // from = where the file now lives (target), to = where it came from (so undo puts it back).
    public static void Record(string from, string to)
    {
        lock (Gate) { _current.Add(new Move { From = from, To = to }); Persist(); }
    }

    public static int PendingCount()
    {
        lock (Gate) { return Load().Count; }
    }

    /// <summary>Reverts the last recorded batch. Returns how many files were moved back.</summary>
    public static int UndoLast()
    {
        lock (Gate)
        {
            var moves = Load();
            int n = 0;
            foreach (var m in moves)
            {
                try
                {
                    if (File.Exists(m.From))
                    {
                        var dir = Path.GetDirectoryName(m.To);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (!File.Exists(m.To)) { File.Move(m.From, m.To); n++; }
                    }
                }
                catch { }
            }
            _current = new List<Move>();
            Persist();
            return n;
        }
    }

    private static List<Move> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<Move>>(File.ReadAllText(FilePath)) ?? new List<Move>();
        }
        catch { }
        return new List<Move>();
    }

    private static void Persist()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_current, JsonOptions)); }
        catch { }
    }
}
