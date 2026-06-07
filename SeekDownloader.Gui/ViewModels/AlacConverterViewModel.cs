using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia.Threading;

namespace SeekDownloader.Gui.ViewModels;

/// <summary>
/// Converts a source folder of audio (e.g. FLAC) to Apple Lossless (ALAC, .m4a) into a destination
/// folder, mirroring structure and SKIPPING files already present in the destination. Runs several
/// conversions in parallel, can be stopped, and is interrupt-safe (writes to .part, renames on success).
/// </summary>
public class AlacConverterViewModel : ViewModelBase
{
    private static readonly string[] Convertible = { ".flac", ".wav", ".aiff", ".aif", ".mp3", ".m4a", ".alac" };

    public ObservableCollection<ConvertItemViewModel> Items { get; } = new();

    private CancellationTokenSource? _cts;

    public AlacConverterViewModel()
    {
        ConvertCommand = new RelayCommand(Convert,
            () => !IsConverting && !string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(OutputFolder));
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsConverting);
    }

    private string _sourceFolder = string.Empty;
    public string SourceFolder
    {
        get => _sourceFolder;
        set { if (SetField(ref _sourceFolder, value)) ConvertCommand.RaiseCanExecuteChanged(); }
    }

    private string _outputFolder = string.Empty;
    public string OutputFolder
    {
        get => _outputFolder;
        set { if (SetField(ref _outputFolder, value)) ConvertCommand.RaiseCanExecuteChanged(); }
    }

    private bool _mirrorStructure = true;
    public bool MirrorStructure { get => _mirrorStructure; set => SetField(ref _mirrorStructure, value); }

    private bool _deleteOriginals;
    public bool DeleteOriginals { get => _deleteOriginals; set => SetField(ref _deleteOriginals, value); }

    private bool _ipodCompatible = true;
    public bool IpodCompatible { get => _ipodCompatible; set => SetField(ref _ipodCompatible, value); }

    private decimal _concurrency = 6;
    public decimal Concurrency { get => _concurrency; set => SetField(ref _concurrency, value); }

    private bool _isConverting;
    public bool IsConverting
    {
        get => _isConverting;
        private set
        {
            if (SetField(ref _isConverting, value))
            {
                OnPropertyChanged(nameof(IsNotConverting));
                ConvertCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsNotConverting => !IsConverting;

    private string _status = "Kies een bronmap (muziek) en een doelmap (iPod). Al aanwezige bestanden worden overgeslagen.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _detail = string.Empty;
    public string Detail { get => _detail; private set => SetField(ref _detail, value); }
    private void ShowDetail(string d) => Detail = d;

    public RelayCommand ConvertCommand { get; }
    public RelayCommand StopCommand { get; }

    private void Convert()
    {
        if (IsConverting) return;
        var src = SourceFolder;
        var outDir = OutputFolder;
        if (!Directory.Exists(src)) { Status = "Bronmap bestaat niet."; return; }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                .Where(f => Convertible.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();
        }
        catch (Exception e) { Status = "Kon bronmap niet lezen: " + e.Message; return; }

        Items.Clear();
        foreach (var f in files) Items.Add(new ConvertItemViewModel(f, ShowDetail));
        if (Items.Count == 0) { Status = "Geen converteerbare bestanden in de bronmap."; return; }

        IsConverting = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var items = Items.ToList();
        var mirror = MirrorStructure;
        var delete = DeleteOriginals;
        var ipod = IpodCompatible;
        var dop = System.Math.Max(1, (int)Concurrency);

        Task.Run(() =>
        {
            int done = 0, skipped = 0, failed = 0, processed = 0;
            try
            {
                Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = token }, item =>
                {
                    token.ThrowIfCancellationRequested();
                    Dispatcher.UIThread.Post(() => item.Status = "Bezig");
                    try
                    {
                        var rel = mirror ? Path.GetRelativePath(src, item.SourcePath) : Path.GetFileName(item.SourcePath);
                        var dst = Path.Combine(outDir, Path.ChangeExtension(rel, ".m4a"));

                        if (File.Exists(dst))
                        {
                            Interlocked.Increment(ref skipped);
                            Dispatcher.UIThread.Post(() => item.Status = "Overgeslagen");
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            var part = dst + ".part";
                            try
                            {
                                if (AudioConvert.Encode(item.SourcePath, part, ipod, token, out var encErr) && File.Exists(part))
                                {
                                    AudioConvert.CopyTags(item.SourcePath, part);
                                    File.Move(part, dst, true); // atomic publish: dst only appears when complete
                                    if (delete) { try { File.Delete(item.SourcePath); } catch { } }
                                    Interlocked.Increment(ref done);
                                    Dispatcher.UIThread.Post(() => item.Status = "Klaar");
                                }
                                else
                                {
                                    Interlocked.Increment(ref failed);
                                    if (!token.IsCancellationRequested) item.Error = encErr ?? "geen uitvoerbestand";
                                    Dispatcher.UIThread.Post(() => item.Status = token.IsCancellationRequested ? "Onderbroken" : "Mislukt");
                                }
                            }
                            finally
                            {
                                try { if (File.Exists(part)) File.Delete(part); } catch { }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        item.Error = ex.Message;
                        Dispatcher.UIThread.Post(() => item.Status = "Mislukt");
                    }
                    var p = Interlocked.Increment(ref processed);
                    Dispatcher.UIThread.Post(() => Status = $"Converteren… {p}/{items.Count}  ({dop} tegelijk)");
                });
            }
            catch (OperationCanceledException) { }

            Dispatcher.UIThread.Post(() =>
            {
                IsConverting = false;
                Status = token.IsCancellationRequested
                    ? $"Gestopt — {done} geconverteerd, {skipped} overgeslagen. Start opnieuw om te hervatten."
                    : $"Klaar — {done} geconverteerd, {skipped} overgeslagen (al aanwezig), {failed} mislukt.";
            });
        });
    }

}
