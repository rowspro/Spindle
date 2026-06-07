using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>Artist group in the sort preview tree.</summary>
public class SortArtistGroupViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<SortAlbumGroupViewModel> Albums { get; } = new();
    public readonly Dictionary<string, SortAlbumGroupViewModel> Index = new();

    public SortArtistGroupViewModel(string name) => Name = name;

    public string Header => Name;
}

/// <summary>Album group inside an artist, holding the individual files.</summary>
public class SortAlbumGroupViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<SortItemViewModel> Files { get; } = new();

    public SortAlbumGroupViewModel(string name) => Name = name;

    public string Header => string.IsNullOrEmpty(Name) ? $"({Files.Count})" : $"{Name}  ({Files.Count})";

    public void Add(SortItemViewModel f)
    {
        Files.Add(f);
        OnPropertyChanged(nameof(Header));
    }
}
