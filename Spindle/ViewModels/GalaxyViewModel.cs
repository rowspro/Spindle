using Avalonia.Threading;

namespace Spindle.ViewModels;

/// <summary>One point in the galaxy (a track or an album).</summary>
public sealed class GalaxyPoint
{
    public float X, Y, Z, Size;
    public uint Color;          // ARGB
    public bool Dim;            // filter: niet-matchende punten dimmen
    public bool Lossless;
    public bool HasCover;
    public string Artist = "", Album = "", Label = "";
}

public sealed class GalaxyLabel
{
    public float X, Y, Z;
    public uint Color;
    public string Text = "";
}

/// <summary>
/// Fase 4: de Galaxy — 3D-puntenwolk van de bibliotheek. v1 positioneert op tag-features:
/// genre-clusters (Fibonacci-bol) → artiest-subclusters → album-offsets → track-jitter,
/// met jaar als radiale diepte. Deterministisch, O(n), gecachet als arrays voor de renderer.
/// </summary>
public sealed class GalaxyViewModel : ViewModelBase
{
    private readonly LibraryService _lib;
    private readonly Func<string> _root;
    private readonly Action<string, string> _onOpenAlbum;
    private readonly Func<bool> _getAlbumLevel;
    private readonly Action<bool> _setAlbumLevel;
    private string _filter = "alles";
    private bool _loaded;

    public GalaxyPoint[] Points { get; private set; } = Array.Empty<GalaxyPoint>();
    public GalaxyLabel[] ClusterLabels { get; private set; } = Array.Empty<GalaxyLabel>();

