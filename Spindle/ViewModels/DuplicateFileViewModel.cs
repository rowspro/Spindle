using Avalonia.Media;
using ATL;

namespace Spindle.ViewModels;

/// <summary>One copy of a duplicated track, with comparable quality info.</summary>
public class DuplicateFileViewModel : ViewModelBase
{
    public string Path { get; }
    public string FileName { get; }
    public string Album { get; }
    public string Quality { get; }   // "FLAC 24-bit/96 kHz" or "MP3 320 kbps"
    public string SizeText { get; }
    public long Score { get; }       // higher = better quality (for the suggested keep)
    public RelayCommand KeepCommand { get; }

    public DuplicateFileViewModel(string path, Action<DuplicateFileViewModel> onKeep)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);

        long size = 0;
        try { size = new System.IO.FileInfo(path).Length; } catch { }
        SizeText = Format.Size(size);

        try
        {
            var t = new Track(path);
            Album = string.IsNullOrWhiteSpace(t.Album) ? "(no album)" : t.Album + (t.Year > 0 ? $" ({t.Year})" : "");
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            int bitrate = t.Bitrate;
            double sr = t.SampleRate;
            int bd = t.BitDepth;
            bool lossless = ext is "FLAC" or "WAV" or "AIFF" or "AIF" || (ext == "M4A" && bd > 0);

            Quality = lossless && bd > 0
                ? $"{ext} {bd}-bit/{sr / 1000.0:0.#} kHz"
                : $"{ext} {bitrate} kbps";

            Score = (lossless ? 1_000_000_000L : 0)
                    + (long)System.Math.Max(bd, 0) * 10_000_000
                    + (long)sr
                    + (long)bitrate * 1000
                    + size / 100_000;
        }
        catch
        {
            Album = "?";
            Quality = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        }

        KeepCommand = new RelayCommand(() => onKeep(this));
    }

    private bool _keep;
    public bool Keep
    {
        get => _keep;
        set { if (SetField(ref _keep, value)) { OnPropertyChanged(nameof(KeepLabel)); OnPropertyChanged(nameof(KeepBrush)); } }
    }

    public string KeepLabel => Keep ? "✓ Behouden" : "will be removed";
    public IBrush KeepBrush => Keep
        ? new SolidColorBrush(Color.Parse("#2E7D43"))
        : new SolidColorBrush(Color.Parse("#B23A2E"));
}
