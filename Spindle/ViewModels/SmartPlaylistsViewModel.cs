using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Spindle.ViewModels;

public sealed class SmartRuleVM : ViewModelBase
{
    public static readonly string[] AllFields =
        { "Genre", "Artist", "Album", "Title", "Year", "Rating", "Plays", "Format", "Lossless", "Added (days)" };

    public string[] Fields => AllFields;

    private string _field = "Genre";
    public string Field
    {
        get => _field;
        set
        {
            if (!SetField(ref _field, value)) return;
            OnPropertyChanged(nameof(Ops));
            if (!Ops.Contains(Op)) Op = Ops[0];
            OnPropertyChanged(nameof(IsGenre));
            OnPropertyChanged(nameof(IsText));
        }
    }

    // Genre picks from the standard list (dropdown); other fields are free text.
    public bool IsGenre => Field == "Genre";
    public bool IsText => Field != "Genre";
    public IReadOnlyList<string> GenreOptions => Spindle.Genres.Standard;

    public string[] Ops => Field switch
    {
        "Year" or "Rating" or "Plays" or "Added (days)" => new[] { "≥", "≤", "is" },
        "Format" or "Lossless" => new[] { "is" },
        _ => new[] { "contains", "is" },
    };

    private string _op = "contains";
    public string Op { get => _op; set => SetField(ref _op, value); }

    private string _value = "";
    public string Value { get => _value; set => SetField(ref _value, value); }
}

public sealed class SmartPlaylistVM : ViewModelBase
{
    private string _name;
    public SmartPlaylistVM(string name) { _name = name; }
    public string Name { get => _name; set => SetField(ref _name, value); }

    private bool _matchAll = true;
    public bool MatchAll { get => _matchAll; set { if (SetField(ref _matchAll, value)) RaiseSummary(); } }

    public ObservableCollection<SmartRuleVM> Rules { get; } = new();

    private int _count;
    public int Count { get => _count; set { if (SetField(ref _count, value)) RaiseSummary(); } }

    public string Summary => $"{Count} tracks · match {(MatchAll ? "all" : "any")} of {Rules.Count} rule(s)";
    public void RaiseSummary() => OnPropertyChanged(nameof(Summary));
}

/// <summary>Rule-based playlists, evaluated live against the index (genre/year/rating/plays/format/added).</summary>
public sealed class SmartPlaylistsViewModel : ViewModelBase
{
    private readonly LibraryService _lib;
    private readonly Func<string> _root;
    private readonly PlayerViewModel _player;
    private readonly Action _persist;

