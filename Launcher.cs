using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island;

// Quick app launcher: pinned apps stored in %APPDATA%\Island\apps.json
public partial class MainWindow
{
    record PinnedApp(string Name, string Path);
    List<PinnedApp> _apps = [];
    static readonly string AppsFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "apps.json");

    void LoadApps()
    {
        try { if (File.Exists(AppsFile)) _apps = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(AppsFile)) ?? []; } catch { }
        RenderApps();
    }

    void SaveApps()
    {
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AppsFile)!); File.WriteAllText(AppsFile, JsonSerializer.Serialize(_apps)); } catch { }
    }

    void AddApps(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p) && !Directory.Exists(p)) continue;
            if (_apps.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            _apps.Add(new PinnedApp(System.IO.Path.GetFileNameWithoutExtension(p), p));
        }
        SaveApps(); RenderApps();
    }

    void RenderApps()
    {
        AppGrid.Children.Clear();
        foreach (var app in _apps)
        {
            var a = app;
            var tile = MakeTile(IconFor(a.Path), null, a.Name);
            tile.MouseLeftButtonUp += (_, _) =>
            {
                try { Shell(a.Path); } catch { }
                _hover = false; Refresh();
            };
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "Remove" };
            remove.Click += (_, _) => { _apps.Remove(a); SaveApps(); RenderApps(); };
            menu.Items.Add(remove);
            tile.ContextMenu = menu;
            AppGrid.Children.Add(tile);
        }

        var add = MakeTile(null, "\uE710", "Add");
        add.MouseLeftButtonUp += (_, _) => PickApp();
        AppGrid.Children.Add(add);
        AppHint.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Border MakeTile(ImageSource? icon, string? glyph, string label)
    {
        // round glass bubble holding the icon
        var bubble = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("Tile"), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            Child = icon != null
                ? new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        RenderOptions.SetBitmapScalingMode(bubble, BitmapScalingMode.HighQuality);
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(bubble);
        stack.Children.Add(new TextBlock
        {
            Text = label, FontSize = 10, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 54, HorizontalAlignment = HorizontalAlignment.Center
        });
        var b = new Border { Width = 60, Height = 68, Margin = new Thickness(1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = stack };
        var normal = (Brush)FindResource("Tile");
        var hot = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        b.MouseEnter += (_, _) => bubble.Background = hot;
        b.MouseLeave += (_, _) => bubble.Background = normal;
        return b;
    }

    void PickApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick an app to pin",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs",
            Multiselect = true
        };
        _pickerOpen = true;
        try { if (dlg.ShowDialog(this) == true) AddApps(dlg.FileNames); }
        finally { _pickerOpen = false; }
    }
    bool _pickerOpen;

    // drag files / shortcuts from Desktop, Explorer or Start menu onto the island
    void OnDragEnter(object s, DragEventArgs e)
    {
        if (_draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _hover = true;
        if (_page != PageApps || _view != View.Expanded) ShowShelfFiles(); // default target: the shelf
        Refresh();
        e.Effects = DragDropEffects.Link;
    }

    void OnDrop(object s, DragEventArgs e)
    {
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (_page == PageApps) AddApps(files); else { AddShelf(files); ShowShelfFiles(); }
    }

    // ---------- icons via the Windows shell, with real alpha (no black background) ----------
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(Size2 size, int flags, out IntPtr hbm); }
    [StructLayout(LayoutKind.Sequential)] struct Size2 { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapHdr { public int Type, Width, Height, WidthBytes; public short Planes, BitsPixel; public IntPtr Bits; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BitmapHdr b);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    static ImageSource? IconFor(string path)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var f);
            // SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK – 64px so it stays crisp when scaled
            if (f.GetImage(new Size2 { cx = 64, cy = 64 }, 0x4 | 0x1, out var hbm) != 0 || hbm == IntPtr.Zero) return IconFallback(path);
            try
            {
                var hdr = new BitmapHdr();
                GetObject(hbm, Marshal.SizeOf<BitmapHdr>(), ref hdr);
                int w = hdr.Width, h = Math.Abs(hdr.Height);
                var px = new byte[w * h * 4];
                var bi = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, Bits = 32 };
                var dc = CreateCompatibleDC(IntPtr.Zero);
                GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0);
                DeleteDC(dc);

                // some legacy icons have no alpha channel at all – make them opaque instead of invisible
                bool anyAlpha = false;
                for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
                if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

                var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return IconFallback(path); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo { public IntPtr hIcon; public int iIcon; public uint attr; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attr, ref ShFileInfo fi, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static ImageSource? IconFallback(string path)
    {
        try
        {
            var fi = new ShFileInfo();
            SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf<ShFileInfo>(), 0x100);
            if (fi.hIcon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            DestroyIcon(fi.hIcon);
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
