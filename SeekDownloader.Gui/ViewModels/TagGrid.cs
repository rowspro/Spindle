using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using ATL;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One editable row in the tag table (fase 2). Original values are kept for dirty-tracking and undo.</summary>
public sealed class TagRowViewModel : ViewModelBase
{
    public string Path { get; internal set; }

    private string _fileName;
    public string FileName { get => _fileName; internal set => SetField(ref _fileName, value); }

    internal string OTitle = "", OArtist = "", OAlbumArtist = "", OAlbum = "", OGenre = "", OTrack = "", ODisc = "", OYear = "";

    private string _title = "", _artist = "", _albumArtist = "", _album = "", _genre = "", _track = "", _disc = "", _year = "";
    public string Title { get => _title; set { if (SetField(ref _title, value)) OnPropertyChanged(nameof(DirtyMark)); } }
    public string Artist { get => _artist; set { if (SetField(ref _artist, value)) OnPropertyChanged(nameof(DirtyMark)); } }
    public string AlbumArtist { get => _albumArtist; set { if (SetField(ref _albumArtist, value)) OnPropertyChanged(nameof(DirtyMark)); } }
    public string Album { get => _album; set { if (SetField(ref _album, value)) OnPropertyChanged(nameof(DirtyMark)); } }
    public string Genre { get => _genre; set { if (SetField(ref _genre, value)) OnPropertyChanged(nameof(DirtyMark)); } }
    public string Track { get => _track; set { if (SetField(ref _track, value)) OnPropertyChanged(nameof(DirtyMark)); } }
    public string Disc { get => _disc; set { if (SetField(ref _disc, value)) OnPropertyChanged(nameof(DirtyMark)); } }
    public string Year { get => _year; set { if (SetField(ref _year, value)) OnPropertyChanged(nameof(DirtyMark)); } }

    public bool IsDirty =>
        Title != OTitle || Artist != OArtist || AlbumArtist != OAlbumArtist || Album != OAlbum ||
        Genre != OGenre || Track != OTrack || Disc != ODisc || Year != OYear;

    public string DirtyMark => IsDirty ? "●" : "";

    public TagRowViewModel(string path)
    {
        Path = path;
        _fileName = System.IO.Path.GetFileName(path);
    }

    internal void Accept()
    {
        OTitle = Title; OArtist = Artist; OAlbumArtist = AlbumArtist; OAlbum = Album;
        OGenre = Genre; OTrack = Track; ODisc = Disc; OYear = Year;
        OnPropertyChanged(nameof(DirtyMark));
    }

    internal UndoJournal.TagOp Snapshot() =>
        new(Path, OTitle, OArtist, OAlbumArtist, OAlbum, OGenre, OTrack, ODisc, OYear);
}

/// <summary>Format-string-taal van de converters: %artist% %albumartist% %album% %title% %track% %disc% %year% %genre%.</summary>
public static class TagPattern
{
    private static readonly Regex Token = new("%(artist|albumartist|album|title|track|disc|year|genre)%", RegexOptions.IgnoreCase);

    public static string Format(string pattern, TagRowViewModel r)
        => Token.Replace(pattern ?? "", m => SanitizeName(Get(r, m.Groups[1].Value.ToLowerInvariant())));

    private static string Get(TagRowViewModel r, string token) => token switch
    {
        "artist" => r.Artist,
        "albumartist" => r.AlbumArtist,
        "album" => r.Album,
        "title" => r.Title,
        "track" => int.TryParse(r.Track, out var tn) ? tn.ToString("00") : r.Track,
        "disc" => r.Disc,
        "year" => r.Year,
        "genre" => r.Genre,
        _ => "",
    };

    /// <summary>Parse a filename (without extension) against the pattern. Null = no match.</summary>
    public static Dictionary<string, string>? Parse(string pattern, string name)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var tokens = new List<string>();
        var rx = "^";
        int pos = 0;
        foreach (Match m in Token.Matches(pattern))
        {
            rx += Regex.Escape(pattern.Substring(pos, m.Index - pos));
            var tok = m.Groups[1].Value.ToLowerInvariant();
            tokens.Add(tok);
            rx += tok is "track" or "disc" or "year" ? "(\\d+)" : "(.+?)";
            pos = m.Index + m.Length;
        }
        rx += Regex.Escape(pattern.Substring(pos)) + "$";
        var match = Regex.Match(name, rx, RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var dict = new Dictionary<string, string>();
        for (int i = 0; i < tokens.Count; i++) dict[tokens[i]] = match.Groups[i + 1].Value.Trim();
        return dict;
    }

    public static string SanitizeName(string s)
    {
        s = Regex.Replace(s ?? "", "[/\\\\:*?\"<>|\\x00-\\x1f]", "-");
        s = Regex.Replace(s, "\\s+", " ").Trim().TrimEnd('.', ' ');
        return s;
    }
}

/// <summary>
/// Fase 2: de Mp3tag-achtige tabel-editor. Alles loopt via pending wijzigingen in de rijen
/// (Acties en Bestandsnaam→Tags vullen de tabel; Bewaar schrijft + registreert tag-undo).
/// Tags→Bestandsnaam hernoemt direct met move-undo.
/// </summary>
public sealed class TagGridViewModel : ViewModelBase
{
    private readonly LibraryService? _lib;
    private readonly UndoJournal? _undo;
    private List<string> _lastFiles = new();
    private bool _busy;

