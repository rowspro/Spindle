using Avalonia.Media;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One file in the sort tool: source, target and outcome. Click to see full path + cause.</summary>
public class SortItemViewModel : ViewModelBase
{
    public string FileName { get; }
    public string SourcePath { get; }
    public string Target { get; }
    public string Status { get; }
    public string Error { get; }
    public RelayCommand ShowCommand { get; }

    public SortItemViewModel(string fileName, string sourcePath, string target, string status, string error, Action<string> onShow)
    {
        FileName = fileName;
        SourcePath = sourcePath;
        Target = target;
        Status = status;
        Error = error;
        ShowCommand = new RelayCommand(() => onShow(Detail));
    }

    public bool IsError => Status is "Mislukt" or "fout";

    public string Detail => string.IsNullOrEmpty(Error)
        ? $"{SourcePath}\n→ {Target}"
        : $"{SourcePath}\n\nFout: {Error}";

    public IBrush StatusBrush => Status switch
    {
        "Verplaatst" => new SolidColorBrush(Color.Parse("#2E7D43")),
        "Mislukt" or "fout" => new SolidColorBrush(Color.Parse("#B23A2E")),
        "Geen tags" => new SolidColorBrush(Color.Parse("#B7791F")),
        _ => new SolidColorBrush(Color.Parse("#8A8370")),
    };
}
