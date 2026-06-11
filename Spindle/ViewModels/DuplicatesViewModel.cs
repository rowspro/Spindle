using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Threading;
using ATL;

namespace Spindle.ViewModels;

/// <summary>
/// Finds duplicate tracks and shows the copies side by side with their quality, so you can pick which
/// to keep. First pass: same artist + title (fast, multi-core tag read). Optional second pass: AcoustID
/// acoustic fingerprint over the remaining files (catches the same recording with different/missing
/// tags; needs an AcoustID key, throttled/sequential by rate limit). Non-kept copies move to
/// "_Dubbele_verwijderd" (reversible).
/// </summary>
public class DuplicatesViewModel : ViewModelBase
{
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".aif", ".opus" };
    private const string TrashName = "_Dubbele_verwijderd";

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    private CancellationTokenSource? _cts;

    public DuplicatesViewModel()
    {
        ScanCommand = new RelayCommand(Scan, () => !IsBusy && !string.IsNullOrWhiteSpace(Folder));
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RemoveCommand = new RelayCommand(RemoveUnchosen, () => !IsBusy && Groups.Count > 0);
    }

    private string _folder = string.Empty;
    public string Folder { get => _folder; set { if (SetField(ref _folder, value)) ScanCommand.RaiseCanExecuteChanged(); } }

    private bool _useFingerprint;
    public bool UseFingerprint { get => _useFingerprint; set => SetField(ref _useFingerprint, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _status = "Pick a folder and search for duplicate tracks (same artist + title).";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public RelayCommand ScanCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RemoveCommand { get; }

    private void Scan()
    {
        if (IsBusy) return;
        var folder = Folder;
        if (!Directory.Exists(folder)) { Status = "Map bestaat niet."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var useFp = UseFingerprint;
        var fpKey = Settings.Load().AcoustIdKey ?? string.Empty;
        Status = "Searching for duplicates...";
        Groups.Clear();

        Task.Run(async () =>
        {
            List<string> all;
            try
            {
                all = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant())
                                && !Path.GetFileName(f).StartsWith("._") && !f.Contains(TrashName))
                    .ToList();
            }
            catch (Exception e) { Dispatcher.UIThread.Post(() => { IsBusy = false; Status = "Couldn't read folder: " + e.Message; }); return; }

            int scanned = 0;
            var bag = new ConcurrentBag<(string Key, string File)>();
            try
            {
                Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token }, f =>
                {
                    try
                    {
                        var t = new Track(f);
                        var key = Norm(t.Artist) + "|" + Norm(t.Title);
                        if (key != "|") bag.Add((key, f));
                    }
                    catch { }
                    var s = Interlocked.Increment(ref scanned);
                    if (s % 100 == 0) Dispatcher.UIThread.Post(() => Status = $"Scanning... {s} files");
                });
            }
            catch (OperationCanceledException) { Finish(token, 0); return; }

            var byKey = new Dictionary<string, List<string>>();
            foreach (var (key, file) in bag)
            {
                if (!byKey.TryGetValue(key, out var list)) { list = new List<string>(); byKey[key] = list; }
                list.Add(file);
            }

            var tagDupes = byKey.Where(kv => kv.Value.Count > 1).ToList();
            var grouped = new HashSet<string>(tagDupes.SelectMany(kv => kv.Value));
            Dispatcher.UIThread.Post(() => { foreach (var kv in tagDupes) AddGroup(kv.Value, ""); });

            int acousticSets = 0;
            if (useFp)
            {
                if (string.IsNullOrWhiteSpace(fpKey) || !FingerprintService.Available)
                {
                    Dispatcher.UIThread.Post(() => Status = "Tag pass done. Fingerprint skipped: no AcoustID key (see Metadata tab) or Chromaprint missing.");
                }
                else
                {
                    var remaining = all.Where(f => !grouped.Contains(f)).ToList();
                    var byAid = new Dictionary<string, List<string>>();
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;
                        var snap = i + 1;
                        Dispatcher.UIThread.Post(() => Status = $"Vingerafdruk... {snap}/{remaining.Count}");
                        var aid = await FingerprintService.AcoustIdOf(remaining[i], fpKey);
                        if (aid != null)
                        {
                            if (!byAid.TryGetValue(aid, out var list)) { list = new List<string>(); byAid[aid] = list; }
                            list.Add(remaining[i]);
                        }
                        try { await Task.Delay(350, token); } catch (OperationCanceledException) { break; }
                    }
                    var aidDupes = byAid.Where(kv => kv.Value.Count > 1).ToList();
                    acousticSets = aidDupes.Count;
                    Dispatcher.UIThread.Post(() => { foreach (var kv in aidDupes) AddGroup(kv.Value, "🔊 "); });
                }
            }

            Finish(token, tagDupes.Count, acousticSets, scanned);
        });
    }

    private void Finish(CancellationToken token, int tagSets, int acousticSets = 0, int scanned = 0)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsBusy = false;
            RemoveCommand.RaiseCanExecuteChanged();
            if (token.IsCancellationRequested)
                Status = $"Stopped - {Groups.Count} duplicate set(s) so far.";
            else if (Groups.Count == 0)
                Status = $"No duplicates found ({scanned} files scanned).";
            else
                Status = $"{tagSets} op tags + {acousticSets} op vingerafdruk = {Groups.Count} dubbele set(s). Beste kopie voorgeselecteerd.";
        });
    }

    private void AddGroup(List<string> paths, string prefix)
    {
        Track t0;
        try { t0 = new Track(paths[0]); } catch { return; }
        var title = prefix + $"{(string.IsNullOrWhiteSpace(t0.Artist) ? "?" : t0.Artist)} — {(string.IsNullOrWhiteSpace(t0.Title) ? "?" : t0.Title)}";
        var group = new DuplicateGroupViewModel(title);
        foreach (var path in paths)
            group.Files.Add(new DuplicateFileViewModel(path, group.SelectKeep));
        group.SelectKeep(group.Files.OrderByDescending(x => x.Score).First());
        Groups.Add(group);
    }

    private void RemoveUnchosen()
    {
        if (IsBusy || Groups.Count == 0) return;
        var folder = Folder;
        var trash = Path.Combine(folder, TrashName);
        int moved = 0;
        try
        {
            foreach (var group in Groups)
            {
                if (!group.Files.Any(f => f.Keep)) continue;
                foreach (var f in group.Files.Where(f => !f.Keep))
                {
                    try
                    {
                        Directory.CreateDirectory(trash);
                        var dest = Path.Combine(trash, Path.GetFileName(f.Path));
                        int n = 2;
                        while (File.Exists(dest))
                            dest = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(f.Path)} ({n++}){Path.GetExtension(f.Path)}");
                        File.Move(f.Path, dest);
                        moved++;
                    }
                    catch { }
                }
            }
        }
        catch (Exception e) { Status = "Delete failed: " + e.Message; return; }

        Groups.Clear();
        RemoveCommand.RaiseCanExecuteChanged();
        Status = $"{moved} duplicate file(s) moved to '{TrashName}'. Search again to verify.";
    }

    private static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
}
