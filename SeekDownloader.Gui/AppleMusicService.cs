using System.Diagnostics;

namespace SeekDownloader.Gui;

public class ArtistStat
{
    public string Name = string.Empty;
    public int Plays;
}

public class PlaylistStat
{
    public string Name = string.Empty;
    public int TrackCount;
}

/// <summary>
/// Reads the local Music.app library via AppleScript (osascript): most-played artists,
/// user playlists, and a playlist's tracks. No API token needed; the app asks for
/// Automation permission to control Music the first time.
/// </summary>
public static class AppleMusicService
{
    public static List<ArtistStat> GetTopArtists(int topN)
    {
        const string script = @"
with timeout of 180 seconds
  tell application ""Music""
    set arts to artist of every track of library playlist 1
    set plays to played count of every track of library playlist 1
  end tell
end timeout
set out to """"
repeat with i from 1 to count of arts
  set out to out & (item i of plays) & tab & (item i of arts) & linefeed
end repeat
return out";
        var output = RunOsa(script);
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (!int.TryParse(line.Substring(0, tab).Trim(), out var plays)) continue;
            var artist = line[(tab + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(artist)) continue;
            totals[artist] = totals.TryGetValue(artist, out var cur) ? cur + plays : plays;
        }
        return totals
            .Select(kv => new ArtistStat { Name = kv.Key, Plays = kv.Value })
            .Where(a => a.Plays > 0)
            .OrderByDescending(a => a.Plays)
            .Take(topN)
            .ToList();
    }

    public static List<PlaylistStat> GetPlaylists()
    {
        const string script = @"
tell application ""Music""
  set out to """"
  repeat with p in user playlists
    try
      set out to out & (name of p) & tab & (count of tracks of p) & linefeed
    end try
  end repeat
end tell
return out";
        var output = RunOsa(script);
        var list = new List<PlaylistStat>();
        foreach (var line in output.Split('\n'))
        {
            var tab = line.LastIndexOf('\t');
            if (tab <= 0) continue;
            var name = line.Substring(0, tab).Trim();
            int.TryParse(line[(tab + 1)..].Trim(), out var count);
            if (!string.IsNullOrWhiteSpace(name) && count > 0)
                list.Add(new PlaylistStat { Name = name, TrackCount = count });
        }
        return list.OrderByDescending(p => p.TrackCount).ToList();
    }

    public static List<string> GetPlaylistTracks(string playlistName)
    {
        var safe = playlistName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script = $@"
tell application ""Music""
  set pl to first user playlist whose name is ""{safe}""
  set arts to artist of every track of pl
  set tits to title of every track of pl
end tell
set out to """"
repeat with i from 1 to count of tits
  set out to out & (item i of arts) & "" - "" & (item i of tits) & linefeed
end repeat
return out";
        var output = RunOsa(script);
        return output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "-")
            .ToList();
    }

    private static string RunOsa(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("osascript kon niet starten");
        p.StandardInput.Write(script);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "AppleScript-fout" : err.Trim());
        return output;
    }
}
