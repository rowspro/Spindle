using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Spindle.ViewModels;

/// <summary>An album on the wantlist — the shopping list for Nicotine+.</summary>
public sealed class WantAlbumViewModel : ViewModelBase
{
    public string Artist { get; }
    public string Album { get; }
    public string Year { get; }
    public string Display => Album + (Year.Length > 0 ? $" ({Year})" : "");
    public string SearchTerm => $"{Artist} {Album}";
    public RelayCommand RemoveCommand { get; }

    public WantAlbumViewModel(string artist, string album, string year, Action<WantAlbumViewModel> onRemove)
    {
        Artist = artist; Album = album; Year = year;
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }
}

/// <summary>One album row in a followed artist's discography.</summary>
public sealed class DiscoAlbumViewModel : ViewModelBase
{
    public string Artist { get; }
    public string Title { get; }
    public string Year { get; }
    public string Display => Title + (Year.Length > 0 ? $" ({Year})" : "");
    public string SearchTerm => $"{Artist} {Title}";

    private bool _owned;
    public bool Owned { get => _owned; set { if (SetField(ref _owned, value)) OnPropertyChanged(nameof(NotOwned)); } }
    public bool NotOwned => !_owned;

    private bool _wanted;
    public bool Wanted { get => _wanted; set { if (SetField(ref _wanted, value)) OnPropertyChanged(nameof(WantLabel)); } }
    public string WantLabel => _wanted ? "On wantlist ✓" : "+ Want";

    public RelayCommand WantCommand { get; }

    public DiscoAlbumViewModel(string artist, string title, string year, bool owned, bool wanted,
        Action<DiscoAlbumViewModel> onToggle)
    {
        Artist = artist; Title = title; Year = year; _owned = owned; _wanted = wanted;
        WantCommand = new RelayCommand(() => onToggle(this));
    }
}

