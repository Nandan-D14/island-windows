using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island;

// Developer aid: `Island.exe --shots` renders every page to %TEMP%\island-shots\*.png.
// (The island hides itself from normal screenshots, so this is the only way to see it from outside.)
public partial class MainWindow
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct PtS { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PtS p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    async Task TakeShots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "island-shots");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
        _onboarding = true; _hover = true; Refresh();
        await Task.Delay(900);

        var pages = new (string name, Action show)[]
        {
            ("01-media", () => OpenPage(TabMedia, PageMedia)),
            ("02-calendar", () => OpenPage(TabGlance, PageCal)),
            ("03-weather", () => OpenPage(TabGlance, PageWeather)),
            ("04-stats", () => OpenPage(TabGlance, PageStats)),
            ("05-timer", () => OpenPage(TabTimer, PageTimer)),
            ("06-shelf", () => OpenPage(TabShelf, PageShelf)),
            ("07-notes", () => OpenPage(TabShelf, PageNotes)),
            ("08-todo", () => OpenPage(TabShelf, PageTodo)),
            ("09-apps", () => OpenPage(TabApps, PageApps)),
            ("10-tools", () => OpenPage(TabApps, PageTools)),
            ("11-controls", () => OpenPage(TabCtl, PageCtl)),
            ("12-safety", () => OpenPage(TabCtl, PagePrivacy)),
            ("13-activity", () => OpenPage(TabCtl, PageLog)),
            ("14-phone", () => OpenPage(TabCtl, PagePhone)),
            ("15-settings", () => OpenPage(TabCtl, PageIsland)),
            ("16-health", () => OpenPage(TabCtl, PageHealth)),
            ("17-notifications", () => OpenPage(TabNotifs, PageNotifs)),
            ("17b-notif-options", () => NotifOptions.Visibility = Visibility.Visible),
            ("18-gemini", () => { NotifOptions.Visibility = Visibility.Collapsed; OpenPage(TabAi, PageAi); }),
        };
        foreach (var (name, show) in pages)
        {
            show();
            await Task.Delay(500); // let the fade/slide finish
            Save(Path.Combine(dir, name + ".png"));
        }
        // compact states
        _onboarding = false; _hover = false; Refresh();
        await Task.Delay(900);
        Save(Path.Combine(dir, "20-pill-idle.png"));
        ShowNotification("WhatsApp", "Rahul", "Are we still meeting at 6?");
        await Task.Delay(900);
        Save(Path.Combine(dir, "21-pill-notification.png"));
        // small voice view + action chip
        _notifOn = false;
        _aiOn = true; SetOrb(OrbState.Speaking);
        AiYou.Text = "set a timer for five minutes"; AiSays.Text = "Sure, five minutes on the clock!";
        Refresh();
        await Task.Delay(900);
        ShowChip("\u2713 Timer 5:00", 5000);
        await Task.Delay(400);
        SaveRoot(Path.Combine(dir, "22-voice-mini.png"));
        ShowChip("Hmm, couldn't find Spotify", 5000);
        await Task.Delay(400);
        SaveRoot(Path.Combine(dir, "23-voice-chip-fail.png"));
        // pop-out chat window
        _aiOn = false; Refresh();
        bool wasPopped = _ai.ChatPopped; var rect = _ai.ChatPopRect;
        AppendChat("You", "what's the weather like?");
        RenderReply(AppendChat("Gemini", "It's **24°** and sunny in Bengaluru.\n- Evening: *cooler*, 19°\n- Rain: `0%`\n\nNice for a walk!"));
        OpenChatPop();
        PopTab("chat");
        await Task.Delay(900);
        void SavePop(string name)
        {
            if (_pop?.Content is not FrameworkElement pc) return;
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var bmp = new RenderTargetBitmap((int)(pc.ActualWidth * s) + 12, (int)(pc.ActualHeight * s) + 12, 96 * s, 96 * s, PixelFormats.Pbgra32);
            bmp.Render(pc);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, name));
            enc.Save(fs);
        }
        SavePop("24-chat-pop.png");
        // resize edges: does Windows find the pop-out (not what's behind it) at each edge, and which edge is it?
        if (_pop != null)
        {
            var h = new System.Windows.Interop.WindowInteropHelper(_pop).Handle;
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int L = (int)(_pop.Left * s), T = (int)(_pop.Top * s), R = (int)((_pop.Left + _pop.ActualWidth) * s), B = (int)((_pop.Top + _pop.ActualHeight) * s);
            var log = new System.Text.StringBuilder();
            foreach (var (name, x, y) in new[] { ("top", (L + R) / 2, T + 3), ("bottom", (L + R) / 2, B - 3), ("left", L + 3, (T + B) / 2), ("right", R - 3, (T + B) / 2), ("bottom-right", R - 3, B - 3) })
            {
                var under = WindowFromPoint(new PtS { X = x, Y = y });
                int hit = (int)SendMessage(h, 0x84, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
                log.AppendLine($"{name}: ours={under == h} hit={hit}");
            }
            File.WriteAllText(Path.Combine(dir, "resize-test.txt"), log.ToString());
        }
        PopTab("notes");
        await Task.Delay(400);
        SavePop("25-chat-pop-notes.png");
        PopTab("chatgpt");
        for (int k = 0; k < 40 && (!_webs.TryGetValue("chatgpt", out var wv0) || wv0.CoreWebView2 == null || wv0.CoreWebView2.DocumentTitle == ""); k++) await Task.Delay(500);
        await Task.Delay(1500);
        File.WriteAllText(Path.Combine(dir, "claude-test.txt"),
            _webs.TryGetValue("chatgpt", out var wv1) && wv1.CoreWebView2 is { } cw ? $"url={cw.Source}\ntitle={cw.DocumentTitle}\nsize={wv1.ActualWidth:0}x{wv1.ActualHeight:0}" : "webview: " + (_webMsg?.Text ?? "not created"));
        SavePop("26-chat-pop-claude.png");
        PopTab("chat");
        ClosePop(); _ai.ChatPopped = wasPopped; _ai.ChatPopRect = rect; _ai.Save();
        Application.Current.Shutdown();
    }

    // the whole top of the window (island + anything under it, like the action chip)
    void SaveRoot(string file)
    {
        var root = (FrameworkElement)Content;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX, w = root.ActualWidth, h = 90;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None }, null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap((int)(w * s), (int)(h * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    void Save(string file)
    {
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var w = (int)Math.Ceiling(Pill.ActualWidth * s); var h = (int)Math.Ceiling(Pill.ActualHeight * s);
        if (w < 2 || h < 2) return;
        // paint over a mid-grey "desktop" so the glass edges are visible
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A)), null, new Rect(0, 0, Pill.ActualWidth + 40, Pill.ActualHeight + 30));
            dc.DrawRectangle(new VisualBrush(Pill), null, new Rect(20, 0, Pill.ActualWidth, Pill.ActualHeight));
        }
        var bmp = new RenderTargetBitmap((int)((Pill.ActualWidth + 40) * s), (int)((Pill.ActualHeight + 30) * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
