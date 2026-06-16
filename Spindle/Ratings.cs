namespace Spindle;

/// <summary>
/// Writes a 1–5 star rating into the file's standard tag (POPM for MP3, RATING/FMPS_RATING for
/// FLAC/Vorbis, the rating atom for M4A) via ATL's cross-format Popularity (0..1). This is an explicit
/// user action, so touching the source is allowed; it makes ratings portable (iTunes/MusicBee/foobar,
/// and Rockbox on the iPod copy). Play counts stay in Spindle's index, never in the file.
/// </summary>
public static class Ratings
{
    public static void WriteTag(string path, int stars)
    {
        try
        {
            var t = new ATL.Track(path);
            t.Popularity = stars <= 0 ? null : System.Math.Clamp(stars, 1, 5) / 5f;
            t.Save();
        }
        catch { }
    }
}
