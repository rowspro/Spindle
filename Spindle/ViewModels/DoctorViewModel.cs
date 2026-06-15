using System.Collections.ObjectModel;
using ATL;
using Avalonia.Threading;

namespace Spindle.ViewModels;

/// <summary>One Library Doctor finding (artist spelling cluster, misplaced album, genre variant, album oddity).</summary>
public sealed class DoctorFinding : ViewModelBase
{
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    private string _sub = "";
    public string Sub { get => _sub; set => SetField(ref _sub, value); }
    public List<string> Files { get; init; } = new();
    public string FixLabel { get; init; } = "Fix";

    // Artist clusters: the user picks the canonical spelling.
    public ObservableCollection<string> Spellings { get; } = new();
    public bool HasSpellings => Spellings.Count > 0;
    private string? _selectedSpelling;
    public string? SelectedSpelling { get => _selectedSpelling; set => SetField(ref _selectedSpelling, value); }

    public RelayCommand? FixCommand { get; set; }
    public RelayCommand? EditCommand { get; set; }
    public RelayCommand? DismissCommand { get; set; }
    public bool HasFix => FixCommand != null;
    public bool HasEdit => EditCommand != null;
    public bool HasDismiss => DismissCommand != null;

    internal List<UndoJournal.MoveOp> PlannedMoves { get; } = new();
    internal string GenreCanonical = "";
    // Duplicates: label (shown in the picker) → file path; the chosen one is kept, the rest trashed.
    internal Dictionary<string, string> CopyByLabel { get; } = new();
    internal string DupIgnoreKey = "";
}

/// <summary>
/// Library Doctor: scans the index for fixable inconsistencies — artist spelling variants
/// (AC/DC vs AC &amp; DC), files that don't sit where the filename template says, non-canonical
/// genres, and album oddities (mixed quality, multiple years, track-number gaps).
/// Every fix is one undo batch (Cmd+Z).
/// </summary>
public sealed class DoctorViewModel : ViewModelBase
{
    private readonly LibraryService _lib;
    private readonly UndoJournal _undo;
    private readonly Func<string> _root;
    private readonly Func<string> _template;
    private readonly Action<IReadOnlyList<string>, string> _onEdit;

    public DoctorViewModel(LibraryService lib, UndoJournal undo, Func<string> root, Func<string> template,
        Action<IReadOnlyList<string>, string> onEdit)
    {
        _lib = lib; _undo = undo; _root = root; _template = template; _onEdit = onEdit;
        RunCommand = new RelayCommand(Run, () => !IsBusy);
        FixAllLocationsCommand = new RelayCommand(() => FixAll("Locations"),
            () => !IsBusy && Findings.Any(f => f.Category == "Locations"));
        FixAllGenresCommand = new RelayCommand(() => FixAll("Genres"),
            () => !IsBusy && Findings.Any(f => f.Category == "Genres"));
        FixAllDuplicatesCommand = new RelayCommand(DedupeAll,
            () => !IsBusy && Findings.Any(f => f.Category == "Duplicates"));
    }

    public ObservableCollection<DoctorFinding> Findings { get; } = new();
    public RelayCommand RunCommand { get; }
    public RelayCommand FixAllLocationsCommand { get; }
    public RelayCommand FixAllGenresCommand { get; }
    public RelayCommand FixAllDuplicatesCommand { get; }
    public bool HasRun { get; private set; }

    private bool _useFingerprint;
    /// <summary>Opt-in: also match duplicates by AcoustID fingerprint (slower, needs a key) during the checkup.</summary>
    public bool UseFingerprint { get => _useFingerprint; set => SetField(ref _useFingerprint, value); }

