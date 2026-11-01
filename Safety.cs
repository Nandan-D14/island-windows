using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Island;

// Safety layer for everything Gemini does on the PC:
//  - every AI tool call passes through Gate(): Allowed / Ask first / Blocked per category
//  - "Ask first" shows an Allow / Deny card in the island (30 s, then denied)
//  - after Gemini reads the web, the screen, the clipboard or another app, risky actions always ask
//    (a page could contain hidden instructions)
//  - everything is written to the activity log (Controls › Activity), with Undo where possible
public partial class MainWindow
{
    enum Perm { Allowed, Ask, Blocked }
    record PermCat(string Key, string Label, Perm Default);

    static readonly PermCat[] Cats =
    [
        new("media", "Media, volume & brightness", Perm.Allowed),
        new("time", "Timers, focus & reminders", Perm.Allowed),
        new("notes", "Notes, to-dos & memory", Perm.Allowed),
        new("web", "Web search", Perm.Allowed),
        new("open", "Open apps, files, sites & settings", Perm.Allowed),
        new("windows", "Switch, snap & minimize windows", Perm.Allowed),
        new("close", "Close apps", Perm.Ask),
        new("move", "Move files", Perm.Ask),
        new("delete", "Delete files (to Recycle Bin)", Perm.Blocked),
        new("clipboard", "Clipboard & selected text", Perm.Allowed),
        new("screen", "Look at / share your screen", Perm.Allowed),
        new("system", "Wi-Fi, Bluetooth, dark mode, power", Perm.Allowed),
        new("control", "Click & type inside apps", Perm.Ask),
        new("lock", "Lock PC", Perm.Ask),
        new("routines", "Routines", Perm.Allowed),
        new("keys", "Keyboard shortcuts", Perm.Allowed),
        new("power", "Shut down / restart / sleep (always asks)", Perm.Ask),
        new("terminal", "Run terminal commands (always asks)", Perm.Ask),
    ];

    // these can never be set to "Allowed": every single time shows the Allow / Deny card
    static readonly HashSet<string> AlwaysAsk = ["power", "terminal"];

    static readonly Dictionary<string, string> ToolCat = new()
    {
        ["play_pause"] = "media", ["next_track"] = "media", ["previous_track"] = "media", ["now_playing"] = "media",
        ["set_volume"] = "media", ["change_volume"] = "media", ["mute"] = "media", ["set_brightness"] = "media", ["change_brightness"] = "media",
        ["set_timer"] = "time", ["stop_timer"] = "time", ["start_stopwatch"] = "time", ["start_focus"] = "time", ["stop_focus"] = "time",
        ["set_reminder"] = "time", ["list_reminders"] = "time", ["cancel_reminder"] = "time",
        ["add_todo"] = "notes", ["list_todos"] = "notes", ["complete_todo"] = "notes", ["remove_todo"] = "notes", ["clear_completed_todos"] = "notes",
        ["add_note"] = "notes", ["read_notes"] = "notes", ["remember"] = "notes", ["forget"] = "notes", ["list_memory"] = "notes",
        ["get_weather"] = "web", ["search_web"] = "web", ["system_stats"] = "media",
        ["open_app"] = "open", ["open_url"] = "open", ["open_search_in_browser"] = "open", ["open_folder"] = "open",
        ["open_file"] = "open", ["open_latest_download"] = "open", ["find_files"] = "open", ["open_settings"] = "open", ["pick_color"] = "open",
        ["list_windows"] = "windows", ["get_active_window"] = "windows", ["daily_brief"] = "time",
        ["get_notifications"] = "notes", ["clear_notifications"] = "notes",
        ["set_dnd"] = "time", ["set_notification_mode"] = "time", ["dnd_status"] = "time", ["switch_to_window"] = "windows", ["snap_window"] = "windows", ["minimize_all"] = "windows",
        ["close_app"] = "close", ["move_files"] = "move", ["recycle_file"] = "delete",
        ["copy_to_clipboard"] = "clipboard", ["read_clipboard"] = "clipboard", ["get_selected_text"] = "clipboard",
        ["look_at_screen"] = "screen", ["share_screen"] = "screen", ["take_screenshot"] = "screen",
        ["set_wifi"] = "system", ["set_bluetooth"] = "system", ["set_dark_mode"] = "system", ["set_power_mode"] = "system",
        ["list_controls"] = "control", ["click_control"] = "control", ["type_text"] = "control",
        ["lock_pc"] = "lock",
        ["create_routine"] = "routines", ["run_routine"] = "routines", ["list_routines"] = "routines", ["delete_routine"] = "routines",
        ["press_keys"] = "keys", ["power"] = "power", ["cancel_power"] = "time", ["run_command"] = "terminal",
    };

