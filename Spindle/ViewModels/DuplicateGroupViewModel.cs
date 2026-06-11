using System.Collections.ObjectModel;

namespace Spindle.ViewModels;

/// <summary>A set of files that are the same track (same artist + title).</summary>
public class DuplicateGroupViewModel : ViewModelBase
{
    public string Title { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; } = new();

    public DuplicateGroupViewModel(string title) => Title = title;

    public void SelectKeep(DuplicateFileViewModel keep)
    {
        foreach (var f in Files) f.Keep = ReferenceEquals(f, keep);
    }
}
