using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Spindle.ViewModels;

public sealed class PlayerItem
{
    // Properties (not fields) so the queue list can bind to Title/Sub.
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public int Duration { get; set; }   // seconden (0 = onbekend)
}

/// <summary>
/// Ingebouwde mini-speler. Engine = afplay (macOS): hele tracks, pauze via SIGSTOP/SIGCONT,
/// wachtrij met auto-advance door het album. Op andere platforms meldt hij zich netjes af.
/// </summary>
public sealed class PlayerViewModel : ViewModelBase
{
    public static PlayerViewModel? Current { get; set; }

    private List<PlayerItem> _queue = new();
    private int _idx = -1;
    private Process? _proc;            // de sh-wrapper (Exited = einde nummer)
    private volatile int _afplayPid;   // het echte afplay-proces (pauze/stop)
    private bool _paused;
    private readonly Stopwatch _sw = new();
    private readonly DispatcherTimer _tick;

    public PlayerViewModel()
    {
        PlayPauseCommand = new RelayCommand(PlayPause, () => HasTrack);
        NextCommand = new RelayCommand(Next, () => HasTrack && _idx < _queue.Count - 1);
        PrevCommand = new RelayCommand(Prev, () => HasTrack && _idx > 0);
        StopCommand = new RelayCommand(Stop, () => HasTrack);
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => UpdateTime();
    }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand StopCommand { get; }

    public bool HasTrack => _idx >= 0 && _idx < _queue.Count;
    public string CurrentPath => HasTrack ? _queue[_idx].Path : "";
    public string NowTitle => HasTrack ? _queue[_idx].Title : "";
    public string NowSub => HasTrack ? _queue[_idx].Sub : "";

    /// <summary>The live queue (for the Player tab list). NowIndex marks the playing item.</summary>
    public ObservableCollection<PlayerItem> Queue { get; } = new();
    public int NowIndex => _idx;

    private Bitmap? _nowArt;
    public Bitmap? NowArt { get => _nowArt; private set { if (SetField(ref _nowArt, value)) OnPropertyChanged(nameof(HasArt)); } }
    public bool HasArt => _nowArt != null;

    // Ratings + stats hooks (wired to the library index by MainViewModel).
    public Func<string, int>? RatingOf;
    public Action<string, int>? OnRate;
    public Action<string>? OnPlayed;

    private int _nowRating;
    public int NowRating { get => _nowRating; private set => SetField(ref _nowRating, value); }

    /// <summary>Rate the now-playing track (0 clears). Writes to the index + the file tag.</summary>
    public void SetNowRating(int stars)
    {
        if (!HasTrack) return;
        if (stars == _nowRating) stars = 0;   // click the current star again to clear
        OnRate?.Invoke(CurrentPath, stars);
        NowRating = stars;
    }
    public string PlayPauseIcon => _paused ? "" : ""; // play_arrow : pause

