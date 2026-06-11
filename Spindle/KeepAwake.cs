using System.Diagnostics;

namespace Spindle;

/// <summary>
/// Keeps the Mac awake while long work runs (like Netflix during playback):
/// spawns `caffeinate -ims -w <pid>` and kills it on Dispose. The -w guard makes
/// macOS clean it up automatically if Spindle ever exits unexpectedly.
/// No-op on other platforms.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private Process? _proc;

    public static KeepAwake Start()
    {
        var k = new KeepAwake();
        try
        {
            if (OperatingSystem.IsMacOS())
                k._proc = Process.Start(new ProcessStartInfo("/usr/bin/caffeinate",
                    $"-ims -w {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch { }
        return k;
    }

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        _proc = null;
    }
}
