using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace IPTV101.Player;

public enum EndKind { Eof, Error, Stopped }

public sealed record PlayerStats(
    string? HwDec, string? Codec, long Width, long Height, double Fps,
    long FrameDrops, long DecoderDrops, double CacheSeconds, double Volume, bool Buffering, string? VideoOut, double TimePos);

/// <summary>Reproductor basado en libmpv incrustado en una ventana hija.</summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private Thread? _eventThread;
    private volatile bool _closing;
    private readonly Dispatcher _ui;
    private long _currentEntry = -1;
    private string? _lastError;
    private readonly object _errLock = new();

    public event Action? FileStarted;
    public event Action? PlaybackStarted;
    public event Action<EndKind, string?>? FileEnded;
    public event Action<bool>? BufferingChanged;
    public event Action<long>? BufferPercentChanged;
    public event Action? VideoReconfigured;
    public event Action<bool>? PauseChanged;

    public bool IsReady => _ctx != IntPtr.Zero && !_closing;

    public MpvPlayer(Dispatcher ui) { _ui = ui; }

    /// <summary>Crea e inicializa mpv. Se llama fuera del hilo de la interfaz.</summary>
    public void Initialize(IntPtr hostHwnd, double volume, bool muted, string? logFile)
    {
        var ctx = MpvNative.Create();
        if (ctx == IntPtr.Zero) throw new InvalidOperationException("No se pudo crear el motor de video.");

        void O(string k, string v) => MpvNative.SetOption(ctx, k, v);

        O("wid", ((long)hostHwnd).ToString(CultureInfo.InvariantCulture));
        O("config", "no");
        O("terminal", "no");
        O("input-default-bindings", "no");
        O("input-vo-keyboard", "no");
        O("input-cursor", "no");
        O("osc", "no");
        O("osd-level", "0");
        O("idle", "yes");
        O("force-window", "yes");
        O("keep-open", "no");
        O("ytdl", "no");
        O("stop-screensaver", "yes");
        O("background-color", "#000000");

        // Video: Direct3D 11 sobre la GPU integrada; decodificación por hardware con respaldo por copia y por software.
        O("vo", "gpu");
        O("gpu-api", "d3d11");
        O("gpu-context", "d3d11");
        O("d3d11-adapter", "Intel");
        // Presentación BLT (no flip-model): la cadena de intercambio flip-model que usa mpv por defecto
        // se presenta por un canal de DWM que puede no llegar a componerse de forma fiable cuando la
        // ventana de video es un HWND hijo embebido dentro del árbol nativo de WPF (HwndHost); con
        // algunos drivers/compuestos esto deja el área de video en negro pese a que la decodificación
        // funciona bien. BLT es más compatible en este escenario a cambio de un poco más de CPU.
        O("d3d11-flip", "no");
        // d3d11va sin copia comparte la textura decodificada directamente con el renderizador: en esta
        // Intel HD 510 (driver antiguo) esa ruta es la mas sospechosa de dejar el video en negro.
        // d3d11va-copy copia el cuadro a una textura normal antes de dibujarlo: mas lento, pero fiable.
        // NOTA para quien continue: probar hwdec=no (software puro) fue el siguiente paso de
        // diagnostico que quedo pendiente por una corrupcion del compositor de Windows (ver informe).
        O("hwdec", "d3d11va-copy,dxva2-copy,no");
        O("hwdec-codecs", "h264,hevc,mpeg2video,vc1,vp9,av1");
        O("profile", "fast");
        O("video-sync", "audio");
        O("interpolation", "no");
        O("vd-lavc-threads", "2");
        O("vd-lavc-dr", "yes");
        O("framedrop", "vo");
        O("keepaspect", "yes");

        // Red y búfer para transmisiones en vivo.
        O("cache", "yes");
        O("cache-secs", "20");
        O("demuxer-readahead-secs", "8");
        O("demuxer-max-bytes", "48MiB");
        O("demuxer-max-back-bytes", "8MiB");
        O("cache-pause", "yes");
        O("cache-pause-initial", "no");
        O("cache-pause-wait", "2");
        O("network-timeout", "15");
        O("hls-bitrate", "max");
        O("user-agent", Channels.Channel.DefaultUserAgent);
        O("stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=4");
        O("demuxer-lavf-o-add", "reconnect=1");
        O("demuxer-lavf-o-add", "reconnect_streamed=1");
        O("demuxer-lavf-o-add", "reconnect_on_network_error=1");
        O("demuxer-lavf-o-add", "reconnect_delay_max=4");
        O("demuxer-lavf-probesize", "1000000");
        O("demuxer-lavf-analyzeduration", "2");

        O("audio-fallback-to-null", "yes");
        O("volume-max", "150");
        O("volume", volume.ToString("0", CultureInfo.InvariantCulture));
        O("mute", muted ? "yes" : "no");
        O("audio-client-name", "IPTV101");
        if (!string.IsNullOrEmpty(logFile))
        {
            O("log-file", logFile);
            O("msg-level", "all=v");
        }

        int r = MpvNative.Initialize(ctx);
        if (r < 0)
        {
            MpvNative.TerminateDestroy(ctx);
            throw new InvalidOperationException("No se pudo iniciar el motor de video: " + MpvNative.FromUtf8(MpvNative.ErrorString(r)));
        }

        MpvNative.RequestLog(ctx, "error");
        MpvNative.Observe(ctx, 1, "paused-for-cache", MpvNative.FORMAT_FLAG);
        MpvNative.Observe(ctx, 2, "cache-buffering-state", MpvNative.FORMAT_INT64);
        MpvNative.Observe(ctx, 3, "pause", MpvNative.FORMAT_FLAG);

        _ctx = ctx;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events", Priority = ThreadPriority.AboveNormal };
        _eventThread.Start();
    }

    private void Post(Action a)
    {
        if (_closing) return;
        _ui.BeginInvoke(a, DispatcherPriority.Normal);
    }

    private void EventLoop()
    {
        while (!_closing)
        {
            var ev = MpvNative.WaitEvent(_ctx, -1);
            if (_closing) break;
            if (ev == null) continue;
            switch (ev->EventId)
            {
                case MpvNative.EVENT_NONE:
                    break;
                case MpvNative.EVENT_SHUTDOWN:
                    return;
                case MpvNative.EVENT_LOG_MESSAGE:
                    {
                        var m = (MpvNative.EventLogMessage*)ev->Data;
                        var text = MpvNative.FromUtf8(m->Text)?.Trim();
                        var prefix = MpvNative.FromUtf8(m->Prefix);
                        if (!string.IsNullOrEmpty(text))
                            lock (_errLock) _lastError = prefix + ": " + text;
                        break;
                    }
                case MpvNative.EVENT_START_FILE:
                    {
                        long id = *(long*)ev->Data;
                        Interlocked.Exchange(ref _currentEntry, id);
                        lock (_errLock) _lastError = null;
                        Post(() => FileStarted?.Invoke());
                        break;
                    }
                case MpvNative.EVENT_END_FILE:
                    {
                        var e = (MpvNative.EventEndFile*)ev->Data;
                        if (e->PlaylistEntryId != Interlocked.Read(ref _currentEntry)) break;
                        EndKind kind;
                        if (e->Reason == MpvNative.END_ERROR) kind = EndKind.Error;
                        else if (e->Reason == MpvNative.END_EOF) kind = EndKind.Eof;
                        else if (e->Reason == MpvNative.END_STOP || e->Reason == MpvNative.END_QUIT) kind = EndKind.Stopped;
                        else break;
                        string? detail;
                        lock (_errLock) detail = _lastError;
                        if (kind == EndKind.Error && detail == null)
                            detail = MpvNative.FromUtf8(MpvNative.ErrorString(e->Error));
                        Post(() => FileEnded?.Invoke(kind, detail));
                        break;
                    }
                case MpvNative.EVENT_PLAYBACK_RESTART:
                    Post(() => PlaybackStarted?.Invoke());
                    break;
                case MpvNative.EVENT_VIDEO_RECONFIG:
                    Post(() => VideoReconfigured?.Invoke());
                    break;
                case MpvNative.EVENT_PROPERTY_CHANGE:
                    {
                        var p = (MpvNative.EventProperty*)ev->Data;
                        if (p->Data == IntPtr.Zero) break;
                        switch (ev->ReplyUserdata)
                        {
                            case 1 when p->Format == MpvNative.FORMAT_FLAG:
                                bool b = *(int*)p->Data != 0;
                                Post(() => BufferingChanged?.Invoke(b));
                                break;
                            case 2 when p->Format == MpvNative.FORMAT_INT64:
                                long pc = *(long*)p->Data;
                                Post(() => BufferPercentChanged?.Invoke(pc));
                                break;
                            case 3 when p->Format == MpvNative.FORMAT_FLAG:
                                bool pa = *(int*)p->Data != 0;
                                Post(() => PauseChanged?.Invoke(pa));
                                break;
                        }
                        break;
                    }
            }
        }
    }

    /// <summary>Codifica un valor para la lista de opciones de loadfile (formato %largo%valor).</summary>
    private static string Esc(string v) => "%" + Encoding.UTF8.GetByteCount(v).ToString(CultureInfo.InvariantCulture) + "%" + v;

    public void Load(string url, string? userAgent, string? referrer, IReadOnlyList<string>? headers)
    {
        if (!IsReady) return;
        var opts = new List<string>();
        if (!string.IsNullOrEmpty(userAgent)) opts.Add("user-agent=" + Esc(userAgent));
        if (!string.IsNullOrEmpty(referrer)) opts.Add("referrer=" + Esc(referrer));
        if (headers is { Count: > 0 }) opts.Add("http-header-fields=" + Esc(string.Join(",", headers.Select(h => h.Replace(",", "\\,")))));
        if (opts.Count > 0)
            MpvNative.CommandAsync(_ctx, 0, "loadfile", url, "replace", "0", string.Join(",", opts));
        else
            MpvNative.CommandAsync(_ctx, 0, "loadfile", url, "replace");
    }

    public void Stop() { if (IsReady) MpvNative.CommandAsync(_ctx, 0, "stop"); }

    public void Set(string property, string value) { if (IsReady) MpvNative.CommandAsync(_ctx, 0, "set", property, value); }

    public void SetPause(bool paused) => Set("pause", paused ? "yes" : "no");
    public void SetVolume(double v) => Set("volume", v.ToString("0", CultureInfo.InvariantCulture));
    public void SetMute(bool m) => Set("mute", m ? "yes" : "no");

    public void SetAspect(AspectMode mode)
    {
        switch (mode)
        {
            case AspectMode.Auto: Set("keepaspect", "yes"); Set("panscan", "0"); Set("video-aspect-override", "-1"); break;
            case AspectMode.R16x9: Set("keepaspect", "yes"); Set("panscan", "0"); Set("video-aspect-override", "16:9"); break;
            case AspectMode.R4x3: Set("keepaspect", "yes"); Set("panscan", "0"); Set("video-aspect-override", "4:3"); break;
            case AspectMode.Fill: Set("keepaspect", "yes"); Set("video-aspect-override", "-1"); Set("panscan", "1.0"); break;
            case AspectMode.Stretch: Set("panscan", "0"); Set("video-aspect-override", "-1"); Set("keepaspect", "no"); break;
        }
    }

    /// <summary>Lee estadísticas del motor. Seguro para llamar desde cualquier hilo.</summary>
    public PlayerStats? ReadStats()
    {
        if (!IsReady) return null;
        var c = _ctx;
        MpvNative.TryGetLong(c, "width", out var w);
        MpvNative.TryGetLong(c, "height", out var h);
        MpvNative.TryGetDouble(c, "estimated-vf-fps", out var fps);
        MpvNative.TryGetLong(c, "frame-drop-count", out var drops);
        MpvNative.TryGetLong(c, "decoder-frame-drop-count", out var ddrops);
        MpvNative.TryGetDouble(c, "demuxer-cache-duration", out var cache);
        MpvNative.TryGetDouble(c, "volume", out var vol);
        MpvNative.TryGetLong(c, "paused-for-cache", out var pfc);
        MpvNative.TryGetDouble(c, "time-pos", out var tpos);
        return new PlayerStats(
            MpvNative.GetString(c, "hwdec-current"),
            MpvNative.GetString(c, "video-codec-name") ?? MpvNative.GetString(c, "video-format"),
            w, h, fps, drops, ddrops, cache, vol, pfc != 0,
            MpvNative.GetString(c, "current-gpu-context"), tpos);
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero || _closing) return;
        _closing = true;
        var ctx = _ctx;
        MpvNative.Wakeup(ctx);
        _eventThread?.Join(2000);
        _ctx = IntPtr.Zero;
        MpvNative.TerminateDestroy(ctx);
    }
}

public enum AspectMode { Auto, R16x9, R4x3, Fill, Stretch }
