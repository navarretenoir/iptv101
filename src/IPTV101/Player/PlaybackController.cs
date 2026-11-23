using System.Windows.Threading;
using IPTV101.Channels;

namespace IPTV101.Player;

public enum PlayState { Idle, Connecting, Playing, Buffering, Paused, Reconnecting, Failed, Stopped }

/// <summary>
/// Estado de reproducción del canal actual: conexión, búfer, reconexión automática con espera creciente
/// y vigilancia de transmisiones congeladas.
/// </summary>
public sealed class PlaybackController
{
    private static readonly int[] RetryNeverPlayed = { 2, 5 };
    private static readonly int[] RetryAfterPlaying = { 1, 3, 5, 8, 12, 15, 15, 20, 20, 30 };
    private const int ConnectTimeoutSeconds = 30;
    private const int StallSeconds = 20;

    private readonly MpvPlayer _player;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _connectTimer;
    private readonly Timer _watchdog;
    private int _attempt;
    private bool _hadPlayed;
    private double _lastPos = double.NaN;
    private DateTime _lastProgress = DateTime.UtcNow;
    private int _watchBusy;
    private bool _userPaused;

    public Channel? Current { get; private set; }
    public PlayState State { get; private set; } = PlayState.Idle;
    public string Detail { get; private set; } = "";
    public long BufferPercent { get; private set; }
    public PlayerStats? LastStats { get; private set; }
    public bool AudioOnly { get; private set; }

    public event Action? StateChanged;
    public event Action<PlayerStats>? StatsUpdated;

