namespace SeekDownloader.Gui.ViewModels;

/// <summary>A failed file in a batch operation: which file and why.</summary>
public class FailureViewModel
{
    public string Path { get; }
    public string FileName { get; }
    public string Reason { get; }

    public FailureViewModel(string path, string reason)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Reason = string.IsNullOrWhiteSpace(reason) ? "unknown error" : reason.Trim();
    }

    public string Line => $"{FileName} — {Reason}";
}
