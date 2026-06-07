using System.Text.RegularExpressions;
using FuzzySharp;
using SeekDownloader;
using SeekDownloader.Gui.ViewModels;

namespace SeekDownloader.Gui;

/// <summary>
/// Turns raw Soulseek results into candidate albums for artist mode.
/// Preferred path: match Soulseek folders against an official album list from MusicBrainz.
/// Fallback (MusicBrainz unavailable): infer albums from folder names only.
/// </summary>
public static class AlbumGrouper
{
    // A single album rarely exceeds this; bigger folders are almost always discography dumps.
    public const int MaxAlbumTracks = 30;
    private const int MatchThreshold = 70;

    // Best matching album folder for ONE specific album — used by artist mode's per-album search.
    public static (List<SearchResult> Tracks, string User)? BestFolderForAlbum(
        IEnumerable<SearchResult> results, string artist, string album, Func<string, string> trackNameOf)
    {
        var albumNorm = NormalizeKey(album);
        var best = BuildFolders(results, artist, trackNameOf)
            .Where(f => f.Tracks.Count >= 2 && f.Tracks.Count <= MaxAlbumTracks && f.NormName.Length > 0)
            .Select(f => (f, score: string.IsNullOrEmpty(albumNorm) ? 100 : Fuzz.TokenSetRatio(albumNorm, f.NormName)))
            .Where(x => x.score >= MatchThreshold)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.f.Lossless)
            .ThenByDescending(x => x.f.Tracks.Count)
            .Select(x => (x.f.Tracks, x.f.User))
            .FirstOrDefault();
        return best.Tracks == null ? null : best;
    }

    private sealed class FolderCand
    {
        public List<SearchResult> Tracks = new();
        public string Display = string.Empty;
        public string NormName = string.Empty; // normalized album title for matching ("" if none)
        public string Key = string.Empty;      // dedup key (falls back to user+folder)
        public string User = string.Empty;
        public int Lossless;
    }

    // ---- Preferred: match Soulseek folders to MusicBrainz official albums ----
    public static List<AlbumGroupViewModel> MatchToOfficial(
        List<MbAlbum> official, IEnumerable<SearchResult> results, string artist, Func<string, string> trackNameOf)
    {
        var folders = BuildFolders(results, artist, trackNameOf)
            .Where(f => f.Tracks.Count >= 2 && f.Tracks.Count <= MaxAlbumTracks && f.NormName.Length > 0)
            .ToList();

        var albums = new List<AlbumGroupViewModel>();
        foreach (var mb in official)
        {
            var mbNorm = NormalizeKey(mb.Title);
            var matches = folders
                .Select(f => (f, score: Fuzz.TokenSetRatio(mbNorm, f.NormName)))
                .Where(x => x.score >= MatchThreshold)
                .ToList();

            if (matches.Count > 0)
            {
                var best = matches
                    .OrderByDescending(x => x.score)
                    .ThenByDescending(x => x.f.Lossless)
                    .ThenBy(x => mb.ExpectedTracks > 0 ? Math.Abs(x.f.Tracks.Count - mb.ExpectedTracks) : 0)
                    .ThenByDescending(x => x.f.Tracks.Count)
                    .First().f;
                albums.Add(new AlbumGroupViewModel(mb.Title, mb.Year, true, best.Tracks.Count, mb.ExpectedTracks, best.User, best.Tracks)
                {
                    IsSelected = true
                });
            }
            else
            {
                albums.Add(new AlbumGroupViewModel(mb.Title, mb.Year, false, 0, mb.ExpectedTracks, string.Empty, new List<SearchResult>()));
            }
        }
        return albums;
    }

    // ---- Fallback: infer albums purely from folder names ----
    public static List<AlbumGroupViewModel> Group(IEnumerable<SearchResult> results, string artist, Func<string, string> trackNameOf, out int skippedLarge)
    {
        var folders = BuildFolders(results, artist, trackNameOf)
            .Where(f => f.Tracks.Count >= 2)
            .ToList();

        skippedLarge = folders.Count(f => f.Tracks.Count > MaxAlbumTracks);

        return folders
            .Where(f => f.Tracks.Count <= MaxAlbumTracks) // hide discography dumps
            .GroupBy(f => f.Key)
            .Select(grp =>
            {
                var best = grp.OrderByDescending(f => f.Lossless).ThenByDescending(f => f.Tracks.Count).First();
                return new AlbumGroupViewModel(best.Display, string.Empty, true, best.Tracks.Count, 0, best.User, best.Tracks);
            })
            .OrderByDescending(a => a.TrackCount)
            .ToList();
    }

    private static List<FolderCand> BuildFolders(IEnumerable<SearchResult> results, string artist, Func<string, string> trackNameOf)
    {
        return results
            .GroupBy(r => r.Username + " " + SeekRunner.GetFolder(r.Filename))
            .Select(g =>
            {
                var tracks = DedupeByTrack(g, trackNameOf);
                var folderName = SeekRunner.LastFolderName(SeekRunner.GetFolder(g.First().Filename));
                var display = CleanAlbum(folderName, artist);
                var norm = NormalizeKey(display);
                return new FolderCand
                {
                    Tracks = tracks,
                    Display = string.IsNullOrWhiteSpace(display) ? "(losse tracks)" : display,
                    NormName = norm,
                    Key = norm.Length > 0 ? norm : g.Key,
                    User = g.First().Username,
                    Lossless = tracks.Count(t => SeekRunner.FormatRank(t.Filename) >= 4)
                };
            })
            .ToList();
    }

    private static List<SearchResult> DedupeByTrack(IEnumerable<SearchResult> files, Func<string, string> trackNameOf)
    {
        return files
            .GroupBy(f =>
            {
                var t = trackNameOf(f.Filename);
                return string.IsNullOrWhiteSpace(t)
                    ? (f.FileNameWithExt ?? f.Filename).ToLower()
                    : t.ToLower();
            })
            .Select(g => g
                .OrderByDescending(f => SeekRunner.FormatRank(f.Filename))
                .ThenByDescending(f => f.Size)
                .First())
            .ToList();
    }

    private static string CleanAlbum(string folder, string artist)
    {
        var s = folder;
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");
        if (!string.IsNullOrWhiteSpace(artist))
            s = Regex.Replace(s, "^\\s*" + Regex.Escape(artist) + "\\s*[-–_]+\\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim().Trim('-', '_', '–', ' ');
        return s.Length > 0 ? s : folder;
    }

    private static string NormalizeKey(string name)
    {
        var s = name.ToLower();
        s = Regex.Replace(s, @"\(.*?\)|\[.*?\]", " ");
        s = Regex.Replace(s, @"\b(19|20)\d{2}\b", " ");
        s = Regex.Replace(s, @"\b(flac|mp3|wav|opus|m4a|aiff|web|cd|vinyl|320|kbps|lossless|deluxe|edition|explicit|remastered)\b", " ");
        s = Regex.Replace(s, @"[^a-z0-9]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }
}
