using FuzzySharp;
using SeekDownloader.Enums;
using SeekDownloader.Models;
using SeekDownloader.Services;
using File = System.IO.File;

namespace SeekDownloader.Gui;

/// <summary>
/// Runs a download session by reusing the existing DownloadService/FileSeekService core.
/// This mirrors the orchestration in SeekDownloader.Commands.RootCommand.ExecuteAsync,
/// but reports progress through the services' public state (polled by the GUI), honours
/// cancellation, and adds an "album mode" that grabs one complete album folder without duplicates.
/// </summary>
public class SeekRunner
{
    public DownloadService Download { get; }
    public FileSeekService Seeker { get; } = new();

    // Share one DownloadService (= one Soulseek client) across the whole app so auto and manual mode
    // never spin up competing clients on the same listener port.
    public SeekRunner(DownloadService? download = null) => Download = download ?? new DownloadService();

    // Higher = preferred. Used to favour lossless formats (e.g. flac over mp3).
    public static int FormatRank(string filename)
    {
        var ext = filename.Contains('.') ? filename[(filename.LastIndexOf('.') + 1)..].ToLower() : string.Empty;
        return ext switch
        {
            "flac" => 6,
            "wav" => 5,
            "aiff" or "aif" or "aaif" => 4,
            "m4a" => 3,
            "opus" => 2,
            "mp3" => 1,
            _ => 0
        };
    }

    // Friendly explanation when the Soulseek login didn't succeed (includes the server's reason if any).
    public static string ConnectErrorMessage(DownloadService d)
    {
        var reason = string.IsNullOrWhiteSpace(d.LastConnectError) ? "" : $" Servermelding: {d.LastConnectError}";
        return "Kon niet inloggen op Soulseek. Controleer je gebruikersnaam en wachtwoord. "
             + "Let op: het account moet bestaan (registreer eenmalig in de officiële Soulseek-app of Nicotine+) "
             + "en mag niet tegelijk ergens anders ingelogd zijn." + reason;
    }

    public async Task RunAsync(SeekConfig cfg, CancellationToken ct)
    {
        Download.SoulSeekUsername = cfg.SoulseekUsername;
        Download.SoulSeekPassword = cfg.SoulseekPassword;
        Download.ThreadCount = cfg.ThreadCount;
        Download.NicotineListenPort = cfg.SoulseekListenPort;
        Download.DownloadFolderNicotine = cfg.DownloadFilePath;
        // Album mode downloads a whole folder, so the "stop after one song" behaviour must be off.
        Download.DownloadSingles = cfg.DownloadSingles && !cfg.AlbumMode;
        Download.UpdateAlbumName = cfg.UpdateAlbumName;
        Download.CheckTags = cfg.CheckTags;
        Download.CheckTagsDelete = cfg.CheckTagsDelete;
        Download.AllowNonTaggedFiles = cfg.AllowNonTaggedFiles;
        Download.InMemoryDownloads = cfg.InMemoryDownloads;
        Download.InMemoryDownloadMaxSize = cfg.InMemoryDownloadMaxSize;
        Download.IncludeUsernameInPath = cfg.SaveInUploaderSubfolder;
        Download.DownloadArchiveFilePath = cfg.DownloadArchiveFilePath!;

        // Never start the console ProgressThread from the GUI: it calls Console.SetCursorPosition,
        // which throws when there is no console buffer.
        Download.OutputStatus = false;

        if (!string.IsNullOrWhiteSpace(Download.DownloadArchiveFilePath) &&
            File.Exists(Download.DownloadArchiveFilePath))
        {
            foreach (var path in await File.ReadAllLinesAsync(Download.DownloadArchiveFilePath, ct))
            {
                Download.DownloadArchiveList.Add(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.MusicLibrary))
        {
            Seeker.MusicLibraries.Add(cfg.MusicLibrary);
        }

        if (!string.IsNullOrWhiteSpace(cfg.SearchTerm))
        {
            Download.MissingNames.Add(cfg.SearchTerm);
        }
        if (!string.IsNullOrWhiteSpace(cfg.SearchFilePath))
        {
            foreach (var path in (await File.ReadAllLinesAsync(cfg.SearchFilePath, ct))
                     .Where(line => !string.IsNullOrWhiteSpace(line))
                     .Distinct()
                     .Reverse())
            {
                Download.MissingNames.Add(path);
            }
        }

        await Download.ConnectAsync();
        if (!Download.IsLoggedIn)
            throw new Exception(ConnectErrorMessage(Download));

        if (cfg.AlbumMode)
            await RunAlbumModeAsync(cfg, ct);
        else
            await RunSearchModeAsync(cfg, ct);

        while (Download.InQueueCount > 0 || Download.AnyThreadDownloading())
        {
            if (ct.IsCancellationRequested) break;
            await Task.Delay(100, CancellationToken.None);
        }

        Download.StopThreads();
    }

    // ----- Album mode: one complete album folder per search line, deduped, lossless preferred -----
    private async Task RunAlbumModeAsync(SeekConfig cfg, CancellationToken ct)
    {
        foreach (var raw in Download.MissingNames.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            while (Download.InQueueCount > 100)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(1000, CancellationToken.None);
            }
            ct.ThrowIfCancellationRequested();

            var (artist, album) = SplitArtistAlbum(raw, cfg.SearchDelimeter);
            Download.CurrentlySeeking = string.IsNullOrWhiteSpace(album) ? artist : $"{artist} - {album}";

            List<SearchResult> results;
            try
            {
                results = await AlbumSearchAsync(artist, album, cfg);
            }
            catch
            {
                if (!Download.SoulClient!.State.ToString().Contains(Soulseek.SoulseekClientStates.LoggedIn.ToString()))
                    await Download.ConnectAsync();
                continue;
            }

            Download.SeekCount++;

            if (results.Count == 0)
                continue;

            EnqueueBestAlbum(results, artist, album, cfg);
        }
    }

