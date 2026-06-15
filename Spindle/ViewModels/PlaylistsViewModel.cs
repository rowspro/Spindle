using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Spindle.ViewModels;

/// <summary>One playlist: a name + its tracks (reusing PlayerItem so it plays straight into the queue).</summary>
public sealed class PlaylistVM : ViewModelBase
{
    private string _name;
    public string Name { get => _name; set => SetField(ref _name, value); }
    public ObservableCollection<PlayerItem> Tracks { get; } = new();
    public string Summary => $"{Tracks.Count} track{(Tracks.Count == 1 ? "" : "s")}";
    public void RaiseSummary() => OnPropertyChanged(nameof(Summary));
    public PlaylistVM(string name) { _name = name; }
}

/// <summary>
/// Playlist tool: build playlists from the player queue (queue tracks from the Library, then save),
/// play them back in the app, and export them as Rockbox-friendly .m3u files on the iPod.
/// </summary>
public sealed class PlaylistsViewModel : ViewModelBase
{
    private readonly LibraryService _lib;
    private readonly Func<string> _root;
    private readonly PlayerViewModel _player;
    private readonly Action _persist;
    private Dictionary<string, IndexedTrack>? _map;

    public PlaylistsViewModel(LibraryService lib, Func<string> root, PlayerViewModel player, Action persist)
    {
        _lib = lib; _root = root; _player = player; _persist = persist;
        NewFromQueueCommand = new RelayCommand(NewFromQueue, () => _player.Queue.Count > 0);
        AppendQueueCommand = new RelayCommand(AppendQueue, () => Selected != null && _player.Queue.Count > 0);
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected != null);
        PlayCommand = new RelayCommand(PlaySelected, () => Selected != null && Selected.Tracks.Count > 0);
        RemoveTrackCommand = new RelayCommand(RemoveTrack, () => SelectedTrack != null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => Move(1), () => CanMove(1));
        _player.Queue.CollectionChanged += (_, _) =>
        {
            NewFromQueueCommand.RaiseCanExecuteChanged();
            AppendQueueCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<PlaylistVM> Playlists { get; } = new();

    private PlaylistVM? _selected;
    public PlaylistVM? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            AppendQueueCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            PlayCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasSelection => Selected != null;

    private PlayerItem? _selectedTrack;
    public PlayerItem? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!SetField(ref _selectedTrack, value)) return;
            RemoveTrackCommand.RaiseCanExecuteChanged();
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand NewFromQueueCommand { get; }
    public RelayCommand AppendQueueCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand RemoveTrackCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    private void NewFromQueue()
    {
        var p = new PlaylistVM(UniqueName("New playlist"));
        foreach (var it in _player.Queue) p.Tracks.Add(Clone(it));
        Playlists.Add(p);
        Selected = p;
        p.RaiseSummary();
        _persist();
    }

    private void AppendQueue()
    {
        if (Selected == null) return;
        foreach (var it in _player.Queue) Selected.Tracks.Add(Clone(it));
        Selected.RaiseSummary();
        PlayCommand.RaiseCanExecuteChanged();
        _persist();
    }

    private void DeleteSelected()
    {
        if (Selected == null) return;
        Playlists.Remove(Selected);
        Selected = Playlists.FirstOrDefault();
        _persist();
    }

    private void PlaySelected()
    {
        if (Selected == null || Selected.Tracks.Count == 0) return;
        _player.PlayQueue(Selected.Tracks.ToList(), 0);
    }

    private void RemoveTrack()
    {
        if (Selected == null || SelectedTrack == null) return;
        Selected.Tracks.Remove(SelectedTrack);
        Selected.RaiseSummary();
        PlayCommand.RaiseCanExecuteChanged();
        _persist();
    }

    private bool CanMove(int d)
    {
        if (Selected == null || SelectedTrack == null) return false;
        var i = Selected.Tracks.IndexOf(SelectedTrack);
        var n = i + d;
        return i >= 0 && n >= 0 && n < Selected.Tracks.Count;
    }

    private void Move(int d)
    {
        if (!CanMove(d)) return;
        var i = Selected!.Tracks.IndexOf(SelectedTrack!);
        Selected.Tracks.Move(i, i + d);
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        _persist();
    }

    /// <summary>Persist on rename (called from the view when the name box loses focus).</summary>
    public void Persist() => _persist();

    private static PlayerItem Clone(PlayerItem it) =>
        new() { Path = it.Path, Title = it.Title, Sub = it.Sub, Duration = it.Duration };

    private string UniqueName(string b)
    {
        var n = b; int k = 2;
        while (Playlists.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) n = $"{b} {k++}";
        return n;
    }

    public void Load(List<PlaylistDto>? dtos)
    {
        Playlists.Clear();
        _map = null;
        foreach (var d in dtos ?? new List<PlaylistDto>())
        {
            var p = new PlaylistVM(d.Name);
            foreach (var path in d.Paths) p.Tracks.Add(Resolve(path));
            Playlists.Add(p);
        }
        Selected = Playlists.FirstOrDefault();
    }

    public List<PlaylistDto> Snapshot() =>
        Playlists.Select(p => new PlaylistDto { Name = p.Name, Paths = p.Tracks.Select(t => t.Path).ToList() }).ToList();

    private PlayerItem Resolve(string path)
    {
        EnsureMap();
        if (_map!.TryGetValue(path, out var r))
            return new PlayerItem
            {
                Path = path,
                Title = string.IsNullOrWhiteSpace(r.Title) ? System.IO.Path.GetFileName(path) : r.Title,
                Sub = (r.AlbumArtist.Length > 0 ? r.AlbumArtist : r.Artist) + " — " + r.Album,
                Duration = r.Duration,
            };
        return new PlayerItem { Path = path, Title = System.IO.Path.GetFileName(path), Sub = "" };
    }

    private void EnsureMap()
    {
        if (_map != null) return;
        _map = new Dictionary<string, IndexedTrack>(StringComparer.Ordinal);
        try { foreach (var r in _lib.Index.AllTracks(_root())) _map[r.Path] = r; } catch { }
    }
}
