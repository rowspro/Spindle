using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace SeekDownloader.Gui;

/// <summary>
/// Computes ReplayGain (track gain + peak) with ffmpeg's EBU R128 loudness analysis and writes it to
/// the file's tags (ATL). Reference level -18 LUFS (ReplayGain 2.0). Players that honour ReplayGain
/// then play everything at an even volume — handy on the iPod.
/// </summary>
public static class ReplayGainService
{
    private static readonly string[] Candidates = { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" };

    public static bool Available => Candidates.Any(File.Exists);
    private static string FfmpegPath => Candidates.FirstOrDefault(File.Exists) ?? "ffmpeg";

    public static bool AnalyzeAndTag(string path, CancellationToken token)
    {
        try
        {
            var i = Measure(path, token, out double truePeak);
            if (double.IsNaN(i)) return false;
            double gain = -18.0 - i;                       // dB to reach the -18 LUFS reference
            double peak = System.Math.Pow(10, truePeak / 20.0); // dBTP -> linear amplitude

            var t = new ATL.Track(path);
            t.AdditionalFields["REPLAYGAIN_TRACK_GAIN"] = gain.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " dB";
            t.AdditionalFields["REPLAYGAIN_TRACK_PEAK"] = peak.ToString("0.000000", CultureInfo.InvariantCulture);
            return t.Save();
        }
        catch { return false; }
    }

    private static double Measure(string path, CancellationToken token, out double truePeak)
    {
        truePeak = -1.0;
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-hide_banner", "-nostats", "-i", path, "-af", "loudnorm=print_format=json", "-f", "null", "-" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null) return double.NaN;

        string err;
        using (token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
        {
            err = p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        if (token.IsCancellationRequested) return double.NaN;

        var mtp = Regex.Match(err, "\"input_tp\"\\s*:\\s*\"?(-?\\d+(?:\\.\\d+)?)");
        if (mtp.Success) double.TryParse(mtp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out truePeak);

        var mi = Regex.Match(err, "\"input_i\"\\s*:\\s*\"?(-?\\d+(?:\\.\\d+)?)");
        if (!mi.Success) return double.NaN;
        return double.TryParse(mi.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var i) ? i : double.NaN;
    }
}
