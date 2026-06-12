namespace Spindle;

/// <summary>
/// Applies the enabled tag-cleanup steps (see <see cref="CleanupOptions"/>) consistently, so the
/// metadata editor, the tag grid and the Inbox auto-clean all agree on what "clean up" means.
/// </summary>
public static class TagCleanup
{
    /// <summary>Clean a set of tag values according to the current preferences. Pure — no I/O.</summary>
    public static (string Title, string Artist, string AlbumArtist, string Album, string Genre) Apply(
        string title, string artist, string albumArtist, string album, string genre)
    {
        var nTitle = CleanupOptions.TitleCaseTitlesAndAlbums ? TextFormat.Title(title) : title;
        var nAlbum = CleanupOptions.TitleCaseTitlesAndAlbums ? TextFormat.Title(album) : album;
        var nArtist = TextFormat.FormatArtists(artist);
        var primarySrc = string.IsNullOrWhiteSpace(artist) ? albumArtist : artist;
        var nAlbumArtist = CleanupOptions.GroupCollabsUnderPrimaryArtist ? TextFormat.PrimaryArtist(primarySrc) : albumArtist;
        var nGenre = GenreFormat.Normalize(genre);
        return (nTitle, nArtist, nAlbumArtist, nAlbum, nGenre);
    }

    /// <summary>Clean a file's tags in place. Returns true if anything changed. Best-effort.</summary>
    public static bool ApplyToFile(string path)
    {
        try
        {
            var t = new ATL.Track(path);
            var (ti, ar, aa, al, ge) = Apply(t.Title ?? "", t.Artist ?? "", t.AlbumArtist ?? "", t.Album ?? "", t.Genre ?? "");
            if (ti == (t.Title ?? "") && ar == (t.Artist ?? "") && aa == (t.AlbumArtist ?? "")
                && al == (t.Album ?? "") && ge == (t.Genre ?? "")) return false;
            t.Title = ti; t.Artist = ar; t.AlbumArtist = aa; t.Album = al; t.Genre = ge;
            t.Save();
            return true;
        }
        catch { return false; }
    }
}