    // "Not a duplicate" memory — survives checkups (persisted in SpindleConfig.DuplicateIgnores).
    private readonly HashSet<string> _dupIgnores = new(StringComparer.OrdinalIgnoreCase);
    public void LoadDupIgnores(List<string>? keys)
    {
        _dupIgnores.Clear();
        foreach (var k in keys ?? new()) if (!string.IsNullOrEmpty(k)) _dupIgnores.Add(k);
    }
    public List<string> DupIgnoreKeys() => _dupIgnores.ToList();
    private static string DupIgnoreKeyOf(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]", "");
    private static string NormDup(string? s) =>
        System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    private bool _busy;
    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) RaiseCmds(); } }

    private string _summary = "Run a checkup to examine your library.";
    public string Summary { get => _summary; private set => SetField(ref _summary, value); }

    private void RaiseCmds()
    {
        RunCommand.RaiseCanExecuteChanged();
        FixAllLocationsCommand.RaiseCanExecuteChanged();
        FixAllGenresCommand.RaiseCanExecuteChanged();
        FixAllDuplicatesCommand.RaiseCanExecuteChanged();
    }

    // ---- conventions (mirrored from the inbox approve flow) ----
    private static string Clean(string s)
    {
        s = (s ?? "").Trim().Replace('/', '-').Replace('\\', '-');
        s = System.Text.RegularExpressions.Regex.Replace(s, "[:*?\"<>|\\x00-\\x1f]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim().TrimEnd('.', ' ');
        return s.Length > 0 ? s : "Unknown";
    }

    private static string Eff(IndexedTrack t) => t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.Artist;

    /// <summary>Loose key so spelling variants collide: lowercase, drop leading "the", keep letters/digits.</summary>
    private static string NormArtistKey(string s)
    {
        s = s.ToLowerInvariant().Trim();
        if (s.StartsWith("the ")) s = s[4..];
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static UndoJournal.TagOp Snapshot(Track t, string path)
    {
        int tn = (int?)t.TrackNumber ?? 0, dn = (int?)t.DiscNumber ?? 0, y = (int?)t.Year ?? 0;
        return new UndoJournal.TagOp(path, t.Title ?? "", t.Artist ?? "", t.AlbumArtist ?? "",
            t.Album ?? "", t.Genre ?? "", tn > 0 ? tn.ToString() : "", dn > 0 ? dn.ToString() : "", y.ToString());
    }

    public void Run()
    {
        if (IsBusy) return;
        var root = _root();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { Summary = "Set your music library first (Settings)."; return; }
        IsBusy = true;
        HasRun = true;
        Summary = "Examining your library…";
        var template = _template();
        Task.Run(async () =>
        {
            var found = new List<DoctorFinding>();
            List<IndexedTrack> tracks;
            try { tracks = _lib.Index.AllTracks(root); } catch { tracks = new List<IndexedTrack>(); }

            // 1) Artist spelling variants
            foreach (var cl in tracks.GroupBy(t => NormArtistKey(Eff(t))))
            {
                if (cl.Key.Length == 0) continue;
                var spellings = cl.GroupBy(Eff, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToList();
                if (spellings.Count < 2) continue;
                var f = new DoctorFinding
                {
                    Category = "Artists",
                    Title = string.Join("   ·   ", spellings.Select(g => g.Key)),
                    Files = cl.Select(t => t.Path).ToList(),
                    FixLabel = "Unify",
                };
                f.Sub = $"{f.Files.Count} tracks, {spellings.Count} spellings — pick the right one and unify (retag + move, undoable)";
                foreach (var g in spellings) f.Spellings.Add(g.Key);
                f.SelectedSpelling = spellings[0].Key;
                f.FixCommand = new RelayCommand(() => FixArtistCluster(f));
                found.Add(f);
            }

            // 2) Files that aren't where the template says (per album)
            foreach (var alb in tracks.GroupBy(t => (A: Eff(t).ToLowerInvariant(), B: t.Album.ToLowerInvariant())))
            {
                var first = alb.First();
                var eff = Eff(first);
                var albumName = first.Album;
                int yr = alb.Max(t => t.Year);
                var artistDir = Clean(string.IsNullOrWhiteSpace(eff) ? "Unknown Artist" : eff);
                var albumDir = Singles.IsSingle(albumName) ? Singles.Folder : Clean(yr > 0 ? $"{albumName} ({yr})" : albumName);
                bool multiDisc = alb.Max(t => t.Disc) > 1;
                var moves = new List<UndoJournal.MoveOp>();
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in alb)
                {
                    var ext = Path.GetExtension(t.Path);
                    string name = !string.IsNullOrWhiteSpace(t.Title)
                        ? NameTemplate.Build(template, string.IsNullOrWhiteSpace(t.Artist) ? eff : t.Artist,
                            albumName, t.Title, t.TrackNo, t.Year > 0 ? t.Year.ToString() : "", Clean, t.Disc, multiDisc) + ext
                        : Path.GetFileName(t.Path);
                    var dest = Path.Combine(root, artistDir, albumDir, name);
                    if (string.Equals(dest, t.Path, StringComparison.OrdinalIgnoreCase)) { taken.Add(dest); continue; }
                    int n2 = 2;
                    while (!taken.Add(dest) || File.Exists(dest))
                    {
                        if (string.Equals(dest, t.Path, StringComparison.OrdinalIgnoreCase)) break;
                        dest = Path.Combine(root, artistDir, albumDir,
                            Path.GetFileNameWithoutExtension(name) + $" ({n2++})" + ext);
                    }
                    if (!string.Equals(dest, t.Path, StringComparison.OrdinalIgnoreCase))
                        moves.Add(new UndoJournal.MoveOp(t.Path, dest));
                }
                if (moves.Count == 0) continue;
                var f = new DoctorFinding
                {
                    Category = "Locations",
                    Title = (eff.Length > 0 ? eff + " — " : "") + (albumName.Length > 0 ? albumName : "Singles"),
                    Files = alb.Select(t => t.Path).ToList(),
                    FixLabel = "Move into place",
                };
                f.PlannedMoves.AddRange(moves);
                f.Sub = $"{moves.Count} of {f.Files.Count} files are not where the template says ({Path.Combine(artistDir, albumDir)})";
                f.FixCommand = new RelayCommand(() => FixLocations(f));
                found.Add(f);
            }

            // 3) Non-canonical genres
            foreach (var g in tracks.Where(t => t.Genre.Length > 0).GroupBy(t => t.Genre, StringComparer.Ordinal))
            {
                var canon = GenreFormat.Normalize(g.Key);
                if (string.IsNullOrWhiteSpace(canon) || string.Equals(canon, g.Key, StringComparison.Ordinal)) continue;
                var f = new DoctorFinding
                {
                    Category = "Genres",
                    Title = $"{g.Key}  →  {canon}",
                    Files = g.Select(t => t.Path).ToList(),
                    FixLabel = "Retag",
                    GenreCanonical = canon,
                };
                f.Sub = $"{f.Files.Count} tracks";
                f.FixCommand = new RelayCommand(() => FixGenre(f));
                found.Add(f);
            }

            // 3b) Genre-less or non-conforming genres → pick a standard genre and retag.
            if (Genres.Standard.Count > 0)
            {
                DoctorFinding MakeGenreStd(string title, string sub, List<string> files)
                {
                    var gf = new DoctorFinding { Category = "GenreStd", Title = title, Sub = sub, Files = files, FixLabel = "Retag" };
                    foreach (var g in Genres.Standard) gf.Spellings.Add(g);
                    // No preselect — the user consciously picks a target (Fix guards against empty).
                    gf.FixCommand = new RelayCommand(() =>
                    {
                        if (string.IsNullOrWhiteSpace(gf.SelectedSpelling)) { Summary = "Pick a genre first."; return; }
                        gf.GenreCanonical = gf.SelectedSpelling!;
                        FixGenre(gf);
                    });
                    return gf;
                }

                // Non-standard genres that don't already canonicalize to a standard (those are check 3).
                foreach (var g in tracks.Where(t => t.Genre.Length > 0
                                && !Genres.AllStandard(t.Genre)
                                && !Genres.AllStandard(GenreFormat.Normalize(t.Genre)))
                            .GroupBy(t => t.Genre, StringComparer.Ordinal))
                    found.Add(MakeGenreStd($"{g.Key}  →  ?", $"{g.Count()} tracks · non-standard genre",
                        g.Select(t => t.Path).ToList()));

                // Genre-less tracks, grouped per album so you assign one genre at a time.
                foreach (var alb in tracks.Where(t => string.IsNullOrWhiteSpace(t.Genre))
                            .GroupBy(t => (A: Eff(t).ToLowerInvariant(), B: t.Album.ToLowerInvariant())))
                {
                    var first = alb.First();
                    var title = (Eff(first).Length > 0 ? Eff(first) + " — " : "") + (first.Album.Length > 0 ? first.Album : "(no album)");
                    found.Add(MakeGenreStd(title, $"{alb.Count()} tracks · no genre", alb.Select(t => t.Path).ToList()));
                }
            }

            // 4) Album oddities (informational → Metadata)
            foreach (var alb in tracks.GroupBy(t => (A: Eff(t).ToLowerInvariant(), B: t.Album.ToLowerInvariant())))
            {
                var first = alb.First();
                if (first.Album.Length == 0) continue;
                if (Singles.IsSingle(first.Album)) continue;   // singles aren't one album — no track-number checks
                var issues = new List<string>();
                int lossless = alb.Count(t => t.Lossless);
                if (lossless > 0 && lossless < alb.Count())
                    issues.Add($"mixed quality ({lossless} lossless, {alb.Count() - lossless} lossy)");
                var years = alb.Select(t => t.Year).Where(y => y > 0).Distinct().OrderBy(y => y).ToList();
                if (years.Count > 1) issues.Add("multiple years: " + string.Join(", ", years));
                bool multiDisc = alb.Max(t => t.Disc) > 1;
                foreach (var disc in alb.GroupBy(t => Math.Max(1, t.Disc)))
                {
                    var nums = disc.Select(t => t.TrackNo).Where(n => n > 0).ToList();
                    if (nums.Count == 0) continue;
                    var suffix = multiDisc ? $" (disc {disc.Key})" : "";
                    var missing = Enumerable.Range(1, nums.Max()).Except(nums).ToList();
                    var dupes = nums.GroupBy(n => n).Where(g2 => g2.Count() > 1).Select(g2 => g2.Key).ToList();
                    if (missing.Count is > 0 and <= 8) issues.Add($"missing track #{string.Join(", #", missing)}{suffix}");
                    if (dupes.Count > 0) issues.Add($"duplicate track #{string.Join(", #", dupes)}{suffix}");
                }
                if (issues.Count == 0) continue;
                var f = new DoctorFinding
                {
                    Category = "Albums",
                    Title = (Eff(first).Length > 0 ? Eff(first) + " — " : "") + first.Album,
                    Files = alb.OrderBy(t => t.Disc).ThenBy(t => t.TrackNo).Select(t => t.Path).ToList(),
                };
                f.Sub = string.Join(" · ", issues);
                f.EditCommand = new RelayCommand(() => _onEdit(f.Files, $"Doctor — {f.Title}: {f.Sub}"));
                found.Add(f);
            }

            // 5) Duplicate tracks across the whole library (same artist + title; from the index, fast).
            var byKey = new Dictionary<string, List<IndexedTrack>>();
            foreach (var t in tracks)
            {
                if (string.IsNullOrWhiteSpace(t.Title)) continue;
                var who = t.Artist.Length > 0 ? t.Artist : t.AlbumArtist;
                if (who.Length == 0) continue;
                var key = NormDup(who) + "|" + NormDup(t.Title);
                if (!byKey.TryGetValue(key, out var l)) byKey[key] = l = new List<IndexedTrack>();
                l.Add(t);
            }
            var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byKey.Where(kv => kv.Value.Count > 1))
            {
                var df = MakeDupFinding(kv.Value, "");
                if (df != null) { found.Add(df); foreach (var t in kv.Value) grouped.Add(t.Path); }
            }

            // 5b) Optional deep pass: AcoustID fingerprint over the rest (catches same recording, different tags).
            if (UseFingerprint)
            {
                var fpKey = Settings.Load().AcoustIdKey ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fpKey) && FingerprintService.Available)
                {
                    var remaining = tracks.Where(t => !grouped.Contains(t.Path)).ToList();
                    var byAid = new Dictionary<string, List<IndexedTrack>>();
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        var snap = i + 1;
                        Dispatcher.UIThread.Post(() => Summary = $"Fingerprinting… {snap}/{remaining.Count}");
                        string? aid = null;
                        try { aid = await FingerprintService.AcoustIdOf(remaining[i].Path, fpKey); } catch { }
                        if (aid != null)
                        {
                            if (!byAid.TryGetValue(aid, out var l)) byAid[aid] = l = new List<IndexedTrack>();
                            l.Add(remaining[i]);
                        }
                        try { await Task.Delay(350); } catch { }
                    }
                    foreach (var kv in byAid.Where(kv => kv.Value.Count > 1))
                    {
                        var df = MakeDupFinding(kv.Value, "🔊 ");
                        if (df != null) found.Add(df);
                    }
                }
            }

            var order = new Dictionary<string, int> { ["Artists"] = 0, ["Locations"] = 1, ["Genres"] = 2, ["GenreStd"] = 3, ["Albums"] = 4, ["Duplicates"] = 5 };
            var sorted = found.OrderBy(f => order[f.Category]).ThenByDescending(f => f.Files.Count).ToList();

            // Every finding is openable in the metadata editor (button + double-click) so you can
            // inspect and fix the album before applying any move/retag.
            foreach (var f in sorted)
                f.EditCommand ??= new RelayCommand(() => _onEdit(f.Files, $"Doctor — {f.Title}: {f.Sub}"));

            Dispatcher.UIThread.Post(() =>
            {
                Findings.Clear();
                foreach (var f in sorted) Findings.Add(f);
                int na = sorted.Count(f => f.Category == "Artists");
                int nl = sorted.Count(f => f.Category == "Locations");
                int ng = sorted.Count(f => f.Category == "Genres");
                int ngs = sorted.Count(f => f.Category == "GenreStd");
                int nal = sorted.Count(f => f.Category == "Albums");
                int ndup = sorted.Count(f => f.Category == "Duplicates");
                Summary = sorted.Count == 0
                    ? $"Checkup done — {tracks.Count:N0} tracks examined, nothing to fix. Your library is in great shape."
                    : $"{sorted.Count} findings — {na} artist spellings · {nl} misplaced albums · {ng} genre variants · {ngs} non-standard/missing genres · {nal} album oddities · {ndup} duplicate sets";
                IsBusy = false;
            });
        });
    }

    private void FixArtistCluster(DoctorFinding f)
    {
        if (IsBusy) return;
        var canonical = f.SelectedSpelling;
        if (string.IsNullOrWhiteSpace(canonical)) return;
        var root = _root();
        var spellings = f.Spellings.ToList();
        var files = f.Files.ToList();
        IsBusy = true;
        Summary = $"Unifying to \"{canonical}\"…";
        Task.Run(() =>
        {
            var tags = new List<UndoJournal.TagOp>();
            var moves = new List<UndoJournal.MoveOp>();
            var byAlbum = new Dictionary<string, List<(string Path, string Album, int Year)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var t = new Track(path);
                    var before = Snapshot(t, path);
                    bool changed = false;
                    var curAA = t.AlbumArtist ?? "";
                    var curA = t.Artist ?? "";
                    var effNow = curAA.Length > 0 ? curAA : curA;
                    if (!string.Equals(curAA, canonical, StringComparison.Ordinal)) { t.AlbumArtist = canonical; changed = true; }
                    if (spellings.Any(sp => string.Equals(curA, sp, StringComparison.OrdinalIgnoreCase))
                        && !string.Equals(curA, canonical, StringComparison.Ordinal)) { t.Artist = canonical; changed = true; }
                    if (changed) { t.Save(); tags.Add(before); }
                    var alb = t.Album ?? "";
                    if (!byAlbum.TryGetValue(alb.ToLowerInvariant(), out var l)) byAlbum[alb.ToLowerInvariant()] = l = new();
                    l.Add((path, alb, (int?)t.Year ?? 0));
                }
                catch { }
            }
            // Move everything under the canonical artist folder (file names stay as they are).
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                var artistDir = Clean(canonical);
                foreach (var kv in byAlbum)
                {
                    var albumName = kv.Value[0].Album;
                    int yr = kv.Value.Max(v => v.Year);
                    var albumDir = Singles.IsSingle(albumName) ? Singles.Folder : Clean(yr > 0 ? $"{albumName} ({yr})" : albumName);
                    foreach (var (path, _, _) in kv.Value)
                    {
                        try
                        {
                            var dest = Path.Combine(root, artistDir, albumDir, Path.GetFileName(path));
                            if (string.Equals(dest, path, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!File.Exists(path)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            int n2 = 2;
                            while (File.Exists(dest))
                                dest = Path.Combine(root, artistDir, albumDir,
                                    Path.GetFileNameWithoutExtension(path) + $" ({n2++})" + Path.GetExtension(path));
                            File.Move(path, dest);
                            moves.Add(new UndoJournal.MoveOp(path, dest));
                        }
                        catch { }
                    }
                }
                CleanEmptyDirs(root, moves.Select(m => Path.GetDirectoryName(m.From)!));
            }
            _undo.RecordBatch($"Doctor: artist unified to '{canonical}'", moves, tags);
            try { _lib.Refresh(root); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                Findings.Remove(f);
                Summary = $"\"{canonical}\": {tags.Count} files retagged, {moves.Count} moved (Cmd+Z = undo).";
                IsBusy = false;
            });
        });
    }

    private void FixLocations(DoctorFinding f)
    {
        if (IsBusy) return;
        var root = _root();
        IsBusy = true;
        Summary = "Moving files into place…";
        Task.Run(() =>
        {
            var done = DoMoves(f.PlannedMoves, root);
            _undo.RecordBatch($"Doctor: {done.Count} files moved into place", done, new List<UndoJournal.TagOp>());
            try { _lib.Refresh(root); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                Findings.Remove(f);
                Summary = $"{done.Count} files moved (Cmd+Z = undo).";
                IsBusy = false;
            });
        });
    }

    private void FixGenre(DoctorFinding f)
    {
        if (IsBusy) return;
        IsBusy = true;
        Summary = $"Retagging genre to \"{f.GenreCanonical}\"…";
        Task.Run(() =>
        {
            var tags = RetagGenre(f);
            _undo.RecordBatch($"Doctor: genre → {f.GenreCanonical}", new List<UndoJournal.MoveOp>(), tags);
            _lib.RefreshConfigured();
            Dispatcher.UIThread.Post(() =>
            {
                Findings.Remove(f);
                Summary = $"{tags.Count} tracks retagged (Cmd+Z = undo).";
                IsBusy = false;
            });
        });
    }

    private void FixAll(string category)
    {
        if (IsBusy) return;
        var list = Findings.Where(x => x.Category == category && x.HasFix).ToList();
        if (list.Count == 0) return;
        var root = _root();
        IsBusy = true;
        Summary = category == "Genres" ? "Retagging all genre variants…" : "Moving all misplaced files…";
        Task.Run(() =>
        {
            var tags = new List<UndoJournal.TagOp>();
            var moves = new List<UndoJournal.MoveOp>();
            int i = 0;
            foreach (var f in list)
            {
                var snap = ++i;
                Dispatcher.UIThread.Post(() => Summary = category == "Genres"
                    ? $"Retagging genres… {snap}/{list.Count}"
                    : $"Moving into place… {snap}/{list.Count}");
                if (category == "Genres") tags.AddRange(RetagGenre(f));
                else moves.AddRange(DoMoves(f.PlannedMoves, root));
            }
            _undo.RecordBatch($"Doctor: fixed all {category.ToLowerInvariant()}", moves, tags);
            try { if (category == "Genres") _lib.RefreshConfigured(); else _lib.Refresh(root); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var f in list) Findings.Remove(f);
                Summary = category == "Genres"
                    ? $"{tags.Count} tracks retagged (Cmd+Z = undo)."
                    : $"{moves.Count} files moved (Cmd+Z = undo).";
                IsBusy = false;
            });
        });
    }

    // ---- Duplicates (part of the checkup) ----
    private static string Quality(IndexedTrack t)
    {
        var q = t.Format;
        if (t.Bitrate > 0) q += $" {t.Bitrate}";
        return q.Length > 0 ? q : "?";
    }

    private DoctorFinding? MakeDupFinding(List<IndexedTrack> copies, string prefix)
    {
        var ordered = copies.OrderByDescending(t => t.Lossless)
                            .ThenByDescending(t => t.Bitrate)
                            .ThenByDescending(t => t.Size).ToList();
        var first = ordered[0];
        var who = first.Artist.Length > 0 ? first.Artist : first.AlbumArtist;
        var title = prefix + $"{(who.Length > 0 ? who : "?")} — {(first.Title.Length > 0 ? first.Title : "?")}";
        var ignoreKey = DupIgnoreKeyOf(title);
        if (_dupIgnores.Contains(ignoreKey)) return null;
        var f = new DoctorFinding
        {
            Category = "Duplicates",
            Title = title,
            Files = ordered.Select(t => t.Path).ToList(),
            FixLabel = "Dedupe",
            DupIgnoreKey = ignoreKey,
        };
        foreach (var t in ordered)
        {
            var label = $"keep {Quality(t)} · {Path.GetFileName(t.Path)}";
            var lbl = label; int n = 2;
            while (f.CopyByLabel.ContainsKey(lbl)) lbl = $"{label} ({n++})";
            f.Spellings.Add(lbl);
            f.CopyByLabel[lbl] = t.Path;
        }
        f.SelectedSpelling = f.Spellings[0];
        f.Sub = $"{ordered.Count} copies · keep {Quality(first)}, others → trash";
        f.FixCommand = new RelayCommand(() => Dedupe(f));
        f.DismissCommand = new RelayCommand(() => DismissDup(f));
        return f;
    }

    private static string DupTrashDir(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(full);
        return parent != null ? Path.Combine(parent, "_Dubbele_verwijderd (Spindle)") : Path.Combine(full, "_Dubbele_verwijderd");
    }

    private static List<UndoJournal.MoveOp> TrashDuplicates(DoctorFinding f, string? keep, string root)
    {
        var moves = new List<UndoJournal.MoveOp>();
        var trash = DupTrashDir(root);
        foreach (var path in f.Files)
        {
            if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!File.Exists(path)) continue;
                Directory.CreateDirectory(trash);
                var dest = Path.Combine(trash, Path.GetFileName(path));
                for (int n = 2; File.Exists(dest); n++)
                    dest = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}");
                File.Move(path, dest);
                moves.Add(new UndoJournal.MoveOp(path, dest));
            }
            catch { }
        }
        return moves;
    }

    private static string? Keeper(DoctorFinding f) =>
        f.SelectedSpelling != null && f.CopyByLabel.TryGetValue(f.SelectedSpelling, out var p) ? p : f.Files.FirstOrDefault();

    private void Dedupe(DoctorFinding f)
    {
        if (IsBusy) return;
        IsBusy = true;
        Summary = "Removing duplicates…";
        var root = _root();
        var keep = Keeper(f);
        Task.Run(() =>
        {
            var moves = TrashDuplicates(f, keep, root);
            _undo.RecordBatch($"Doctor: dedupe {f.Title}", moves, new List<UndoJournal.TagOp>());
            try { _lib.Refresh(root); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                Findings.Remove(f);
                Summary = $"{moves.Count} duplicate file(s) moved to trash (Cmd+Z = undo).";
                IsBusy = false;
            });
        });
    }

    private void DedupeAll()
    {
        if (IsBusy) return;
        var list = Findings.Where(f => f.Category == "Duplicates").ToList();
        if (list.Count == 0) return;
        IsBusy = true;
        Summary = "Removing duplicates…";
        var root = _root();
        Task.Run(() =>
        {
            var moves = new List<UndoJournal.MoveOp>();
            int i = 0;
            foreach (var f in list)
            {
                var snap = ++i;
                Dispatcher.UIThread.Post(() => Summary = $"Deduping… {snap}/{list.Count}");
                moves.AddRange(TrashDuplicates(f, Keeper(f), root));
            }
            _undo.RecordBatch($"Doctor: deduped {list.Count} set(s)", moves, new List<UndoJournal.TagOp>());
            try { _lib.Refresh(root); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var f in list) Findings.Remove(f);
                Summary = $"{moves.Count} duplicate file(s) moved to trash (Cmd+Z = undo).";
                IsBusy = false;
            });
        });
    }

    private void DismissDup(DoctorFinding f)
    {
        if (f.DupIgnoreKey.Length > 0) _dupIgnores.Add(f.DupIgnoreKey);
        Findings.Remove(f);
        Summary = $"Marked as not-a-duplicate — won't be flagged again ({_dupIgnores.Count} remembered).";
        RaiseCmds();
    }

    private List<UndoJournal.TagOp> RetagGenre(DoctorFinding f)
    {
        var tags = new List<UndoJournal.TagOp>();
        foreach (var path in f.Files)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var t = new Track(path);
                tags.Add(Snapshot(t, path));
                t.Genre = f.GenreCanonical;
                t.Save();
            }
            catch { }
        }
        return tags;
    }

    private static List<UndoJournal.MoveOp> DoMoves(List<UndoJournal.MoveOp> planned, string root)
    {
        var done = new List<UndoJournal.MoveOp>();
        foreach (var op in planned)
        {
            try
            {
                if (!File.Exists(op.From)) continue;
                var dest = op.To;
                if (File.Exists(dest))
                {
                    var dir = Path.GetDirectoryName(dest)!;
                    var bare = Path.GetFileNameWithoutExtension(dest);
                    var ext = Path.GetExtension(dest);
                    int n2 = 2;
                    while (File.Exists(dest)) dest = Path.Combine(dir, bare + $" ({n2++})" + ext);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(op.From, dest);
                done.Add(new UndoJournal.MoveOp(op.From, dest));
            }
            catch { }
        }
        if (!string.IsNullOrEmpty(root)) CleanEmptyDirs(root, done.Select(m => Path.GetDirectoryName(m.From)!));
        return done;
    }

    /// <summary>Remove now-empty source folders (and their empty parents) inside the library root.</summary>
    private static void CleanEmptyDirs(string root, IEnumerable<string> dirs)
    {
        string rootFull;
        try { rootFull = Path.GetFullPath(root); } catch { return; }
        foreach (var d0 in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var d = d0;
            try
            {
                while (!string.IsNullOrEmpty(d) && Directory.Exists(d)
                       && Path.GetFullPath(d).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(Path.GetFullPath(d), rootFull, StringComparison.OrdinalIgnoreCase)
                       && !Directory.EnumerateFileSystemEntries(d)
                              .Any(p => !Path.GetFileName(p).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var junk in Directory.GetFiles(d)) File.Delete(junk);
                    Directory.Delete(d);
                    d = Path.GetDirectoryName(d) ?? "";
                }
            }
            catch { }
        }
    }
}
