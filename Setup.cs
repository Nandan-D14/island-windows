using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Island;

// Health check (Controls › Health) and the first-run welcome
public partial class MainWindow
{
    // ================= Health check =================
    enum Hs { Ok, Warn, Bad, Wait }
    record HealthRow(string Name, Hs State, string Detail, string? FixLabel = null, Action? Fix = null);

    bool _healthBusy;

    async void OnHealthRun(object s, RoutedEventArgs e) => await RunHealth(full: true);

    async Task RunHealth(bool full)
    {
        if (_healthBusy) return;
        _healthBusy = true;
        HealthRunBtn.IsEnabled = false;
        var rows = new List<HealthRow>();
        void Show() => RenderHealth(rows);
        try
        {
            // ---- local checks (instant) ----
            rows.Add(InstallRow());
            rows.Add(StartupRow());
            rows.Add(MicRow());
            rows.Add(WakeRow());
            rows.Add(SpeakerRow());
            rows.Add(await NotifRow());
            rows.Add(await CameraRow());
            rows.Add(WeatherRow());
            rows.Add(ErrorsRow());
            Show();

            // ---- online checks ----
            rows.Insert(0, new HealthRow("Internet", Hs.Wait, "Checking…"));
            rows.Insert(1, new HealthRow("Gemini (chat)", Hs.Wait, full ? "Testing…" : "Tap “Run full test” to check"));
            rows.Insert(2, new HealthRow("Gemini (voice)", Hs.Wait, full ? "Testing…" : "Tap “Run full test” to check"));
            Show();
            rows[0] = await InternetRow();
            Show();
            if (full)
            {
                rows[1] = await ChatRow();
                Show();
                rows[2] = await VoiceRow();
                Show();
            }
        }
        finally
        {
            _healthBusy = false;
            HealthRunBtn.IsEnabled = true;
            HealthTime.Text = "Checked " + DateTime.Now.ToString("h:mm tt");
        }
    }

