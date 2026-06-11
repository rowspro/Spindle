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

    // Losse kwaliteitsfacetten zodat de groep kan uitleggen wáárom de winnaar wint.
    public bool Lossless { get; }
    public int BitDepth { get; }
    public double SampleRate { get; }
    public int BitrateVal { get; }
    public long SizeVal { get; }

    public RelayCommand KeepCommand { get; }

    public DuplicateFileViewModel(string path, Action<DuplicateFileViewModel> onKeep)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);

        long size = 0;
        try { size = new System.IO.FileInfo(path).Length; } catch { }
        SizeVal = size;
        SizeText = Format.Size(size);

        try
        {
            var t = new Track(path);
            Album = string.IsNullOrWhiteSpace(t.Album) ? "(no album)" : t.Album + (t.Year > 0 ? $" ({t.Year})" : "");
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            BitrateVal = t.Bitrate;
            SampleRate = t.SampleRate;
            BitDepth = t.BitDepth;
            Lossless = ext is "FLAC" or "WAV" or "AIFF" or "AIF" || (ext == "M4A" && BitDepth > 0);

            Quality = Lossless && BitDepth > 0
                ? $"{ext} {BitDepth}-bit/{SampleRate / 1000.0:0.#} kHz"
                : $"{ext} {BitrateVal} kbps";

            Score = (Lossless ? 1_000_000_000L : 0)
                    + (long)System.Math.Max(BitDepth, 0) * 10_000_000
                    + (long)SampleRate
                    + (long)BitrateVal * 1000
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

    public string KeepLabel => Keep ? "✓ keep" : "will be removed";
    public IBrush KeepBrush => Keep
        ? new SolidColorBrush(Color.Parse("#2E7D43"))
        : new SolidColorBrush(Color.Parse("#B23A2E"));
}