/// <summary>A followed artist with their official discography (MusicBrainz) vs the library.</summary>
public sealed class FollowedArtistViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<DiscoAlbumViewModel> Albums { get; } = new();

    private string _progress = "Not fetched yet — press the refresh arrow.";
    public string Progress { get => _progress; set => SetField(ref _progress, value); }

    private double _ownedPercent;
    public double OwnedPercent { get => _ownedPercent; private set => SetField(ref _ownedPercent, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UnfollowCommand { get; }

    public FollowedArtistViewModel(string name, Action<FollowedArtistViewModel> refresh, Action<FollowedArtistViewModel> unfollow)
    {
        Name = name;
        RefreshCommand = new RelayCommand(() => refresh(this));
        UnfollowCommand = new RelayCommand(() => unfollow(this));
    }

    public void UpdateProgress()
    {
        int owned = Albums.Count(a => a.Owned);
        Progress = Albums.Count == 0
            ? "No official albums found."
            : $"{owned} of {Albums.Count} albums in your library or downloads";
        OwnedPercent = Albums.Count == 0 ? 0 : 100.0 * owned / Albums.Count;
    }
}

/// <summary>
/// Wantlist: follow artists, compare their official discography (MusicBrainz) against the index,
/// keep a shopping list for Nicotine+. Wanted albums tick themselves off the moment the index
/// sees them in the library (so approving them in the Inbox is enough).
/// </summary>
public sealed class WantlistViewModel : ViewModelBase
{
    private readonly LibraryService _lib;
    private readonly Func<string> _root;
    private readonly Func<string> _inbox;
    private bool _autoFetched;

    public WantlistViewModel(LibraryService lib, Func<string> root, Func<string> inbox)
    {
        _lib = lib; _root = root; _inbox = inbox;
        FollowCommand = new RelayCommand(Follow, () => !string.IsNullOrWhiteSpace(FollowName));
        RefreshAllCommand = new RelayCommand(() => _ = RefreshAllAsync(), () => Artists.Count > 0 && !IsBusy);
    }

    public ObservableCollection<FollowedArtistViewModel> Artists { get; } = new();
    public ObservableCollection<WantAlbumViewModel> Wanted { get; } = new();
    public RelayCommand FollowCommand { get; }
    public RelayCommand RefreshAllCommand { get; }

    private string _followName = "";
    public string FollowName { get => _followName; set { if (SetField(ref _followName, value)) FollowCommand.RaiseCanExecuteChanged(); } }

    private string _status = "Follow an artist to see which albums you're missing.";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private bool _busy;
    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) RefreshAllCommand.RaiseCanExecuteChanged(); } }

    public int WantedCount => Wanted.Count;
    public bool HasWanted => Wanted.Count > 0;
    public bool HasArtists => Artists.Count > 0;
    private void RaiseWanted() { OnPropertyChanged(nameof(WantedCount)); OnPropertyChanged(nameof(HasWanted)); }
    public void Notify(string msg) => Status = msg;

    private static string Norm(string? s) =>
        System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
    private static string Key(string artist, string album) => Norm(artist) + "|" + Norm(album);

    // "Have it" = already in the library OR already sitting in the downloads/inbox folder, so a
    // wanted album ticks off the moment it's downloaded — no need to approve it first.
    private HashSet<string> BuildOwned()
    {
        var set = new HashSet<string>();
        AddAlbums(set, _root());
        AddAlbums(set, _inbox());
        return set;
    }

    private void AddAlbums(HashSet<string> set, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try { foreach (var a in _lib.Index.Albums(root)) set.Add(Key(a.AlbumArtist, a.Album)); } catch { }
    }

    // ---- persistentie (SpindleConfig.Watchlist + SpindleConfig.Wantlist) ----
    public void Load(List<string> watch, List<string> want)
    {
        Artists.Clear();
        Wanted.Clear();
        foreach (var n in watch.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            Artists.Add(MakeArtist(n));
        foreach (var e in want)
        {
            var parts = e.Split('|', 3);   // artist|year|album (album mag '|' bevatten)
            if (parts.Length == 3) Wanted.Add(MakeWant(parts[0], parts[2], parts[1]));
        }
        RaiseWanted();
        OnPropertyChanged(nameof(HasArtists));
        RefreshAllCommand.RaiseCanExecuteChanged();
    }

    public List<string> WatchNames() => Artists.Select(a => a.Name).ToList();
    public List<string> WantEntries() => Wanted.Select(w => $"{w.Artist}|{w.Year}|{w.Album}").ToList();

    private FollowedArtistViewModel MakeArtist(string name) =>
        new(name, fa => _ = FetchAsync(fa),
            fa => { Artists.Remove(fa); OnPropertyChanged(nameof(HasArtists)); RefreshAllCommand.RaiseCanExecuteChanged(); });

    private WantAlbumViewModel MakeWant(string artist, string album, string year) =>
        new(artist, album, year, w => { Wanted.Remove(w); SyncWantFlags(); RaiseWanted(); });

    private void Follow()
    {
        var name = FollowName.Trim();
        if (name.Length == 0) return;
        if (Artists.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"Already following {name}.";
            return;
        }
        var fa = MakeArtist(name);
        Artists.Insert(0, fa);   // nieuwste bovenaan
        FollowName = "";
        OnPropertyChanged(nameof(HasArtists));
        RefreshAllCommand.RaiseCanExecuteChanged();
        _ = FetchAsync(fa);
    }

    private async Task FetchAsync(FollowedArtistViewModel fa)
    {
        fa.Progress = "Looking up the discography…";
        List<MbAlbum> albums;
        try { albums = await MusicBrainzClient.GetOfficialAlbumsAsync(fa.Name); }
        catch { albums = new List<MbAlbum>(); }
        var have = BuildOwned();
        Dispatcher.UIThread.Post(() =>
        {
            fa.Albums.Clear();
            foreach (var al in albums)
                fa.Albums.Add(new DiscoAlbumViewModel(fa.Name, al.Title, al.Year,
                    have.Contains(Key(fa.Name, al.Title)),
                    Wanted.Any(w => Key(w.Artist, w.Album) == Key(fa.Name, al.Title)),
                    ToggleWant));
            fa.UpdateProgress();
            Status = albums.Count == 0
                ? $"No official albums found for \"{fa.Name}\" — check the spelling (MusicBrainz)."
                : $"{fa.Name}: {fa.Albums.Count(a => a.Owned)}/{fa.Albums.Count} albums in your library or downloads.";
        });
    }

    private void ToggleWant(DiscoAlbumViewModel row)
    {
        var k = Key(row.Artist, row.Title);
        var existing = Wanted.FirstOrDefault(w => Key(w.Artist, w.Album) == k);
        if (existing != null)
        {
            Wanted.Remove(existing);
            row.Wanted = false;
        }
        else
        {
            Wanted.Add(MakeWant(row.Artist, row.Title, row.Year));   // achteraan: oudste wens blijft bovenaan
            row.Wanted = true;
            Status = $"On the wantlist: {row.Artist} — {row.Title}. Copy the search and paste it into Nicotine+.";
        }
        RaiseWanted();
    }

    private void SyncWantFlags()
    {
        var keys = Wanted.Select(w => Key(w.Artist, w.Album)).ToHashSet();
        foreach (var fa in Artists)
            foreach (var r in fa.Albums)
                r.Wanted = keys.Contains(Key(r.Artist, r.Title));
    }

    /// <summary>Re-check ownership against the index; auto-tick wanted albums that arrived.</summary>
    public void RefreshOwned()
    {
        var have = BuildOwned();
        foreach (var fa in Artists)
        {
            foreach (var r in fa.Albums) r.Owned = have.Contains(Key(r.Artist, r.Title));
            fa.UpdateProgress();
        }
        var got = Wanted.Where(w => have.Contains(Key(w.Artist, w.Album))).ToList();
        if (got.Count > 0)
        {
            foreach (var w in got) Wanted.Remove(w);
            SyncWantFlags();
            RaiseWanted();
            Status = "Landed ✓ (in your library or downloads)  " + string.Join(" · ", got.Select(w => $"{w.Artist} — {w.Album}"));
        }
    }

    /// <summary>Called when the tab opens: refresh ownership, fetch discographies once per session.</summary>
    public void OpenedTab()
    {
        RefreshOwned();
        if (!_autoFetched && Artists.Count > 0)
        {
            _autoFetched = true;
            _ = RefreshAllAsync();
        }
    }

    private async Task RefreshAllAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        foreach (var fa in Artists.ToList())
        {
            await FetchAsync(fa);
            try { await Task.Delay(500); } catch { }   // MusicBrainz rate limit
        }
        IsBusy = false;
    }
}