    // tools that bring outside text into the conversation (could carry hidden instructions)
    static readonly HashSet<string> Readers = ["search_web", "look_at_screen", "read_clipboard", "get_selected_text", "list_controls", "read_notes", "get_notifications"];
    // categories that change things — these always ask while that outside text is fresh
    static readonly HashSet<string> Risky = ["open", "close", "move", "delete", "system", "control", "lock", "routines", "keys", "power", "terminal"];
    static readonly HashSet<string> RiskyTools = ["copy_to_clipboard", "remember", "create_routine"];
    DateTime _taintUntil;

    Perm Level(string cat)
    {
        var p = _ai.Perms.TryGetValue(cat, out var s) && Enum.TryParse<Perm>(s, out var v) ? v : Cats.FirstOrDefault(c => c.Key == cat)?.Default ?? Perm.Allowed;
        return AlwaysAsk.Contains(cat) && p == Perm.Allowed ? Perm.Ask : p;
    }

    // every tool call from Gemini (text chat or voice) comes through here
    async Task<string> Gate(string name, JsonObject a, bool fromRoutine = false, string origin = "")
    {
        if (!_ai.AiEnabled && origin == "") return "AI is switched off by the user.";
        var cat = ToolCat.GetValueOrDefault(name, "");
        var level = cat == "" ? Perm.Allowed : Level(cat);
        var what = (origin == "phone" ? "\uD83D\uDCF1 " : "") + Describe(name, a);
        if (level == Perm.Blocked)
        {
            LogAction(what, "blocked");
            return "Blocked: the user turned this off in Controls › AI safety. Tell them briefly.";
        }
        bool tainted = !fromRoutine && (_sharing || DateTime.Now < _taintUntil) && (Risky.Contains(cat) || RiskyTools.Contains(name));
        var extra = ExtraRisk(name, a); // e.g. opening a program file
        if (name == "run_command")
        {
            if (origin == "phone") { LogAction(what, "blocked"); return "Terminal commands can only be approved on the PC itself."; }
            var problem = CommandProblem(a["command"]?.ToString() ?? "");
            if (problem != null) { LogAction(what, "blocked"); return "Blocked: " + problem; } // refused before you're even asked
        }
        if (level == Perm.Ask || tainted || extra != null || AlwaysAsk.Contains(cat))
        {
            var detail = name == "run_command" ? a["command"]?.ToString() : null;
            if (!await AskUser(what, extra ?? (tainted ? "Asked because Gemini just read the web or your screen." : null), cat, origin, detail))
            {
                LogAction(what, "denied");
                return "The user denied this action. Don't retry it.";
            }
        }
        if (Readers.Contains(name)) _taintUntil = DateTime.Now.AddMinutes(3);
        var res = await ExecCore(name, a);
        if (!name.StartsWith("list_") && name is not ("now_playing" or "get_weather" or "system_stats" or "read_notes" or "list_memory"))
            LogAction(what, Short(res), _pendingUndo);
        _pendingUndo = null;
        return res;
    }

    Action? _pendingUndo; // set by a tool that can be undone

