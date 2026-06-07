using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace SeekDownloader.Gui;

/// <summary>
/// Identifies (near-)untagged audio by acoustic fingerprint: Chromaprint's `fpcalc` produces a
/// fingerprint, AcoustID maps it to a MusicBrainz recording (title + artist). Needs a free AcoustID
/// API key. Returns just (artist, title); the rest is filled by the normal MusicBrainz lookup.
/// </summary>
public static class FingerprintService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static string FpcalcPath =>
        System.IO.File.Exists("/opt/homebrew/bin/fpcalc") ? "/opt/homebrew/bin/fpcalc" : "/usr/local/bin/fpcalc";

    public static bool Available => System.IO.File.Exists(FpcalcPath);

    public static async Task<(string artist, string title)?> IdentifyAsync(string path, string apiKey)
    {
        if (!Available || string.IsNullOrWhiteSpace(apiKey)) return null;
        var fpd = RunFpcalc(path);
        if (fpd == null) return null;
        var (dur, fp) = fpd.Value;

        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client"] = apiKey,
                ["duration"] = dur.ToString(),
                ["fingerprint"] = fp,
                ["meta"] = "recordings"
            });
            using var resp = await Http.PostAsync("https://api.acoustid.org/v2/lookup", form);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var res in results.EnumerateArray())
            {
                if (!res.TryGetProperty("recordings", out var recs) || recs.GetArrayLength() == 0) continue;
                var rec = recs[0];
                var title = rec.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var artist = "";
                if (rec.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var a in arts.EnumerateArray())
                        if (a.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } s)
                            names.Add(s);
                    artist = string.Join(", ", names);
                }
                if (title.Length > 0 || artist.Length > 0) return (artist, title);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Returns the AcoustID cluster id for a file (same audio → same id), or null.</summary>
    public static async Task<string?> AcoustIdOf(string path, string apiKey)
    {
        if (!Available || string.IsNullOrWhiteSpace(apiKey)) return null;
        var fpd = RunFpcalc(path);
        if (fpd == null) return null;
        var (dur, fp) = fpd.Value;
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client"] = apiKey,
                ["duration"] = dur.ToString(),
                ["fingerprint"] = fp
            });
            using var resp = await Http.PostAsync("https://api.acoustid.org/v2/lookup", form);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;
            string? bestId = null;
            double bestScore = 0;
            foreach (var r in results.EnumerateArray())
            {
                double score = r.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0;
                var id = r.TryGetProperty("id", out var idp) ? idp.GetString() : null;
                if (id != null && score > bestScore) { bestScore = score; bestId = id; }
            }
            return bestScore >= 0.85 ? bestId : null;
        }
        catch { return null; }
    }

    private static (int dur, string fp)? RunFpcalc(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FpcalcPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-json");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(outp);
            var dur = (int)Math.Round(doc.RootElement.GetProperty("duration").GetDouble());
            var fp = doc.RootElement.GetProperty("fingerprint").GetString() ?? "";
            return fp.Length == 0 ? null : (dur, fp);
        }
        catch { return null; }
    }
}