    void RenderHealth(List<HealthRow> rows)
    {
        HealthList.Children.Clear();
        foreach (var r in rows)
        {
            var (glyph, color) = r.State switch
            {
                Hs.Ok => ("\uE73E", Color.FromRgb(0x30, 0xD1, 0x58)),
                Hs.Warn => ("\uE7BA", Color.FromRgb(0xFF, 0x9F, 0x0A)),
                Hs.Bad => ("\uE711", Color.FromRgb(0xFF, 0x45, 0x3A)),
                _ => ("\uE895", Color.FromRgb(0x8E, 0x8E, 0x93)),
            };
            var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new TextBlock { Text = r.Name, FontSize = 12, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = r.Detail, FontSize = 11, Foreground = (Brush)FindResource("Dim"), TextWrapping = TextWrapping.Wrap },
            } };
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6), ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            g.Children.Add(dot);
            Grid.SetColumn(text, 1); g.Children.Add(text);
            if (r.Fix != null)
            {
                var b = new Button { Style = (Style)FindResource("Chip"), Content = r.FixLabel ?? "Fix", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                b.Click += (_, _) => { r.Fix(); _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => _ = RunHealth(false))); };
                Grid.SetColumn(b, 2); g.Children.Add(b);
            }
            HealthList.Children.Add(g);
        }
        int bad = rows.Count(r => r.State == Hs.Bad), warn = rows.Count(r => r.State == Hs.Warn), wait = rows.Count(r => r.State == Hs.Wait);
        HealthSummary.Text = bad > 0 ? $"{bad} problem{(bad == 1 ? "" : "s")}" + (warn > 0 ? $", {warn} to look at" : "")
                           : warn > 0 ? $"Working \u00B7 {warn} thing{(warn == 1 ? "" : "s")} to look at"
                           : wait > 0 ? "Checking…" : "Everything works";
        HealthSummary.Foreground = new SolidColorBrush(bad > 0 ? Color.FromRgb(0xFF, 0x6B, 0x5E) : warn > 0 ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0x30, 0xD1, 0x58));
    }

    static void Settings(string page) { try { Shell("ms-settings:" + page); } catch { } }

    // ---- individual checks ----
    HealthRow InstallRow()
    {
        var me = Environment.ProcessPath ?? "";
        var ver = "v" + AppVersion.ToString(3);
        if (string.Equals(me, App.InstalledExe, StringComparison.OrdinalIgnoreCase)) return new("Installed", Hs.Ok, $"Island {ver} \u00B7 Start menu + Settings \u203A Apps");
        if (me.Contains(@"\bin\")) return new("Installed", Hs.Warn, $"Test build {ver} running from the project folder");
        return new("Installed", Hs.Warn, $"Running a copy ({ver}) that isn't installed", "Install", () =>
        {
            try { Process.Start(new ProcessStartInfo(me, "--update") { UseShellExecute = true }); Application.Current.Shutdown(); } catch { }
        });
    }

    HealthRow StartupRow()
    {
        if (!_ai.StartWithWindows) return new("Start with Windows", Hs.Warn, "Turned off", "Turn on", () => { _ai.StartWithWindows = true; _ai.Save(); App.SetStartup(true); RenderIslandSettings(); });
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN \"DynamicIsland\" /XML") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var xml = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
            if (p.ExitCode == 0)
            {
                bool mine = xml.Contains(System.Security.SecurityElement.Escape(App.InstalledExe), StringComparison.OrdinalIgnoreCase) ||
                            xml.Contains(System.Security.SecurityElement.Escape(Environment.ProcessPath ?? "?"), StringComparison.OrdinalIgnoreCase);
                return mine ? new("Start with Windows", Hs.Ok, "Starts a few seconds after you sign in")
                            : new("Start with Windows", Hs.Warn, "Points to another copy of Island", "Repair", () => App.SetStartup(true));
            }
        }
        catch { }
        bool run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("DynamicIsland") != null;
        return run ? new("Start with Windows", Hs.Warn, "Using the slow startup list (can be minutes late)", "Repair", () => App.SetStartup(true))
                   : new("Start with Windows", Hs.Bad, "Not set up", "Repair", () => App.SetStartup(true));
    }

    static string? Consent(string cap, bool desktop)
    {
        var path = $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{cap}" + (desktop ? @"\NonPackaged" : "");
        return (Registry.CurrentUser.OpenSubKey(path)?.GetValue("Value") ?? Registry.LocalMachine.OpenSubKey(path)?.GetValue("Value")) as string;
    }

    HealthRow MicRow()
    {
        int n = 0; string name = "";
        try { n = NAudio.Wave.WaveInEvent.DeviceCount; if (n > 0) name = NAudio.Wave.WaveInEvent.GetCapabilities(0).ProductName; } catch { }
        if (n == 0) return new("Microphone", Hs.Bad, "No microphone found", "Sound settings", () => Settings("sound"));
        if (Consent("microphone", false) == "Deny" || Consent("microphone", true) == "Deny")
            return new("Microphone", Hs.Bad, "Blocked in Windows privacy settings", "Allow", () => Settings("privacy-microphone"));
        return new("Microphone", Hs.Ok, name);
    }

    HealthRow WakeRow()
    {
        if (!_ai.WakeWord) return new("“Hey Gemini”", Hs.Warn, "Wake word is off", "Turn on", () => { WakeBtn.IsChecked = true; OnWakeToggle(WakeBtn, new RoutedEventArgs()); });
        if (_ai.Key == "") return new("“Hey Gemini”", Hs.Warn, "Needs a Gemini key first");
        if (_wake == null) return new("“Hey Gemini”", Hs.Bad, "Windows speech engine couldn't start (English speech pack missing?)", "Speech settings", () => Settings("speech"));
        if (!_wakeMicOn) return new("“Hey Gemini”", Hs.Warn, _locked ? "Paused while locked" : "Paused to save battery (\u2264 20%)");
        return new("“Hey Gemini”", Hs.Ok, "Listening (offline, nothing leaves the PC)");
    }

    HealthRow SpeakerRow()
    {
        var a = Audio.Get();
        if (a == null) return new("Speakers", Hs.Bad, "No sound output found", "Sound settings", () => Settings("sound"));
        if (a.Value.mute || a.Value.vol == 0) return new("Speakers", Hs.Warn, "Muted \u00B7 you won't hear Gemini", "Unmute", () => { Audio.SetMute(false); if ((Audio.Get()?.vol ?? 0) == 0) Audio.SetVolume(40); });
        return new("Speakers", Hs.Ok, $"Volume {a.Value.vol}%");
    }

    async Task<HealthRow> NotifRow()
    {
        try
        {
            var st = await Windows.UI.Notifications.Management.UserNotificationListener.Current.RequestAccessAsync();
            return st == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed
                ? new("Notifications", Hs.Ok, $"Reading Windows notifications \u00B7 {_notifs.Count} in the list")
                : new("Notifications", Hs.Bad, "Island isn't allowed to read notifications", "Allow", () => Settings("privacy-notifications"));
        }
        catch { return new("Notifications", Hs.Warn, "Using the backup method (banners only)"); }
    }

    async Task<HealthRow> CameraRow()
    {
        int n = 0;
        try { n = (await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture)).Count; } catch { }
        if (n == 0) return new("Camera (mirror)", Hs.Warn, "No camera found");
        if (Consent("webcam", false) == "Deny" || Consent("webcam", true) == "Deny")
            return new("Camera (mirror)", Hs.Warn, "Blocked in Windows privacy settings", "Allow", () => Settings("privacy-webcam"));
        return new("Camera (mirror)", Hs.Ok, $"{n} camera{(n == 1 ? "" : "s")} available");
    }

    HealthRow WeatherRow() =>
        WxCity.Text is "" or "Offline"
            ? new("Weather", Hs.Warn, "Not loaded yet", "Retry", () => _ = LoadWeather())
            : new("Weather", Hs.Ok, $"{WxCity.Text} \u00B7 {WxTemp.Text} {WxDesc.Text}".Trim());

    HealthRow ErrorsRow()
    {
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "error.log");
        if (!File.Exists(log)) return new("Errors", Hs.Ok, "None logged");
        int today = 0;
        try { today = File.ReadLines(log).Count(l => l.StartsWith("[" + DateTime.UtcNow.ToString("yyyy-MM-dd"))); } catch { }
        return today == 0 ? new("Errors", Hs.Ok, "None today")
                          : new("Errors", Hs.Warn, $"{today} error{(today == 1 ? "" : "s")} logged today (the island kept running)", "Open log", () => { try { Shell(log); } catch { } });
    }

    static async Task<HealthRow> InternetRow()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var sw = Stopwatch.StartNew();
            using var r = await http.GetAsync("https://generativelanguage.googleapis.com/", HttpCompletionOption.ResponseHeadersRead);
            return new("Internet", Hs.Ok, $"Google AI reachable ({sw.ElapsedMilliseconds} ms)");
        }
        catch { return new("Internet", Hs.Bad, "Can't reach Google AI \u00B7 check Wi-Fi", "Network", () => Settings("network")); }
    }

    async Task<HealthRow> ChatRow()
    {
        if (_ai.Key == "") return new("Gemini (chat)", Hs.Bad, "No API key", "Add key", () => { _hover = true; TabAi.IsChecked = true; ShowAiSetup(true); Refresh(); });
        if (!_ai.AiEnabled) return new("Gemini (chat)", Hs.Warn, "Switched off in AI safety", "Turn on", () => OnAiOnToggle(this, new RoutedEventArgs()));
        var sw = Stopwatch.StartNew();
        try
        {
            await Gemini.Quick(_ai.Key, _ai.TextModel, "Reply with just: OK");
            var m = Gemini.LastModel;
            return m == _ai.TextModel
                ? new("Gemini (chat)", Hs.Ok, $"{m} \u00B7 answered in {sw.ElapsedMilliseconds} ms")
                : new("Gemini (chat)", Hs.Warn, $"{_ai.TextModel} is busy or out of free quota \u00B7 using {m} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            return ex.Message.Contains("free limit") ? new("Gemini (chat)", Hs.Warn, ex.Message)
                 : ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase) ? new("Gemini (chat)", Hs.Bad, "Key rejected: " + ex.Message, "New key", () => { _hover = true; TabAi.IsChecked = true; ShowAiSetup(true); Refresh(); })
                 : new("Gemini (chat)", Hs.Bad, ex.Message);
        }
    }

    async Task<HealthRow> VoiceRow()
    {
        if (_ai.Key == "") return new("Gemini (voice)", Hs.Bad, "No API key");
        if (_ai.LiveModel == "") return new("Gemini (voice)", Hs.Warn, "This key has no voice model \u00B7 typing still works");
        if (_live != null) return new("Gemini (voice)", Hs.Ok, $"In a voice chat now ({_live.Model})");
        var (ok, model, err) = await LiveSession.Probe(_ai.Key, _ai.LiveModel);
        return !ok ? new("Gemini (voice)", Hs.Bad, "Can't connect: " + err)
             : model == _ai.LiveModel ? new("Gemini (voice)", Hs.Ok, $"{model} connects")
             : new("Gemini (voice)", Hs.Warn, $"{_ai.LiveModel} unavailable \u00B7 voice will use {model}");
    }

    // ================= Welcome (first run) =================
    bool _onboarding;

    void MaybeWelcome()
    {
        if (_ai.Onboarded) return;
        if (_ai.KeyEnc != "") { _ai.Onboarded = true; _ai.Save(); return; } // existing users skip it
        _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            _onboarding = true; _hover = true;
            WelcomeStep(0);
            ShowPage(PageWelcome);
            Refresh();
        }));
    }

    void WelcomeStep(int i)
    {
        Welcome0.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        Welcome1.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        Welcome2.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeDots.Children.Clear();
        for (int k = 0; k < 3; k++)
            WelcomeDots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0), Fill = new SolidColorBrush(k == i ? Colors.White : Color.FromArgb(0x55, 255, 255, 255)) });
        WelcomeHotkey.Text = _ai.Hotkey == "Ctrl" ? "double-tap Ctrl" : _ai.Hotkey == "Off" ? "press the mic in the \u2726 tab" : "double-tap Alt";
    }

    void OnWelcomeNext(object s, RoutedEventArgs e) => WelcomeStep(int.Parse((string)((Button)s).Tag));
    void OnWelcomeGetKey(object s, RoutedEventArgs e) { try { Shell("https://aistudio.google.com/apikey"); } catch { } }

    async void OnWelcomeConnect(object s, RoutedEventArgs e)
    {
        var key = WelcomeKey.Text.Trim();
        if (key == "") { WelcomeMsg.Text = "Paste your key first."; return; }
        WelcomeMsg.Text = "Checking your key…";
        try
        {
            var (text, live) = await Gemini.PickModels(key);
            _ai.Key = key; _ai.TextModel = text; _ai.LiveModel = live; _ai.Save();
            WelcomeKey.Clear(); EndTyping();
            WelcomeMsg.Text = "";
            WelcomeStep(2);
        }
        catch (Exception ex) { WelcomeMsg.Text = "That key didn't work: " + ex.Message; }
    }

    void OnWelcomeDone(object s, RoutedEventArgs e)
    {
        _ai.Onboarded = true; _ai.Save();
        _onboarding = false;
        TabMedia.IsChecked = true; OnTab(TabMedia, e);
        _hover = IsMouseOver; Refresh();
        ShowToast("Hover the island any time \u00B7 " + WelcomeHotkey.Text + " to talk", Colors.White);
    }
}
