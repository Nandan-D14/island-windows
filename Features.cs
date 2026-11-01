using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Island;

public partial class MainWindow
{
    // ================= Weather (Open-Meteo, no API key) =================
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    DateTime _wxAt;

    async Task LoadWeather()
    {
        try
        {
            var (lat, lon, city) = await Locate();

            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=5");
            using var wx = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = wx.RootElement.GetProperty("current");
            var daily = wx.RootElement.GetProperty("daily");

            int code = cur.GetProperty("weather_code").GetInt32();
            bool day = cur.GetProperty("is_day").GetInt32() == 1;
            int temp = (int)Math.Round(cur.GetProperty("temperature_2m").GetDouble());
            var (icon, desc) = Wmo(code, day);

            WxCity.Text = city;
            WxIcon.Text = icon;
            WxTemp.Text = $"{temp}°";
            WxDesc.Text = desc;
            WxSub.Text = $"Feels {Math.Round(cur.GetProperty("apparent_temperature").GetDouble())}°  ·  Humidity {cur.GetProperty("relative_humidity_2m").GetInt32()}%";
            WxMini.Text = $"{icon} {temp}°";
            IdleWx.Text = WxMini.Text;

            WxDays.Children.Clear();
            var dates = daily.GetProperty("time"); var codes = daily.GetProperty("weather_code");
            var max = daily.GetProperty("temperature_2m_max"); var min = daily.GetProperty("temperature_2m_min");
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                var d = DateTime.Parse(dates[i].GetString()!);
                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                panel.Children.Add(new TextBlock { Text = i == 0 ? "Today" : d.ToString("ddd"), FontSize = 11, Foreground = (Brush)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Center });
                panel.Children.Add(new TextBlock { Text = Wmo(codes[i].GetInt32(), true).icon, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 20, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
                panel.Children.Add(new TextBlock { Text = $"{Math.Round(max[i].GetDouble())}°", FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
                panel.Children.Add(new TextBlock { Text = $"{Math.Round(min[i].GetDouble())}°", FontSize = 11, Foreground = (Brush)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Center });
                WxDays.Children.Add(panel);
            }
            _wxAt = DateTime.Now;
        }
        catch
        {
            if (_wxAt == default) { WxCity.Text = "Offline"; WxDesc.Text = "Weather unavailable"; }
        }
    }

    // approximate location from IP (tries a fallback provider)
    static async Task<(double lat, double lon, string city)> Locate()
    {
        foreach (var url in new[] { "https://ipwho.is/", "https://get.geojs.io/v1/ip/geo.json" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var r = doc.RootElement;
                static double Num(JsonElement e) => e.ValueKind == JsonValueKind.String
                    ? double.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : e.GetDouble();
                return (Num(r.GetProperty("latitude")), Num(r.GetProperty("longitude")),
                        r.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "");
            }
            catch { }
        }
        throw new Exception("location unavailable");
    }

    static (string icon, string desc) Wmo(int c, bool day) => c switch
    {
        0 => (day ? "\u2600" : "\u263D", "Clear"),
        1 or 2 => (day ? "\u26C5" : "\u2601", "Partly cloudy"),
        3 => ("\u2601", "Cloudy"),
        45 or 48 => ("\u2601", "Fog"),
        >= 51 and <= 57 => ("\u2602", "Drizzle"),
        >= 61 and <= 67 => ("\u2614", "Rain"),
        >= 71 and <= 77 => ("\u2744", "Snow"),
        >= 80 and <= 82 => ("\u2614", "Showers"),
        85 or 86 => ("\u2744", "Snow showers"),
        >= 95 => ("\u26A1", "Thunderstorm"),
        _ => ("\u2601", "—")
    };

    // ================= To-do (saved to %APPDATA%\Island\todos.json) =================
    record Todo(string Text, bool Done);
    List<Todo> _todos = [];
    static readonly string TodoFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "todos.json");

    void LoadTodos()
    {
        try { if (File.Exists(TodoFile)) _todos = JsonSerializer.Deserialize<List<Todo>>(File.ReadAllText(TodoFile)) ?? []; } catch { }
        RenderTodos();
    }

    void SaveTodos()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TodoFile)!);
            File.WriteAllText(TodoFile, JsonSerializer.Serialize(_todos));
        }
        catch { }
    }

    void RenderTodos()
    {
        TodoList.Children.Clear();
        // pending first, done at the bottom
        foreach (var t in _todos.Where(t => !t.Done).Concat(_todos.Where(t => t.Done)))
        {
            var item = t;
            var row = new Grid { Margin = new Thickness(2, 0, 2, 2), Background = Brushes.Transparent, Height = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = item.Done ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)),
                Background = item.Done ? (Brush)FindResource("Accent") : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = item.Done ? "\uE73E" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            check.MouseLeftButtonUp += (_, _) => { Replace(item, item with { Done = !item.Done }); };

            var text = new TextBlock
            {
                Text = item.Text, FontSize = 13, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = item.Done ? (Brush)FindResource("Dim") : Brushes.White,
                TextDecorations = item.Done ? TextDecorations.Strikethrough : null
            };
            Grid.SetColumn(text, 1);

            var del = new TextBlock
            {
                Text = "\uE711", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11, Cursor = Cursors.Hand,
                Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden, Margin = new Thickness(0, 0, 6, 0)
            };
            del.MouseLeftButtonUp += (_, _) => { _todos.Remove(item); SaveTodos(); RenderTodos(); };
            Grid.SetColumn(del, 2);
            row.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
            row.MouseLeave += (_, _) => del.Visibility = Visibility.Hidden;

            row.Children.Add(check); row.Children.Add(text); row.Children.Add(del);
            TodoList.Children.Add(row);
        }
        TodoEmpty.Visibility = _todos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Replace(Todo oldT, Todo newT)
    {
        int i = _todos.IndexOf(oldT);
        if (i >= 0) _todos[i] = newT;
        SaveTodos(); RenderTodos();
    }

    void OnTodoKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(TodoInput.Text))
        {
            _todos.Insert(0, new Todo(TodoInput.Text.Trim(), false));
            TodoInput.Clear(); SaveTodos(); RenderTodos();
        }
        else if (e.Key == Key.Escape) EndTyping();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    // The island never takes focus normally; allow it only while typing.
    // Windows blocks background windows from stealing focus, so briefly attach to the
    // foreground thread's input queue to be allowed to become the foreground window.
    void OnInputMouseDown(object s, MouseButtonEventArgs e)
    {
        if (InPop(s)) { Keyboard.Focus((IInputElement)s); return; } // the pop-out chat is a normal window
        SetWindowLong(_hwnd, -20, GetWindowLong(_hwnd, -20) & ~0x08000000);
        var fg = GetForegroundWindow();
        if (fg != _hwnd) _prevFg = fg; // so "summarize this" can still reach the app you were in
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
        if (fg != _hwnd && fgThread != me) AttachThreadInput(fgThread, me, true);
        SetForegroundWindow(_hwnd);
        Activate();
        if (fg != _hwnd && fgThread != me) AttachThreadInput(fgThread, me, false);
        _typing = true;
        Keyboard.Focus((IInputElement)s);
    }

    bool _typing;
    void EndTyping()
    {
        if (!_typing) return;
        _typing = false;
        SetWindowLong(_hwnd, -20, GetWindowLong(_hwnd, -20) | 0x08000000);
        Keyboard.ClearFocus();
        _hover = IsMouseOver; Refresh();
    }
}