    public ObservableCollection<TagRowViewModel> Rows { get; } = new();

    public TagGridViewModel(LibraryService? lib, UndoJournal? undo)
    {
        _lib = lib;
        _undo = undo;
        SaveCommand = new RelayCommand(Save, () => HasDirty && !_busy);
        ActionAppleCommand = new RelayCommand(() => Mutate(r =>
        {
            r.Title = TextFormat.Title(r.Title);
            r.Album = TextFormat.Title(r.Album);
            r.Artist = TextFormat.AppleArtist(r.Artist);
            r.AlbumArtist = TextFormat.PrimaryArtist(string.IsNullOrWhiteSpace(r.Artist) ? r.AlbumArtist : r.Artist);
            r.Genre = GenreFormat.Normalize(r.Genre);
        }));
        ActionGenreCommand = new RelayCommand(() => Mutate(r => r.Genre = GenreFormat.Normalize(r.Genre)));
        ActionTrimCommand = new RelayCommand(() => Mutate(r =>
        {
            r.Title = Collapse(r.Title); r.Artist = Collapse(r.Artist); r.AlbumArtist = Collapse(r.AlbumArtist);
            r.Album = Collapse(r.Album); r.Genre = Collapse(r.Genre);
        }));
        ApplyF2TCommand = new RelayCommand(ApplyF2T, () => Rows.Count > 0);
        ApplyT2FCommand = new RelayCommand(ApplyT2F, () => Rows.Count > 0 && !_busy);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ActionAppleCommand { get; }
    public RelayCommand ActionGenreCommand { get; }
    public RelayCommand ActionTrimCommand { get; }
    public RelayCommand ApplyF2TCommand { get; }
    public RelayCommand ApplyT2FCommand { get; }

    public bool HasDirty => Rows.Any(r => r.IsDirty);
    public string DirtyText => $"{Rows.Count(r => r.IsDirty)} gewijzigd · {Rows.Count} rijen";

    private string _status = "";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _patternF2T = "%artist% - %title%";
    public string PatternF2T { get => _patternF2T; set { if (SetField(ref _patternF2T, value)) UpdatePreviews(); } }

    private string _patternT2F = "%track% %title%";
    public string PatternT2F { get => _patternT2F; set { if (SetField(ref _patternT2F, value)) UpdatePreviews(); } }

    private string _previewF2T = "";
    public string PreviewF2T { get => _previewF2T; private set => SetField(ref _previewF2T, value); }

    private string _previewT2F = "";
    public string PreviewT2F { get => _previewT2F; private set => SetField(ref _previewT2F, value); }

    private static string Collapse(string s) => Regex.Replace(s ?? "", "\\s+", " ").Trim();

    public void Load(IReadOnlyList<string> files)
    {
        _lastFiles = files.ToList();
        var snapshot = _lastFiles.ToList();
        Status = "Tabel laden…";
        Task.Run(() =>
        {
            var rows = new List<TagRowViewModel>();
            foreach (var f in snapshot)
            {
                var row = new TagRowViewModel(f);
                try
                {
                    var t = new Track(f);
                    row.Title = t.Title ?? ""; row.Artist = t.Artist ?? ""; row.AlbumArtist = t.AlbumArtist ?? "";
                    row.Album = t.Album ?? ""; row.Genre = t.Genre ?? "";
                    var tn = t.TrackNumber ?? 0; row.Track = tn > 0 ? tn.ToString() : "";
                    var dn = t.DiscNumber ?? 0; row.Disc = dn > 0 ? dn.ToString() : "";
                    int y = (int?)t.Year ?? 0; row.Year = y > 0 ? y.ToString() : "";
                }
                catch { }
                row.Accept();
                rows.Add(row);
            }
            Dispatcher.UIThread.Post(() =>
            {
                Rows.Clear();
                foreach (var r in rows)
                {
                    r.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(DirtyText)); SaveCommand.RaiseCanExecuteChanged(); };
                    Rows.Add(r);
                }
                OnPropertyChanged(nameof(DirtyText));
                SaveCommand.RaiseCanExecuteChanged();
                ApplyF2TCommand.RaiseCanExecuteChanged();
                ApplyT2FCommand.RaiseCanExecuteChanged();
                UpdatePreviews();
                Status = "";
            });
        });
    }

    public void Reload() { if (_lastFiles.Count > 0) Load(_lastFiles); }

    private void Mutate(Action<TagRowViewModel> fn)
    {
        foreach (var r in Rows) fn(r);
        OnPropertyChanged(nameof(DirtyText));
        SaveCommand.RaiseCanExecuteChanged();
        Status = "Actie toegepast — controleer de tabel en klik Bewaar.";
    }

    private void Save()
    {
        var dirty = Rows.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0) return;
        _busy = true;
        SaveCommand.RaiseCanExecuteChanged();
        Status = $"{dirty.Count} bestanden opslaan…";
        var before = dirty.Select(r => r.Snapshot()).ToList();

        Task.Run(() =>
        {
            int n = 0;
            foreach (var r in dirty)
            {
                try
                {
                    var t = new Track(r.Path);
                    t.Title = r.Title; t.Artist = r.Artist; t.AlbumArtist = r.AlbumArtist;
                    t.Album = r.Album; t.Genre = r.Genre;
                    t.TrackNumber = int.TryParse(r.Track, out var tn) && tn > 0 ? tn : null;
                    t.DiscNumber = int.TryParse(r.Disc, out var dn) && dn > 0 ? dn : null;
                    t.Year = int.TryParse(r.Year, out var y) ? y : 0;
                    t.Save();
                    n++;
                }
                catch { }
            }
            _undo?.RecordTags($"Tags bewerkt: {n} nummers", before);
            _lib?.RefreshConfigured();
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var r in dirty) r.Accept();
                _busy = false;
                OnPropertyChanged(nameof(DirtyText));
                SaveCommand.RaiseCanExecuteChanged();
                Status = $"{n} bestanden opgeslagen (Cmd+Z = ongedaan maken).";
            });
        });
    }

    private void ApplyF2T()
    {
        int hit = 0;
        foreach (var r in Rows)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(r.FileName);
            var d = TagPattern.Parse(PatternF2T, name);
            if (d == null) continue;
            hit++;
            if (d.TryGetValue("artist", out var v) && v.Length > 0) r.Artist = v;
            if (d.TryGetValue("albumartist", out v) && v.Length > 0) r.AlbumArtist = v;
            if (d.TryGetValue("album", out v) && v.Length > 0) r.Album = v;
            if (d.TryGetValue("title", out v) && v.Length > 0) r.Title = v;
            if (d.TryGetValue("track", out v) && v.Length > 0) r.Track = v.TrimStart('0').Length > 0 ? v.TrimStart('0') : v;
            if (d.TryGetValue("disc", out v) && v.Length > 0) r.Disc = v;
            if (d.TryGetValue("year", out v) && v.Length > 0) r.Year = v;
            if (d.TryGetValue("genre", out v) && v.Length > 0) r.Genre = v;
        }
        OnPropertyChanged(nameof(DirtyText));
        SaveCommand.RaiseCanExecuteChanged();
        Status = $"{hit}/{Rows.Count} bestandsnamen gematcht — controleer de tabel en klik Bewaar.";
    }

    private void ApplyT2F()
    {
        if (_busy) return;
        var jobs = new List<(TagRowViewModel Row, string NewPath)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows)
        {
            var dir = System.IO.Path.GetDirectoryName(r.Path) ?? "";
            var ext = System.IO.Path.GetExtension(r.Path);
            var newName = TagPattern.Format(PatternT2F, r);
            if (string.IsNullOrWhiteSpace(newName)) continue;
            var dest = System.IO.Path.Combine(dir, newName + ext);
            if (string.Equals(dest, r.Path, StringComparison.Ordinal)) continue;
            if (!taken.Add(dest)) continue;       // botsing binnen de selectie
            jobs.Add((r, dest));
        }
        if (jobs.Count == 0) { Status = "Niets te hernoemen."; return; }

        _busy = true;
        ApplyT2FCommand.RaiseCanExecuteChanged();
        Status = $"{jobs.Count} bestanden hernoemen…";
        Task.Run(() =>
        {
            var ops = new List<UndoJournal.MoveOp>();
            int n = 0, skipped = 0;
            foreach (var (row, dest) in jobs)
            {
                try
                {
                    if (File.Exists(dest)) { skipped++; continue; }
                    File.Move(row.Path, dest);
                    ops.Add(new UndoJournal.MoveOp(row.Path, dest));
                    var d2 = dest;
                    Dispatcher.UIThread.Post(() => { row.Path = d2; row.FileName = System.IO.Path.GetFileName(d2); });
                    n++;
                }
                catch { skipped++; }
            }
            _undo?.Record($"Hernoemd: {n} bestanden", ops);
            _lib?.RefreshConfigured();
            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                ApplyT2FCommand.RaiseCanExecuteChanged();
                UpdatePreviews();
                Status = $"{n} hernoemd" + (skipped > 0 ? $", {skipped} overgeslagen" : "") + " (Cmd+Z = ongedaan maken).";
            });
        });
    }

    private void UpdatePreviews()
    {
        var r = Rows.FirstOrDefault();
        if (r == null) { PreviewF2T = "(geen bestanden geladen)"; PreviewT2F = "(geen bestanden geladen)"; return; }
        var name = System.IO.Path.GetFileNameWithoutExtension(r.FileName);
        var d = TagPattern.Parse(PatternF2T, name);
        PreviewF2T = d == null
            ? $"„{name}”  →  (geen match met patroon)"
            : $"„{name}”  →  " + string.Join("  ·  ", d.Select(kv => $"{kv.Key}={kv.Value}"));
        var ext = System.IO.Path.GetExtension(r.FileName);
        PreviewT2F = $"„{r.FileName}”  →  „{TagPattern.Format(PatternT2F, r)}{ext}”";
    }
}
