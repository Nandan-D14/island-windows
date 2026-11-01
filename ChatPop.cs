using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Island;

// Pop-out chat: the Gemini chat (same messages, input, mic, screen share) moves into its own floating
// glass window you can drag and resize. Closing it puts the chat back in the island.
public partial class MainWindow
{
    Window? _pop;
    Grid? _popBody, _popChat, _popNotes, _popWeb;
    Button? _popPin, _tabChat, _tabNotes;
    readonly Dictionary<string, Button> _tabWeb = [];

    void OnChatPop(object s, RoutedEventArgs e) { if (_pop != null) ClosePop(); else OpenChatPop(); }
    void OnChatPopShow(object s, RoutedEventArgs e) { if (_pop != null) { _pop.Show(); _pop.Activate(); } }
    void OnChatPopDock(object s, RoutedEventArgs e) => ClosePop();

    // closed by you (not by the app shutting down): don't reopen it next start
    void ClosePop() { _ai.ChatPopped = false; _pop?.Close(); }

    void OpenChatPop()
    {
        if (_pop != null) { _pop.Activate(); return; }
        EndTyping();

        var w = new Window
        {
            Title = "Gemini", Icon = ((Window)this).Icon, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.CanResize, ShowInTaskbar = true, Topmost = _ai.ChatPopPin,
            MinWidth = 440, MinHeight = 360, Width = 500, Height = 660,
            FontFamily = FontFamily, Foreground = Brushes.White, UseLayoutRounding = true
        };
        // same styles / colours as the island
        foreach (var k in Resources.Keys) w.Resources[k] = Resources[k];
        // resize from every edge (borderless window), no system caption
        WindowChrome.SetWindowChrome(w, new WindowChrome { ResizeBorderThickness = new Thickness(7), CaptionHeight = 0, GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });

        // where it was last time (kept on screen), else the right side of the screen
        var wa = SystemParameters.WorkArea;
        var r = ParseRect(_ai.ChatPopRect);
        if (r is { } p && p.Width >= w.MinWidth && p.Height >= w.MinHeight && wa.IntersectsWith(new Rect(p.X + 40, p.Y + 10, p.Width - 80, 30)))
        { w.Left = p.X; w.Top = p.Y; w.Width = p.Width; w.Height = p.Height; }
        else { w.Left = wa.Right - w.Width - 24; w.Top = wa.Top + 80; }

        // ---- glass card, like the island ----
        var star = new TextBlock { Text = "\u2726", FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _tabChat = new Button { Style = (Style)FindResource("Seg"), Content = "Chat" };
        _tabNotes = new Button { Style = (Style)FindResource("Seg"), Content = "Notes" };
        _tabChat.Click += (_, _) => PopTab("chat");
        _tabNotes.Click += (_, _) => PopTab("notes");
        var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabChat, _tabNotes } };
        _tabWeb.Clear();
        foreach (var site in Sites)
        {
            var b = new Button { Style = (Style)FindResource("Seg"), Content = site.Name };
            var k = site.Key;
            b.Click += (_, _) => PopTab(k);
            _tabWeb[k] = b; tabRow.Children.Add(b);
        }
        var tabs = new Border { Style = (Style)FindResource("SegTrack"), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Arrow, Child = tabRow };
        var title = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Children = { star, tabs } };
        _popPin = new Button { Style = (Style)FindResource("Mini"), FontSize = 12 };
        var close = new Button { Style = (Style)FindResource("Mini"), Content = "\uE711", FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        _popPin.Click += (_, _) => { _ai.ChatPopPin = !_ai.ChatPopPin; _ai.Save(); w.Topmost = _ai.ChatPopPin; ShowPin(); SyncWeb(); };
        close.Click += (_, _) => ClosePop();
        ShowPin();
        var bar = new Grid { Background = Brushes.Transparent, Height = 34, Cursor = Cursors.SizeAll, Margin = new Thickness(16, 6, 10, 0) };
        bar.Children.Add(title);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Arrow, Children = { _popPin, close } });
        bar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) return; try { w.DragMove(); } catch { } };

        _popBody = new Grid { Margin = new Thickness(16, 4, 12, 14) };
        var layout = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        layout.Children.Add(bar);
        layout.Children.Add(_popBody); Grid.SetRow(_popBody, 1);

        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, 255, 255, 255), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 255, 255, 255), 0.35));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, 255, 255, 255), 0.65));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 255, 255, 255), 1));
        var tint = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x16, 0x16, 0x1C), 0));
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(0xF6, 0x08, 0x08, 0x0C), 1));
        // the 6px margin around the card must be hit-testable (fully transparent pixels let the mouse fall through),
        // otherwise the top / bottom resize edges can't be grabbed
        w.Content = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Children = { new Border
        {
            Margin = new Thickness(6), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1.2), BorderBrush = rim, Background = tint,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 },
            Child = layout
        } } };

        // move the chat and the notes in
        _popChat = new Grid(); _popNotes = new Grid { Visibility = Visibility.Collapsed };
        _popWeb = new Grid { Visibility = Visibility.Collapsed, ClipToBounds = true };
        _popBody.Children.Add(_popChat); _popBody.Children.Add(_popNotes); _popBody.Children.Add(_popWeb);
        PageAi.Children.Remove(AiMain); PageAi.Children.Remove(AiSetup);
        _popChat.Children.Add(AiMain); _popChat.Children.Add(AiSetup);
        PageNotes.Children.Remove(NotesHost);
        _popNotes.Children.Add(NotesHost);
        NotesPopped.Visibility = Visibility.Visible;
        AiPopped.Visibility = Visibility.Visible;
        PopBtn.Content = "\uE8A0"; // now: "put it back"
        PopTab(_popTab);

        w.SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, 0x11); // like the island: not in screenshots / screen share
        w.Closing += (_, _) =>
        {
            _ai.ChatPopRect = $"{w.Left:0},{w.Top:0},{w.Width:0},{w.Height:0}";
            // the page window belongs to this window right now: take it back first, or it closes with it
            if (_webWin != null && _webWin.Owner == w) { _webWin.Hide(); try { _webWin.Owner = this; } catch { } _webHost = null; }
        };
        w.LocationChanged += (_, _) => SyncWeb();
        w.SizeChanged += (_, _) => SyncWeb();
        w.StateChanged += (_, _) => SyncWeb();
        _popWeb.SizeChanged += (_, _) => SyncWeb();
        w.Closed += (_, _) => DockChat();
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape && _popTab is "chat" or "notes" && !AiInput.IsKeyboardFocused && !NotesBox.IsKeyboardFocused) ClosePop(); };
        _pop = w;
        _ai.ChatPopped = true; _ai.Save();
        _hover = false; Refresh();
        w.Show(); w.Activate();
        if (_popTab == "chat") AiInput.Focus();
        AiScroll.ScrollToEnd();
    }

    string _popTab = "chat";   // chat · notes · claude · chatgpt · gemini
    void PopTab(string tab)
    {
        _popTab = tab;
        if (_popChat == null || _popNotes == null || _popWeb == null) return;
        bool web = _tabWeb.ContainsKey(tab);
        _popChat.Visibility = tab == "chat" ? Visibility.Visible : Visibility.Collapsed;
        _popNotes.Visibility = tab == "notes" ? Visibility.Visible : Visibility.Collapsed;
        _popWeb.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        Seg(_tabChat!, tab == "chat"); Seg(_tabNotes!, tab == "notes");
        foreach (var (k, b) in _tabWeb) Seg(b, k == tab);
        if (tab == "notes") { Keyboard.ClearFocus(); ShowNotesView(); } // notes open formatted; click to edit
        else if (web) ShowSite(tab); // the page window moves onto this window (same chats, same sign-in)
        else AiInput.Focus();
        SyncWeb();
    }

    // chat goes back into the island
    void DockChat()
    {
        if (_popChat != null) { _popChat.Children.Remove(AiMain); _popChat.Children.Remove(AiSetup); }
        if (_popNotes != null) _popNotes.Children.Remove(NotesHost);
        PageAi.Children.Insert(0, AiMain); PageAi.Children.Insert(1, AiSetup);
        PageNotes.Children.Insert(0, NotesHost);
        NotesPopped.Visibility = Visibility.Collapsed;
        AiPopped.Visibility = Visibility.Collapsed;
        PopBtn.Content = "\uE8A7";
        _pop = null; _popBody = _popChat = _popNotes = _popWeb = null; _popPin = _tabChat = _tabNotes = null; _tabWeb.Clear();
        SyncWeb(); // pages go back to the island (or hide)
        _ai.Save();
        AiScroll.ScrollToEnd();
    }

    void ShowPin()
    {
        if (_popPin == null) return;
        _popPin.Content = _ai.ChatPopPin ? "\uE840" : "\uE718"; // pinned (always on top) / not pinned
        _popPin.Opacity = _ai.ChatPopPin ? 1 : 0.55;
    }

    // the input belongs to the pop-out (a normal window): just focus it
    bool InPop(object s) => _pop != null && s is DependencyObject d && Window.GetWindow(d) == _pop;

    // it was open when the island last closed: open it again
    void RestoreChatPop()
    {
        if (_ai.ChatPopped) Dispatcher.BeginInvoke(OpenChatPop, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Closing += (_, _) => _pop?.Close(); // never outlive the island
    }

    static Rect? ParseRect(string s)
    {
        var p = s.Split(',');
        if (p.Length != 4) return null;
        var v = new double[4];
        for (int i = 0; i < 4; i++) if (!double.TryParse(p[i], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
        return new Rect(v[0], v[1], v[2], v[3]);
    }
}
