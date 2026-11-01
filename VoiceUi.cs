using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.Wave;

namespace Island;

// Small voice view: a dot + one live caption line, a chip under the island showing what Gemini is doing,
// and soft chimes when voice starts / ends.
public partial class MainWindow
{
    static readonly SolidColorBrush CapYou = new(Color.FromRgb(0x9A, 0x9A, 0xA0)), CapGemini = new(Colors.White), CapStatus = new(Color.FromRgb(0x8E, 0x8E, 0x93));

    bool VoiceSmall => _ai.VoiceView != "Full";

    // the caption follows the big panel's text: whichever side spoke last
    void InitVoiceUi()
    {
        var d = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        d.AddValueChanged(AiYou, (_, _) => { if (AiYou.Text != "") Caption(AiYou.Text, CapYou); });
        d.AddValueChanged(AiSays, (_, _) =>
        {
            var t = AiSays.Text;
            bool status = t.Length < 40 && (t.EndsWith('\u2026') || t.StartsWith("Paused"));
            if (t != "") Caption(t, status ? CapStatus : CapGemini);
        });
    }

    void Caption(string text, Brush color)
    {
        VoiceCap.Text = Tail(text.Replace('\n', ' '), 50);
        VoiceCap.Foreground = color;
    }

    // last ~n characters, starting on a word
    static string Tail(string s, int n)
    {
        s = s.Trim();
        if (s.Length <= n) return s;
        var cut = s[^n..];
        int sp = cut.IndexOf(' ');
        return "\u2026" + (sp >= 0 && sp < 14 ? cut[(sp + 1)..] : cut);
    }

    // ================= Action chips =================
    int _chipGen;

    // voice tool calls go through here: chip while it runs, a friendly result after
    async Task<string> VoiceExec(string name, JsonObject a)
    {
        var busy = await Dispatcher.InvokeAsync(() => BusyText(name, a));
        if (busy != null) await Dispatcher.InvokeAsync(() => ShowChip(busy, 15000));
        var result = await Exec(name, a);
        if (busy != null)
            await Dispatcher.InvokeAsync(() =>
            {
                var done = DoneText(name, a, result);
                if (done != null) ShowChip(done); else HideChip();
            });
        return result;
    }

    static bool Silent(string name) =>
        name.StartsWith("list_") || name.StartsWith("get_") || name.StartsWith("read_") ||
        name is "now_playing" or "system_stats" or "daily_brief" or "dnd_status" or "find_files" or "pause_listening" or "end_voice" or "share_screen";

