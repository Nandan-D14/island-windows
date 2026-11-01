using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Island;

// downscaled JPEG of the primary screen (the island itself is excluded from capture)
static class ScreenGrab
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int i);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out Pt p);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style, int width, int color);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] static extern bool Ellipse(IntPtr dc, int l, int t, int r, int b);
    struct Pt { public int X, Y; }

    // ---------- private windows are blacked out before any frame leaves the PC ----------
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rc r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref Rc r, IntPtr brush);
    [StructLayout(LayoutKind.Sequential)] struct Rc { public int L, T, R, B; }

    static readonly System.Text.RegularExpressions.Regex PrivateTitle = new(
        @"incognito|inprivate|private brows|password|1password|bitwarden|keepass|lastpass|dashlane|nordpass|net ?banking|bank|paypal|paytm|phonepe|google pay|\bupi\b|credit card|otp",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly HashSet<string> PrivateProcs = ["1password", "bitwarden", "keepass", "keepassxc", "lastpass", "dashlane", "nordpass", "enpass", "proton pass"];

    public static int HiddenCount { get; private set; }

    // same rule for the active-window hint Gemini gets
    public static bool IsPrivate(string title, string proc) => PrivateTitle.IsMatch(title) || PrivateProcs.Contains(proc.ToLowerInvariant());

    static List<Rc> PrivateRects()
    {
        var list = new List<Rc>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, 14 /*CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256);
            var title = sb.ToString();
            bool priv = title != "" && PrivateTitle.IsMatch(title);
            if (!priv)
            {
                GetWindowThreadProcessId(h, out var pid);
                try { priv = PrivateProcs.Contains(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant()); } catch { }
            }
            if (priv && DwmGetWindowAttribute(h, 9 /*EXTENDED_FRAME_BOUNDS*/, out Rc r, Marshal.SizeOf<Rc>()) == 0) list.Add(r);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ring around the mouse so "what's this?" has a target (white halo + red ring, visible on any background)
    static void DrawPointer(IntPtr dc, double scale)
    {
        if (!GetPhysicalCursorPos(out var p)) return; // physical pixels, same as DESKTOPHORZRES
        int x = (int)(p.X * scale), y = (int)(p.Y * scale), r = 22;
        var oldBrush = SelectObject(dc, GetStockObject(5 /*NULL_BRUSH*/));
        foreach (var (w, c, rr) in new[] { (7, 0xFFFFFF, r), (4, 0x5000FF, r) }) // COLORREF is 0x00BBGGRR
        {
            var pen = CreatePen(0, w, c);
            var oldPen = SelectObject(dc, pen);
            Ellipse(dc, x - rr, y - rr, x + rr, y + rr);
            SelectObject(dc, oldPen); DeleteObject(pen);
        }
        SelectObject(dc, oldBrush);
    }

    public static byte[] Jpeg(int maxWidth = 1280, int quality = 60)
    {
        var screen = GetDC(IntPtr.Zero);
        int sw = GetDeviceCaps(screen, 118), sh = GetDeviceCaps(screen, 117); // physical desktop size
        int w = Math.Min(maxWidth, sw), h = sh * w / sw;
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            SetStretchBltMode(mem, 4 /*HALFTONE*/);
            StretchBlt(mem, 0, 0, w, h, screen, 0, 0, sw, sh, 0x00CC0020 /*SRCCOPY*/);
            double sc = (double)w / sw;
            var priv = PrivateRects();
            HiddenCount = priv.Count;
            foreach (var r in priv)
            {
                var rr = new Rc { L = (int)(r.L * sc), T = (int)(r.T * sc), R = (int)Math.Ceiling(r.R * sc), B = (int)Math.Ceiling(r.B * sc) };
                FillRect(mem, ref rr, GetStockObject(4 /*BLACK_BRUSH*/));
            }
            DrawPointer(mem, sc);
            SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new JpegBitmapEncoder { QualityLevel = quality };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }
}
