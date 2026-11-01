using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.Media.Control;

namespace Island;

public partial class MainWindow : Window
{
    enum View { Idle, Live, Toast, Hud, Notif, Expanded, Tucked, Assistant, Ask, VoiceMini }

    // media
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    GlobalSystemMediaTransportControlsSessionTimelineProperties? _timeline;
    bool _playing, _hasMedia;

    // state
    bool _hover, _hudOn, _notifOn, _toastOn, _syncing;
    bool? _lastCharging;
    int _lastVol = -1; bool _lastMute;
    View _view = (View)(-1);
    FrameworkElement? _page;

    // timer / stopwatch
    bool _stopwatch, _tRunning;
    TimeSpan _tRemaining, _swAccum;
    DateTime _tEnd, _swStart;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _volPoll = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    readonly DispatcherTimer _notifTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs().Contains("--dump-tools")) // diagnostics: what Gemini receives
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "island-tools.json"), Tools.ToJsonString());
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;

        _tick.Tick += (_, _) => OnTick();
        _volPoll.Tick += (_, _) => PollVolume();
        _hudTimer.Tick += (_, _) => { _hudTimer.Stop(); _hudOn = false; Refresh(); };
        _notifTimer.Tick += (_, _) => { _notifTimer.Stop(); _notifOn = false; Refresh(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastOn = false; Refresh(); };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox || _pickerOpen || _onboarding) return;
            if (WebKeepsIslandOpen()) { _leaveTimer.Start(); return; } // on / typing in a Claude · ChatGPT · Gemini page: check again shortly
            _hover = false; Refresh();
        };
        Deactivated += (_, _) => EndTyping();

        SourceInitialized += (_, _) => MakeToolWindow();
        Loaded += async (_, _) =>
        {
            Stats.Cpu(); Stats.Net();
            OnTick(); PollVolume();
            _tick.Start(); _volPoll.Start();
            Brightness.Watch(v => Dispatcher.InvokeAsync(() => OnBrightnessChanged(v)));
            Toasts.Watch(t => Dispatcher.InvokeAsync(() => OnWindowsToast(t)), OnBattery);
            TabMedia.IsChecked = true;
            LoadTodos();
            LoadApps();
            StartAutoHide();
            Ducker.RecoverAfterCrash(); // a previous run closed while Gemini had other apps turned down
            InitAi(); StartWake(); InitWakePower();
            LoadShelf(); LoadNotes(); StartLive(); LoadSafety(); LoadAbilities(); InitRemote(); InitLooks(); RenderNotifs(); MaybeWelcome(); InitPower(); RenderDnd(); UpdateWinDnd();
            if (Environment.GetCommandLineArgs().Contains("--updated")) _ = Task.Delay(1200).ContinueWith(_ => Dispatcher.Invoke(() => ShowToast($"Updated to Island {AppVersion.ToString(3)}", Color.FromRgb(0x30, 0xD1, 0x58))));
            // tabs open on hover (short delay so sweeping across them doesn't flicker)
            foreach (var tab in new[] { TabMedia, TabGlance, TabTimer, TabShelf, TabApps, TabCtl, TabNotifs, TabAi })
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                t.Tick += (_, _) => { t.Stop(); if (tab.IsMouseOver) tab.IsChecked = true; };
                tab.MouseEnter += (_, _) => t.Start();
                tab.MouseLeave += (_, _) => t.Stop();
            }
            _ = LoadWeather();
            Refresh();
            await InitMedia();
            if (Environment.GetCommandLineArgs().Contains("--shots")) { await Task.Delay(2500); await TakeShots(); return; }
            if (Environment.GetCommandLineArgs().Contains("--health")) { await Task.Delay(2500); await HealthToFile(); return; }
            await Task.Delay(2500); Trim();
        };
    }

    // ================= Window plumbing =================
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);

    void MakeToolWindow()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(_hwnd, -20, GetWindowLong(_hwnd, -20) | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/);
        SetWindowDisplayAffinity(_hwnd, 0x11); // WDA_EXCLUDEFROMCAPTURE: lets us sample what's *behind* us
        InitGlass();
    }

    // ---------- Liquid glass: sample the screen behind, downscale + blur, clip to pill ----------
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int m);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BitmapInfo bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo { public int Size, Width, Height; public short Planes, Bits; public int Compression, SizeImage, XPpm, YPpm, ClrUsed, ClrImportant; }

    const double FullH = 500, SmallH = 52;   // window shrinks to a strip when compact (less to redraw); tall enough for the big chat pages
    int _shrinkGen;
    const int GlassScale = 6;          // capture at 1/6 resolution: tiny + naturally soft
    IntPtr _hwnd, _memDc, _memBmp;
    int _gw, _gh;
    byte[] _px = [], _tmp = [];
    WriteableBitmap? _glass;
    ImageBrush? _glassBrush;
    readonly DispatcherTimer _glassTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    DateTime _morphUntil;
    bool _rendering;

    void InitGlass()
    {
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _gw = Math.Max(1, (int)(Width * s / GlassScale));
        _gh = Math.Max(1, (int)(FullH * s / GlassScale));
        var screen = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screen);
        _memBmp = CreateCompatibleBitmap(screen, _gw, _gh);
        SelectObject(_memDc, _memBmp);
        SetStretchBltMode(_memDc, 4 /*HALFTONE*/);
        ReleaseDC(IntPtr.Zero, screen);

        _px = new byte[_gw * _gh * 4]; _tmp = new byte[_px.Length];
        _glass = new WriteableBitmap(_gw, _gh, 96, 96, PixelFormats.Bgr32, null);
        _glassBrush = new ImageBrush(_glass) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_glassBrush, BitmapScalingMode.Linear);
        Pill.Background = _glassBrush;

        _glassTimer.Tick += (_, _) => CaptureGlass();
        _glassTimer.Start();
        CaptureGlass(); UpdateGlassView();
    }

    void CaptureGlass()
    {
        if (_glass == null) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var screen = GetDC(IntPtr.Zero);
        StretchBlt(_memDc, 0, 0, _gw, _gh, screen, (int)(Left * s), (int)(Top * s), (int)(Width * s), (int)(FullH * s), 0x00CC0020);
        ReleaseDC(IntPtr.Zero, screen);

        var bi = new BitmapInfo { Size = 40, Width = _gw, Height = -_gh, Planes = 1, Bits = 32 };
        GetDIBits(_memDc, _memBmp, 0, (uint)_gh, _px, ref bi, 0);
        BoxBlur(); BoxBlur();
        _glass.WritePixels(new Int32Rect(0, 0, _gw, _gh), _px, _gw * 4, 0);
    }

    // separable 3x3-ish box blur on the tiny bitmap (a few thousand pixels -> microseconds)
    void BoxBlur()
    {
        const int r = 2;
        for (int pass = 0; pass < 2; pass++)
        {
            bool horiz = pass == 0;
            var src = horiz ? _px : _tmp; var dst = horiz ? _tmp : _px;
            for (int y = 0; y < _gh; y++)
                for (int x = 0; x < _gw; x++)
                {
                    int b = 0, g = 0, rr = 0, n = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int xx = horiz ? Math.Clamp(x + k, 0, _gw - 1) : x;
                        int yy = horiz ? y : Math.Clamp(y + k, 0, _gh - 1);
                        int i = (yy * _gw + xx) * 4;
                        b += src[i]; g += src[i + 1]; rr += src[i + 2]; n++;
                    }
                    int o = (y * _gw + x) * 4;
                    dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(rr / n); dst[o + 3] = 255;
                }
        }
    }

    // map the pill's current rect onto the captured bitmap
    void UpdateGlassView()
    {
        if (_glassBrush == null) return;
        double k = _gw / Width;
        double w = Pill.Width, h = Pill.Height, x = (Width - w) / 2, y = Pill.Margin.Top;
        _glassBrush.Viewbox = new Rect(x * k, y * k, w * k, h * k);
    }

    // follow the spring animation frame-by-frame, then unhook (no idle cost)
    void TrackMorph()
    {
        _morphUntil = DateTime.Now.AddMilliseconds(520);
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRender;
    }

    void OnRender(object? s, EventArgs e)
    {
        UpdateGlassView();
        if (DateTime.Now < _morphUntil) return;
        CompositionTarget.Rendering -= OnRender;
        _rendering = false;
        SyncWeb(); // the island finished growing: put the web page on it
    }

    static void Trim()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    // ================= Layout state machine =================
    void Refresh()
    {
        bool timerLive = _tRunning || (_stopwatch && _swAccum > TimeSpan.Zero) || (!_stopwatch && _tRemaining > TimeSpan.Zero);
        bool mini = _aiOn && VoiceSmall;
        View v = _askOn ? View.Ask
               : _hover || _onboarding ? View.Expanded   // hover always opens the menus, even while Gemini talks
               : _aiOn ? (mini ? View.VoiceMini : View.Assistant)
               : _hudOn ? View.Hud
               : _notifOn ? View.Notif
               : _toastOn ? View.Toast
               : _covered ? View.Tucked
               : (_hasMedia || timerLive || _liveLine != null) ? View.Live
               : View.Idle;
        if (v != View.Expanded) StopMirror(); // camera only runs while you're looking at it
        if (v is not (View.VoiceMini or View.Assistant) && ActChip.Visibility == Visibility.Visible) HideChip();

        // compact contents
        bool lineOn = v == View.Live && _liveLine != null;
        LiveText.Visibility = lineOn ? Visibility.Visible : Visibility.Collapsed;
        if (lineOn) { LiveText.Text = _liveLine; LiveText.Margin = new Thickness(_hasMedia ? 34 : 6, 0, timerLive ? 84 : _hasMedia ? 40 : 6, 0); }
        MiniArt.Visibility = v == View.Live && _hasMedia ? Visibility.Visible : Visibility.Collapsed;
        TimerMini.Visibility = v == View.Live && timerLive ? Visibility.Visible : Visibility.Collapsed;
        Bars.Visibility = v == View.Live && _hasMedia && !timerLive ? Visibility.Visible : Visibility.Collapsed;
        CompactText.Visibility = v == View.Toast ? Visibility.Visible : Visibility.Collapsed;
        IdleInfo.Visibility = v == View.Idle ? Visibility.Visible : Visibility.Collapsed;
        SetBars(Bars.Visibility == Visibility.Visible && _playing);

        if (v == _view) return;
        _view = v;
        _glassTimer.Interval = TimeSpan.FromMilliseconds((v == View.Expanded ? 350 : 1000) * (OnBattery() ? 3 : 1));
        if (v == View.Tucked || !GlassOn) _glassTimer.Stop(); else _glassTimer.Start(); // no screen copying over full-screen apps / in black style
        ResizeFor(v is View.Expanded or View.Notif or View.Assistant or View.Ask or View.VoiceMini); // (voice: room for the action chip)

        switch (v)
        {
            case View.Ask: Morph(470, AskCmdBox.Visibility == Visibility.Visible ? 140 : 92, 30); break;
            case View.Expanded: _expSize = ExpandedSize(); Morph(_expSize.w, _expSize.h, 30); break;
            case View.Assistant: Morph(420, 84, 28); break;
            case View.VoiceMini: Morph(360, 34, 17); break;
            case View.Notif: Morph(400, 72, 26); break;
            case View.Hud: Morph(300, 34, 17); break;
            case View.Toast: Morph(260, 32, 16); break;
            case View.Live: Morph(_liveLine != null ? 360 : _hasMedia && timerLive ? 250 : 220, 32, 16); break;
            case View.Tucked: Morph(200, 5, 3); break;
            default: Morph(230, 32, 16); break;
        }

        bool compact = v is View.Idle or View.Live or View.Toast;
        Fade(Compact, compact);
        Fade(Hud, v == View.Hud);
        Fade(Notif, v == View.Notif);
        Fade(AiView, v == View.Assistant);
        ActChip.Margin = new Thickness(0, (v == View.Assistant ? 84 : 34) + 6, 0, 0); // chip sits just under the island
        Fade(VoiceMini, v == View.VoiceMini);
        Fade(AskView, v == View.Ask);
        Fade(Expanded, v == View.Expanded);
        if (v == View.Expanded) RefreshPage();
        SyncWeb();
    }

    async void ResizeFor(bool big)
    {
        int gen = ++_shrinkGen;
        if (big) { Height = FullH; return; }
        await Task.Delay(600); // let the collapse animation finish first
        if (gen == _shrinkGen) Height = SmallH;
    }

    void Morph(double w, double h, double radius)
    {
        var d = TimeSpan.FromMilliseconds(450);
        var ease = new ElasticEase { Oscillations = 1, Springiness = 7, EasingMode = EasingMode.EaseOut };
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = ease });
        // flush with the top edge: square top, rounded bottom (like a notch)
        Pill.CornerRadius = new CornerRadius(0, 0, radius, radius);
        Sheen.CornerRadius = Tint.CornerRadius = new CornerRadius(0, 0, Math.Max(0, radius - 1), Math.Max(0, radius - 1));
        TrackMorph();
    }

    static void Fade(UIElement el, bool show)
    {
        if (show) el.Visibility = Visibility.Visible;
        var a = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 240 : 100))
        { BeginTime = TimeSpan.FromMilliseconds(show ? 130 : 0) };
        if (!show) a.Completed += (_, _) => { if (el.Opacity == 0) el.Visibility = Visibility.Collapsed; };
        el.BeginAnimation(OpacityProperty, a);
    }

    void OnEnter(object s, System.Windows.Input.MouseEventArgs e)
    {
        _leaveTimer.Stop();
        if (_notifOn && !_hover) TabNotifs.IsChecked = true; // hovering a notification opens the notification center
        _hover = true; Refresh();
    }
    void OnLeave(object s, System.Windows.Input.MouseEventArgs e) => _leaveTimer.Start();

    bool _barsOn;
    void SetBars(bool on)
    {
        if (on == _barsOn) return;
        _barsOn = on;
        var rnd = new Random();
        foreach (Rectangle r in Bars.Children)
        {
            var st = (ScaleTransform)r.RenderTransform;
            if (!on) { st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleY = 0.35; continue; }
            var anim = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(280 + rnd.Next(320)))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(anim, 15);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    // ================= Tabs =================
    // chat pages (Gemini chat, AI websites) open bigger than the rest
    (double w, double h) ExpandedSize() => _page == PageAi || _page == PageWeb ? (660, 470) : (520, 226);
    (double w, double h) _expSize;
    // 7 main tabs; Glance / Shelf / Apps hold several pages, switched with ‹ › on the sides
    FrameworkElement[]? _group;
    readonly Dictionary<object, int> _groupIdx = [];

    FrameworkElement[]? GroupOf(object tab) =>
        tab == TabGlance ? [PageCal, PageWeather, PageStats]
      : tab == TabShelf ? [PageShelf, PageNotes, PageTodo]
      : tab == TabApps ? [PageApps, PageTools]
      : tab == TabCtl ? [PageCtl, PagePrivacy, PageLog, PagePhone, PageIsland, PageHealth]
      : tab == TabAi ? [PageAi, PageWeb] : null;

    RadioButton CurrentTab() => new[] { TabMedia, TabGlance, TabTimer, TabShelf, TabApps, TabCtl, TabNotifs, TabAi }.First(t => t.IsChecked == true);

    void OnTab(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _group = GroupOf(s);
        var arrows = _group != null ? Visibility.Visible : Visibility.Collapsed;
        PrevPage.Visibility = NextPage.Visibility = arrows;
        PageHost.Margin = new Thickness(0, 12, 0, 0);
        ShowPage(_group != null ? _group[_groupIdx.GetValueOrDefault(s)] : s == TabMedia ? PageMedia : s == TabTimer ? PageTimer : s == TabNotifs ? PageNotifs : PageAi);
    }

    void OnPrevPage(object s, RoutedEventArgs e) => StepPage(-1);
    void OnNextPage(object s, RoutedEventArgs e) => StepPage(1);

    void StepPage(int dir)
    {
        if (_group == null) return;
        var tab = CurrentTab();
        int i = (_groupIdx.GetValueOrDefault(tab) + dir + _group.Length) % _group.Length; // wraps around
        _groupIdx[tab] = i;
        ShowPage(_group[i], dir);
    }

    // jump straight to a page inside a group (drag & drop, AI, etc.)
    void OpenPage(RadioButton tab, FrameworkElement page)
    {
        if (_page == page && tab.IsChecked == true) return; // drag events repeat; don't re-animate
        var g = GroupOf(tab);
        if (g != null) _groupIdx[tab] = Math.Max(0, Array.IndexOf(g, page));
        if (tab.IsChecked == true) OnTab(tab, new RoutedEventArgs()); else tab.IsChecked = true;
    }

    void ShowPage(FrameworkElement page, int dir = 0)
    {
        foreach (var p in new FrameworkElement[] { PageMedia, PageTimer, PageCal, PageStats, PageCtl, PageWeather, PageTodo, PageAi, PageWeb, PageApps, PageShelf, PageNotes, PageTools, PagePrivacy, PageLog, PagePhone, PageIsland, PageNotifs, PageHealth, PageWelcome })
            p.Visibility = Visibility.Collapsed;
        _page = page;
        if (_view == View.Expanded && ExpandedSize() != _expSize) { _expSize = ExpandedSize(); Morph(_expSize.w, _expSize.h, 30); } // chat pages are bigger
        if (_page != PageTools) StopMirror();
        _page.Visibility = Visibility.Visible;
        _page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        if (dir != 0)
        {   // slide in from the side you clicked
            var tt = new TranslateTransform(dir * 28, 0);
            _page.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        UpdatePageDots();
        RefreshPage();
        SyncWeb();
    }

    void UpdatePageDots()
    {
        PageDots.Children.Clear();
        if (_group == null) return;
        foreach (var p in _group)
            PageDots.Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(2, 0, 2, 0), Fill = new SolidColorBrush(p == _page ? Colors.White : Color.FromArgb(0x55, 255, 255, 255)) });
    }

    void RefreshPage()
    {
        if (_page == PageMedia) UpdateProgress();
        else if (_page == PageWeb) { WebPopped.Visibility = WebInPop ? Visibility.Visible : Visibility.Collapsed; if (!WebInPop) ShowSite(_site); }
        else if (_page == PageTimer) UpdateTimerUi();
        else if (_page == PageCal) BuildCalendar();
        else if (_page == PageStats) UpdateStats();
        else if (_page == PageCtl) _ = SyncControls();
        else if (_page == PageWeather && (DateTime.Now - _wxAt).TotalMinutes > 15) _ = LoadWeather();
        else if (_page == PageNotifs) { RenderNotifs(); MarkNotifsRead(); }
        else if (_page == PageHealth && !_healthBusy) _ = RunHealth(false); // quick local checks; the online test is a button
    }

    // ================= 1-second tick =================
    int _ticks;
    void OnTick()
    {
        if (++_ticks % 120 == 0 && _view != View.Expanded) Trim();
        if (_ticks % 1800 == 0) _ = LoadWeather(); // every 30 min
        CheckCovered();
        UpdateKeepAwake();
        CheckVoiceIdle();
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        IdleTime.Text = DateTime.Now.ToString("h:mm tt");
        UpdateBattery();
        UpdateWakePower();
        LiveTick();
        CheckReminders();
        CheckDnd();
        CheckBusy();
        if (_ticks % 60 == 0) TrimWebs();
        SyncWeb();
        if (_ticks % 5 == 0) CheckBrief();
        _volPoll.Interval = TimeSpan.FromMilliseconds(OnBattery() ? 600 : 250);
        UpdateTimerUi();
        if (_view != View.Expanded) return;
        if (_page == PageStats) UpdateStats();
        else if (_page == PagePhone) UpdatePhoneStatus();
        else if (_page == PageMedia && _playing) UpdateProgress();
    }

    // ================= Media =================
    async Task InitMedia()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Dispatcher.InvokeAsync(Attach);
            Attach();
        }
        catch { }
    }

    void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = _mgr?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        _ = RefreshMedia(); RefreshPlayback(); RefreshTimeline();
    }

    void OnMediaChanged(object? s, object e) => Dispatcher.InvokeAsync(async () => await RefreshMedia());
    void OnPlaybackChanged(object? s, object e) => Dispatcher.InvokeAsync(RefreshPlayback);
    void OnTimelineChanged(object? s, object e) => Dispatcher.InvokeAsync(RefreshTimeline);

    async Task RefreshMedia()
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try { if (_session != null) props = await _session.TryGetMediaPropertiesAsync(); } catch { }
        _hasMedia = props != null && !string.IsNullOrWhiteSpace(props.Title);
        TitleText.Text = _hasMedia ? props!.Title : "Nothing playing";
        ArtistText.Text = _hasMedia ? props!.Artist : "Play something to control it here";

        ImageSource? art = null;
        if (props?.Thumbnail != null)
        {
            try
            {
                using var ras = await props.Thumbnail.OpenReadAsync();
                using var s = ras.AsStream();
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 112; bmp.StreamSource = s; bmp.EndInit(); bmp.Freeze();
                art = bmp;
            }
            catch { }
        }
        ArtImg.ImageSource = art; MiniArtImg.ImageSource = art;
        _view = (View)(-1); Refresh();
        _ = LoadLyrics();
    }

    void RefreshPlayback()
    {
        var info = _session?.GetPlaybackInfo();
        _playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        PlayBtn.Content = _playing ? "\uE769" : "\uE768";
        // keep the screen on while a video plays (music apps report Music and are skipped to save battery)
        _videoPlaying = _playing && info?.PlaybackType != Windows.Media.MediaPlaybackType.Music; // applied on the UI tick (execution state is per-thread)
        Refresh();
        UpdateLyricState();
    }

    bool _videoPlaying, _awake;

    // Windows only counts keyboard/mouse as "activity"; an always-on-top window like the island can make it
    // ignore the video player's own keep-awake request, so the island asks for the display itself while watching
    void UpdateKeepAwake()
    {
        bool want = _videoPlaying || (_playing && _covered); // any media playing in a full-screen app counts too
        if (want == _awake) return;
        _awake = want;
        SetThreadExecutionState(want ? 0x80000003u /*CONTINUOUS|DISPLAY|SYSTEM*/ : 0x80000000u);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    void RefreshTimeline() { _timeline = _session?.GetTimelineProperties(); UpdateProgress(); UpdateLyricState(); }

    void UpdateProgress()
    {
        if (_timeline == null || _timeline.EndTime <= TimeSpan.Zero)
        {
            ProgressFill.Width = 0; PosText.Text = DurText.Text = ""; return;
        }
        var pos = _timeline.Position;
        if (_playing) pos += DateTimeOffset.Now - _timeline.LastUpdatedTime;
        if (pos > _timeline.EndTime) pos = _timeline.EndTime;
        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * pos.TotalSeconds / _timeline.EndTime.TotalSeconds);
        PosText.Text = Fmt(pos); DurText.Text = "-" + Fmt(_timeline.EndTime - pos);
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    async void OnPlay(object s, RoutedEventArgs e) { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
    async void OnNext(object s, RoutedEventArgs e) { if (_session != null) await _session.TrySkipNextAsync(); }
    async void OnPrev(object s, RoutedEventArgs e) { if (_session != null) await _session.TrySkipPreviousAsync(); }

    // ================= Volume / brightness HUD =================
    void PollVolume()
    {
        var a = Audio.Get();
        if (a == null) return;
        var (vol, mute) = a.Value;
        if (vol == _lastVol && mute == _lastMute) return;
        bool first = _lastVol < 0;
        _lastVol = vol; _lastMute = mute;

        _syncing = true; VolSlider.Value = vol; MuteBtn.IsChecked = mute; _syncing = false;
        if (!first && !_hover)
            ShowHud(mute || vol == 0 ? "\uE74F" : vol < 34 ? "\uE993" : vol < 67 ? "\uE994" : "\uE995", mute ? 0 : vol);
    }

    void OnBrightnessChanged(int v)
    {
        _syncing = true; BrightSlider.Value = v; _syncing = false;
        if (!_hover) ShowHud("\uE706", v);
    }

    void ShowHud(string icon, int value)
    {
        HudIcon.Text = icon;
        HudText.Text = value.ToString();
        HudFill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(0, 196 * value / 100.0), TimeSpan.FromMilliseconds(120)));
        _hudOn = true; _hudTimer.Stop(); _hudTimer.Start();
        Refresh();
    }

    // ================= Notifications =================
    void ShowNotification(string app, string title, string body, string icon = "\uEA8F", bool urgent = false, NotifItem? item = null)
    {
        if ((_focus || DndActive) && !urgent) return; // focus / Do Not Disturb: hold notifications
        if (!urgent && HoldIfBusy(app, title, body, icon, item)) return; // busy: wait for a natural break
        NotifIcon.Text = icon;
        NotifAppImg.Source = null;
        NotifApp.Text = app; NotifTitle.Text = title; NotifBody.Text = body;
        NotifBody.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        _notifOn = true; _notifTimer.Stop(); _notifTimer.Start();
        _view = (View)(-1); Refresh();
    }

    // ================= Battery =================
    static bool OnBattery() => Stats.GetSystemPowerStatus(out var p) && p.AC == 0;

    void UpdateBattery()
    {
        if (!Stats.GetSystemPowerStatus(out var p) || p.Flag == 128 || p.Percent > 100)
        {
            BatIcon.Text = ""; BatText.Text = ""; BatBig.Text = "AC"; BatSub.Text = "No battery"; return;
        }
        bool charging = p.AC == 1;
        int pct = p.Percent;
        BatIcon.Text = ((char)((charging ? 0xEBAB : 0xEBA0) + Math.Clamp(pct / 10, 0, 10))).ToString();
        BatIcon.Foreground = charging ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
                           : pct <= 20 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : (Brush)FindResource("Dim");
        BatText.Text = $"{pct}%";
        IdleBatIcon.Text = BatIcon.Text; IdleBatIcon.Foreground = BatIcon.Foreground; IdleBat.Text = BatText.Text;
        BatBig.Text = $"{pct}%";
        BatSub.Text = charging ? "Charging" : p.Life > 0 ? $"{p.Life / 3600}h {p.Life % 3600 / 60}m left" : "On battery";

        if (_lastCharging != null && charging != _lastCharging)
        {
            ShowToast(charging ? $"\u26A1 Charging  {pct}%" : $"On Battery  {pct}%", charging ? Color.FromRgb(0x30, 0xD1, 0x58) : Colors.White);
            if (charging) FlashRim(Color.FromRgb(0x30, 0xD1, 0x58));
        }
        else if (_lastCharging == false && !charging && pct is 20 or 10 && BatText.Tag as int? != pct)
        {
            ShowToast($"Low Battery  {pct}%", Color.FromRgb(0xFF, 0x45, 0x3A));
            FlashRim(Color.FromRgb(0xFF, 0x45, 0x3A));
        }
        BatText.Tag = pct;
        _lastCharging = charging;
    }

    void ShowToast(string text, Color color)
    {
        CompactText.Text = text;
        CompactText.Foreground = new SolidColorBrush(color);
        _toastOn = true; _toastTimer.Stop(); _toastTimer.Start();
        _view = (View)(-1); Refresh();
    }

    // ================= Timer / stopwatch =================
    TimeSpan TimerValue() => _stopwatch
        ? _swAccum + (_tRunning ? DateTime.Now - _swStart : TimeSpan.Zero)
        : _tRunning ? _tEnd - DateTime.Now : _tRemaining;

    void UpdateTimerUi()
    {
        var t = TimerValue();
        if (!_stopwatch && _tRunning && t <= TimeSpan.Zero)
        {
            _tRunning = false; _tRemaining = TimeSpan.Zero; t = TimeSpan.Zero;
            SystemSounds.Exclamation.Play();
            if (_focus) EndFocus(true);
            else ShowToast("\u23F0 Timer done", Color.FromRgb(0xFF, 0x9F, 0x0A));
        }
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (_focus && (_stopwatch || (!_tRunning && t == TimeSpan.Zero))) EndFocus(false); // timer was reset / replaced
        var txt = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        TimerBig.Text = txt;
        TimerMini.Text = (_focus ? "\u25CE " : _stopwatch ? "\u23F1 " : "\u23F2 ") + txt;
        TimerMini.Foreground = new SolidColorBrush(_focus ? Color.FromRgb(0xBF, 0x5A, 0xF2) : Color.FromRgb(0xFF, 0x9F, 0x0A));
        TimerStartBtn.Content = _tRunning ? "\uE769" : "\uE768";
        Refresh();
    }

    void OnTimerStart(object s, RoutedEventArgs e)
    {
        if (_tRunning)
        {
            if (_stopwatch) _swAccum += DateTime.Now - _swStart; else _tRemaining = _tEnd - DateTime.Now;
            _tRunning = false;
        }
        else
        {
            if (!_stopwatch && _tRemaining <= TimeSpan.Zero) return;
            if (_stopwatch) _swStart = DateTime.Now; else _tEnd = DateTime.Now + _tRemaining;
            _tRunning = true;
        }
        UpdateTimerUi();
    }

    void OnTimerReset(object s, RoutedEventArgs e)
    {
        _tRunning = false; _tRemaining = _swAccum = TimeSpan.Zero; _stopwatch = false;
        UpdateTimerUi();
    }

    void OnTimerAdd(object s, RoutedEventArgs e)
    {
        var add = TimeSpan.FromMinutes(int.Parse((string)((Button)s).Tag));
        if (_stopwatch) { _stopwatch = false; _tRunning = false; _swAccum = TimeSpan.Zero; _tRemaining = TimeSpan.Zero; }
        if (_tRunning) _tEnd += add; else _tRemaining += add;
        UpdateTimerUi();
    }

    void OnTimerSet(object s, RoutedEventArgs e)
    {
        if ((string)((Button)s).Tag == "25") { StartFocus(25); return; }
        _stopwatch = false; _swAccum = TimeSpan.Zero;
        _tRemaining = TimeSpan.FromMinutes(int.Parse((string)((Button)s).Tag));
        _tEnd = DateTime.Now + _tRemaining; _tRunning = true;
        UpdateTimerUi();
    }

    void OnStopwatch(object s, RoutedEventArgs e)
    {
        _stopwatch = true; _tRemaining = TimeSpan.Zero; _swAccum = TimeSpan.Zero;
        _swStart = DateTime.Now; _tRunning = true;
        UpdateTimerUi();
    }

    // ================= Calendar =================
    DateTime _calBuilt;
    void BuildCalendar()
    {
        var today = DateTime.Today;
        CalDay.Text = today.ToString("dddd").ToUpperInvariant();
        CalDate.Text = today.Day.ToString();
        CalMonth.Text = today.ToString("MMMM yyyy");
        if (_calBuilt == today) return;
        _calBuilt = today;

        CalGrid.Children.Clear();
        foreach (var d in new[] { "M", "T", "W", "T", "F", "S", "S" })
            CalGrid.Children.Add(new TextBlock { Text = d, FontSize = 10, Foreground = (Brush)FindResource("Dim"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 3) });

        var first = new DateTime(today.Year, today.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) CalGrid.Children.Add(new Border());
        for (int d = 1; d <= DateTime.DaysInMonth(today.Year, today.Month); d++)
        {
            bool isToday = d == today.Day;
            bool weekend = (offset + d - 1) % 7 >= 5;
            CalGrid.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = isToday ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = d.ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isToday ? Brushes.White : weekend ? (Brush)FindResource("Dim") : Brushes.White
                }
            });
        }
    }

    // ================= System stats =================
    void UpdateStats()
    {
        int cpu = Stats.Cpu();
        CpuText.Text = $"{cpu}%";
        CpuBar.Width = 74 * cpu / 100.0;
        CpuBar.Background = new SolidColorBrush(cpu > 80 ? Color.FromRgb(0xFF, 0x45, 0x3A) : cpu > 50 ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0x30, 0xD1, 0x58));

        var (load, used, total) = Stats.Ram();
        RamText.Text = $"{load}%";
        RamBar.Width = 74 * load / 100.0;
        RamSub.Text = $"{used:0.0} / {total:0} GB";

        var (down, up) = Stats.Net();
        NetDown.Text = "↓ " + Stats.Speed(down);
        NetUp.Text = "↑ " + Stats.Speed(up);
    }

    // ================= Quick controls =================
    async Task SyncControls()
    {
        _syncing = true;
        var a = Audio.Get();
        if (a != null) { VolSlider.Value = a.Value.vol; MuteBtn.IsChecked = a.Value.mute; }
        var b = Brightness.Supported ? Brightness.Get() : null;
        BrightRow.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        if (b != null) BrightSlider.Value = b.Value;
        _syncing = false;

        var wifi = await Radios.Find(RadioKind.WiFi);
        var bt = await Radios.Find(RadioKind.Bluetooth);
        WifiBtn.IsEnabled = wifi != null; WifiBtn.IsChecked = wifi?.State == RadioState.On;
        BtBtn.IsEnabled = bt != null; BtBtn.IsChecked = bt?.State == RadioState.On;
    }

    void OnVolSlider(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Audio.SetVolume((int)e.NewValue);
        if (MuteBtn.IsChecked == true) { Audio.SetMute(false); MuteBtn.IsChecked = false; }
    }

    void OnBrightSlider(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing) Brightness.Set((int)e.NewValue);
    }

    void OnMute(object s, RoutedEventArgs e) => Audio.SetMute(MuteBtn.IsChecked == true);

    async void OnWifi(object s, RoutedEventArgs e)
    {
        if (!await Radios.Toggle(RadioKind.WiFi, WifiBtn.IsChecked == true)) WifiBtn.IsChecked = !WifiBtn.IsChecked;
    }

    async void OnBluetooth(object s, RoutedEventArgs e)
    {
        if (!await Radios.Toggle(RadioKind.Bluetooth, BtBtn.IsChecked == true)) BtBtn.IsChecked = !BtBtn.IsChecked;
    }

    void OnScreenshot(object s, RoutedEventArgs e)
    {
        _hover = false; Refresh();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); } catch { }
    }

    void OnLock(object s, RoutedEventArgs e) => LockWorkStation();
}
