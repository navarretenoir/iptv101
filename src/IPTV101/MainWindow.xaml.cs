using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPTV101.Channels;
using IPTV101.Data;
using IPTV101.Player;
using IPTV101.ViewModels;

namespace IPTV101;

public partial class MainWindow : Window
{
    private const string KindAll = "Todos", KindFav = "Favoritos", KindRecent = "Recientes";
    private static readonly string[] AspectNames = { "Auto", "16:9", "4:3", "Rellenar", "Estirar" };

    private readonly Settings _s;
    private readonly MpvPlayer _player;
    private readonly PlaybackController _pb;
    private readonly LogoCache _logos;
    private OsdWindow? _osd;

    private List<ChannelItem> _items = new();
    private Dictionary<string, ChannelItem> _byUrl = new();
    private List<ChannelItem> _view = new();
    private ChannelItem? _current;
    private string _kind = KindAll;
    private string? _group;
    private bool _suppressFilter;
    private bool _playerReady;
    private bool _channelsReady;

    private bool _fs, _guide;
    private WindowState _restoreState;
    private ResizeMode _restoreResize;

    private readonly DispatcherTimer _bannerTimer, _chipTimer, _fsBarTimer, _digitTimer, _searchTimer, _zapTimer, _saveTimer, _cursorTimer;
    private string _digits = "";
    private ChannelItem? _pendingZap;
    private bool _statsVisible;
    private int _aspect;
    private bool _updatingVolume;
    private uint _activateMsg;
    private StreamWriter? _diag;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public MainWindow(Settings settings)
    {
        _s = settings;
        InitializeComponent();

        _logos = new LogoCache(Dispatcher);
        ChannelItem.Logos = _logos;
        _player = new MpvPlayer(Dispatcher);
        _pb = new PlaybackController(_player, Dispatcher);
        _pb.StateChanged += OnPlaybackState;
        _pb.StatsUpdated += OnStats;
        _player.VideoReconfigured += () =>
        {
            Host.PassThroughChildren();
            // mpv engancha su ventana de video (wid) justo en este momento, que puede caer después de
            // que WPF ya acomodó el layout a su tamaño real; sin este empujón, mpv a veces arranca con
            // el tamaño/relación de aspecto de un instante anterior y el video sale recortado/corrido
            // hasta que algo fuerza un redimensionado real (maximizar y restaurar, por ejemplo). Esto
            // dispara ESE MISMO camino, pero por el sistema de layout propio de WPF (nunca tocando el
            // HWND nativo por fuera de WPF, que rompía la composición): se cambia el ancho de la
            // ventana en una fracción de pixel y se vuelve de inmediato, imperceptible para quien mira.
            Dispatcher.BeginInvoke(NudgeLayout, DispatcherPriority.Loaded);
        };

        _bannerTimer = Timer(4.5, () => { if (!BannerSticky) SetOsd(o => o.Banner, false); });
        _chipTimer = Timer(1.6, () => SetOsd(o => o.Chip, false));
        _fsBarTimer = Timer(3.0, HideFsBar);
        _digitTimer = Timer(1.8, CommitDigits);
        _searchTimer = Timer(0.18, () => ApplyFilter(true));
        _zapTimer = Timer(0.32, () => { if (_pendingZap != null) { var z = _pendingZap; _pendingZap = null; _pb.Play(z.Channel); } });
        _saveTimer = Timer(2.0, () => _s.Save());
        _cursorTimer = Timer(3.0, () => { if (_fs) Host.CursorHidden = true; });

        _aspect = Math.Max(0, Array.IndexOf(AspectNames, _s.Aspect));
        AspectText.Text = AspectNames[_aspect];
        _updatingVolume = true;
        VolumeSlider.Value = _s.Volume;
        _updatingVolume = false;
        UpdateMuteIcon();
        UpdateThemeChecks();
        UpdateSkinChecks();
        RestorePlacement();
        BuildPills();
        UpdateNowPlaying();
        UpdateButtons();

        Host.SurfaceDoubleClick += () => ToggleFullscreen();
        Host.SurfaceMouseMove += OnSurfaceActivity;
        Host.SurfaceClick += () => { OnSurfaceActivity(); Activate(); };
        Host.SurfaceWheel += d => ChangeVolume(d > 0 ? 5 : -5);

        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => PlaceOsd();
        SizeChanged += (_, _) => PlaceOsd();
        StateChanged += (_, _) =>
        {
            if (_osd != null) _osd.Suspended = WindowState == WindowState.Minimized;
            BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
            PlaceOsd();
        };
        VideoFrame.SizeChanged += (_, _) => { PlaceOsd(); UpdateVideoRegion(); };
        PreviewKeyDown += OnPreviewKeyDown;
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        ChannelList.PreviewKeyDown += ChannelList_PreviewKeyDown;

        if (App.DiagFile != null)
        {
            try { _diag = new StreamWriter(App.DiagFile, append: true, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (IOException) { }
        }
    }

    private DispatcherTimer Timer(double seconds, Action tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    private static void Restart(DispatcherTimer t) { t.Stop(); t.Start(); }

    // ───────────────────────────── Inicio ─────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var src = (HwndSource)PresentationSource.FromVisual(this);
        _activateMsg = App.ActivateMessage;
        src.AddHook(WndHook);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _osd = new OsdWindow { Owner = this };
        WireOsd(_osd);
        PlaceOsd();
        ShowCenter("", "Cargando canales…", "", "");

        var hwnd = Host.Handle;
        var volume = _s.Volume;
        var muted = _s.Muted;
        var log = App.EngineLogFile;

        Task.Run(() =>
        {
            var list = ChannelStore.LoadAll();
            var items = list.Select(c => new ChannelItem(c)).ToList();
            Dispatcher.BeginInvoke(() => OnChannelsLoaded(items));
        });

        Task.Run(() =>
        {
            try
            {
                _player.Initialize(hwnd, volume, muted, log);
                Dispatcher.BeginInvoke(() =>
                {
                    _playerReady = true;
                    Host.PassThroughChildren();
                    _player.SetAspect((AspectMode)_aspect);
                    if (_current != null) _pb.Play(_current.Channel);
                    else TryAutoPlay();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => ShowCenter("", "No se pudo iniciar el video", ex.Message, "Revisa que libmpv-2.dll esté junto a IPTV101.exe."));
            }
        });
    }

    private void OnChannelsLoaded(List<ChannelItem> items)
    {
        SetItems(items);
        foreach (var it in _items) it.IsFavorite = _s.Favorites.Contains(it.Url);

        _suppressFilter = true;
        SelectKind(_s.ListFilter, focus: false);
        _group = _s.GroupFilter;
        RebuildGroups();
        _suppressFilter = false;
        ApplyFilter(false);
        _channelsReady = true;
        UpdateEmptyState();
        TryAutoPlay();
    }

    /// <summary>IPTV 101 se instala sin canales: mientras no haya ninguna lista importada, la celda de
    /// la lista muestra una invitacion a importar en vez de quedar en blanco sin explicacion.</summary>
    private void UpdateEmptyState()
    {
        bool empty = _channelsReady && _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ChannelList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) HideCenter();
    }

    private void SetItems(List<ChannelItem> items)
    {
        _items = items;
        _byUrl = new Dictionary<string, ChannelItem>(StringComparer.Ordinal);
        foreach (var it in items) _byUrl.TryAdd(it.Url, it);
    }

    private bool _autoPlayed;

    private void TryAutoPlay()
    {
        if (!_playerReady || !_channelsReady || _autoPlayed) return;
        _autoPlayed = true;
        if (App.StartChannel is int n && _items.FirstOrDefault(i => i.Number == n) is { } forced)
        {
            Play(forced);
            return;
        }
        if (_s.AutoPlayLast && _s.LastUrl != null && _byUrl.TryGetValue(_s.LastUrl, out var last))
        {
            Play(last);
            SelectInList(last, scroll: true);
        }
        else
        {
            ShowCenter("", "Elige un canal", "Usa la lista de la izquierda, las flechas o escribe el número del canal.", "");
        }
        FocusList();
    }

    // ───────────────────────────── Listas y filtros ─────────────────────────────

    private IEnumerable<string> Kinds()
    {
        yield return KindAll;
        yield return KindFav;
        yield return KindRecent;
        foreach (var s in ChannelStore.LoadSources().Where(s => s.Enabled != 0)) yield return s.Name;
    }

    private void BuildPills()
    {
        ListPills.Children.Clear();
        foreach (var k in Kinds())
        {
            var rb = new RadioButton
            {
                Content = k,
                GroupName = "kind",
                Style = (Style)FindResource("Pill"),
                Tag = k,
                IsChecked = k == _kind,
            };
            AutomationPropertiesHelper.SetName(rb, "Lista " + k);
            rb.Checked += (_, _) => { if (!_suppressFilter) { _kind = k; _group = null; RebuildGroups(); ApplyFilter(false); SaveSoon(); } };
            ListPills.Children.Add(rb);
        }
    }

    private void SelectKind(string kind, bool focus)
    {
        var rb = ListPills.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == kind)
                 ?? ListPills.Children.OfType<RadioButton>().First();
        _kind = (string)rb.Tag;
        rb.IsChecked = true;
        if (focus) rb.Focus();
    }

    private void CycleKind(int dir)
    {
        var pills = ListPills.Children.OfType<RadioButton>().ToList();
        int i = pills.FindIndex(p => (string)p.Tag == _kind);
        var next = pills[(i + dir + pills.Count) % pills.Count];
        next.IsChecked = true;
        ShowChip("", (string)next.Tag);
    }

    private IEnumerable<ChannelItem> KindSource()
    {
        switch (_kind)
        {
            case KindAll: return _items;
            case KindFav: return _s.Favorites.Select(u => _byUrl.GetValueOrDefault(u)).OfType<ChannelItem>();
            case KindRecent: return _s.Recents.Select(u => _byUrl.GetValueOrDefault(u)).OfType<ChannelItem>();
            default: return _items.Where(i => i.Source == _kind);
        }
    }

    private sealed record GroupOption(string? Name, string Label)
    {
        public override string ToString() => Label;
    }

    private void RebuildGroups()
    {
        var src = KindSource().ToList();
        var groups = src.GroupBy(i => i.Group).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new GroupOption(g.Key, $"{g.Key}  ·  {g.Count()}")).ToList();
        groups.Insert(0, new GroupOption(null, $"Todos los grupos  ·  {src.Count}"));
        bool prev = _suppressFilter;
        _suppressFilter = true;
        GroupBox.ItemsSource = groups;
        var sel = groups.FirstOrDefault(g => g.Name == _group) ?? groups[0];
        _group = sel.Name;
        GroupBox.SelectedItem = sel;
        _suppressFilter = prev;
    }

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        _group = (GroupBox.SelectedItem as GroupOption)?.Name;
        ApplyFilter(false);
        SaveSoon();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Restart(_searchTimer);
    }

    private void ApplyFilter(bool fromSearch)
    {
        IEnumerable<ChannelItem> q = KindSource();
        if (_group != null) q = q.Where(i => i.Group == _group);
        var text = SearchBox.Text.Trim();
        if (text.Length > 0)
        {
            var tokens = M3uParser.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool numeric = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var num);
            q = q.Where(i => (numeric && i.Number == num) || tokens.All(t => i.Channel.SearchKey.Contains(t, StringComparison.Ordinal)
                                                                        || i.Group.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }
        _view = q.ToList();
        ChannelList.ItemsSource = _view;
        FooterText.Text = _view.Count == 1 ? "1 canal" : $"{_view.Count:N0} canales".Replace(',', '.');
        if (_current != null && _view.Contains(_current)) SelectInList(_current, scroll: !fromSearch);
        else if (_view.Count > 0) ChannelList.SelectedIndex = 0;
    }

    private void SelectInList(ChannelItem it, bool scroll)
    {
        if (!_view.Contains(it)) return;
        ChannelList.SelectedItem = it;
        if (scroll) ChannelList.ScrollIntoView(it);
    }

    private void FocusList()
    {
        if (_view.Count == 0) { SearchBox.Focus(); return; }
        var it = ChannelList.SelectedItem as ChannelItem ?? _view[0];
        ChannelList.SelectedItem = it;
        ChannelList.ScrollIntoView(it);
        ChannelList.UpdateLayout();
        if (ChannelList.ItemContainerGenerator.ContainerFromItem(it) is ListBoxItem c) c.Focus();
        else ChannelList.Focus();
    }

    // ───────────────────────────── Reproducción ─────────────────────────────

    private void Play(ChannelItem it, bool zap = false)
    {
        if (_current != null && _current != it)
        {
            _current.IsPlaying = false;
            _s.PreviousUrl = _current.Url;
        }
        _current = it;
        _autoPlayed = true;
        it.IsPlaying = true;
        _s.LastUrl = it.Url;
        _s.PushRecent(it.Url);
        SaveSoon();
        HideCenter();
        UpdateNowPlaying();
        ShowBanner();

        if (!_playerReady) return;
        if (zap)
        {
            _pendingZap = it;
            Restart(_zapTimer);
        }
        else
        {
            _pendingZap = null;
            _zapTimer.Stop();
            _pb.Play(it.Channel);
        }
    }

    private void Zap(int dir)
    {
        var list = _view.Count > 0 ? _view : _items;
        if (list.Count == 0) return;
        int idx = _current != null ? list.IndexOf(_current) : -1;
        if (idx < 0 && ChannelList.SelectedItem is ChannelItem sel) idx = list.IndexOf(sel);
        int next = idx < 0 ? (dir > 0 ? 0 : list.Count - 1) : (idx + dir + list.Count) % list.Count;
        var it = list[next];
        Play(it, zap: true);
        if (!SearchBox.IsKeyboardFocusWithin) SelectInList(it, scroll: true);
    }

    private void Recall()
    {
        if (_s.PreviousUrl != null && _byUrl.TryGetValue(_s.PreviousUrl, out var prev))
        {
            Play(prev);
            SelectInList(prev, scroll: true);
        }
    }

    private void TogglePause()
    {
        if (_current == null)
        {
            if (ChannelList.SelectedItem is ChannelItem sel) Play(sel);
            return;
        }
        if (_pendingZap != null) return;
        _pb.TogglePause();
    }

    private void StopPlayback()
    {
        if (_current == null) return;
        _pendingZap = null;
        _zapTimer.Stop();
        _pb.Stop();
    }

    private void RetryNow()
    {
        if (_current == null) return;
        HideCenter();
        _pb.Retry();
    }

    private void OnPlaybackState()
    {
        UpdateNowPlaying();
        UpdateButtons();
        KeepAwake(_pb.State is PlayState.Playing or PlayState.Buffering or PlayState.Connecting or PlayState.Reconnecting);

        switch (_pb.State)
        {
            case PlayState.Failed:
                ShowCenter("", "Canal no disponible", _pb.Detail,
                    "Enter o Reintentar para volver a probar  ·  ▲ ▼ o RePág / AvPág para cambiar de canal");
                ShowBanner();
                break;
            case PlayState.Stopped:
                HideCenter();
                ShowBanner();
                break;
            case PlayState.Playing when _pb.AudioOnly:
                HideCenter();
                ShowCenter("", _current?.Name ?? "", "Este canal transmite solo audio.", "");
                break;
            case PlayState.Playing:
                HideCenter();
                ShowBanner();
                break;
            default:
                HideCenter();
                ShowBanner();
                break;
        }
    }

    private static void KeepAwake(bool on)
    {
        Native.SetThreadExecutionState(on
            ? Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED
            : Native.ES_CONTINUOUS);
    }

    private (string text, Color color) StatusOf()
    {
        if (_current == null) return ("Elige un canal", Color.FromRgb(0x9E, 0x9E, 0x9E));
        if (_pendingZap != null) return ("Cambiando de canal…", Color.FromRgb(0x4C, 0xC2, 0xFF));
        return _pb.State switch
        {
            PlayState.Connecting => ("Conectando…", Color.FromRgb(0x4C, 0xC2, 0xFF)),
            PlayState.Playing => (_pb.AudioOnly ? "En vivo · solo audio" : "En vivo", Color.FromRgb(0x3D, 0xD6, 0x8C)),
            PlayState.Buffering => ($"Cargando búfer… {_pb.BufferPercent} %", Color.FromRgb(0xF5, 0xC2, 0x42)),
            PlayState.Paused => ("En pausa", Color.FromRgb(0xB0, 0xB0, 0xB0)),
            PlayState.Reconnecting => ($"Reconectando (intento {Math.Max(1, _pb.Attempt)})…", Color.FromRgb(0xF5, 0xC2, 0x42)),
            PlayState.Failed => ("No disponible", Color.FromRgb(0xFF, 0x6B, 0x6B)),
            PlayState.Stopped => ("Detenido", Color.FromRgb(0xB0, 0xB0, 0xB0)),
            _ => ("Listo", Color.FromRgb(0x9E, 0x9E, 0x9E)),
        };
    }

    private void UpdateNowPlaying()
    {
        var (text, color) = StatusOf();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        if (_current == null)
        {
            NowName.Text = "Sin canal";
            NowStatus.Text = text;
            StatusDot.Fill = brush;
            NowLogoBox.Visibility = Visibility.Collapsed;
            NowInitials.Text = "";
            Title = "IPTV 101";
            return;
        }
        NowName.Text = $"{_current.Number}  {_current.Name}";
        NowStatus.Text = text + "  ·  " + _current.Group;
        StatusDot.Fill = brush;
        NowBadge.Background = _current.BadgeBrush;
        NowInitials.Text = _current.Initials;
        var logo = _current.Logo;
        NowLogo.Source = logo;
        NowLogoBox.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
        if (logo == null && _current.Channel.LogoUrl != null)
        {
            var c = _current;
            PropertyChangedEventHandler? h = null;
            h = (_, e) =>
            {
                if (e.PropertyName != nameof(ChannelItem.Logo)) return;
                c.PropertyChanged -= h;
                if (c == _current) UpdateNowPlaying();
            };
            c.PropertyChanged += h;
        }
        Title = _current.Name + " — IPTV 101";
        FavButton.Content = _current.IsFavorite ? "" : "";
        if (_osd != null && _osd.Banner.Visibility == Visibility.Visible) FillBanner();
    }

    private void UpdateButtons()
    {
        bool playing = _pb.State is PlayState.Playing or PlayState.Buffering or PlayState.Connecting or PlayState.Reconnecting;
        var glyph = playing ? "" : "";
        PlayButton.Content = glyph;
        if (_osd != null) _osd.FsPlay.Content = glyph;
        RetryButton.Visibility = _pb.State is PlayState.Failed or PlayState.Reconnecting ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────────── OSD ─────────────────────────────

    private bool BannerSticky => _pb.State is PlayState.Connecting or PlayState.Reconnecting or PlayState.Failed or PlayState.Buffering
                                 || _pendingZap != null;

    private void WireOsd(OsdWindow o)
    {
        o.FsGuide.Click += (_, _) => ShowGuide(!_guide);
        o.FsPrev.Click += (_, _) => Zap(-1);
        o.FsNext.Click += (_, _) => Zap(1);
        o.FsPlay.Click += (_, _) => TogglePause();
        o.FsMute.Click += (_, _) => ToggleMute();
        o.FsAspect.Click += (_, _) => CycleAspect();
        o.FsExit.Click += (_, _) => SetFullscreen(false);
        o.FsVolume.Value = _s.Volume;
        o.FsVolume.ValueChanged += (_, e) => { if (!_updatingVolume) SetVolume(e.NewValue); };
        o.MouseMove += (_, _) => OnSurfaceActivity();
    }

    private void SetOsd(Func<OsdWindow, FrameworkElement> pick, bool visible)
    {
        if (_osd == null) return;
        pick(_osd).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _osd.Refresh();
    }

    private void PlaceOsd()
    {
        if (_osd == null || Host.Handle == IntPtr.Zero) return;
        Native.GetWindowRect(Host.Handle, out var r);
        _osd.PlaceOver(r);
    }

    private void FillBanner()
    {
        if (_osd == null || _current == null) return;
        var (text, color) = StatusOf();
        _osd.BannerNumber.Text = _current.NumberText;
        _osd.BannerName.Text = _current.Name;
        _osd.BannerGroup.Text = _current.Group + (_current.Source != _current.Group ? "  ·  " + _current.Source : "");
        _osd.BannerStatus.Text = _pb.State == PlayState.Reconnecting && !string.IsNullOrEmpty(_pb.Detail) && _pendingZap == null
            ? text + "  " + _pb.Detail
            : text;
        var b = new SolidColorBrush(color); b.Freeze();
        _osd.BannerDot.Fill = b;
        _osd.BannerBadge.Background = _current.BadgeBrush;
        _osd.BannerInitials.Text = _current.Initials;
        var logo = _current.Logo;
        _osd.BannerLogo.Source = logo;
        _osd.BannerLogoBox.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowBanner()
    {
        if (_osd == null || _current == null) return;
        FillBanner();
        SetOsd(o => o.Banner, true);
        Restart(_bannerTimer);
    }

    private void ShowChip(string icon, string text)
    {
        if (_osd == null) return;
        _osd.ChipIcon.Text = icon;
        _osd.ChipIcon.Visibility = string.IsNullOrEmpty(icon) ? Visibility.Collapsed : Visibility.Visible;
        _osd.ChipText.Text = text;
        SetOsd(o => o.Chip, true);
        Restart(_chipTimer);
    }

    private void ShowCenter(string icon, string title, string detail, string hint)
    {
        if (_osd == null) return;
        _osd.CenterIcon.Text = icon;
        _osd.CenterTitle.Text = title;
        _osd.CenterDetail.Text = detail;
        _osd.CenterDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        _osd.CenterHint.Text = hint;
        _osd.CenterHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        SetOsd(o => o.Center, true);
    }

    private void HideCenter() => SetOsd(o => o.Center, false);

    private void OnSurfaceActivity()
    {
        Host.CursorHidden = false;
        if (_fs)
        {
            Restart(_cursorTimer);
            ShowFsBar();
        }
    }

    private void ShowFsBar()
    {
        if (!_fs || _guide) return;
        SetOsd(o => o.FsBar, true);
        Restart(_fsBarTimer);
    }

    private void HideFsBar()
    {
        if (_osd != null && _osd.FsBar.IsMouseOver) { Restart(_fsBarTimer); return; }
        SetOsd(o => o.FsBar, false);
    }

    private void OnStats(PlayerStats st)
    {
        if (_statsVisible && _osd != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Video     {st.Codec ?? "—"}  {st.Width}×{st.Height}  {st.Fps:0.##} fps");
            sb.AppendLine($"Decodif.  {(string.IsNullOrEmpty(st.HwDec) || st.HwDec == "no" ? "software" : "hardware (" + st.HwDec + ")")}");
            sb.AppendLine($"Salida    {st.VideoOut ?? "—"}");
            sb.AppendLine($"Búfer     {st.CacheSeconds:0.0} s{(st.Buffering ? "  (esperando datos)" : "")}");
            sb.Append($"Cuadros perdidos  {st.FrameDrops} salida · {st.DecoderDrops} decodif.");
            _osd.StatsText.Text = sb.ToString();
        }
        if (_diag != null)
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{{\"t\":{_clock.Elapsed.TotalSeconds:0.0},\"canal\":{Json(_current?.Name)},\"num\":{_current?.Number ?? 0},\"estado\":\"{_pb.State}\"," +
                    $"\"hwdec\":{Json(st.HwDec)},\"codec\":{Json(st.Codec)},\"w\":{st.Width},\"h\":{st.Height},\"fps\":{st.Fps:0.##}," +
                    $"\"drop_vo\":{st.FrameDrops},\"drop_dec\":{st.DecoderDrops},\"cache\":{st.CacheSeconds:0.0},\"pos\":{st.TimePos:0.0}," +
                    $"\"cpu_s\":{p.TotalProcessorTime.TotalSeconds:0.00},\"ws_mb\":{p.WorkingSet64 / 1048576.0:0.0},\"priv_mb\":{p.PrivateMemorySize64 / 1048576.0:0.0}," +
                    $"\"handles\":{p.HandleCount},\"threads\":{p.Threads.Count},\"gc_mb\":{GC.GetTotalMemory(false) / 1048576.0:0.0},\"detalle\":{Json(_pb.Detail)}}}");
                _diag.WriteLine(line);
            }
            catch (Exception) { }
        }
    }

    private static string Json(string? s) => s == null ? "null" : System.Text.Json.JsonSerializer.Serialize(s, StringJson.Default.String);

    private void ToggleStats()
    {
        _statsVisible = !_statsVisible;
        if (_statsVisible && _pb.LastStats != null) OnStats(_pb.LastStats);
        else if (_statsVisible && _osd != null) _osd.StatsText.Text = "Esperando datos…";
        SetOsd(o => o.Stats, _statsVisible);
    }

    // ───────────────────────────── Pantalla completa ─────────────────────────────

    private void ToggleFullscreen() => SetFullscreen(!_fs);

    private void SetFullscreen(bool on)
    {
        if (on == _fs) return;
        _fs = on;
        if (on)
        {
            _restoreState = WindowState;
            _restoreResize = ResizeMode;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _guide = false;
            ApplyLayout();
            ShowFsBar();
            Restart(_cursorTimer);
            FsButton.Content = "";
        }
        else
        {
            _guide = false;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;
            ApplyLayout();
            SetOsd(o => o.FsBar, false);
            Host.CursorHidden = false;
            _cursorTimer.Stop();
            FsButton.Content = "";
            FocusList();
        }
        Dispatcher.BeginInvoke(PlaceOsd, DispatcherPriority.Loaded);
    }

    private void ShowGuide(bool show)
    {
        if (!_fs) { FocusList(); return; }
        _guide = show;
        ApplyLayout();
        if (show)
        {
            SetOsd(o => o.FsBar, false);
            if (_current != null) SelectInList(_current, scroll: true);
            Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
        }
        else
        {
            Keyboard.Focus(this);
            FocusManager.SetFocusedElement(this, this);
        }
        Dispatcher.BeginInvoke(PlaceOsd, DispatcherPriority.Loaded);
    }

    private void ApplyLayout()
    {
        bool sidebar = !_fs || _guide;
        SideCol.Width = sidebar ? new GridLength(336) : new GridLength(0);
        Sidebar.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        ControlBar.Visibility = _fs ? Visibility.Collapsed : Visibility.Visible;
        TitleRow.Height = _fs ? new GridLength(0) : new GridLength(34);
        VideoFrame.Margin = _fs ? new Thickness(0) : new Thickness(6, 8, 12, 0);
        VideoFrame.CornerRadius = _fs ? new CornerRadius(0) : new CornerRadius(8);
        UpdateVideoRegion();
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    /// <summary>Esquinas redondeadas del video en modo ventana (recorta también la superficie del motor).</summary>
    private void UpdateVideoRegion()
    {
        if (Host.Handle == IntPtr.Zero) return;
        if (_fs) { SetWindowRgn(Host.Handle, IntPtr.Zero, true); return; }
        Native.GetClientRect(Host.Handle, out var rc);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int r = (int)Math.Round(16 * dpi);
        var rgn = CreateRoundRectRgn(0, 0, rc.Right + 1, rc.Bottom + 1, r, r);
        SetWindowRgn(Host.Handle, rgn, true);
    }

    /// <summary>
    /// Fuerza un recálculo de layout real (equivalente a maximizar y restaurar) pasando por el propio
    /// sistema de WPF, nunca tocando el HWND nativo del video por fuera de WPF: cambia el ancho de la
    /// ventana una fracción de píxel y vuelve de inmediato. mpv engancha su ventana de video justo
    /// cuando el video arranca, lo que puede caer después de que WPF ya acomodó el layout a su tamaño
    /// final; sin este empujón a veces queda con el tamaño/relación de aspecto de un instante anterior
    /// (se ve recortado/corrido) hasta que algo fuerza un redimensionado real.
    /// </summary>
    private void NudgeLayout()
    {
        if (_fs) return; // en pantalla completa el tamaño ya quedó fijado por SetFullscreen.
        var w = Width;
        Width = w + 0.5;
        UpdateLayout();
        Dispatcher.BeginInvoke(() =>
        {
            Width = w;
            UpdateLayout();
        }, DispatcherPriority.Render);
    }

    // ───────────────────────────── Volumen, aspecto, favoritos ─────────────────────────────

    private void SetVolume(double v)
    {
        v = Math.Clamp(Math.Round(v), 0, 130);
        _s.Volume = v;
        _updatingVolume = true;
        VolumeSlider.Value = v;
        if (_osd != null) _osd.FsVolume.Value = v;
        _updatingVolume = false;
        _player.SetVolume(v);
        if (_s.Muted && v > 0) { _s.Muted = false; _player.SetMute(false); UpdateMuteIcon(); }
        SaveSoon();
    }

    private void ChangeVolume(int delta)
    {
        SetVolume(_s.Volume + delta);
        ShowChip(_s.Muted ? "" : "", $"{_s.Volume:0}");
        OnSurfaceActivity();
    }

    private void ToggleMute()
    {
        _s.Muted = !_s.Muted;
        _player.SetMute(_s.Muted);
        UpdateMuteIcon();
        ShowChip(_s.Muted ? "" : "", _s.Muted ? "Silencio" : $"{_s.Volume:0}");
        SaveSoon();
    }

    private void UpdateMuteIcon()
    {
        var g = _s.Muted ? "" : "";
        MuteButton.Content = g;
        if (_osd != null) _osd.FsMute.Content = g;
    }

    private void CycleAspect()
    {
        _aspect = (_aspect + 1) % AspectNames.Length;
        _s.Aspect = AspectNames[_aspect];
        AspectText.Text = _s.Aspect;
        _player.SetAspect((AspectMode)_aspect);
        ShowChip("", _s.Aspect);
        SaveSoon();
    }

    private void ToggleFavorite(ChannelItem? it)
    {
        if (it == null) return;
        it.IsFavorite = !it.IsFavorite;
        if (it.IsFavorite) { if (!_s.Favorites.Contains(it.Url)) _s.Favorites.Add(it.Url); }
        else _s.Favorites.Remove(it.Url);
        ShowChip(it.IsFavorite ? "" : "", it.IsFavorite ? "En favoritos" : "Fuera de favoritos");
        if (it == _current) FavButton.Content = it.IsFavorite ? "" : "";
        if (_kind == KindFav) { RebuildGroups(); ApplyFilter(false); }
        SaveSoon();
    }

    private void SaveSoon() => Restart(_saveTimer);

    // ───────────────────────────── Números ─────────────────────────────

    private void AddDigit(int d)
    {
        if (_digits.Length >= 4) _digits = "";
        _digits += d.ToString(CultureInfo.InvariantCulture);
        ShowChip("", _digits + (_digits.Length < 4 ? " –" : ""));
        _chipTimer.Stop();
        Restart(_digitTimer);
    }

    private void CommitDigits()
    {
        _digitTimer.Stop();
        if (_digits.Length == 0) return;
        int n = int.Parse(_digits, CultureInfo.InvariantCulture);
        _digits = "";
        var it = _items.FirstOrDefault(i => i.Number == n);
        if (it == null) { ShowChip("", $"No existe el {n}"); return; }
        SetOsd(o => o.Chip, false);
        Play(it);
        SelectInList(it, scroll: true);
        if (_guide) ShowGuide(false);
    }

    private void CancelDigits()
    {
        _digits = "";
        _digitTimer.Stop();
        SetOsd(o => o.Chip, false);
    }

    // ───────────────────────────── Teclado y control remoto ─────────────────────────────

    private enum Cmd { None, Next, Prev, PlayPause, Play, Pause, Stop, VolUp, VolDown, Mute, Back, Home, Menu }

    private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == Native.WM_APPCOMMAND)
        {
            int cmd = (int)(((long)lParam >> 16) & 0x0FFF);
            var c = cmd switch
            {
                1 => Cmd.Back,          // APPCOMMAND_BROWSER_BACKWARD
                7 => Cmd.Home,          // APPCOMMAND_BROWSER_HOME
                8 => Cmd.Mute,          // APPCOMMAND_VOLUME_MUTE
                9 => Cmd.VolDown,       // APPCOMMAND_VOLUME_DOWN
                10 => Cmd.VolUp,        // APPCOMMAND_VOLUME_UP
                11 => Cmd.Next,         // APPCOMMAND_MEDIA_NEXTTRACK
                12 => Cmd.Prev,         // APPCOMMAND_MEDIA_PREVIOUSTRACK
                13 => Cmd.Stop,         // APPCOMMAND_MEDIA_STOP
                14 => Cmd.PlayPause,    // APPCOMMAND_MEDIA_PLAY_PAUSE
                46 => Cmd.Play,         // APPCOMMAND_MEDIA_PLAY
                47 => Cmd.Pause,        // APPCOMMAND_MEDIA_PAUSE
                51 => Cmd.Next,         // APPCOMMAND_MEDIA_CHANNEL_UP
                52 => Cmd.Prev,         // APPCOMMAND_MEDIA_CHANNEL_DOWN
                _ => Cmd.None,
            };
            if (c != Cmd.None)
            {
                Run(c);
                handled = true;
                return (IntPtr)1;
            }
        }
        else if (_activateMsg != 0 && (uint)msg == _activateMsg)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Run(Cmd c)
    {
        switch (c)
        {
            case Cmd.Next: Zap(1); break;
            case Cmd.Prev: Zap(-1); break;
            case Cmd.PlayPause: TogglePause(); break;
            case Cmd.Play: if (_pb.State is PlayState.Paused or PlayState.Stopped or PlayState.Failed || _current == null) TogglePause(); break;
            case Cmd.Pause: if (_pb.State is PlayState.Playing or PlayState.Buffering) TogglePause(); break;
            case Cmd.Stop: StopPlayback(); break;
            case Cmd.VolUp: ChangeVolume(5); break;
            case Cmd.VolDown: ChangeVolume(-5); break;
            case Cmd.Mute: ToggleMute(); break;
            case Cmd.Back: Back(true); break;
            case Cmd.Home:
                if (_fs) ShowGuide(true);
                SelectKind(KindAll, focus: false);
                if (_current != null) SelectInList(_current, scroll: true);
                FocusList();
                break;
            case Cmd.Menu:
                if (_fs) ShowGuide(!_guide);
                else FocusList();
                break;
        }
        if (_fs && !_guide && c is not (Cmd.VolUp or Cmd.VolDown or Cmd.Mute)) ShowFsBar();
    }

    /// <summary>Esc / Atrás: cierra lo que esté abierto; en último caso vuelve al canal anterior (solo con Atrás).</summary>
    private void Back(bool recall)
    {
        if (_digits.Length > 0) { CancelDigits(); return; }
        if (_statsVisible) { ToggleStats(); return; }
        if (_fs && _guide) { ShowGuide(false); return; }
        if (_fs) { SetFullscreen(false); return; }
        if (SearchBox.IsKeyboardFocusWithin)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else FocusList();
            return;
        }
        if (recall) Recall();
    }

    private static int DigitOf(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => k - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => k - Key.NumPad0,
        _ => -1,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool typing = SearchBox.IsKeyboardFocusWithin;
        bool inList = ChannelList.IsKeyboardFocusWithin;
        bool inSidebar = Sidebar.IsKeyboardFocusWithin;
        var mods = Keyboard.Modifiers;
        if (_fs) { OnSurfaceActivity(); }

        // Teclas multimedia y de navegador: siempre.
        Cmd media = key switch
        {
            Key.MediaNextTrack => Cmd.Next,
            Key.MediaPreviousTrack => Cmd.Prev,
            Key.MediaPlayPause => Cmd.PlayPause,
            Key.Play => Cmd.Play,
            Key.MediaStop => Cmd.Stop,
            Key.VolumeUp => Cmd.VolUp,
            Key.VolumeDown => Cmd.VolDown,
            Key.VolumeMute => Cmd.Mute,
            Key.BrowserBack => Cmd.Back,
            Key.BrowserHome => Cmd.Home,
            Key.Apps => Cmd.Menu,
            _ => Cmd.None,
        };
        if (media != Cmd.None) { Run(media); e.Handled = true; return; }

        switch (key)
        {
            case Key.F11:
                ToggleFullscreen(); e.Handled = true; return;
            case Key.PageUp:
                Zap(-1); e.Handled = true; return;
            case Key.PageDown:
                Zap(1); e.Handled = true; return;
            case Key.F when mods == ModifierKeys.Control:
            case Key.E when mods == ModifierKeys.Control:
                if (_fs && !_guide) ShowGuide(true);
                SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return;
            case Key.Escape:
                Back(false); e.Handled = true; return;
        }

        if (typing)
        {
            if (key is Key.Down or Key.Enter)
            {
                if (_view.Count > 0) { ChannelList.SelectedIndex = 0; FocusList(); }
                e.Handled = true;
            }
            return;
        }

        int digit = DigitOf(key);
        if (digit >= 0 && mods == ModifierKeys.None) { AddDigit(digit); e.Handled = true; return; }

        switch (key)
        {
            case Key.Enter:
                if (_digits.Length > 0) { CommitDigits(); e.Handled = true; return; }
                if (inList) return; // lo maneja la lista
                if (Keyboard.FocusedElement is ButtonBase or ComboBox or ComboBoxItem or MenuItem) return;
                if (_pb.State == PlayState.Failed) RetryNow();
                else if (_fs) ShowGuide(true);
                else FocusList();
                e.Handled = true; return;
            case Key.Back:
                Back(true); e.Handled = true; return;
            case Key.Home when !inList:
                Run(Cmd.Home); e.Handled = true; return;
            case Key.Space when !(Keyboard.FocusedElement is ButtonBase or ComboBox):
                TogglePause(); e.Handled = true; return;
            case Key.F:
                ToggleFullscreen(); e.Handled = true; return;
            case Key.M:
                ToggleMute(); e.Handled = true; return;
            case Key.A:
                CycleAspect(); e.Handled = true; return;
            case Key.I:
                ToggleStats(); e.Handled = true; return;
            case Key.R:
                RetryNow(); e.Handled = true; return;
            case Key.Multiply:
            case Key.D when mods == ModifierKeys.Control:
                ToggleFavorite(inList ? ChannelList.SelectedItem as ChannelItem : _current); e.Handled = true; return;
            case Key.Add:
            case Key.OemPlus:
                ChangeVolume(5); e.Handled = true; return;
            case Key.Subtract:
            case Key.OemMinus:
                ChangeVolume(-5); e.Handled = true; return;
            case Key.Oem2 when mods == ModifierKeys.None: // "/"
            case Key.Divide:
                if (_fs && !_guide) ShowGuide(true);
                SearchBox.Focus(); e.Handled = true; return;
        }

        // Navegación con flechas dentro del panel (pensada para control remoto).
        if (Keyboard.FocusedElement == GroupBox && !GroupBox.IsDropDownOpen)
        {
            switch (key)
            {
                case Key.Up: FocusCheckedPill(); e.Handled = true; return;
                case Key.Down: FocusList(); e.Handled = true; return;
                case Key.Enter: case Key.Space: GroupBox.IsDropDownOpen = true; e.Handled = true; return;
            }
        }
        if (Keyboard.FocusedElement is RadioButton pill && ListPills.Children.Contains(pill))
        {
            switch (key)
            {
                case Key.Up: SearchBox.Focus(); e.Handled = true; return;
                case Key.Down: GroupBox.Focus(); e.Handled = true; return;
                case Key.Enter: pill.IsChecked = true; FocusList(); e.Handled = true; return;
            }
        }

        // Sin la lista a la vista: las flechas cambian canal (▲▼) y volumen (◀▶).
        if (!inSidebar && !(Keyboard.FocusedElement is ButtonBase or Slider))
        {
            switch (key)
            {
                case Key.Up: Zap(-1); e.Handled = true; return;
                case Key.Down: Zap(1); e.Handled = true; return;
                case Key.Left: ChangeVolume(-5); e.Handled = true; return;
                case Key.Right: ChangeVolume(5); e.Handled = true; return;
            }
        }
        else if (!inSidebar && Keyboard.FocusedElement is ButtonBase && key is Key.Up)
        {
            FocusList(); e.Handled = true;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e) { }

    private void FocusCheckedPill()
    {
        var rb = ListPills.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        rb?.Focus();
    }

    private void ChannelList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (_digits.Length > 0) return;
                if (ChannelList.SelectedItem is ChannelItem it)
                {
                    Play(it);
                    if (_guide) ShowGuide(false);
                }
                e.Handled = true;
                break;
            case Key.Left:
                CycleKind(-1); e.Handled = true;
                Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
                break;
            case Key.Right:
                CycleKind(1); e.Handled = true;
                Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
                break;
            case Key.Up when ChannelList.SelectedIndex <= 0:
                GroupBox.Focus(); e.Handled = true;
                break;
        }
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnderMouse(e) is { } it)
        {
            if (it != _current) Play(it);
            SetFullscreen(true);
        }
    }

    private void ChannelList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnderMouse(e) is { } it && it != _current) Play(it);
    }

    private ChannelItem? ItemUnderMouse(MouseEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as ChannelItem;
    }

    // ───────────────────────────── Botones ─────────────────────────────

    private void Prev_Click(object sender, RoutedEventArgs e) => Zap(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Zap(1);
    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void Retry_Click(object sender, RoutedEventArgs e) => RetryNow();
    private void Fav_Click(object sender, RoutedEventArgs e) => ToggleFavorite(_current);
    private void Aspect_Click(object sender, RoutedEventArgs e) => CycleAspect();
    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleStats_Click(object sender, RoutedEventArgs e) => ToggleStats();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingVolume && IsLoaded) SetVolume(e.NewValue);
    }

    private void CtxPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is ChannelItem it) Play(it);
    }

    private void CtxFav_Click(object sender, RoutedEventArgs e) => ToggleFavorite(ChannelList.SelectedItem as ChannelItem);

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }

    private void CaptionMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CaptionMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _s.Theme = (string)((MenuItem)sender).Tag;
        App.ApplyTheme(_s.Theme);
        UpdateThemeChecks();
        SaveSoon();
    }

    private void UpdateThemeChecks()
    {
        ThemeSystem.IsChecked = _s.Theme == "Sistema";
        ThemeLight.IsChecked = _s.Theme == "Claro";
        ThemeDark.IsChecked = _s.Theme == "Oscuro";
    }

    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((MenuItem)sender).Tag;
        _s.Skin = id;
        Skins.SkinManager.Apply(id);
        UpdateSkinChecks();
        SaveSoon();
    }

    private void UpdateSkinChecks()
    {
        foreach (var mi in new[] { Skin95, Skin98, SkinMe, Skin2000, SkinXP, Skin7, Skin8, Skin1011 })
            mi.IsChecked = (string)mi.Tag == _s.Skin;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var w = new AboutWindow { Owner = this };
        _osd?.Hide();
        w.ShowDialog();
        _osd?.Refresh();
    }

    private async void ManageLists_Click(object sender, RoutedEventArgs e)
    {
        var w = new Lists.ManageListsWindow { Owner = this };
        _osd?.Hide();
        w.ShowDialog();
        _osd?.Refresh();
        if (w.ChangedAnything) await ReloadChannelsAsync();
    }

    private bool _updating;

    private async void UpdateLists_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true;
        FooterText.Text = "Actualizando listas desde sus fuentes…";
        try
        {
            var results = await ChannelStore.UpdateAllAsync(CancellationToken.None);
            await ReloadChannelsAsync();
            var summary = string.Join("  ·  ", results.Select(r => r.Ok ? $"{r.Source}: {r.Message}" : $"{r.Source}: {r.Message} (se mantuvo la copia local)"));
            FooterText.Text = results.Count == 0 ? "No hay listas con URL para actualizar." : summary;
            FooterText.ToolTip = string.Join("\n", results.Select(r => $"{r.Source}: {r.Message}"));
        }
        catch (Exception ex)
        {
            FooterText.Text = "No se pudieron actualizar las listas; se mantienen las copias locales. " + ex.Message;
        }
        finally { _updating = false; }
    }

    /// <summary>Vuelve a leer todas las listas del disco (tras importar, quitar o actualizar) y refresca la interfaz.</summary>
    private async Task ReloadChannelsAsync()
    {
        var items = await Task.Run(() => ChannelStore.LoadAll().Select(c => new ChannelItem(c)).ToList());
        var currentUrl = _current?.Url;
        SetItems(items);
        foreach (var it in _items) it.IsFavorite = _s.Favorites.Contains(it.Url);
        _current = currentUrl != null ? _byUrl.GetValueOrDefault(currentUrl) : null;
        if (_current != null) _current.IsPlaying = true;
        var keepGroup = _group;
        _suppressFilter = true;
        BuildPills();
        SelectKind(_kind, focus: false);
        _group = keepGroup;
        RebuildGroups();
        _suppressFilter = false;
        ApplyFilter(false);
        UpdateNowPlaying();
        UpdateEmptyState();
    }

    // ───────────────────────────── Ventana ─────────────────────────────

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _s.WindowWidth);
        Height = Math.Max(MinHeight, _s.WindowHeight);
        if (!double.IsNaN(_s.WindowLeft) && !double.IsNaN(_s.WindowTop))
        {
            var vl = SystemParameters.VirtualScreenLeft; var vt = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth; var vh = SystemParameters.VirtualScreenHeight;
            if (_s.WindowLeft >= vl - 50 && _s.WindowTop >= vt - 10 && _s.WindowLeft < vl + vw - 100 && _s.WindowTop < vt + vh - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _s.WindowLeft;
                Top = _s.WindowTop;
            }
        }
        if (Width > SystemParameters.WorkArea.Width) Width = SystemParameters.WorkArea.Width;
        if (Height > SystemParameters.WorkArea.Height) Height = SystemParameters.WorkArea.Height;
        if (_s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_fs) SetFullscreen(false);
        var b = RestoreBounds;
        if (!b.IsEmpty)
        {
            _s.WindowLeft = b.Left; _s.WindowTop = b.Top; _s.WindowWidth = b.Width; _s.WindowHeight = b.Height;
        }
        _s.WindowMaximized = WindowState == WindowState.Maximized;
        _s.ListFilter = _kind;
        _s.GroupFilter = _group;
        _s.Save();
        KeepAwake(false);
        _osd?.Close();
        Hide();
        _pb.Shutdown();
        _player.Dispose();
        _diag?.Dispose();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class StringJson : System.Text.Json.Serialization.JsonSerializerContext { }

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);
}