    static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    string Describe(string name, JsonObject a)
    {
        string S(string k) => a[k]?.ToString() ?? "";
        return name switch
        {
            "close_app" => $"Close {S("name")}",
            "move_files" => $"Move {(S("name_contains") + " " + S("extension")).Trim() switch { "" => "files", var f => "“" + f + "” files" }} from {S("source")} to {S("destination")}",
            "recycle_file" => $"Move {Path.GetFileName(S("path"))} to the Recycle Bin",
            "open_file" => $"Open {Path.GetFileName(S("path"))}",
            "open_latest_download" => "Open your latest download",
            "open_app" => $"Open {S("name")}",
            "open_url" => $"Open {S("url")}",
            "open_folder" => $"Open {S("name")} folder",
            "click_control" => $"Click “{S("control")}” in {S("window")}",
            "type_text" => $"Type “{Short(S("text"))}” in {S("window")}",
            "lock_pc" => "Lock your PC",
            "set_wifi" => $"Turn Wi-Fi {(a["on"]?.GetValue<bool>() == false ? "off" : "on")}",
            "set_bluetooth" => $"Turn Bluetooth {(a["on"]?.GetValue<bool>() == false ? "off" : "on")}",
            "set_dark_mode" => $"Switch to {(a["on"]?.GetValue<bool>() == false ? "light" : "dark")} mode",
            "set_power_mode" => $"Set power mode to {S("mode").Replace('_', ' ')}",
            "copy_to_clipboard" => $"Copy “{Short(S("text"))}” to the clipboard",
            "remember" => $"Remember “{Short(S("fact"))}”",
            "create_routine" => $"Create routine “{S("name")}”",
            "run_routine" => $"Run routine “{S("name")}”",
            "switch_to_window" => $"Switch to {S("name")}",
            "snap_window" => $"Snap {S("name")} {S("position")}",
            "minimize_all" => "Minimize all windows",
            "set_volume" => $"Volume {S("level")}",
            "press_keys" => $"Press {NormKeys(S("keys"))}" + (S("times") is "" or "0" or "1" ? "" : $" \u00D7{S("times")}"),
            "power" => PowerText(S("action"), S("in_minutes")),
            "cancel_power" => "Cancel scheduled shutdown",
            "run_command" => "Run a command" + (S("why") != "" ? ": " + Short(S("why")) : ""),
            _ => name.Replace('_', ' ') + (a.Count > 0 ? ": " + Short(string.Join(", ", a.Select(kv => kv.Value?.ToString()))) : "")
        };
    }

    static string PowerText(string action, string minutes)
    {
        var act = action.ToLowerInvariant() switch { "restart" or "reboot" => "Restart", "sleep" or "suspend" => "Sleep", "hibernate" => "Hibernate", _ => "Shut down" };
        double.TryParse(minutes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var m);
        return act + " your PC " + (m <= 0 ? "now" : m < 60 ? $"in {m:0.#} min" : $"at {DateTime.Now.AddMinutes(m):h:mm tt}");
    }

    // ================= Allow / Deny card =================
    TaskCompletionSource<bool>? _askTcs;
    int _askId;
    readonly SemaphoreSlim _askLock = new(1, 1);
    bool _askOn;
    string _askCat = "";
    readonly System.Windows.Threading.DispatcherTimer _askTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    async Task<bool> AskUser(string what, string? why, string cat, string origin = "", string? detail = null)
    {
        await _askLock.WaitAsync();
        try
        {
            AskHead.Text = origin == "phone" ? "FROM YOUR PHONE" : "GEMINI WANTS TO";
            AskTitle.Text = what;
            AskWhy.Text = why ?? (cat == "terminal" ? "Check the command. It runs as you (not admin), for up to 60 s."
                                : cat == "power" ? "Save your work first. You'll be asked every time."
                                : origin == "phone" ? "Allow this?" : "Gemini wants to do this. Allow?");
            AskCmd.Text = detail ?? "";
            AskCmdBox.Visibility = detail != null ? Visibility.Visible : Visibility.Collapsed;
            // never offer "always" for flagged requests or for power / terminal
            AskAlways.Visibility = why == null && cat != "" && !AlwaysAsk.Contains(cat) ? Visibility.Visible : Visibility.Collapsed;
            _askCat = cat;
            _askId++;
            _askTcs = new TaskCompletionSource<bool>();
            _askOn = true; _view = (View)(-1); Refresh();
            System.Media.SystemSounds.Asterisk.Play();
            _askTimer.Stop(); _askTimer.Start();
            return await _askTcs.Task;
        }
        finally
        {
            _askTimer.Stop();
            _askOn = false; _view = (View)(-1); Refresh();
            _askLock.Release();
        }
    }