    private Task<List<SearchResult>> AlbumSearchAsync(string artist, string album, SeekConfig cfg)
        => SoulseekSearch.SearchAsync(Download.SoulClient!, artist, album, cfg);

    private void EnqueueBestAlbum(List<SearchResult> results, string artist, string album, SeekConfig cfg)
    {
        var best = results
            .GroupBy(r => new { r.Username, Folder = GetFolder(r.Filename) })
            .Select(g =>
            {
                var tracks = DedupeByTrack(g);
                return new
                {
                    Tracks = tracks,
                    AlbumMatch = string.IsNullOrWhiteSpace(album)
                        ? 0
                        : Fuzz.PartialRatio(LastFolderName(g.Key.Folder).ToLower(), album.ToLower()),
                    LosslessCount = tracks.Count(t => FormatRank(t.Filename) >= 4),
                    FreeSlot = g.Any(f => f.HasFreeUploadSlot),
                    Speed = g.Max(f => f.UploadSpeed)
                };
            })
            .OrderByDescending(g => g.AlbumMatch)   // right album
            .ThenByDescending(g => g.LosslessCount) // prefer flac/wav over mp3
            .ThenByDescending(g => g.Tracks.Count)  // most complete
            .ThenByDescending(g => g.FreeSlot)      // ready to upload
            .ThenByDescending(g => g.Speed)
            .FirstOrDefault();

        if (best == null || best.Tracks.Count == 0)
            return;

        // Only commit when the chosen folder actually matches the requested album,
        // otherwise we'd grab an unrelated folder.
        if (!string.IsNullOrWhiteSpace(album) && best.AlbumMatch < cfg.SearchMatchAlbumPercentage)
            return;

        Download.SeekSuccessCount++;
        Download.EnqueueDownload(new SearchGroup
        {
            SearchResults = best.Tracks,
            TargetArtistName = artist,
            TargetAlbumName = album,
            SongNames = new List<string>()
        });
    }

    // One result per distinct track in a folder, preferring the best format (e.g. flac over mp3).
    private List<SearchResult> DedupeByTrack(IEnumerable<SearchResult> files)
    {
        return files
            .GroupBy(f =>
            {
                var t = Seeker.GetSeekTrackName(f.Filename);
                return string.IsNullOrWhiteSpace(t)
                    ? (f.FileNameWithExt ?? f.Filename).ToLower()
                    : t.ToLower();
            })
            .Select(g => g
                .OrderByDescending(f => FormatRank(f.Filename))
                .ThenByDescending(f => f.Size)
                .First())
            .ToList();
    }

