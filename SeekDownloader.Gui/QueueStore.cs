using System.Text.Json;

namespace SeekDownloader.Gui;

/// <summary>One persisted, resumable queue entry.</summary>
public class QueueEntry
{
    public string Username { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>
/// Persists the pending download queue so unfinished downloads survive a restart and can be resumed
/// (instead of having to search again). Stored next to settings.json in the app-data folder.
/// </summary>
public static class QueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SeekDownloader");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "queue.json");
        }
    }

    public static List<QueueEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<QueueEntry>>(json);
                if (list != null) return list;
            }
        }
        catch { }
        return new List<QueueEntry>();
    }

    public static void Save(IEnumerable<QueueEntry> entries)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries.ToList(), JsonOptions));
        }
        catch { }
    }
}
