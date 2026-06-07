using System;
using Avalonia.Media;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One file queued for ALAC conversion. Click to see full path + failure cause.</summary>
public class ConvertItemViewModel : ViewModelBase
{
    public string SourcePath { get; }
    public string FileName { get; }
    private readonly Action<string> _onShow;

    public ConvertItemViewModel(string sourcePath, Action<string> onShow)
    {
        SourcePath = sourcePath;
        FileName = System.IO.Path.GetFileName(sourcePath);
        _onShow = onShow;
        _status = "Wachtrij";
        ShowCommand = new RelayCommand(() => _onShow(Detail));
    }

    private string _status;
    public string Status
    {
        get => _status;
        set { if (SetField(ref _status, value)) OnPropertyChanged(nameof(StatusBrush)); }
    }

    // Failure reason (afconvert stderr / exception message), shown when the row is clicked.
    public string Error { get; set; } = string.Empty;

    public RelayCommand ShowCommand { get; }

    public string Detail => string.IsNullOrEmpty(Error)
        ? SourcePath
        : $"{SourcePath}\n\nFout: {Error}";

    public IBrush StatusBrush => Status switch
    {
        "Klaar" => new SolidColorBrush(Color.Parse("#2E7D43")),
        "Mislukt" => new SolidColorBrush(Color.Parse("#B23A2E")),
        "Bezig" => new SolidColorBrush(Color.Parse("#3568C4")),
        _ => new SolidColorBrush(Color.Parse("#8A8370")),
    };
}
