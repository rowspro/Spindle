using System.Text.Json;

namespace SeekDownloader.Gui;

/// <summary>
/// Persists the last-used configuration to a JSON file in the user's app-data folder,
/// so credentials and paths survive between launches.
/// Note: this file is stored in plain text (same trust model as the CLI's environment variables).
/// </summary>
public static class Settings
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
            return Path.Combine(dir, "settings.json");
        }
    }

    public static SeekConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<SeekConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // Ignore corrupt/unreadable settings and fall back to defaults.
        }
        return new SeekConfig();
    }

    public static void SaveAcoustIdKey(string key)
    {
        var c = Load();
        c.AcoustIdKey = key;
        Save(c);
    }

    public static void SaveWatchlist(List<string> list)
    {
        var c = Load();
        c.Watchlist = list;
        Save(c);
    }

    public static void Save(SeekConfig config)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch
        {
            // Persisting settings is best-effort; never block a run on it.
        }
    }
}