    public GalaxyViewModel(LibraryService lib, Func<string> root, Action<string, string> onOpenAlbum,
        Func<bool> getAlbumLevel, Action<bool> setAlbumLevel)
    {
        _lib = lib;
        _root = root;
        _onOpenAlbum = onOpenAlbum;
        _getAlbumLevel = getAlbumLevel;
        _setAlbumLevel = setAlbumLevel;
        RefreshCommand = new RelayCommand(Refresh);
        FilterAllCommand = new RelayCommand(() => SetFilter("alles"));
        FilterLossyCommand = new RelayCommand(() => SetFilter("lossy"));
        FilterNoCoverCommand = new RelayCommand(() => SetFilter("hoes"));
        SwitchToAlbumsCommand = new RelayCommand(() => _setAlbumLevel(true));
        _lib.Changed += () => { if (_loaded) Refresh(); };
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand FilterAllCommand { get; }
    public RelayCommand FilterLossyCommand { get; }
    public RelayCommand FilterNoCoverCommand { get; }
    public RelayCommand SwitchToAlbumsCommand { get; }

    public bool IsFilterAll => _filter == "alles";
    public bool IsFilterLossy => _filter == "lossy";
    public bool IsFilterNoCover => _filter == "hoes";

    private string _status = "The galaxy builds when you arrive here.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private bool _warningVisible;
    public bool WarningVisible { get => _warningVisible; private set => SetField(ref _warningVisible, value); }

    private string _warningText = "";
    public string WarningText { get => _warningText; private set => SetField(ref _warningText, value); }

    public void Open(GalaxyPoint p)
    {
        if (p.Artist.Length > 0 || p.Album.Length > 0) _onOpenAlbum(p.Artist, p.Album);
    }

    private void SetFilter(string f)
    {
        _filter = f;
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterLossy));
        OnPropertyChanged(nameof(IsFilterNoCover));
        foreach (var p in Points)
            p.Dim = f switch { "lossy" => p.Lossless, "hoes" => p.HasCover, _ => false };
    }

    public void Refresh()
    {
        var root = _root();
        if (string.IsNullOrWhiteSpace(root)) { Status = "Set your music library (Settings)."; return; }
        var rows = _lib.Index.AllTracks(root);
        bool albumLevel = _getAlbumLevel();
        _loaded = true;

        // genre → cluster-index (gesorteerd op omvang zodat kleuren stabiel-ish zijn)
        static string G(string? g) => string.IsNullOrWhiteSpace(g) ? "unknown" : g.Trim().ToLowerInvariant();
        var genreCounts = rows.GroupBy(r => G(r.Genre)).ToDictionary(g => g.Key, g => g.Count());
        var genreOrder = genreCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var genreIdx = new Dictionary<string, int>();
        for (int i = 0; i < genreOrder.Count; i++) genreIdx[genreOrder[i]] = i;
        int gn = Math.Max(1, genreOrder.Count);

        (float, float, float) GenreDir(int i)
        {
            double k = i + 0.5;
            double phi = Math.Acos(1 - 2 * k / gn);
            double theta = Math.PI * (1 + Math.Sqrt(5)) * k;
            return ((float)(Math.Sin(phi) * Math.Cos(theta)), (float)(Math.Sin(phi) * Math.Sin(theta)), (float)Math.Cos(phi));
        }

        var pts = new List<GalaxyPoint>(rows.Count);
        var labelAcc = new Dictionary<int, (float X, float Y, float Z, int N)>();

        void AddPoint(string genreKey, string artist, string album, string label, int year,
                      float size, bool lossless, bool hasCover)
        {
            int gi = genreIdx[genreKey];
            var (gx, gy, gz) = GenreDir(gi);
            var (ax, ay, az) = HashDir(artist, 0.34f);
            var (bx, by, bz) = HashDir(artist + "|" + album, 0.13f);
            var (jx, jy, jz) = HashDir(label + album, 0.05f);
            float yr = year >= 1950 ? Math.Clamp((year - 1950) / 76f, 0f, 1f) : 0.6f;
            float scale = 1.25f - yr * 0.45f;   // ouder = verder van het centrum
            var p = new GalaxyPoint
            {
                X = (gx * 0.8f + ax + bx + jx) * scale,
                Y = (gy * 0.8f + ay + by + jy) * scale,
                Z = (gz * 0.8f + az + bz + jz) * scale,
                Size = size,
                Color = HueColor(gi),
                Artist = artist, Album = album, Label = label,
                Lossless = lossless, HasCover = hasCover,
            };
            p.Dim = _filter switch { "lossy" => p.Lossless, "hoes" => p.HasCover, _ => false };
            pts.Add(p);
            labelAcc.TryGetValue(gi, out var acc);
            labelAcc[gi] = (acc.X + p.X, acc.Y + p.Y, acc.Z + p.Z, acc.N + 1);
        }

        if (albumLevel)
        {
            foreach (var grp in rows.GroupBy(r =>
                ((!string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist).ToLowerInvariant(), r.Album.ToLowerInvariant())))
            {
                var ts = grp.ToList();
                var first = ts[0];
                var artist = !string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.AlbumArtist : first.Artist;
                var genre = G(ts.Select(t => t.Genre).FirstOrDefault(g2 => !string.IsNullOrWhiteSpace(g2)));
                AddPoint(genre, artist, first.Album, first.Album, ts.Max(t => t.Year),
                    2.2f + Math.Min(ts.Count, 30) / 30f * 2.6f,
                    ts.All(t => t.Lossless), ts.Any(t => t.HasCover));
            }
        }
        else
        {
            foreach (var r in rows)
            {
                var artist = !string.IsNullOrWhiteSpace(r.AlbumArtist) ? r.AlbumArtist : r.Artist;
                AddPoint(G(r.Genre), artist, r.Album,
                    string.IsNullOrWhiteSpace(r.Title) ? System.IO.Path.GetFileName(r.Path) : r.Title,
                    r.Year, 1.6f + Math.Min(r.Duration, 420) / 420f * 1.5f, r.Lossless, r.HasCover);
            }
        }

        var labels = new List<GalaxyLabel>();
        foreach (var gi in genreOrder.Take(12).Select(g2 => genreIdx[g2]))
        {
            if (!labelAcc.TryGetValue(gi, out var acc) || acc.N < 4) continue;
            labels.Add(new GalaxyLabel
            {
                X = acc.X / acc.N, Y = acc.Y / acc.N, Z = acc.Z / acc.N,
                Color = HueColor(gi),
                Text = genreOrder[gi],
            });
        }

        Points = pts.ToArray();
        ClusterLabels = labels.ToArray();
        WarningVisible = !albumLevel && rows.Count > 20000;
        WarningText = WarningVisible
            ? $"{rows.Count:N0} tracks at track level — that can stutter on a regular laptop. Album level renders smoothly."
            : "";
        Status = $"{Points.Length:N0} punten ({(albumLevel ? "albums" : "tracks")}) · color = genre · drag = rotate · scroll = zoom · click = open album";
    }

    private static (float, float, float) HashDir(string s, float mag)
    {
        unchecked
        {
            int h = 17;
            foreach (var c in s ?? "") h = h * 31 + c;
            var rnd = new Random(h);
            double theta = rnd.NextDouble() * Math.PI * 2;
            double z = rnd.NextDouble() * 2 - 1;
            double r = Math.Sqrt(Math.Max(0, 1 - z * z));
            return ((float)(Math.Cos(theta) * r * mag), (float)(Math.Sin(theta) * r * mag), (float)(z * mag));
        }
    }

    private static uint HueColor(int index)
    {
        double h = (index * 137.508) % 360.0;
        double s = 0.72, v = 0.95;
        double c = v * s, x = c * (1 - Math.Abs(h / 60.0 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        byte R = (byte)((r + m) * 255), Gc = (byte)((g + m) * 255), B = (byte)((b + m) * 255);
        return 0xFF000000u | ((uint)R << 16) | ((uint)Gc << 8) | B;
    }
}
