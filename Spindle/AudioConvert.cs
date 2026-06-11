using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Spindle;

/// <summary>Shared audio conversion/tag-copy helpers (afconvert), used by the ALAC and iPod-sync tools.</summary>
public static class AudioConvert
{
    // afconvert to outPath. iPod mode = two-step downconvert to 16-bit/44.1 kHz (5.5G can't play hi-res).
    // On failure, `error` holds the afconvert message (stderr) so callers can show why it failed.
    public static bool Encode(string src, string outPath, bool ipodCompatible, CancellationToken token, out string? error)
    {
        if (!ipodCompatible)
            return Afc(src, outPath, token, out error, "-d", "alac", "-f", "m4af");

        var tmpWav = Path.Combine(Path.GetTempPath(), "spindle_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            if (!Afc(src, tmpWav, token, out error, "-d", "LEI16@44100", "-r", "127", "-f", "WAVE")) return false;
            return Afc(tmpWav, outPath, token, out error, "-d", "alac", "-f", "m4af");
        }
        finally
        {
            try { File.Delete(tmpWav); } catch { }
        }
    }

    private static bool Afc(string input, string output, CancellationToken token, out string? error, params string[] args)
    {
        error = null;
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/afconvert",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add(output);

        using var p = Process.Start(psi);
        if (p == null) { error = "afconvert kon niet starten"; return false; }

        string stderr;
        using (token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
        {
            stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        if (token.IsCancellationRequested) { error = "onderbroken"; return false; }
        if (p.ExitCode != 0)
        {
            error = string.IsNullOrWhiteSpace(stderr) ? $"afconvert-foutcode {p.ExitCode}" : stderr.Trim();
            return false;
        }
        return true;
    }

    // artistFromAlbumArtist: ALAC-uitvoer is voor de iPod — daar wordt Artist gelijkgezet aan
    // AlbumArtist zodat de artiestenlijst niet vol "feat."-varianten komt. De bron blijft intact.
    public static void CopyTags(string src, string dst, bool artistFromAlbumArtist = false)
    {
        try
        {
            var s = new ATL.Track(src);
            var artist = artistFromAlbumArtist && !string.IsNullOrWhiteSpace(s.AlbumArtist) ? s.AlbumArtist : s.Artist;
            var d = new ATL.Track(dst)
            {
                Title = s.Title,
                Artist = artist,
                AlbumArtist = s.AlbumArtist,
                Album = s.Album,
                Composer = s.Composer,
                Genre = s.Genre,
                TrackNumber = s.TrackNumber,
                DiscNumber = s.DiscNumber,
                Year = s.Year
            };
            foreach (var pic in s.EmbeddedPictures)
                d.EmbeddedPictures.Add(pic);
            d.Save();
        }
        catch { }
    }
}
