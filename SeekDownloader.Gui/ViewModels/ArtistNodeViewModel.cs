using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>Artist node in the transfer tree: groups its albums with a tri-state "select whole artist".</summary>
public class ArtistNodeViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<AlbumEntryViewModel> Albums { get; } = new();
    private bool _updating;

    public ArtistNodeViewModel(string name, IEnumerable<AlbumEntryViewModel> albums)
    {
        Name = name;
        foreach (var a in albums)
        {
            Albums.Add(a);
            a.PropertyChanged += OnAlbumChanged;
        }
        Recompute();
    }

    // Stop reacting to albums when this node is discarded (the tree is rebuilt on every filter change).
    public void Detach()
    {
        foreach (var a in Albums) a.PropertyChanged -= OnAlbumChanged;
    }

    private void OnAlbumChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumEntryViewModel.Selected) || e.PropertyName == nameof(AlbumEntryViewModel.OnIpod))
            Recompute();
    }

    private bool? _isChecked = false;
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_updating) return;
            var target = value == true;
            _updating = true;
            foreach (var a in Albums) a.Selected = target;
            _updating = false;
            if (_isChecked != target) { _isChecked = target; OnPropertyChanged(); }
            OnPropertyChanged(nameof(Summary));
        }
    }

    private void Recompute()
    {
        if (_updating) return;
        int sel = Albums.Count(a => a.Selected);
        bool? v = sel == 0 ? false : sel == Albums.Count ? true : (bool?)null;
        if (_isChecked != v) { _isChecked = v; OnPropertyChanged(nameof(IsChecked)); }
        OnPropertyChanged(nameof(Summary));
    }

    public string Summary
    {
        get
        {
            int onIpod = Albums.Count(a => a.OnIpod > 0);
            return onIpod > 0 ? $"{Albums.Count} albums · {onIpod} op iPod" : $"{Albums.Count} albums";
        }
    }
}