    public PlaybackController(MpvPlayer player, Dispatcher ui)
    {
        _player = player;
        _retryTimer = new DispatcherTimer(DispatcherPriority.Normal, ui);
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); Reload(); };
        _connectTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(ConnectTimeoutSeconds) };
        _connectTimer.Tick += (_, _) => { _connectTimer.Stop(); if (State is PlayState.Connecting or PlayState.Reconnecting) Fail("El canal no respondió a tiempo."); };

        _player.FileStarted += () => { };
        _player.PlaybackStarted += OnPlaybackStarted;
        _player.FileEnded += OnFileEnded;
        _player.BufferingChanged += OnBuffering;
        _player.BufferPercentChanged += p => { BufferPercent = p; if (State == PlayState.Buffering) Raise(); };
        _player.PauseChanged += p =>
        {
            if (p && State is PlayState.Playing or PlayState.Buffering) Set(PlayState.Paused);
            else if (!p && State == PlayState.Paused) Set(PlayState.Playing);
        };

        _watchdog = new Timer(_ => Watch(ui), null, 2000, 2000);
    }

    public void Play(Channel c)
    {
        Current = c;
        _attempt = 0;
        _hadPlayed = false;
        _userPaused = false;
        AudioOnly = false;
        _retryTimer.Stop();
        Detail = "";
        StartLoad(PlayState.Connecting);
    }

    /// <summary>Reintento manual (o tras un fallo definitivo).</summary>
    public void Retry()
    {
        if (Current == null) return;
        _attempt = 0;
        _retryTimer.Stop();
        StartLoad(PlayState.Connecting);
    }

    public void Stop()
    {
        _retryTimer.Stop();
        _connectTimer.Stop();
        _player.Stop();
        Set(PlayState.Stopped);
    }

    public void TogglePause()
    {
        switch (State)
        {
            case PlayState.Stopped:
            case PlayState.Failed:
                Retry();
                break;
            case PlayState.Paused:
                _userPaused = false;
                // En vivo: al reanudar se vuelve al borde en directo para no quedar atrasado.
                Reload();
                break;
            case PlayState.Playing:
            case PlayState.Buffering:
                _userPaused = true;
                _player.SetPause(true);
                Set(PlayState.Paused);
                break;
        }
    }

    private void StartLoad(PlayState state)
    {
        if (Current == null) return;
        _lastPos = double.NaN;
        _lastProgress = DateTime.UtcNow;
        BufferPercent = 0;
        _player.SetPause(false);
        _player.Load(Current.Url, Current.UserAgent, Current.Referrer, Current.Headers);
        _connectTimer.Stop();
        _connectTimer.Start();
        Set(state);
    }

    private void Reload() => StartLoad(_hadPlayed || _attempt > 0 ? PlayState.Reconnecting : PlayState.Connecting);

    private void OnPlaybackStarted()
    {
        if (Current == null || State is PlayState.Stopped or PlayState.Idle) return;
        _connectTimer.Stop();
        _hadPlayed = true;
        _attempt = 0;
        _lastProgress = DateTime.UtcNow;
        Detail = "";
        if (!_userPaused) Set(PlayState.Playing);
    }

    private void OnBuffering(bool buffering)
    {
        if (State is PlayState.Stopped or PlayState.Failed or PlayState.Idle or PlayState.Paused) return;
        if (buffering && State == PlayState.Playing) Set(PlayState.Buffering);
        else if (!buffering && State == PlayState.Buffering) Set(PlayState.Playing);
    }

    private void OnFileEnded(EndKind kind, string? detail)
    {
        if (Current == null || State == PlayState.Stopped) return;
        if (kind == EndKind.Stopped) return;
        _connectTimer.Stop();
        ScheduleRetry(Friendly(kind, detail));
    }

    private void ScheduleRetry(string reason)
    {
        var plan = _hadPlayed ? RetryAfterPlaying : RetryNeverPlayed;
        if (_attempt >= plan.Length)
        {
            Fail(reason);
            return;
        }
        int wait = plan[_attempt++];
        Detail = reason;
        _retryTimer.Interval = TimeSpan.FromSeconds(wait);
        _retryTimer.Start();
        Set(PlayState.Reconnecting);
    }

    private void Fail(string reason)
    {
        _retryTimer.Stop();
        _connectTimer.Stop();
        Detail = reason;
        _player.Stop();
        Set(PlayState.Failed);
    }

    public int Attempt => _attempt;

    private void Watch(Dispatcher ui)
    {
        if (Interlocked.Exchange(ref _watchBusy, 1) == 1) return;
        try
        {
            var stats = _player.ReadStats();
            if (stats == null) return;
            ui.BeginInvoke(() =>
            {
                LastStats = stats;
                StatsUpdated?.Invoke(stats);
                if (State is PlayState.Playing or PlayState.Buffering)
                {
                    bool audioOnly = stats.Width == 0 && stats.TimePos > 1;
                    if (audioOnly != AudioOnly) { AudioOnly = audioOnly; Raise(); }
                    if (double.IsNaN(_lastPos) || Math.Abs(stats.TimePos - _lastPos) > 0.05)
                    {
                        _lastPos = stats.TimePos;
                        _lastProgress = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _lastProgress).TotalSeconds > StallSeconds)
                    {
                        _lastProgress = DateTime.UtcNow;
                        ScheduleRetry("La transmisión se detuvo.");
                    }
                }
            }, DispatcherPriority.Background);
        }
        catch (Exception) { }
        finally { Volatile.Write(ref _watchBusy, 0); }
    }

    public void Shutdown()
    {
        using var done = new ManualResetEvent(false);
        if (_watchdog.Dispose(done)) done.WaitOne(1500);
    }

    private static string Friendly(EndKind kind, string? detail)
    {
        if (kind == EndKind.Eof) return "La transmisión terminó o se cortó.";
        var d = detail ?? "";
        if (d.Contains("403")) return "El servidor negó el acceso (403). Puede ser un bloqueo por país.";
        if (d.Contains("404") || d.Contains("410")) return "La transmisión ya no existe en esa dirección (404).";
        if (d.Contains("401")) return "La transmisión pide autorización (401).";
        if (System.Text.RegularExpressions.Regex.IsMatch(d, @"\b5\d\d\b")) return "El servidor del canal está fallando.";
        if (d.Contains("resolve", StringComparison.OrdinalIgnoreCase) || d.Contains("Name or service", StringComparison.OrdinalIgnoreCase))
            return "No se encontró el servidor del canal.";
        if (d.Contains("timed out", StringComparison.OrdinalIgnoreCase) || d.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "El servidor no respondió a tiempo.";
        if (d.Contains("refused", StringComparison.OrdinalIgnoreCase)) return "El servidor rechazó la conexión.";
        if (d.Contains("format", StringComparison.OrdinalIgnoreCase) || d.Contains("demux", StringComparison.OrdinalIgnoreCase))
            return "La dirección no entrega un video reproducible.";
        return "El canal no responde.";
    }

    private void Set(PlayState s) { State = s; Raise(); }
    private void Raise() => StateChanged?.Invoke();
}
