using System.Collections.ObjectModel;

namespace Spindle.ViewModels;

/// <summary>A set of files that are the same track (same artist + title, or same fingerprint).</summary>
public class DuplicateGroupViewModel : ViewModelBase
{
    public string Title { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; } = new();
    public RelayCommand NotDuplicateCommand { get; }

    private string _reason = "";
    public string Reason { get => _reason; set => SetField(ref _reason, value); }

    public DuplicateGroupViewModel(string title, Action<DuplicateGroupViewModel> onNotDuplicate)
    {
        Title = title;
        NotDuplicateCommand = new RelayCommand(() => onNotDuplicate(this));
    }

    public void SelectKeep(DuplicateFileViewModel keep)
    {
        foreach (var f in Files) f.Keep = ReferenceEquals(f, keep);
    }
}
