using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Island;

// Claude, ChatGPT and Gemini websites inside the island (✦ tab, page 2) and the pop-out window.
// The island and the pop-out are see-through windows, where a normal web view can't draw (and the "composition"
// web view crashed). So the pages live in one small normal window that sits exactly on top of the web area of
// whichever is showing it and follows it around. One Edge web view per site, signed in with your own accounts
// (cookies in %LOCALAPPDATA%\Island\WebView2). Loaded on first use, freed after 15 min unused.
// Links to other sites open in your normal browser.
public partial class MainWindow
{
    record WebSite(string Key, string Name, string Url, string[] Hosts);

    static readonly WebSite[] Sites =
    [
        new("claude", "Claude", "https://claude.ai/new", ["claude.ai", "claude.com", "anthropic.com"]),
        new("chatgpt", "ChatGPT", "https://chatgpt.com/", ["chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com", "sora.com"]),
        new("gemini", "Gemini", "https://gemini.google.com/app", ["gemini.google.com"]),
    ];
    // sign-in, captcha and payment pages every site may pass through
    static readonly string[] LoginHosts = ["google.com", "gstatic.com", "googleapis.com", "googleusercontent.com", "youtube.com", "apple.com", "icloud.com",
        "cdn.apple-cloudkit.com", "microsoftonline.com", "live.com", "microsoft.com", "stripe.com", "stripe.network", "cloudflare.com", "hcaptcha.com",
        "recaptcha.net", "arkoselabs.com", "auth0.com", "intercom.io", "intercomcdn.com"];

    static bool Allowed(WebSite s, Uri u) => s.Hosts.Concat(LoginHosts).Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    readonly Dictionary<string, WebView2> _webs = [];
    readonly Dictionary<string, DateTime> _webSeen = [];
    CoreWebView2Environment? _webEnv;
    string _site = "claude";
    Window? _webWin;          // the small normal window that holds the pages
    Grid? _webGrid;
    TextBlock? _webMsg;

    void OnWebSite(object s, RoutedEventArgs e) => ShowSite((string)((Button)s).CommandParameter);
    void OnWebPop(object s, RoutedEventArgs e) { OpenChatPop(); PopTab(_site); }

