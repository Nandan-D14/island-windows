using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Win32;

namespace Island;

// New things Gemini can do. Every one of these runs through Gate() (Safety.cs) first.
// No raw shell commands: only these fixed, narrow actions exist.
public partial class MainWindow
{
    static JsonObject Tool(string name, string desc, params (string n, string type, string d)[] ps)
    {
        var f = new JsonObject { ["name"] = name, ["description"] = desc };
        if (ps.Length > 0)
        {
            var props = new JsonObject();
            foreach (var p in ps) props[p.n] = new JsonObject { ["type"] = p.type, ["description"] = p.d };
            f["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(ps.Select(p => (JsonNode)p.n).ToArray()) };
        }
        return f;
    }

    static IEnumerable<JsonObject> MoreTools() =>
    [
        Tool("list_windows", "List the open app windows."),
        Tool("get_active_window", "Which app/window the user is in right now (for 'this app', 'close this', 'what am I looking at')."),
        Tool("daily_brief", "Today's summary: date, weather, reminders, to-dos, battery."),
        Tool("get_notifications", "Recent notifications on the PC (for 'what did I miss', 'any messages?', 'summarize my notifications')."),
        Tool("set_dnd", "Turn the island's Do Not Disturb on or off. While on, notifications are held (reminders still come through).",
            ("on", "boolean", "true = on"), ("minutes", "number", "how long; 0 = until turned off"), ("until_tomorrow", "boolean", "true = until 8 am tomorrow")),
        Tool("set_notification_mode", "How the island shows notifications.", ("mode", "string", "popups (normal), silent (list only, no pop-ups) or off (ignore them)")),
        Tool("dnd_status", "Is Do Not Disturb on (island and Windows), and the notification mode."),
        Tool("clear_notifications", "Clear the notification list."),
        Tool("switch_to_window", "Bring an open app window to the front, e.g. 'switch to VS Code'.", ("name", "string", "app or window title")),
        Tool("snap_window", "Move/resize an open window.", ("name", "string", "app or window title"), ("position", "string", "left, right, top_left, top_right, bottom_left, bottom_right, maximize, minimize or restore")),
        Tool("minimize_all", "Minimize all windows (show desktop)."),
        Tool("close_app", "Close an app's windows gracefully (it can still ask to save).", ("name", "string", "app name")),
        Tool("find_files", "Find files by name in the user's folders. Returns paths.", ("query", "string", "words in the file name"), ("extension", "string", "e.g. pdf, or empty")),
        Tool("open_file", "Open a file or folder by its full path (from find_files).", ("path", "string", "full path")),
        Tool("open_latest_download", "Open the newest file in Downloads."),
        Tool("move_files", "Move files from one folder to another (e.g. screenshots into a folder). Only inside the user's own folders.",
            ("source", "string", "folder: desktop, downloads, documents, pictures, screenshots, videos, music, or a path"),
            ("destination", "string", "folder name or path; a plain name is created inside the source folder"),
            ("name_contains", "string", "only files whose name contains this, or empty"), ("extension", "string", "only this extension, e.g. png, or empty")),
        Tool("recycle_file", "Send one file or folder to the Recycle Bin (never permanent).", ("path", "string", "full path")),
        Tool("set_dark_mode", "Switch Windows dark mode on or off.", ("on", "boolean", "true = dark")),
        Tool("set_power_mode", "Set the Windows power mode.", ("mode", "string", "best_efficiency, balanced or best_performance")),
        Tool("open_settings", "Open a Windows settings page for things you can't toggle directly (night light, battery saver, do not disturb, display, sound, bluetooth...).", ("page", "string", "nightlight, batterysaver, notifications, display, sound, bluetooth, network, personalization, apps, privacy, windowsupdate")),
        Tool("set_reminder", "Remind the user at a time; it pops up on the island.", ("text", "string", "what to remind"), ("at", "string", "local time yyyy-MM-ddTHH:mm, or empty"), ("in_minutes", "number", "minutes from now, or 0")),
        Tool("list_reminders", "List upcoming reminders."),
        Tool("cancel_reminder", "Cancel a reminder (fuzzy).", ("text", "string", "part of the reminder")),
        Tool("remember", "Remember a fact about the user for later chats (stored only on this PC). Only when the user asks you to remember.", ("fact", "string", "the fact")),
        Tool("forget", "Forget a remembered fact (fuzzy).", ("fact", "string", "part of the fact")),
        Tool("list_memory", "List what you remember about the user."),
        Tool("create_routine", "Save a routine the user can run by name (e.g. 'work mode').", ("name", "string", "routine name"),
            ("steps", "string", "JSON array of tool calls, e.g. [{\"tool\":\"open_app\",\"args\":{\"name\":\"code\"}},{\"tool\":\"set_volume\",\"args\":{\"level\":30}}]")),
        Tool("run_routine", "Run a saved routine.", ("name", "string", "routine name")),
        Tool("list_routines", "List saved routines."),
        Tool("delete_routine", "Delete a routine.", ("name", "string", "routine name")),
        Tool("get_selected_text", "Get the text the user has selected in the app in front (for 'summarize/translate/reply to this')."),
        Tool("list_controls", "List clickable buttons and fields inside an app window.", ("window", "string", "app or window title")),
        Tool("click_control", "Click a button/menu/tab inside an app by its name (use list_controls first).", ("window", "string", "app or window title"), ("control", "string", "button name")),
        Tool("type_text", "Type text into an app (into a named field, or wherever the cursor is).", ("window", "string", "app or window title"), ("field", "string", "field name or empty"), ("text", "string", "text to type")),
    ];

    async Task<string?> ExecMore(string name, JsonObject a)
    {
        string S(string k) => a[k]?.ToString() ?? "";
        bool B(string k) => a[k]?.GetValue<bool>() ?? true;
        double D(string k) => double.TryParse(a[k]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        try
        {
            switch (name)
            {
                case "list_windows": return string.Join("; ", TopWindows().Select(w => $"{w.Proc}: {Trim(w.Title, 50)}").Distinct().Take(25));
                case "get_active_window": { var (p, t) = ActiveApp(); return p == "" ? "No app is in front (desktop)." : $"{p}: {t}"; }
                case "daily_brief": return BriefText(out _, out _, out _);
                case "get_notifications": { var s = NotifSummary(); MarkNotifsRead(); return s; }
                case "clear_notifications": _notifs.Clear(); RenderNotifs(); return "Cleared";
                case "set_dnd":
                    if (!B("on")) { EndDnd(); return "Do Not Disturb off"; }
                    SetDnd(D("minutes"), a["until_tomorrow"]?.GetValue<bool>() == true);
                    return "Do Not Disturb " + DndText();
                case "set_notification_mode":
                    {
                        var mode = S("mode").ToLowerInvariant() switch { var x when x.StartsWith("sil") => "Silent", var x when x.StartsWith("off") || x == "none" => "Off", _ => "Popups" };
                        SetNotifMode(mode); return "Notification mode: " + mode;
                    }
                case "dnd_status": return DndStatus();
                case "switch_to_window": { var w = FindWin(S("name")); if (w == null) return "No open window like that."; Front(w.H); return $"Switched to {w.Proc}"; }
                case "snap_window": { var w = FindWin(S("name")); if (w == null) return "No open window like that."; return Snap(w.H, S("position")); }
                case "minimize_all": { dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!; sh.MinimizeAll(); _pendingUndo = () => { dynamic s2 = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!; s2.UndoMinimizeALL(); }; return "Minimized"; }
                case "close_app":
                    {
                        var q = S("name");
                        var target = FindWin(q);
                        if (target == null) return "That app isn't open.";
                        if (Protected.Contains(target.Proc)) return "That's a system process; not closing it.";
                        var all = TopWindows().Where(w => w.Proc == target.Proc).ToList();
                        foreach (var w in all) PostMessage(w.H, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                        return $"Asked {target.Proc} to close ({all.Count} window{(all.Count == 1 ? "" : "s")})";
                    }
                case "find_files": return await Task.Run(() => FindFiles(S("query"), S("extension")));
                case "open_file": { var p = UserPath(S("path"), mustExist: true, allowAppData: true); Shell(p); return "Opened " + Path.GetFileName(p); }
                case "open_latest_download":
                    {
                        var f = LatestDownload();
                        if (f == null) return "Downloads is empty.";
                        Shell(f); return "Opened " + Path.GetFileName(f);
                    }
                case "move_files": return MoveFiles(S("source"), S("destination"), S("name_contains"), S("extension"));
                case "recycle_file":
                    {
                        var p = UserPath(S("path"), mustExist: true);
                        if (Directory.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        return "Moved to Recycle Bin (restore it from there)";
                    }
                case "set_dark_mode":
                    {
                        bool was = DarkMode(); SetDarkMode(B("on"));
                        _pendingUndo = () => SetDarkMode(was);
                        return B("on") ? "Dark mode on" : "Light mode on";
                    }
                case "set_power_mode": return SetPowerMode(S("mode"));
                case "open_settings":
                    {
                        var page = S("page").ToLowerInvariant().Replace(" ", "");
                        var map = new Dictionary<string, string> { ["nightlight"] = "nightlight", ["batterysaver"] = "batterysaver", ["notifications"] = "notifications", ["donotdisturb"] = "notifications", ["display"] = "display", ["sound"] = "sound", ["bluetooth"] = "bluetooth", ["network"] = "network", ["wifi"] = "network-wifi", ["personalization"] = "personalization", ["apps"] = "appsfeatures", ["privacy"] = "privacy", ["windowsupdate"] = "windowsupdate" };
                        Shell("ms-settings:" + map.GetValueOrDefault(page, "")); return "Opened settings";
                    }
                case "set_reminder":
                    {
                        DateTime at = D("in_minutes") > 0 ? DateTime.Now.AddMinutes(D("in_minutes"))
                                    : DateTime.TryParse(S("at"), out var t) ? t : DateTime.MinValue;
                        if (at <= DateTime.Now) return "I need a future time.";
                        _reminders.Add(new Reminder(at, S("text"))); SaveReminders();
                        return $"Reminder set for {at:ddd h:mm tt}";
                    }
                case "list_reminders": return _reminders.Count == 0 ? "No reminders." : string.Join("; ", _reminders.OrderBy(r => r.At).Select(r => $"{r.At:ddd h:mm tt}: {r.Text}"));
                case "cancel_reminder":
                    {
                        var r = _reminders.FirstOrDefault(r => r.Text.Contains(S("text"), StringComparison.OrdinalIgnoreCase));
                        if (r == null) return "No reminder like that.";
                        _reminders.Remove(r); SaveReminders(); return "Cancelled: " + r.Text;
                    }
                case "remember": { var f = S("fact").Trim(); if (f != "" && !_memory.Contains(f)) { _memory.Add(f); SaveMemory(); } return "Remembered"; }
                case "forget":
                    {
                        int n = _memory.RemoveAll(m => m.Contains(S("fact"), StringComparison.OrdinalIgnoreCase));
                        SaveMemory(); return n == 0 ? "Nothing like that saved." : $"Forgot {n} thing(s)";
                    }
                case "list_memory": return _memory.Count == 0 ? "Nothing remembered." : string.Join("; ", _memory);
                case "create_routine": return CreateRoutine(S("name"), S("steps"));
                case "run_routine": return await RunRoutine(S("name"));
                case "list_routines": return _routines.Count == 0 ? "No routines yet." : string.Join("; ", _routines.Select(r => $"{r.Name} ({r.Steps.Count} steps)"));
                case "delete_routine": { int n = _routines.RemoveAll(r => r.Name.Equals(S("name"), StringComparison.OrdinalIgnoreCase)); SaveRoutines(); return n == 0 ? "No routine with that name." : "Deleted"; }
                case "get_selected_text": return await GrabSelection();
                case "list_controls": { var w = FindWin(S("window")); if (w == null) return "No open window like that."; return await Task.Run(() => ListControls(w.H)); }
                case "click_control": { var w = FindWin(S("window")); if (w == null) return "No open window like that."; return await Task.Run(() => ClickControl(w.H, S("control"))); }
                case "type_text":
                    {
                        var w = FindWin(S("window")); if (w == null) return "No open window like that.";
                        Front(w.H); await Task.Delay(150);
                        if (S("field") != "") { var r = await Task.Run(() => FocusField(w.H, S("field"))); if (r != null) return r; await Task.Delay(100); }
                        TypeUnicode(S("text")); return "Typed";
                    }
            }
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
        return null;
    }

    // extra confirmation for things that are risky no matter what the settings say
    string? ExtraRisk(string name, JsonObject a)
    {
        string? path = name == "open_file" ? a["path"]?.ToString() : name == "open_latest_download" ? LatestDownload() : null;
        if (path != null && RunExts.Contains(Path.GetExtension(path).ToLowerInvariant()))
            return $"{Path.GetFileName(path)} is a program/script. Only allow if you trust it.";
        if (name == "press_keys" && RiskyKeys.Contains(NormKeys(a["keys"]?.ToString() ?? "")))
            return "This shortcut can close windows or delete things.";
        return null;
    }

    static readonly HashSet<string> RunExts = [".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".scr", ".com", ".lnk", ".reg", ".hta", ".jar", ".appx", ".msix"];
    static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ================= Windows =================
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rc r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    record Win(IntPtr H, string Title, string Proc);
    static readonly HashSet<string> Protected = ["explorer", "dwm", "winlogon", "csrss", "lsass", "svchost", "system", "island", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost"];

    List<Win> TopWindows()
    {
        var list = new List<Win>();
        var names = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (h == _hwnd || !IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true; // tool windows
            if (DwmGetWindowAttribute(h, 14 /*CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            var title = sb.ToString();
            if (title == "" || title == "Program Manager") return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!names.TryGetValue(pid, out var proc)) { try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { proc = ""; } names[pid] = proc; }
            list.Add(new Win(h, title, proc));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static readonly Dictionary<string, string> AppAlias = new()
    {
        ["vscode"] = "code", ["visualstudiocode"] = "code", ["edge"] = "msedge", ["word"] = "winword", ["powerpoint"] = "powerpnt",
        ["fileexplorer"] = "explorer", ["files"] = "explorer", ["teams"] = "ms-teams", ["terminal"] = "windowsterminal", ["visualstudio"] = "devenv",
    };

    Win? FindWin(string q)
    {
        var n = new string(q.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (n == "") return null;
        var proc = AppAlias.GetValueOrDefault(n, n);
        var wins = TopWindows();
        string Flat(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return wins.FirstOrDefault(w => w.Proc == proc)
            ?? wins.FirstOrDefault(w => w.Proc.StartsWith(proc) || proc.StartsWith(w.Proc) && w.Proc.Length > 2)
            ?? wins.FirstOrDefault(w => Flat(w.Title).Contains(n));
    }

    void Front(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /*RESTORE*/);
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
        if (fgThread != me) AttachThreadInput(fgThread, me, true);
        SetForegroundWindow(h); BringWindowToTop(h);
        if (fgThread != me) AttachThreadInput(fgThread, me, false);
    }

    string Snap(IntPtr h, string pos)
    {
        pos = pos.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        if (pos is "minimize" or "min") { ShowWindow(h, 6); return "Minimized"; }
        if (pos is "maximize" or "max" or "full" or "fullscreen") { ShowWindow(h, 3); Front(h); return "Maximized"; }
        if (pos is "restore") { ShowWindow(h, 9); Front(h); return "Restored"; }
        var mi = new MonInfo { Size = Marshal.SizeOf<MonInfo>() };
        GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
        var wa = mi.Work;
        int W = wa.R - wa.L, H = wa.B - wa.T;
        (int x, int y, int w, int hh) r = pos switch
        {
            "left" => (wa.L, wa.T, W / 2, H),
            "right" => (wa.L + W / 2, wa.T, W - W / 2, H),
            "top_left" => (wa.L, wa.T, W / 2, H / 2),
            "top_right" => (wa.L + W / 2, wa.T, W - W / 2, H / 2),
            "bottom_left" => (wa.L, wa.T + H / 2, W / 2, H - H / 2),
            "bottom_right" => (wa.L + W / 2, wa.T + H / 2, W - W / 2, H - H / 2),
            "top" => (wa.L, wa.T, W, H / 2),
            "bottom" => (wa.L, wa.T + H / 2, W, H - H / 2),
            _ => (-1, 0, 0, 0)
        };
        if (r.x < 0) return "Unknown position.";
        ShowWindow(h, 9);
        // compensate for the invisible resize borders so the window edges line up exactly
        GetWindowRect(h, out var wr);
        int dl = 0, dt = 0, dr = 0, db = 0;
        if (DwmGetWindowAttribute(h, 9 /*EXTENDED_FRAME_BOUNDS*/, out Rc fr, Marshal.SizeOf<Rc>()) == 0)
        { dl = fr.L - wr.L; dt = fr.T - wr.T; dr = wr.R - fr.R; db = wr.B - fr.B; }
        SetWindowPos(h, IntPtr.Zero, r.x - dl, r.y - dt, r.w + dl + dr, r.hh + dt + db, 0x0004 | 0x0040 /*NOZORDER|SHOWWINDOW*/);
        Front(h);
        return "Snapped " + pos.Replace('_', ' ');
    }

    // ================= Files (user folders only, never permanent delete) =================
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    string ResolveFolder(string s)
    {
        var n = s.Trim().ToLowerInvariant();
        string Known(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        return n switch
        {
            "desktop" => Known(Environment.SpecialFolder.Desktop),
            "documents" or "docs" or "my documents" => Known(Environment.SpecialFolder.MyDocuments),
            "downloads" or "download" => _dlDir != "" ? _dlDir : Path.Combine(Home, "Downloads"),
            "pictures" or "photos" or "images" => Known(Environment.SpecialFolder.MyPictures),
            "screenshots" => Path.Combine(Known(Environment.SpecialFolder.MyPictures), "Screenshots"),
            "videos" => Known(Environment.SpecialFolder.MyVideos),
            "music" => Known(Environment.SpecialFolder.MyMusic),
            "home" or "user" => Home,
            _ => Path.IsPathRooted(s) ? s : Path.Combine(Home, s)
        };
    }

    // only paths inside the user's profile (and not app data) can be touched
    static string UserPath(string p, bool mustExist = false, bool allowAppData = false)
    {
        var full = Path.GetFullPath(p.Trim().Trim('"'));
        if (!full.StartsWith(Home + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("only files inside your user folder are allowed");
        if (!allowAppData && full.StartsWith(Path.Combine(Home, "AppData") + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("app data is off-limits");
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new Exception("not found: " + full);
        return full;
    }

    string FindFiles(string query, string ext)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ext = ext.Trim().TrimStart('.').ToLowerInvariant();
        var roots = new[] { "desktop", "documents", "downloads", "pictures", "videos", "music" }.Select(ResolveFolder)
            .Append(Environment.GetEnvironmentVariable("OneDrive") ?? "").Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var hits = new List<FileInfo>();
        var sw = Stopwatch.StartNew();
        int seen = 0;
        foreach (var r in roots)
        {
            foreach (var f in Directory.EnumerateFiles(r, "*", opt))
            {
                if (++seen > 60000 || sw.ElapsedMilliseconds > 4000) break;
                if (f.Contains("\\node_modules\\") || f.Contains("\\.git\\") || f.Contains("\\obj\\") || f.Contains("\\bin\\")) continue;
                var nm = Path.GetFileName(f).ToLowerInvariant();
                if (ext != "" && !nm.EndsWith("." + ext)) continue;
                if (words.All(w => nm.Contains(w))) { try { hits.Add(new FileInfo(f)); } catch { } }
            }
        }
        if (hits.Count == 0) return "No files found.";
        return string.Join("\n", hits.DistinctBy(h => h.FullName.ToLowerInvariant()).OrderByDescending(h => h.LastWriteTime).Take(8)
            .Select(h => $"{h.FullName} ({h.LastWriteTime:d MMM yyyy}, {Size(h.Length)})"));
    }

    string? LatestDownload()
    {
        var dir = ResolveFolder("downloads");
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).EnumerateFiles().Where(f => !IsPartial(f.FullName) && !f.Name.StartsWith('.') && f.Name != "desktop.ini")
            .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
    }

    string MoveFiles(string source, string destination, string contains, string ext)
    {
        var src = UserPath(ResolveFolder(source), mustExist: true);
        var dst = UserPath(Path.IsPathRooted(destination) || Known(destination) ? ResolveFolder(destination) : Path.Combine(src, destination));
        ext = ext.Trim().TrimStart('.').ToLowerInvariant();
        var files = Directory.EnumerateFiles(src)
            .Where(f => (contains == "" || Path.GetFileName(f).Contains(contains, StringComparison.OrdinalIgnoreCase)) && (ext == "" || f.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Take(500).ToList();
        if (files.Count == 0) return "No matching files.";
        Directory.CreateDirectory(dst);
        var moved = new List<(string from, string to)>();
        foreach (var f in files)
        {
            var to = Path.Combine(dst, Path.GetFileName(f));
            for (int i = 2; File.Exists(to); i++) to = Path.Combine(dst, $"{Path.GetFileNameWithoutExtension(f)} ({i}){Path.GetExtension(f)}");
            try { File.Move(f, to); moved.Add((f, to)); } catch { }
        }
        _pendingUndo = () => { foreach (var (from, to) in moved) if (File.Exists(to) && !File.Exists(from)) File.Move(to, from); };
        return $"Moved {moved.Count} file(s) to {dst}";
    }

    static bool Known(string s) => s.Trim().ToLowerInvariant() is "desktop" or "documents" or "downloads" or "pictures" or "screenshots" or "videos" or "music";

    // ================= System settings =================
    const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr res);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid scheme);

    static bool DarkMode() => Registry.CurrentUser.OpenSubKey(ThemeKey)?.GetValue("AppsUseLightTheme") is int v && v == 0;

    static void SetDarkMode(bool dark)
    {
        using var k = Registry.CurrentUser.CreateSubKey(ThemeKey);
        k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _);
    }

    static string SetPowerMode(string mode)
    {
        var g = mode.ToLowerInvariant() switch
        {
            var m when m.Contains("eff") || m.Contains("saver") => new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"),
            var m when m.Contains("perf") || m.Contains("best_p") => new Guid("ded574b5-45a0-4f42-8737-46345c09c238"),
            _ => Guid.Empty
        };
        return PowerSetActiveOverlayScheme(g) == 0 ? "Power mode: " + mode.Replace('_', ' ') : "Couldn't change power mode on this PC.";
    }

    // ================= Reminders =================
    record Reminder(DateTime At, string Text);
    readonly List<Reminder> _reminders = [];
    static readonly string RemFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "reminders.json");

    void LoadAbilities()
    {
        try { if (File.Exists(RemFile)) _reminders.AddRange(JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(RemFile)) ?? []); } catch { }
        try { if (File.Exists(RoutineFile)) _routines.AddRange(JsonSerializer.Deserialize<List<Routine>>(File.ReadAllText(RoutineFile)) ?? []); } catch { }
    }

    void SaveReminders() { try { Directory.CreateDirectory(Path.GetDirectoryName(RemFile)!); File.WriteAllText(RemFile, JsonSerializer.Serialize(_reminders)); } catch { } }

    // from the 1-second tick
    void CheckReminders()
    {
        var due = _reminders.Where(r => r.At <= DateTime.Now).ToList();
        if (due.Count == 0) return;
        foreach (var r in due) _reminders.Remove(r);
        SaveReminders();
        System.Media.SystemSounds.Exclamation.Play();
        bool f = _focus; _focus = false;           // reminders get through focus mode
        AddNotif("Reminder", due[0].Text, due.Count > 1 ? $"+{due.Count - 1} more" : DateTime.Now.ToString("h:mm tt"), "\uE823", urgent: true);
        _focus = f;
    }

    // ================= Routines =================
    record Step(string Tool, JsonObject Args);
    record Routine(string Name, List<Step> Steps);
    readonly List<Routine> _routines = [];
    static readonly string RoutineFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "routines.json");
    static readonly HashSet<string> NotInRoutines = ["create_routine", "run_routine", "delete_routine", "end_voice", "pause_listening", "share_screen", "recycle_file", "get_selected_text", "type_text", "click_control", "run_command"];

    void SaveRoutines() { try { Directory.CreateDirectory(Path.GetDirectoryName(RoutineFile)!); File.WriteAllText(RoutineFile, JsonSerializer.Serialize(_routines)); } catch { } }

    string CreateRoutine(string name, string stepsJson)
    {
        name = name.Trim();
        if (name == "") return "Routine needs a name.";
        var known = Tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
        var steps = new List<Step>();
        foreach (var s in JsonNode.Parse(stepsJson)?.AsArray() ?? [])
        {
            var tool = s?["tool"]?.GetValue<string>() ?? "";
            if (!known.Contains(tool) || NotInRoutines.Contains(tool)) return $"'{tool}' can't be used in a routine.";
            steps.Add(new Step(tool, s?["args"]?.AsObject().DeepClone().AsObject() ?? []));
        }
        if (steps.Count == 0) return "No steps.";
        _routines.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _routines.Add(new Routine(name, steps)); SaveRoutines();
        return $"Saved “{name}” ({steps.Count} steps). Run it any time by saying its name.";
    }

    async Task<string> RunRoutine(string name)
    {
        var r = _routines.FirstOrDefault(r => r.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
             ?? _routines.FirstOrDefault(r => r.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (r == null) return "No routine with that name.";
        var results = new List<string>();
        foreach (var s in r.Steps)
        {
            results.Add(await Gate(s.Tool, s.Args.DeepClone().AsObject(), fromRoutine: true)); // each step still obeys its permission
            await Task.Delay(250);
        }
        return $"Ran “{r.Name}”: " + string.Join("; ", results.Select(x => Short(x)));
    }

    // ================= Selected text =================
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern uint SendInput(uint n, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct KeyIn { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyIn Key; [FieldOffset(8)] private MouseInPad _pad; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInPad { public int a, b; public uint c, d, e; public IntPtr f; }
    IntPtr _prevFg;
    bool _grabbingSel;

    static Input KeyEv(ushort vk, bool up, ushort scan = 0, bool unicode = false) =>
        new() { Type = 1, Key = new KeyIn { Vk = vk, Scan = scan, Flags = (up ? 2u : 0u) | (unicode ? 4u : 0u) } };

    async Task<string> GrabSelection()
    {
        var target = GetForegroundWindow();
        if (target == _hwnd) target = _prevFg;
        if (target == IntPtr.Zero) return "No app is in front.";
        if (GetForegroundWindow() != target) { Front(target); await Task.Delay(150); }
        string? old = null;
        try { if (Clipboard.ContainsText()) old = Clipboard.GetText(); } catch { }
        uint seq = GetClipboardSequenceNumber();
        _grabbingSel = true;
        try
        {
            var keys = new[] { KeyEv(0x12, true), KeyEv(0x11, false), KeyEv(0x43, false), KeyEv(0x43, true), KeyEv(0x11, true) }; // release Alt, Ctrl+C
            SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
            for (int i = 0; i < 12 && GetClipboardSequenceNumber() == seq; i++) await Task.Delay(50);
            if (GetClipboardSequenceNumber() == seq) return "Nothing is selected.";
            string sel = "";
            for (int i = 0; i < 3; i++) { try { sel = Clipboard.ContainsText() ? Clipboard.GetText() : ""; break; } catch { await Task.Delay(60); } }
            try { if (old != null) Clipboard.SetText(old); } catch { }
            return sel.Trim() == "" ? "Nothing is selected." : Trim(sel, 6000);
        }
        finally { _ = Task.Delay(600).ContinueWith(_ => _grabbingSel = false); }
    }

    static void TypeUnicode(string text)
    {
        var list = new List<Input>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { list.Add(KeyEv(0x0D, false)); list.Add(KeyEv(0x0D, true)); continue; }
            list.Add(KeyEv(0, false, ch, true)); list.Add(KeyEv(0, true, ch, true));
        }
        if (list.Count > 0) SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<Input>());
    }

    // ================= App control (UI Automation) =================
    // built on first use: touching UI Automation loads a lot, so the island only pays for it when you use app control
    static System.Windows.Automation.Condition? _clickable;
    static System.Windows.Automation.Condition Clickable => _clickable ??= new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));

    static List<AutomationElement> Controls(IntPtr h) =>
        AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, Clickable).Cast<AutomationElement>()
            .Where(e => { try { return !e.Current.IsOffscreen && e.Current.Name.Trim() != ""; } catch { return false; } })
            .Take(400).ToList();

    static string ListControls(IntPtr h)
    {
        var items = Controls(h).Select(e => $"{e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}: {Trim(e.Current.Name.Trim(), 40)}").Distinct().Take(60).ToList();
        return items.Count == 0 ? "No named controls found (this app may not expose them)." : string.Join("; ", items);
    }

    static AutomationElement? BestControl(IntPtr h, string name, bool edit = false)
    {
        var all = Controls(h).Where(e => !edit || e.Current.ControlType == ControlType.Edit || e.Current.ControlType == ControlType.ComboBox).ToList();
        return all.FirstOrDefault(e => e.Current.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(e => e.Current.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    string ClickControl(IntPtr h, string name)
    {
        var el = BestControl(h, name);
        if (el == null) return $"No control named “{name}”.";
        Dispatcher.Invoke(() => Front(h));
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) ((InvokePattern)p).Invoke();
        else if (el.TryGetCurrentPattern(TogglePattern.Pattern, out p)) ((TogglePattern)p).Toggle();
        else if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) ((SelectionItemPattern)p).Select();
        else if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p)) ((ExpandCollapsePattern)p).Expand();
        else { el.SetFocus(); return "Focused it (it can't be clicked directly)."; }
        return $"Clicked “{el.Current.Name.Trim()}”";
    }

    static string? FocusField(IntPtr h, string field)
    {
        var el = BestControl(h, field, edit: true);
        if (el == null) return $"No field named “{field}”.";
        el.SetFocus();
        return null;
    }
}
