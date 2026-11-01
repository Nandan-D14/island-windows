using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Island;

// Notification center: every Windows notification is kept (in memory only, last 60), shown with the app's icon,
// with Open / Mute / Dismiss. Muted apps and focus mode hold notifications silently; focus ends with a catch-up.
public partial class MainWindow
{
    record NotifItem(DateTime At, string App, string Title, string Body, string Glyph, bool Held, uint Id = 0, ImageSource? Logo = null)
    {
        public bool Read { get; set; }
    }
    readonly List<NotifItem> _notifs = [];

    int Unread => _notifs.Count(n => !n.Read);

    // from Windows' notification list (existing ones at startup arrive with Fresh = false: listed, not popped)
    void OnWindowsToast(ToastItem t)
    {
        if (t.App.Equals("Island", StringComparison.OrdinalIgnoreCase)) return;
        if (_ai.NotifMode == "Off") return; // notifications switched off in the island
        ImageSource? logo = null;
        if (t.Logo is { Length: > 0 })
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(t.Logo); bi.EndInit(); bi.Freeze();
                logo = bi;
            }
            catch { }
        }
        if (logo != null) _appIcons[t.App] = logo;
        AddNotif(t.App, t.Title, t.Body, "\uEA8F", t.Id, t.Fresh, t.At);
    }

    // Windows toasts + island reminders land here (urgent = reminders: they come through DND / focus)
    void AddNotif(string app, string title, string body, string glyph = "\uEA8F", uint id = 0, bool pop = true, DateTime? at = null, bool urgent = false)
    {
        // the same toast is sometimes reported twice
        if (_notifs.Any(x => x.App == app && x.Title == title && x.Body == body && Math.Abs((x.At - (at ?? DateTime.Now)).TotalSeconds) < 5)) return;
        bool muted = _ai.MutedApps.Contains(app, StringComparer.OrdinalIgnoreCase);
        var n = new NotifItem(at ?? DateTime.Now, app, title, body, glyph, pop && !urgent && (_focus || muted || DndActive), id) { Read = !pop };
        _notifs.Insert(0, n);
        _notifs.Sort((a, b) => b.At.CompareTo(a.At));
        if (_notifs.Count > 60) _notifs.RemoveAt(_notifs.Count - 1);
        if (pop && !n.Held && (urgent || _ai.NotifMode == "Popups"))
        {
            ShowNotification(app, title, body, glyph, urgent, n);
            NotifAppImg.Source = AppIcon(app);
        }
        if (_view == View.Expanded && _page == PageNotifs) n.Read = true;
        RenderNotifs();
    }

    void UpdateBadge()
    {
        int u = Unread;
        TabBadge.Visibility = u > 0 ? Visibility.Visible : Visibility.Collapsed;
        IdleBadge.Visibility = u > 0 ? Visibility.Visible : Visibility.Collapsed;
        IdleBadgeText.Text = u > 9 ? "9+" : u.ToString();
    }

    void MarkNotifsRead()
    {
        foreach (var n in _notifs) n.Read = true;
        UpdateBadge();
    }

    // focus ended: one card summarising what was held
    void FocusCatchUp(DateTime since)
    {
        var held = _notifs.Where(n => n.Held && n.At >= since && !n.Read).ToList();
        if (held.Count == 0) return;
        var apps = string.Join(", ", held.GroupBy(n => n.App).Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key).Take(4));
        ShowNotification("While you were away", $"{held.Count} notification{(held.Count == 1 ? "" : "s")}", apps + "  \u00B7  hover to see them");
    }

    // ================= Notification center page =================
    void RenderNotifs()
    {
        UpdateBadge();
        NotifList.Children.Clear();
        NotifHint.Visibility = _notifs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var n in _notifs.Take(40)) NotifList.Children.Add(NotifRow(n));
        MutedRow.Children.Clear();
        foreach (var app in _ai.MutedApps)
        {
            var b = new Button { Style = (Style)FindResource("Chip"), Content = "\uD83D\uDD15 " + app + "  \u00D7", Margin = new Thickness(0, 0, 6, 4) };
            var a = app;
            b.Click += (_, _) => { _ai.MutedApps.Remove(a); _ai.Save(); RenderNotifs(); ShowToast("Unmuted " + a, Colors.White); };
            MutedRow.Children.Add(b);
        }
        MutedLabel.Visibility = _ai.MutedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement NotifRow(NotifItem n)
    {
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("Tile"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        var img = AppIcon(n.App);
        icon.Child = img != null
            ? new Image { Source = img, Width = 22, Height = 22 }
            : new TextBlock { Text = n.Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var head = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("Dim"), TextTrimming = TextTrimming.CharacterEllipsis };
        head.Inlines.Add(new System.Windows.Documents.Run(n.App + "  \u00B7  " + Ago(n.At)));
        if (n.Held) head.Inlines.Add(new System.Windows.Documents.Run("  \u00B7  held") { Foreground = new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)) });
        var title = new TextBlock { Text = n.Title, FontSize = 13, FontWeight = n.Read ? FontWeights.Normal : FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var body = new TextBlock { Text = n.Body, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = n.Body == "" ? Visibility.Collapsed : Visibility.Visible };
        var text = new StackPanel { Children = { head, title, body } };

        Button Act(string glyph, Action a)
        {
            var b = new Button { Style = (Style)FindResource("Mini"), Content = glyph, FontSize = 11 };
            b.Click += (_, e) => { a(); e.Handled = true; };
            return b;
        }
        var acts = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden,
            Children =
            {
                Act("\uE8A7", () => OpenNotifApp(n.App)),                                                         // open
                Act("\uE7ED", () => { if (!_ai.MutedApps.Contains(n.App)) { _ai.MutedApps.Add(n.App); _ai.Save(); } RenderNotifs(); ShowToast("Muted " + n.App, Colors.White); }), // mute
                Act("\uE711", () => { _notifs.Remove(n); Toasts.Remove(n.Id); RenderNotifs(); }),                    // dismiss (here and in Windows)
            }
        };
        var g = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { icon, text, acts }
        };
        Grid.SetColumn(text, 1); Grid.SetColumn(acts, 2);
        var row = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 6, 4, 6), Margin = new Thickness(0, 0, 0, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g };
        row.MouseEnter += (_, _) => { row.Background = (Brush)FindResource("Tile"); acts.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; acts.Visibility = Visibility.Hidden; };
        row.MouseLeftButtonUp += (_, _) => OpenNotifApp(n.App);
        return row;
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m" : d.TotalDays < 1 ? t.ToString("h:mm tt") : t.ToString("d MMM");
    }

    void OnNotifsClear(object s, RoutedEventArgs e) { _notifs.Clear(); Toasts.ClearAll(); RenderNotifs(); }

    // bring the app forward if it's open, otherwise start it from the Start menu
    void OpenNotifApp(string app)
    {
        var w = FindWin(app);
        if (w != null) { Front(w.H); return; }
        var lnk = StartMenuLink(app);
        if (lnk != null) { try { Shell(lnk); } catch { } }
        else ShowToast("Couldn't find " + app, Colors.White);
    }

    // ================= App icons (from the Start menu shortcut, cached) =================
    readonly Dictionary<string, ImageSource?> _appIcons = new(StringComparer.OrdinalIgnoreCase);
    static List<string>? _startLinks;

    static string? StartMenuLink(string app)
    {
        _startLinks ??= new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) }
            .Where(Directory.Exists).SelectMany(d => { try { return Directory.EnumerateFiles(d, "*.lnk", SearchOption.AllDirectories); } catch { return []; } }).ToList();
        var a = app.Trim();
        if (a.Length < 2) return null;
        return _startLinks.Where(f => Path.GetFileNameWithoutExtension(f).Equals(a, StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
            ?? _startLinks.Where(f => Path.GetFileNameWithoutExtension(f).Contains(a, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).FirstOrDefault();
    }

    ImageSource? AppIcon(string app)
    {
        if (_appIcons.TryGetValue(app, out var cached)) return cached;
        var lnk = StartMenuLink(app);
        var img = lnk != null ? IconFor(lnk) : null;
        _appIcons[app] = img;
        return img;
    }

    // ================= For Gemini and the phone =================
    string NotifSummary(int max = 15)
    {
        if (_notifs.Count == 0) return "No notifications.";
        return string.Join("\n", _notifs.Take(max).Select(n => $"[{Ago(n.At)}] {n.App}: {n.Title}{(n.Body != "" ? " — " + Trim(n.Body, 140) : "")}{(n.Read ? "" : " (unread)")}"));
    }
}