    void EnsureWebWin()
    {
        if (_webWin != null) return;
        _webGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1E, 0x1D)) };
        _webMsg = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 255, 255, 255)), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20) };
        _webGrid.Children.Add(_webMsg);
        _webWin = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Width = 300, Height = 200, Content = _webGrid, Title = "Island web", Background = _webGrid.Background
        };
        _webWin.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_webWin).Handle;
            SetWindowDisplayAffinity(h, 0x11);                 // like the island: not in screenshots / screen share
            int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4); // Windows 11 rounded corners
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80); // tool window: not in Alt+Tab
        };
        _webWin.Deactivated += (_, _) => { if (_webHost == this) _leaveTimer.Start(); }; // clicked elsewhere: the island may close
    }

    // show one site (creates its web view the first time)
    async void ShowSite(string key)
    {
        _site = key;
        foreach (var b in WebSiteRow.Children.OfType<Button>()) Seg(b, (string)b.CommandParameter == key);
        foreach (var (k, b) in _tabWeb) Seg(b, k == key);
        EnsureWebWin();
        foreach (var (k, v) in _webs) v.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
        _webSeen[key] = DateTime.Now;
        SyncWeb();
        if (_webs.ContainsKey(key)) { _webMsg!.Visibility = Visibility.Collapsed; return; }

        var site = Sites.First(x => x.Key == key);
        _webMsg!.Text = $"Loading {site.Name}\u2026"; _webMsg.Visibility = Visibility.Visible;
        try
        {
            _webEnv ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Island", "WebView2"));
            if (_webs.ContainsKey(key)) return; // opened twice quickly
            var wv = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x1F, 0x1E, 0x1D), Visibility = _site == key ? Visibility.Visible : Visibility.Collapsed };
            _webs[key] = wv;
            _webGrid!.Children.Add(wv);
            await wv.EnsureCoreWebView2Async(_webEnv);
            var core = wv.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // stay on the site (and its sign-in pages); anything else opens in your browser.
            // Never launch anything *inside* a web view event (it re-enters): do it right after.
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme is "about" or "data" or "blob") return;
                if (u.Scheme == Uri.UriSchemeHttps && Allowed(site, u)) return;
                e.Cancel = true;
                if (e.IsUserInitiated || !e.IsRedirected) OpenExternalLater(u);
            };
            core.NewWindowRequested += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) { e.Handled = true; return; }
                if (u.Scheme == Uri.UriSchemeHttps && Allowed(site, u)) return; // sign-in pop-ups stay real pop-ups, or the login never comes back
                e.Handled = true;
                OpenExternalLater(u);
            };
            core.NavigationCompleted += (_, _) => { if (_site == key && _webMsg != null) _webMsg.Visibility = Visibility.Collapsed; };
            core.Navigate(site.Url);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            DropWeb(key);
            _webMsg.Text = "This needs the Microsoft Edge WebView2 Runtime.\nGet it from microsoft.com/edge/webview2, then try again.";
        }
        catch (Exception ex)
        {
            DropWeb(key);
            _webMsg.Text = $"Couldn't open {site.Name}: " + ex.Message;
        }
    }

    void DropWeb(string key)
    {
        if (!_webs.Remove(key, out var wv)) return;
        _webGrid?.Children.Remove(wv);
        try { wv.Dispose(); } catch { }
    }

    // ---- keep the page window exactly on top of the web area that's showing ----
    Window? _webHost;    // island or pop-out

    // where the pages should be right now (null = nowhere: hide)
    (Window host, FrameworkElement area)? WebTarget()
    {
        if (_pop != null && _popWeb is { IsVisible: true } && _pop.WindowState != WindowState.Minimized) return (_pop, _popWeb);
        if (_view == View.Expanded && _page == PageWeb && WebFrame.IsVisible && DateTime.Now >= _morphUntil) return (this, WebFrame);
        return null;
    }

    bool WebInPop => _pop != null && _popWeb is { IsVisible: true };

    void SyncWeb()
    {
        if (_webWin == null) return;
        var t = WebTarget();
        if (t is not { } tg || _webs.Count == 0 && _webMsg?.Visibility != Visibility.Visible)
        {
            if (_webWin.IsVisible) _webWin.Hide();
            return;
        }
        var (host, area) = tg;
        if (_webHost != host || _webWin.Owner != host)
        {
            if (_webWin.IsVisible) _webWin.Hide();
            try { _webWin.Owner = host; } catch { }
            _webHost = host;
        }
        _webWin.Topmost = host.Topmost;
        Point p;
        try { p = area.TranslatePoint(new Point(0, 0), host); } catch { return; }
        double x = host.Left + p.X, y = host.Top + p.Y, w = Math.Max(1, area.ActualWidth), h = Math.Max(1, area.ActualHeight);
        if (Math.Abs(_webWin.Left - x) > 0.5) _webWin.Left = x;
        if (Math.Abs(_webWin.Top - y) > 0.5) _webWin.Top = y;
        if (Math.Abs(_webWin.Width - w) > 0.5) _webWin.Width = w;
        if (Math.Abs(_webWin.Height - h) > 0.5) _webWin.Height = h;
        if (!_webWin.IsVisible) _webWin.Show();
    }

    // the mouse is over the page window (counts as hovering the island)
    bool CursorOnWeb()
    {
        if (_webWin is not { IsVisible: true } || _webHost != this) return false;
        if (!GetCursorPos(out var c)) return false;
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double x = c.X / s, y = c.Y / s;
        return x >= _webWin.Left && x <= _webWin.Left + _webWin.ActualWidth && y >= _webWin.Top && y <= _webWin.Top + _webWin.ActualHeight;
    }
    [StructLayout(LayoutKind.Sequential)] struct CurPt { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CurPt p);

    // typing in a page inside the island keeps it open
    bool WebKeepsIslandOpen() => _webHost == this && _webWin is { IsVisible: true } && (_webWin.IsActive || CursorOnWeb());

    // every minute: a site you haven't looked at for 15 min is closed to free memory (you stay signed in)
    void TrimWebs()
    {
        bool showing = _webWin?.IsVisible == true;
        foreach (var key in _webs.Keys.ToList())
            if (!(showing && key == _site) && (DateTime.Now - _webSeen.GetValueOrDefault(key)).TotalMinutes > 15) DropWeb(key);
    }

    // after the current event has fully returned, and the same link at most once every 3 s
    string _lastExternal = ""; DateTime _lastExternalAt;
    void OpenExternalLater(Uri u) => Dispatcher.BeginInvoke(() =>
    {
        if (u.AbsoluteUri == _lastExternal && (DateTime.Now - _lastExternalAt).TotalSeconds < 3) return;
        _lastExternal = u.AbsoluteUri; _lastExternalAt = DateTime.Now;
        OpenExternal(u);
    }, System.Windows.Threading.DispatcherPriority.Background);

    static void OpenExternal(Uri u)
    {
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeMailto) return; // no file:, ms-settings:, etc.
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}