    static string Arg(JsonObject a, string k) { try { return a[k]?.ToString() ?? ""; } catch { return ""; } }
    static string Title(string s) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.Trim());

    static string? BusyText(string name, JsonObject a)
    {
        if (Silent(name)) return null;
        return name switch
        {
            "search_web" => "Searching\u2026",
            "look_at_screen" => "Looking at your screen\u2026",
            "get_weather" => "Checking the weather\u2026",
            "open_app" => $"Opening {Title(Arg(a, "name"))}\u2026",
            "open_url" or "open_search_in_browser" => "Opening the browser\u2026",
            "open_folder" => $"Opening {Arg(a, "name")}\u2026",
            "run_command" => "Running a command\u2026",
            "power" => "Getting that ready\u2026",
            _ => "On it\u2026"
        };
    }

    static bool Failed(string r)
    {
        var t = r.TrimStart();
        string[] bad = ["Error", "Couldn't", "Could not", "Can't", "Cannot", "Denied", "Blocked", "No ", "Not ", "Unknown", "Failed", "User denied", "The user denied"];
        return bad.Any(b => t.StartsWith(b, StringComparison.OrdinalIgnoreCase));
    }

    static string? DoneText(string name, JsonObject a, string r)
    {
        if (Failed(r))
        {
            if (r.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || r.StartsWith("Unknown")) return "Hmm, that didn't work";
            if (r.Contains("denied", StringComparison.OrdinalIgnoreCase) || r.Contains("blocked", StringComparison.OrdinalIgnoreCase)) return "Okay, I won't";
            var s = r.Trim().TrimEnd('.');
            if (s.Length > 38) s = s[..38].TrimEnd() + "\u2026";
            return "Hmm, " + char.ToLower(s[0]) + s[1..];
        }
        string Short(string fallback) => r.Length is > 0 and <= 26 && r != "ok" ? "\u2713 " + r.TrimEnd('.') : fallback;
        return name switch
        {
            "search_web" or "look_at_screen" or "get_weather" => null, // Gemini says the answer
            "play_pause" => "\u2713 Play / pause",
            "next_track" => "\u2713 Next track",
            "previous_track" => "\u2713 Previous track",
            "set_volume" or "change_volume" or "mute" or "set_brightness" or "change_brightness" or "set_wifi" or "set_bluetooth" => Short("\u2713 Done"),
            "set_timer" => "\u2713 Timer " + TimerLabel(Arg(a, "minutes")),
            "stop_timer" => "\u2713 Timer stopped",
            "start_stopwatch" => "\u2713 Stopwatch started",
            "add_todo" => "\u2713 Added to your to-dos",
            "complete_todo" => "\u2713 Ticked off",
            "remove_todo" or "cancel_reminder" or "forget" or "delete_routine" => "\u2713 Removed",
            "add_note" => "\u2713 Note saved",
            "copy_to_clipboard" => "\u2713 Copied",
            "set_reminder" => "\u2713 Reminder set",
            "remember" => "\u2713 I'll remember that",
            "open_app" => $"\u2713 {Title(Arg(a, "name"))} opened",
            "open_url" or "open_search_in_browser" or "open_file" or "open_latest_download" or "open_settings" => "\u2713 Opened",
            "open_folder" => $"\u2713 {Title(Arg(a, "name"))} opened",
            "close_app" => "\u2713 Closed",
            "switch_to_window" => "\u2713 Switched",
            "minimize_all" => "\u2713 Desktop",
            "start_focus" => "\u2713 Focus on",
            "stop_focus" => "\u2713 Focus ended",
            "take_screenshot" => "\u2713 Snipping tool open",
            "pick_color" => "Click any pixel",
            "lock_pc" => "\u2713 Locking\u2026",
            "run_routine" => "\u2713 Routine done",
            _ => "\u2713 Done"
        };
    }

    static string TimerLabel(string minutes)
    {
        if (!double.TryParse(minutes, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) || m <= 0) return "set";
        var t = TimeSpan.FromMinutes(m);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    async void ShowChip(string text, int ms = 2600)
    {
        if (!_aiOn) return;
        int g = ++_chipGen;
        ActChipText.Text = text;
        ActChipText.Foreground = text.StartsWith("Hmm") || text.StartsWith("Okay") ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)) : Brushes.White;
        ActChip.Margin = new Thickness(0, (_view == View.Assistant ? 84 : 34) + 6, 0, 0);
        ActChip.Visibility = Visibility.Visible;
        ActChip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        await Task.Delay(ms);
        if (g == _chipGen) HideChip();
    }

    void HideChip()
    {
        int g = ++_chipGen;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (g == _chipGen) ActChip.Visibility = Visibility.Collapsed; };
        ActChip.BeginAnimation(OpacityProperty, fade);
    }

    // ================= Settings =================
    void OnVoiceView(object s, RoutedEventArgs e)
    {
        _ai.VoiceView = (string)((Button)s).CommandParameter; _ai.Save();
        RenderIslandSettings();
        _view = (View)(-1); Refresh();
    }

    void OnVoicePick(object s, RoutedEventArgs e)
    {
        _ai.Voice = (string)((Button)s).CommandParameter; _ai.Save();
        RenderIslandSettings();
        if (_live != null) ShowToast("New voice from your next chat", Colors.White);
    }

    void OnChimeToggle(object s, RoutedEventArgs e) { _ai.Chimes = ChimeBtn.IsChecked == true; _ai.Save(); if (_ai.Chimes) Chime.Play(true); }

    void RenderVoiceSettings()
    {
        Seg(VViewSmallBtn, VoiceSmall); Seg(VViewFullBtn, !VoiceSmall);
        foreach (var b in VoicePickRow.Children.OfType<Button>()) Seg(b, (string)b.CommandParameter == _ai.Voice);
        ChimeBtn.IsChecked = _ai.Chimes;
    }
}

// two soft notes: rising when voice starts, falling when it ends
static class Chime
{
    static readonly byte[] Up = Make(659.25, 987.77), Down = Make(987.77, 659.25);

    public static void Play(bool up) => Task.Run(() =>
    {
        try
        {
            using var src = new RawSourceWaveStream(new MemoryStream(up ? Up : Down), new WaveFormat(44100, 16, 1));
            using var wo = new WaveOutEvent();
            wo.Init(src); wo.Play();
            while (wo.PlaybackState == PlaybackState.Playing) Thread.Sleep(30);
        }
        catch { }
    });

    static byte[] Make(double f1, double f2)
    {
        const int rate = 44100;
        int len = (int)(rate * 0.34), second = (int)(rate * 0.085);
        var pcm = new byte[len * 2];
        for (int i = 0; i < len; i++)
        {
            double Note(double f, int start)
            {
                if (i < start) return 0;
                double t = (double)(i - start) / rate;
                double env = Math.Min(1, t / 0.006) * Math.Exp(-t * 16); // quick fade-in, soft decay
                return env * (Math.Sin(2 * Math.PI * f * t) + 0.25 * Math.Sin(4 * Math.PI * f * t));
            }
            double v = (Note(f1, 0) + Note(f2, second)) * 0.13;
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(Math.Clamp(v, -1, 1) * 32767));
        }
        return pcm;
    }
}
