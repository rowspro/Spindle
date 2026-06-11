namespace Spindle.ViewModels;

/// <summary>One row in the Cmd+F command palette: a label + an action to run when picked.</summary>
public class PaletteItem : ViewModelBase
{
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public RelayCommand RunCommand { get; }

    public PaletteItem(string title, string subtitle, string glyph, Action run)
    {
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        RunCommand = new RelayCommand(run);
    }
}
