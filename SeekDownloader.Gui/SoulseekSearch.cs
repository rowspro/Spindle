using FuzzySharp;

namespace SeekDownloader.Gui;

/// <summary>
/// Direct Soulseek search on "artist album" (or just "artist"), shared by album mode and artist mode.
/// Returns raw results with match scores; grouping/selection happens elsewhere.
/// </summary>
public static class SoulseekSearch
{
    public static async Task<List<SearchResult>> SearchAsync(Soulseek.SoulseekClient client, string artist, string album, SeekConfig cfg)
    {
        var queryText = string.IsNullOrWhiteSpace(album) ? artist : $"{artist} {album}";

        var options = new Soulseek.SearchOptions(
            fileFilter: f =>
                cfg.SearchFileExtensions.Any(e => f.Filename.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                && (cfg.FilterOutFileNames == null || cfg.FilterOutFileNames.Count == 0
                    || !cfg.FilterOutFileNames.Any(n => f.Filename.ToLower().Contains(n.ToLower())))
                && f.Size < (long)cfg.MaxFileSize * 1024 * 1024,
            fileLimit: int.MaxValue,
            responseLimit: int.MaxValue);

        var search = await client.SearchAsync(Soulseek.SearchQuery.FromText(queryText), options: options);

        return search.Responses
            .SelectMany(r => r.Files.Select(f => new SearchResult
            {
                Username = r.Username,
                Filename = f.Filename,
                Size = f.Size,
                HasFreeUploadSlot = r.HasFreeUploadSlot,
                UploadSpeed = r.UploadSpeed,
                PotentialArtistMatch = Fuzz.PartialRatio(f.Filename.ToLower(), artist.ToLower()),
                PotentialAlbumMatch = string.IsNullOrWhiteSpace(album) ? 100 : Fuzz.PartialRatio(f.Filename.ToLower(), album.ToLower()),
                PotentialTrackMatch = 100,
                PotentialTrackWithoutVersionMatch = 100,
            }))
            .ToList();
    }
}
