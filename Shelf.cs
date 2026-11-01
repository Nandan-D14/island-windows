using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Island;

// File shelf (drop files in, drag them out later) + in-memory clipboard history
public partial class MainWindow
{
    // ================= File shelf =================
    readonly List<string> _shelf = [];
    static readonly string ShelfFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "shelf.json");
    bool _draggingOut;

    void LoadShelf()
    {
        try { if (File.Exists(ShelfFile)) _shelf.AddRange((JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ShelfFile)) ?? []).Where(p => File.Exists(p) || Directory.Exists(p))); } catch { }
        RenderShelf();
        // dragging over a tab switches to it, so you can choose Apps or Shelf while dragging
        TabApps.DragEnter += (_, _) => OpenPage(TabApps, PageApps);
        TabShelf.DragEnter += (_, _) => ShowShelfFiles();
        StartClipboard();
    }

    void ShowShelfFiles() => OpenPage(TabShelf, PageShelf);

    void SaveShelf()
    {
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ShelfFile)!); File.WriteAllText(ShelfFile, JsonSerializer.Serialize(_shelf)); } catch { }
    }

    void AddShelf(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if ((File.Exists(p) || Directory.Exists(p)) && !_shelf.Contains(p, StringComparer.OrdinalIgnoreCase)) _shelf.Add(p);
        SaveShelf(); RenderShelf();
    }

    void RenderShelf()
    {
        ShelfGrid.Children.Clear();
        foreach (var p in _shelf) ShelfGrid.Children.Add(MakeShelfTile(p));
        ShelfHint.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement MakeShelfTile(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        var icon = new Image { Width = 32, Height = 32, Source = IconFor(path), HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = name, FontSize = 10, Foreground = (Brush)FindResource("Dim"), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        var tile = new Border
        {
            Width = 66, Height = 62, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Padding = new Thickness(3, 6, 3, 2), Child = new StackPanel { Children = { icon, label } }
        };
        Point start = default;
        tile.MouseEnter += (_, _) => tile.Background = (Brush)FindResource("Tile");
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { try { Shell(path); } catch { } return; }
            start = e.GetPosition(this);
        };
        tile.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || start == default) return;
            var d = e.GetPosition(this) - start;
            if (Math.Abs(d.X) < 5 && Math.Abs(d.Y) < 5) return;
            start = default;
            _draggingOut = true;
            // copy (never move) so the original file stays safe
            try { DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Link); }
            catch { }
            finally { _draggingOut = false; }
        };
        tile.MouseRightButtonUp += (_, _) => { _shelf.Remove(path); SaveShelf(); RenderShelf(); };
        return tile;
    }

    void OnShelfClear(object s, RoutedEventArgs e) { _shelf.Clear(); SaveShelf(); RenderShelf(); }

    // ================= Clipboard history (memory only, never written to disk) =================
    readonly List<string> _clips = [];
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint f);
    uint _cfExclude, _cfIgnore;

    void StartClipboard()
    {
        _cfExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"); // set by password managers
        _cfIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
        AddClipboardFormatListener(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x031D /*WM_CLIPBOARDUPDATE*/) Dispatcher.BeginInvoke(CaptureClip, System.Windows.Threading.DispatcherPriority.Background);
            return IntPtr.Zero;
        });
        RenderClips();
    }

    async void CaptureClip()
    {
        for (int i = 0; i < 3; i++) // clipboard can be locked for a moment by the app that wrote it
        {
            try
            {
                if (_grabbingSel || IsClipboardFormatAvailable(_cfExclude) || IsClipboardFormatAvailable(_cfIgnore)) return;
                if (!Clipboard.ContainsText()) return;
                var t = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(t)) return;
                AddClip(t.Length > 4000 ? t[..4000] : t);
                return;
            }
            catch { await Task.Delay(80); }
        }
    }

    void AddClip(string t)
    {
        _clips.Remove(t);
        _clips.Insert(0, t);
        if (_clips.Count > 20) _clips.RemoveAt(20);
        RenderClips();
    }

    void RenderClips()
    {
        ClipList.Children.Clear();
        foreach (var c in _clips) ClipList.Children.Add(MakeClipRow(c));
        ClipHint.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement MakeClipRow(string text)
    {
        var one = string.Join(" ", text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var label = new TextBlock { Text = one, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var tr = new Button { Style = (Style)FindResource("Mini"), Content = "\uE8C1", FontSize = 11 };
        Grid.SetColumn(tr, 1);
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { label, tr } };
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 2, 2), Margin = new Thickness(0, 0, 0, 2), Background = (Brush)FindResource("Tile"), Cursor = Cursors.Hand, Child = g };
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
        row.MouseLeave += (_, _) => row.Background = (Brush)FindResource("Tile");
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, tr)) return;
            try { Clipboard.SetText(text); ShowToast("Copied", Colors.White); } catch { }
        };
        row.MouseRightButtonUp += (_, _) => { _clips.Remove(text); RenderClips(); };
        tr.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(_ai.Key)) { ShowToast("Add a Gemini key in the \u2726 tab", Colors.White); return; }
            label.Text = "Translating…";
            try
            {
                var res = await Gemini.Quick(_ai.Key, _ai.TextModel,
                    "Translate the text below into English. If it is already English, translate it into Hindi. Reply with only the translation.\n\n" + text);
                if (res == "") throw new Exception();
                Clipboard.SetText(res); // listener adds it to the history
                ShowToast("Translated \u00B7 copied", Color.FromRgb(0x5E, 0x9C, 0xFF));
            }
            catch { label.Text = one; ShowToast("Translate failed", Color.FromRgb(0xFF, 0x45, 0x3A)); }
        };
        return row;
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }

    void OnClipClear(object s, RoutedEventArgs e) { _clips.Clear(); RenderClips(); }
}