    private static (string artist, string album) SplitArtistAlbum(string term, string delimiter)
    {
        var parts = term.Split(delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (term.Trim(), string.Empty);
        if (parts.Length == 1) return (parts[0], string.Empty);
        return (parts[0], string.Join(" - ", parts.Skip(1)));
    }

    public static string GetFolder(string filename)
    {
        var parts = filename.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? string.Empty : string.Join("/", parts.Take(parts.Length - 1));
    }

    public static string LastFolderName(string folder)
    {
        var parts = folder.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    // ----- Default search mode (unchanged behaviour from the CLI) -----
    private async Task RunSearchModeAsync(SeekConfig cfg, CancellationToken ct)
    {
        var searchTermGroups = Download.MissingNames
            .Select(term => new SearchTermModel(term, cfg.SearchDelimeter))
            .GroupBy(terms => new
            {
                terms.ArtistName,
                terms.AlbumName,
                terms.SearchTermType
            })
            .ToList();

        foreach (var terms in searchTermGroups)
        {
            ct.ThrowIfCancellationRequested();

            while (Download.InQueueCount > 100)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(1000, CancellationToken.None);
            }
            ct.ThrowIfCancellationRequested();

            List<SearchTermModel> searchTerms = new();
            SearchTermModel firstSearchTerm = terms.First();
            Download.CurrentlySeeking = firstSearchTerm.SearchTerm;

            if (cfg.MusicLibraryQuickMatch &&
                firstSearchTerm.SearchTermType is SearchTermType.ArtistTrack or SearchTermType.ArtistAlbumTrack)
            {
                Seeker.AddToCache(firstSearchTerm.ArtistName);

                foreach (var term in terms)
                {
                    if (Seeker.AlreadyInLibraryByTrack(firstSearchTerm.ArtistName, term.SongName!, cfg.MusicLibraryMatch, cfg.SearchFileExtensions))
                    {
                        Download.AlreadyDownloadedSkipCount++;
                    }
                    else
                    {
                        searchTerms.Add(term);
                    }
                }
            }
            else
            {
                searchTerms = terms.ToList();
            }

            var results = await Seeker.SearchAsync(
                searchTerms,
                Download.SoulClient!,
                cfg.FilterOutFileNames,
                cfg.SearchFileExtensions,
                cfg.MusicLibraryMatch,
                cfg.MaxFileSize,
                Download.DownloadArchiveList,
                cfg.SearchMatchArtistPercentage,
                cfg.SearchMatchAlbumPercentage,
                cfg.SearchMatchTrackPercentage);

            if (!string.IsNullOrWhiteSpace(Seeker.LastErrorMessage)
                && !Download.SoulClient!.State.ToString().Contains(Soulseek.SoulseekClientStates.Connected.ToString())
                && !Download.SoulClient.State.ToString().Contains(Soulseek.SoulseekClientStates.LoggedIn.ToString()))
            {
                await Download.ConnectAsync();
            }

            Download.SeekCount += terms.Count();

            if (results.Count > 0)
            {
                List<string> songNames = searchTerms
                    .Select(term => term.SongName)
                    .Where(term => !string.IsNullOrEmpty(term))
                    .ToList()!;

                if (cfg.GroupedDownloads && cfg.DownloadSingles && songNames.Count > 0)
                {
                    foreach (var songName in songNames)
                    {
                        var degroupedResults = results
                            .Select(result => new
                            {
                                Result = result,
                                MatchedFor = Fuzz.PartialRatio(result.FileNameWithExt!.ToLower(), songName.ToLower())
                            })
                            .Where(result => result.MatchedFor >= cfg.SearchMatchTrackPercentage)
                            .OrderByDescending(result => result.MatchedFor)
                            .Select(result => result.Result)
                            .ToList();

                        if (degroupedResults.Count > 0)
                        {
                            Download.SeekSuccessCount++;
                            Download.EnqueueDownload(new SearchGroup
                            {
                                SearchResults = degroupedResults,
                                TargetAlbumName = firstSearchTerm.AlbumName!,
                                TargetArtistName = firstSearchTerm.ArtistName,
                                SongNames = new List<string> { songName }
                            });
                        }
                    }
                }
                else if (cfg.GroupedDownloads)
                {
                    Download.SeekSuccessCount++;
                    Download.EnqueueDownload(new SearchGroup
                    {
                        SearchResults = results,
                        TargetAlbumName = firstSearchTerm.AlbumName!,
                        TargetArtistName = firstSearchTerm.ArtistName,
                        SongNames = songNames
                    });
                }
                else
                {
                    Download.SeekSuccessCount++;
                    foreach (var result in results)
                    {
                        Download.EnqueueDownload(new SearchGroup
                        {
                            SearchResults = new List<SearchResult> { result },
                            TargetAlbumName = firstSearchTerm.AlbumName!,
                            TargetArtistName = firstSearchTerm.ArtistName,
                            SongNames = songNames
                        });
                    }
                }
            }
        }
    }
}
