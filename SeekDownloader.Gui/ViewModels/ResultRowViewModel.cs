using SeekDownloader;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>One selectable search result in the manual download mode.</summary>
public class ResultRowViewModel : ViewModelBase
{
    public SearchResult Source { get; }

    public ResultRowViewModel(SearchResult source)
    {
        Source = source;
    }

    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    public string FileName => Source.FileNameWithExt ?? Source.Filename;

    public string Ext
    {
        get
        {
            var n = FileName;
            var i = n.LastIndexOf('.');
            return i >= 0 ? n[(i + 1)..].ToUpperInvariantSafe() : "";
        }
    }

    private static readonly string[] LosslessExt = { "FLAC", "WAV", "AIFF", "AIF", "ALAC", "APE" };
    /// <summary>true for lossless formats → the badge gets the primary tint, lossy gets the neutral tint.</summary>
    public bool IsLossless => LosslessExt.Contains(Ext);

    public string Meta
    {
        get
        {
            var size = Format.Size(Source.Size);
            var slot = Source.HasFreeUploadSlot ? "  ·  vrije slot" : "";
            return $"{Source.Username}  ·  {size}{slot}";
        }
    }
}

internal static class StringCaseExtensions
{
    public static string ToUpperInvariantSafe(this string s) => string.IsNullOrEmpty(s) ? s : s.ToUpperInvariant();
}

internal static class Format
{
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "–";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1024) return $"{mb / 1024.0:0.0} GB";
        if (mb >= 1) return $"{mb:0.0} MB";
        return $"{bytes / 1024.0:0} KB";
    }
}
