using System.Diagnostics;
using Avalonia.Threading;

namespace Spindle.ViewModels;

public sealed class PlayerItem
{
    public string Path = "";
    public string Title = "";
    public string Sub = "";
    public int Duration;   // seconden (0 = onbekend)
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
    public string PlayPauseIcon => _paused ? "" : ""; // play_arrow : pause

    private double _progress;
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }

    private string _timeText = "";
    public string TimeText { get => _timeText; private set => SetField(ref _timeText, value); }

    public void PlayQueue(List<PlayerItem> items, int start)
    {
        if (items.Count == 0) return;
        _queue = items;
        _idx = Math.Clamp(start, 0, items.Count - 1);
        StartCurrent();
    }

    private void StartCurrent()
    {
        KillProc();
        _paused = false;
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
        _sw.Reset();
        _tick.Stop();
        Progress = 0;
        TimeText = "";
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