    void AnswerAsk(bool ok) => _askTcs?.TrySetResult(ok);
    void OnAskAllow(object s, RoutedEventArgs e) => AnswerAsk(true);
    void OnAskDeny(object s, RoutedEventArgs e) => AnswerAsk(false);
    void OnAskAlways(object s, RoutedEventArgs e)
    {
        if (_askCat != "" && !AlwaysAsk.Contains(_askCat)) { _ai.Perms[_askCat] = Perm.Allowed.ToString(); _ai.Save(); RenderPrivacy(); }
        AnswerAsk(true);
    }

    // ================= Activity log =================
    record LogEntry(DateTime At, string What, string Result);
    readonly List<(LogEntry e, Action? undo)> _log = [];
    static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "activity.json");

    void LoadSafety()
    {
        try { if (File.Exists(LogFile)) _log.AddRange((JsonSerializer.Deserialize<List<LogEntry>>(File.ReadAllText(LogFile)) ?? []).Select(e => (e, (Action?)null))); } catch { }
        LoadMemory();
        _askTimer.Tick += (_, _) => AnswerAsk(false);
        RenderLog(); RenderPrivacy();
    }

    void LogAction(string what, string result, Action? undo = null)
    {
        _log.Insert(0, (new LogEntry(DateTime.Now, what, result), undo));
        if (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!); File.WriteAllText(LogFile, JsonSerializer.Serialize(_log.Select(x => x.e))); } catch { }
        RenderLog();
    }

    void RenderLog()
    {
        LogList.Children.Clear();
        foreach (var (e, undo) in _log.Take(60))
        {
            var time = new TextBlock { Text = e.At.Date == DateTime.Today ? e.At.ToString("HH:mm") : e.At.ToString("d MMM"), FontSize = 11, Foreground = (Brush)FindResource("Dim"), Width = 40, VerticalAlignment = VerticalAlignment.Center };
            var bad = e.Result is "denied" or "blocked" || e.Result.StartsWith("Error");
            var what = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            what.Inlines.Add(new System.Windows.Documents.Run(e.What));
            what.Inlines.Add(new System.Windows.Documents.Run("  " + e.Result) { Foreground = bad ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E)) : (Brush)FindResource("Dim"), FontSize = 11 });
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4), ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            row.Children.Add(time);
            Grid.SetColumn(what, 1); row.Children.Add(what);
            if (undo != null)
            {
                var b = new Button { Style = (Style)FindResource("Chip"), Content = "Undo", Margin = new Thickness(6, 0, 0, 0) };
                var entry = (e, undo);
                b.Click += (_, _) =>
                {
                    try { undo(); LogAction("Undo: " + e.What, "ok"); } catch (Exception ex) { LogAction("Undo: " + e.What, "Error: " + ex.Message); }
                    var i = _log.IndexOf(entry); if (i >= 0) _log[i] = (e, null);
                    RenderLog();
                };
                Grid.SetColumn(b, 2); row.Children.Add(b);
            }
            LogList.Children.Add(row);
        }
        LogHint.Visibility = _log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnLogClear(object s, RoutedEventArgs e)
    {
        _log.Clear();
        try { File.Delete(LogFile); } catch { }
        RenderLog();
    }

    // ================= AI safety page =================
    void RenderPrivacy()
    {
        AiOnBtn.IsChecked = _ai.AiEnabled;
        PermList.Children.Clear();
        bool first = true;
        foreach (var c in Cats)
        {
            if (!first) PermList.Children.Add(new Border { Style = (Style)FindResource("Hair") });
            first = false;
            var label = new TextBlock { Text = c.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var key = c.Key;
            // Allow · Ask · Block  (power and terminal can never be "Allow")
            var opts = AlwaysAsk.Contains(key) ? new[] { Perm.Ask, Perm.Blocked } : [Perm.Allowed, Perm.Ask, Perm.Blocked];
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            var btns = new List<(Button b, Perm p)>();
            void Paint()
            {
                var cur = Level(key);
                foreach (var (b, p) in btns)
                {
                    Seg(b, p == cur);
                    if (p == cur)
                    {
                        var col = p switch { Perm.Allowed => Color.FromRgb(0x30, 0xD1, 0x58), Perm.Ask => Color.FromRgb(0xFF, 0x9F, 0x0A), _ => Color.FromRgb(0xFF, 0x45, 0x3A) };
                        b.Background = new SolidColorBrush(Color.FromArgb(0x70, col.R, col.G, col.B));
                        b.Foreground = Brushes.White;
                    }
                    else { b.ClearValue(BackgroundProperty); b.ClearValue(ForegroundProperty); }
                }
            }
            foreach (var p in opts)
            {
                var b = new Button { Style = (Style)FindResource("Seg"), Content = p switch { Perm.Allowed => "Allow", Perm.Ask => "Ask", _ => "Block" } };
                var pick = p;
                b.Click += (_, _) => { _ai.Perms[key] = pick.ToString(); _ai.Save(); Paint(); };
                btns.Add((b, p)); seg.Children.Add(b);
            }
            Paint();
            var track = new Border { Style = (Style)FindResource("SegTrack"), Child = seg };
            var row = new Grid { Style = (Style)FindResource("Row"), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            row.Children.Add(label);
            Grid.SetColumn(track, 1); row.Children.Add(track);
            PermList.Children.Add(row);
        }
        RenderMemory();
    }

    void OnAiOnToggle(object s, RoutedEventArgs e)
    {
        _ai.AiEnabled = !_ai.AiEnabled; _ai.Save();
        if (!_ai.AiEnabled) { StopVoice(); SetSharing(false); }
        UpdateWakePower();
        RenderPrivacy();
        ShowToast(_ai.AiEnabled ? "Gemini on" : "Gemini off \u00B7 mic off", _ai.AiEnabled ? Colors.White : Color.FromRgb(0xFF, 0x6B, 0x5E));
    }

    void OnPermReset(object s, RoutedEventArgs e) { _ai.Perms.Clear(); _ai.Save(); RenderPrivacy(); }

    // ================= Memory (only on this PC, plain text you can see and delete) =================
    readonly List<string> _memory = [];
    static readonly string MemoryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "memory.json");

    void LoadMemory()
    {
        try { if (File.Exists(MemoryFile)) _memory.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(MemoryFile)) ?? []); } catch { }
    }

    void SaveMemory()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(MemoryFile)!); File.WriteAllText(MemoryFile, JsonSerializer.Serialize(_memory)); } catch { }
        RenderMemory();
    }

    string MemoryPrompt() => _memory.Count == 0 ? "" : " Things the user asked you to remember: " + string.Join("; ", _memory) + ".";

    void RenderMemory()
    {
        MemList.Children.Clear();
        foreach (var m in _memory)
        {
            var t = new TextBlock { Text = m, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var x = new Button { Style = (Style)FindResource("Mini"), Content = "\uE711" };
            var fact = m;
            x.Click += (_, _) => { _memory.Remove(fact); SaveMemory(); };
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            row.Children.Add(t); Grid.SetColumn(x, 1); row.Children.Add(x);
            MemList.Children.Add(row);
        }
        MemHint.Text = _memory.Count == 0 ? "Nothing yet. Say “remember that…”." : $"{_memory.Count} saved on this PC only";
    }

    void OnForgetAll(object s, RoutedEventArgs e)
    {
        _memory.Clear(); SaveMemory();
        _chat.Clear(); AiChat.Children.Clear();
        _clips.Clear(); RenderClips();
        ShowToast("Forgot everything", Colors.White);
    }
}