    public SmartPlaylistsViewModel(LibraryService lib, Func<string> root, PlayerViewModel player, Action persist)
    {
        _lib = lib; _root = root; _player = player; _persist = persist;
        NewCommand = new RelayCommand(NewPlaylist);
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected != null);
        AddRuleCommand = new RelayCommand(AddRule, () => Selected != null);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => Selected != null && SelectedRule != null);
        PlayCommand = new RelayCommand(PlaySelected, () => Selected != null);
        RefreshCommand = new RelayCommand(() => Recount(Selected), () => Selected != null);
    }

    public ObservableCollection<SmartPlaylistVM> Playlists { get; } = new();

    private SmartPlaylistVM? _selected;
    public SmartPlaylistVM? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            DeleteCommand.RaiseCanExecuteChanged();
            AddRuleCommand.RaiseCanExecuteChanged();
            PlayCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            Recount(value);
        }
    }
    public bool HasSelection => Selected != null;

    private SmartRuleVM? _selectedRule;
    public SmartRuleVM? SelectedRule { get => _selectedRule; set { if (SetField(ref _selectedRule, value)) RemoveRuleCommand.RaiseCanExecuteChanged(); } }

    public RelayCommand NewCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand RefreshCommand { get; }

    private void NewPlaylist()
    {
        var p = new SmartPlaylistVM(UniqueName("Smart playlist"));
        p.Rules.Add(new SmartRuleVM { Field = "Rating", Op = "≥", Value = "4" });
        Playlists.Add(p);
        Selected = p;
        _persist();
    }

    private void DeleteSelected()
    {
        if (Selected == null) return;
        Playlists.Remove(Selected);
        Selected = Playlists.FirstOrDefault();
        _persist();
    }

    private void AddRule()
    {
        if (Selected == null) return;
        Selected.Rules.Add(new SmartRuleVM());
        Selected.RaiseSummary();
        Recount(Selected);
        _persist();
    }

    private void RemoveRule()
    {
        if (Selected == null || SelectedRule == null) return;
        Selected.Rules.Remove(SelectedRule);
        Selected.RaiseSummary();
        Recount(Selected);
        _persist();
    }

    private void PlaySelected()
    {
        if (Selected == null) return;
        var items = Evaluate(Selected);
        if (items.Count > 0) _player.PlayQueue(items, 0);
    }

    /// <summary>Evaluate a smart playlist against the index → ordered PlayerItems.</summary>
    public List<PlayerItem> Evaluate(SmartPlaylistVM pl)
    {
        var root = _root();
        if (string.IsNullOrWhiteSpace(root)) return new List<PlayerItem>();
        List<IndexedTrack> tracks;
        try { tracks = _lib.Index.AllTracks(root); } catch { return new List<PlayerItem>(); }
        var stats = _lib.Index.AllStats();
        var rules = pl.Rules.ToList();
        var nowTicks = DateTime.UtcNow.Ticks;

        bool Hit(IndexedTrack t)
        {
            if (rules.Count == 0) return true;
            var st = stats.TryGetValue(t.Path, out var s) ? s : (0, 0, 0L);
            bool MatchRule(SmartRuleVM r)
            {
                var v = (r.Value ?? "").Trim();
                switch (r.Field)
                {
                    case "Genre": return Text(t.Genre, r.Op, v);
                    case "Artist": return Text(t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.Artist, r.Op, v) || Text(t.Artist, r.Op, v);
                    case "Album": return Text(t.Album, r.Op, v);
                    case "Title": return Text(t.Title, r.Op, v);
                    case "Year": return Num(t.Year, r.Op, v);
                    case "Rating": return Num(st.Item1, r.Op, v);
                    case "Plays": return Num(st.Item2, r.Op, v);
                    case "Format": return string.Equals(t.Format, v, StringComparison.OrdinalIgnoreCase);
                    case "Lossless": return t.Lossless == (v.ToLowerInvariant() is "yes" or "true" or "1" or "ja");
                    case "Added (days)":
                        if (!int.TryParse(v, out var days) || days <= 0) return true;
                        var ageDays = (nowTicks - t.Mtime) / (double)TimeSpan.TicksPerDay;
                        return ageDays <= days;
                    default: return true;
                }
            }
            return pl.MatchAll ? rules.All(MatchRule) : rules.Any(MatchRule);
        }

        return tracks.Where(Hit)
            .OrderBy(t => t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Disc).ThenBy(t => t.TrackNo)
            .Select(t => new PlayerItem
            {
                Path = t.Path,
                Title = string.IsNullOrWhiteSpace(t.Title) ? System.IO.Path.GetFileName(t.Path) : t.Title,
                Sub = (t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.Artist) + " — " + t.Album,
                Duration = t.Duration,
            }).ToList();
    }

    public void Recount(SmartPlaylistVM? pl) { if (pl != null) pl.Count = Evaluate(pl).Count; }

    private static bool Text(string field, string op, string v) =>
        op == "is" ? string.Equals(field ?? "", v, StringComparison.OrdinalIgnoreCase)
                   : (field ?? "").Contains(v, StringComparison.OrdinalIgnoreCase);

    private static bool Num(int x, string op, string v) =>
        int.TryParse(v, out var n) && (op == "≥" ? x >= n : op == "≤" ? x <= n : x == n);

    private string UniqueName(string b)
    {
        var n = b; int k = 2;
        while (Playlists.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) n = $"{b} {k++}";
        return n;
    }

    public void Persist() => _persist();

    public void Load(List<SmartPlaylistDto>? dtos)
    {
        Playlists.Clear();
        foreach (var d in dtos ?? new List<SmartPlaylistDto>())
        {
            var p = new SmartPlaylistVM(d.Name) { MatchAll = d.MatchAll };
            foreach (var r in d.Rules) p.Rules.Add(new SmartRuleVM { Field = r.Field, Op = r.Op, Value = r.Value });
            Playlists.Add(p);
        }
        Selected = Playlists.FirstOrDefault();
    }

    public List<SmartPlaylistDto> Snapshot() =>
        Playlists.Select(p => new SmartPlaylistDto
        {
            Name = p.Name,
            MatchAll = p.MatchAll,
            Rules = p.Rules.Select(r => new SmartRuleDto { Field = r.Field, Op = r.Op, Value = r.Value }).ToList(),
        }).ToList();
}