    private double _progress;
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }

    private string _timeText = "";
    public string TimeText { get => _timeText; private set => SetField(ref _timeText, value); }

    public void PlayQueue(List<PlayerItem> items, int start)
    {
        if (items.Count == 0) return;
        _queue = items;
        SyncQueue();
        _idx = Math.Clamp(start, 0, items.Count - 1);
        StartCurrent();
    }

    /// <summary>Append tracks to the queue. Starts playing if nothing is playing yet.</summary>
    public void Enqueue(List<PlayerItem> items)
    {
        if (items == null || items.Count == 0) return;
        bool wasIdle = !HasTrack;
        _queue.AddRange(items);
        SyncQueue();
        if (wasIdle) { _idx = 0; StartCurrent(); }
        else RaiseAll();
    }

    /// <summary>Jump to a specific queue position (click in the Player tab).</summary>
    public void PlayAt(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _idx = index;
        StartCurrent();
    }

    private void SyncQueue()
    {
        Queue.Clear();
        foreach (var it in _queue) Queue.Add(it);
        OnPropertyChanged(nameof(NowIndex));
    }

    private static Bitmap? BitmapFrom(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try { return new Bitmap(new System.IO.MemoryStream(data)); }
        catch { return null; }
    }

    private void LoadArtFor(string path)
    {
        NowArt = null;
        Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                var t = new ATL.Track(path);
                if (t.EmbeddedPictures.Count > 0) bmp = BitmapFrom(t.EmbeddedPictures[0].PictureData);
                if (bmp == null)
                {
                    var dir = System.IO.Path.GetDirectoryName(path) ?? "";
                    foreach (var name in new[] { "folder.jpg", "cover.jpg", "folder.png", "cover.png" })
                    {
                        var p = System.IO.Path.Combine(dir, name);
                        if (System.IO.File.Exists(p)) { try { bmp = new Bitmap(p); } catch { } break; }
                    }
                }
            }
            catch { }
            Dispatcher.UIThread.Post(() => NowArt = bmp);
        });
    }

    private void StartCurrent()
    {
        KillProc();
        _paused = false;
        LoadArtFor(_queue[_idx].Path);
        NowRating = RatingOf?.Invoke(_queue[_idx].Path) ?? 0;
        OnPropertyChanged(nameof(NowIndex));
        try
        {
            // afplay via een sh-watchdog: sterft Spindle (ook bij force quit), dan sterft afplay
            // binnen 2s mee. sh echoot de afplay-pid zodat pauze/stop het juiste proces raken.
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "afplay \"$0\" & A=$!; echo $A; " +
                "(while /bin/kill -0 " + Environment.ProcessId + " 2>/dev/null && /bin/kill -0 $A 2>/dev/null; do /bin/sleep 2; done; /bin/kill -9 $A 2>/dev/null) & " +
                "wait $A");
            psi.ArgumentList.Add(_queue[_idx].Path);
            var proc = Process.Start(psi);
            if (proc == null) { Stop(); return; }
            _proc = proc;
            _afplayPid = 0;
            var stdout = proc.StandardOutput;
            Task.Run(() => { try { if (int.TryParse(stdout.ReadLine(), out var ap)) _afplayPid = ap; } catch { } });
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => Dispatcher.UIThread.Post(() => OnExited(proc));
            _sw.Restart();
            _tick.Start();
        }
        catch
        {
            _proc = null;
            TimeText = "Playback engine unavailable (afplay, macOS only).";
        }
        RaiseAll();
    }

    private void OnExited(Process proc)
    {
        // Alleen het natuurlijke einde van het HUIDIGE proces telt. Een gekild proces is
        // op dat moment al uit _proc gehaald (KillProc nult eerst), dus die exits vallen hier af.
        if (!ReferenceEquals(proc, _proc)) return;
        if (HasTrack) OnPlayed?.Invoke(_queue[_idx].Path);   // a track that finished naturally counts as a play
        if (_idx < _queue.Count - 1) { _idx++; StartCurrent(); }   // natuurlijk einde → volgende
        else Stop();
    }

    public void PlayPause()
    {
        if (!HasTrack || _proc == null || _proc.HasExited || _afplayPid == 0) return;
        try
        {
            Process.Start("/bin/kill", new[] { _paused ? "-CONT" : "-STOP", _afplayPid.ToString() });
            _paused = !_paused;
            if (_paused) { _sw.Stop(); _tick.Stop(); }
            else { _sw.Start(); _tick.Start(); }
        }
        catch { }
        OnPropertyChanged(nameof(PlayPauseIcon));
    }

    public void Next() { if (_idx < _queue.Count - 1) { _idx++; StartCurrent(); } }
    public void Prev() { if (_idx > 0) { _idx--; StartCurrent(); } }

    public void Stop()
    {
        KillProc();
        _idx = -1;
        _queue = new List<PlayerItem>();
        Queue.Clear();
        NowArt = null;
        _sw.Reset();
        _tick.Stop();
        Progress = 0;
        TimeText = "";
        OnPropertyChanged(nameof(NowIndex));
        RaiseAll();
    }

    private void KillProc()
    {
        var p = _proc;
        var ap = _afplayPid;
        _proc = null;       // eerst loskoppelen, dan pas killen — zo kan de Exited-handler nooit meer matchen
        _afplayPid = 0;
        try
        {
            if (ap != 0) Process.Start("/bin/kill", new[] { "-9", ap.ToString() });   // -9 werkt ook op gepauzeerd
            if (p != null && !p.HasExited) p.Kill();
        }
        catch { }
    }

    private void UpdateTime()
    {
        if (!HasTrack) return;
        var el = _sw.Elapsed;
        var dur = _queue[_idx].Duration;
        Progress = dur > 0 ? Math.Min(100, el.TotalSeconds / dur * 100) : 0;
        TimeText = dur > 0
            ? $"{(int)el.TotalMinutes}:{el.Seconds:00} / {dur / 60}:{dur % 60:00}"
            : $"{(int)el.TotalMinutes}:{el.Seconds:00}";
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(NowTitle));
        OnPropertyChanged(nameof(NowSub));
        OnPropertyChanged(nameof(PlayPauseIcon));
        PlayPauseCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        PrevCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        UpdateTime();
    }
}
