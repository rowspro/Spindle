using Avalonia.Media;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>A most-played Apple Music artist; click to search/download it.</summary>
public class ArtistStatViewModel : ViewModelBase
{
    public string Name { get; }
    public int Plays { get; }
    public bool InLibrary { get; }
    public RelayCommand PickCommand { get; }

    public ArtistStatViewModel(string name, int plays, bool inLibrary, Action<string> onPick)
    {
        Name = name;
        Plays = plays;
        InLibrary = inLibrary;
        PickCommand = new RelayCommand(() => onPick(name));
    }

    public string Summary => InLibrary
        ? $"{Plays}× afgespeeld  ·  ✓ in bibliotheek"
        : $"{Plays}× afgespeeld  ·  niet gedownload";

    public IBrush StatusBrush => InLibrary
        ? new SolidColorBrush(Color.Parse("#2E7D43"))
        : new SolidColorBrush(Color.Parse("#B7791F"));
}
