using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Island;

// Tools tab: camera mirror, screen colour picker, focus (pomodoro)
public partial class MainWindow
{
    // ================= Camera mirror =================
    MediaCapture? _cam;
    MediaFrameReader? _camReader;
    WriteableBitmap? _camBmp;
    volatile bool _camBusy;
    int _camGen;
    DateTime _camLast;

    async void OnMirror(object s, RoutedEventArgs e)
    {
        if (MirrorBtn.IsChecked == true) await StartMirror(); else StopMirror();
    }

    async Task StartMirror()
    {
        int gen = ++_camGen;
        ToolHint.Text = "Starting camera…"; SwatchBox.Visibility = Visibility.Collapsed;
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var group = groups.FirstOrDefault(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color))
                        ?? throw new Exception("No camera found.");
            var cam = new MediaCapture();
            try
            {
                await cam.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group, SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                    StreamingCaptureMode = StreamingCaptureMode.Video, MemoryPreference = MediaCaptureMemoryPreference.Cpu
                });
            }
            catch
            {   // camera busy in another app: watch alongside it
                cam.Dispose(); cam = new MediaCapture();
                await cam.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group, SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    StreamingCaptureMode = StreamingCaptureMode.Video, MemoryPreference = MediaCaptureMemoryPreference.Cpu
                });
            }
            if (gen != _camGen) { cam.Dispose(); return; }
            var src = cam.FrameSources.Values.First(f => f.Info.SourceKind == MediaFrameSourceKind.Color);
            // small frames are plenty for a mirror and much lighter on the CPU
            var fmt = src.SupportedFormats.Where(f => f.VideoFormat.Width is >= 320 and <= 960)
                         .OrderByDescending(f => f.VideoFormat.Width).FirstOrDefault();
            if (fmt != null) { try { await src.SetFormatAsync(fmt); } catch { } }
            var reader = await cam.CreateFrameReaderAsync(src, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnCamFrame;
            _cam = cam; _camReader = reader;
            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success) throw new Exception("Camera is busy.");
            if (gen != _camGen) { StopMirror(); return; }
            MirrorBox.Visibility = Visibility.Visible; ToolInfo.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (gen != _camGen) return;
            StopMirror();
            ToolHint.Text = ex is UnauthorizedAccessException
                ? "Camera blocked. Turn on Settings › Privacy › Camera › Let desktop apps access your camera."
                : ex.Message;
        }
    }

    void OnCamFrame(MediaFrameReader r, MediaFrameArrivedEventArgs a)
    {
        using var frame = r.TryAcquireLatestFrame();
        var sb = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sb == null || _camBusy || (DateTime.Now - _camLast).TotalMilliseconds < 40) return; // ~25 fps max
        _camLast = DateTime.Now; _camBusy = true;
        using var bmp = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? SoftwareBitmap.Copy(sb) : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var buf = new byte[w * h * 4];
        bmp.CopyToBuffer(buf.AsBuffer());
        int gen = _camGen;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (gen != _camGen) return;
                if (_camBmp == null || _camBmp.PixelWidth != w || _camBmp.PixelHeight != h)
                {
                    _camBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null); // Bgr32: ignore camera alpha
                    MirrorImg.ImageSource = _camBmp;
                }
                _camBmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
            }
            finally { _camBusy = false; }
        });
    }

    void StopMirror()
    {
        if (_cam == null && MirrorBtn.IsChecked != true) return;
        _camGen++;
        var cam = _cam; var reader = _camReader;
        _cam = null; _camReader = null; _camBmp = null; MirrorImg.ImageSource = null;
        MirrorBtn.IsChecked = false;
        MirrorBox.Visibility = Visibility.Collapsed; ToolInfo.Visibility = Visibility.Visible;
        if (ToolHint.Text == "Starting camera…") ToolHint.Text = "Mirror opens your camera here. Colours you pick are copied as hex.";
        _ = Task.Run(async () =>
        {
            try { if (reader != null) { reader.FrameArrived -= OnCamFrame; await reader.StopAsync(); reader.Dispose(); } } catch { }
            try { cam?.Dispose(); } catch { }
        });
    }

    // ================= Colour picker =================
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out PickPt p);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    [StructLayout(LayoutKind.Sequential)] struct PickPt { public int X, Y; }

    static Color PixelAtCursor()
    {
        GetPhysicalCursorPos(out var p);
        var dc = GetDC(IntPtr.Zero);
        uint c = GetPixel(dc, p.X, p.Y);
        ReleaseDC(IntPtr.Zero, dc);
        return Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
    }

    void OnPickColor(object s, RoutedEventArgs e) => PickColor();

    void PickColor()
    {
        var swatch = new Ellipse { Width = 18, Height = 18, Stroke = Brushes.White, StrokeThickness = 1.5 };
        var hex = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        var loupe = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x16, 0x1A)), CornerRadius = new CornerRadius(14), Padding = new Thickness(8, 5, 12, 5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, hex } }, IsHitTestVisible = false
        };
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Children = { loupe } };
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight,
            Cursor = Cursors.Cross, Content = canvas
        };
        Color cur = Colors.Black;
        void Update(Point pos)
        {
            cur = PixelAtCursor();
            swatch.Fill = new SolidColorBrush(cur);
            hex.Text = $"#{cur.R:X2}{cur.G:X2}{cur.B:X2}";
            Canvas.SetLeft(loupe, pos.X + 18); Canvas.SetTop(loupe, pos.Y + 18);
        }
        win.SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(win).Handle, 0x11); // don't sample our own overlay
        win.MouseMove += (_, ev) => Update(ev.GetPosition(canvas));
        win.MouseLeftButtonDown += (_, _) =>
        {
            var h = $"#{cur.R:X2}{cur.G:X2}{cur.B:X2}";
            win.Close();
            try { Clipboard.SetText(h); } catch { }
            SwatchBox.Background = new SolidColorBrush(cur); SwatchBox.Visibility = Visibility.Visible;
            ToolHint.Text = $"{h}  ·  rgb({cur.R}, {cur.G}, {cur.B})  ·  copied";
            double lum = (0.299 * cur.R + 0.587 * cur.G + 0.114 * cur.B) / 255;
            ShowToast($"\u25CF {h} copied", lum > 0.3 ? cur : Colors.White);
        };
        win.MouseRightButtonDown += (_, _) => win.Close();
        win.KeyDown += (_, ev) => { if (ev.Key == Key.Escape) win.Close(); };
        win.Show(); win.Activate();
        Update(Mouse.GetPosition(canvas));
    }

    // ================= Focus (pomodoro) =================
    bool _focus;
    DateTime _focusSince;

    void StartFocus(double minutes)
    {
        _focus = true; _focusSince = DateTime.Now;
        _stopwatch = false; _swAccum = TimeSpan.Zero;
        _tRemaining = TimeSpan.FromMinutes(minutes); _tEnd = DateTime.Now + _tRemaining; _tRunning = true;
        FocusBtn.IsChecked = true; FocusText.Text = "Focusing…";
        UpdateTimerUi();
        ShowToast($"\u25CE Focus {minutes:0} min \u00B7 alerts held", Color.FromRgb(0xBF, 0x5A, 0xF2));
    }

    void EndFocus(bool done)
    {
        if (!_focus) return;
        _focus = false;
        FocusBtn.IsChecked = false; FocusText.Text = "Focus 25 min";
        if (done) ShowToast("\u25CE Focus done \u00B7 take a 5 min break", Color.FromRgb(0xBF, 0x5A, 0xF2));
        _ = Task.Delay(done ? 3500 : 300).ContinueWith(_ => Dispatcher.Invoke(() => FocusCatchUp(_focusSince))); // after the toast
    }

    void OnFocus(object s, RoutedEventArgs e)
    {
        if (FocusBtn.IsChecked == true) { StartFocus(25); return; }
        EndFocus(false);
        _tRunning = false; _tRemaining = TimeSpan.Zero;
        UpdateTimerUi();
    }
}
