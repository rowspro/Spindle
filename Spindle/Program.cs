using Avalonia;

namespace Spindle;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        MigrateAppSupport();

        // Crashes van een GUI-app via Launch Services laten geen managed stack achter —
        // schrijf die zelf weg zodat een crash naderhand te herleiden is.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    /// <summary>One-time migration: the App Support folder used to be called "SeekDownloader".</summary>
    private static void MigrateAppSupport()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = System.IO.Path.Combine(appData, "SeekDownloader");
            var newDir = System.IO.Path.Combine(appData, "Spindle");
            if (System.IO.Directory.Exists(oldDir) && !System.IO.Directory.Exists(newDir))
                System.IO.Directory.Move(oldDir, newDir);
        }
        catch { }
    }

    private static void LogCrash(object? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spindle");
            System.IO.Directory.CreateDirectory(dir);
            var logPath = System.IO.Path.Combine(dir, "crash.log");
            try { var fi = new System.IO.FileInfo(logPath); if (fi.Exists && fi.Length > 1_000_000) fi.Delete(); } catch { }
            System.IO.File.AppendAllText(logPath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
