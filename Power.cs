using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace Island;

// Keyboard shortcuts, power (shutdown / restart / sleep, now or scheduled) and a guarded terminal runner.
// Power and terminal ALWAYS show the Allow / Deny card (no "always allow"); see Safety.cs → AlwaysAsk.
public partial class MainWindow
{
    static IEnumerable<JsonObject> PowerTools() =>
    [
        Tool("press_keys", "Press a keyboard shortcut in the app the user is using, e.g. ctrl+s, ctrl+shift+t, alt+tab, win+d, f5, space, enter.",
            ("keys", "string", "the shortcut, keys joined with +"), ("times", "number", "how many times, usually 1")),
        Tool("power", "Shut down, restart, sleep or hibernate the PC, now or after some minutes. The user is always asked to confirm.",
            ("action", "string", "shutdown, restart, sleep or hibernate"), ("in_minutes", "number", "0 = now, or minutes from now")),
        Tool("cancel_power", "Cancel a scheduled shutdown / restart / sleep."),
        Tool("run_command", "Run one PowerShell command on the user's PC and get its output (e.g. ipconfig, check disk space, list a folder, git status). " +
            "Runs as the normal user, never admin, 60 s limit. The user sees the exact command and must approve it every time. Prefer the other tools when one fits.",
            ("command", "string", "a single PowerShell command"), ("why", "string", "one short line telling the user what it does")),
    ];

    async Task<string?> ExecPower(string name, JsonObject a)
    {
        string S(string k) => a[k]?.ToString() ?? "";
        double D(string k) => double.TryParse(a[k]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        switch (name)
        {
            case "press_keys": return await PressKeys(S("keys"), Math.Clamp((int)Math.Round(D("times")), 1, 10));
            case "power": return SchedulePower(S("action"), Math.Max(0, D("in_minutes")));
            case "cancel_power": return CancelPower();
            case "run_command": return await RunCommand(S("command"));
        }
        return null;
    }

    // ================= Keyboard shortcuts =================
    static readonly Dictionary<string, (ushort vk, bool ext)> Keys = BuildKeys();

    static Dictionary<string, (ushort, bool)> BuildKeys()
    {
        var k = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = (0x11, false), ["control"] = (0x11, false), ["shift"] = (0x10, false), ["alt"] = (0x12, false),
            ["win"] = (0x5B, true), ["windows"] = (0x5B, true), ["enter"] = (0x0D, false), ["return"] = (0x0D, false),
            ["esc"] = (0x1B, false), ["escape"] = (0x1B, false), ["tab"] = (0x09, false), ["space"] = (0x20, false),
            ["backspace"] = (0x08, false), ["delete"] = (0x2E, true), ["del"] = (0x2E, true), ["insert"] = (0x2D, true),
            ["home"] = (0x24, true), ["end"] = (0x23, true), ["pageup"] = (0x21, true), ["pgup"] = (0x21, true), ["pagedown"] = (0x22, true), ["pgdn"] = (0x22, true),
            ["up"] = (0x26, true), ["down"] = (0x28, true), ["left"] = (0x25, true), ["right"] = (0x27, true),
            ["printscreen"] = (0x2C, true), ["prtsc"] = (0x2C, true),
            ["plus"] = (0xBB, false), ["="] = (0xBB, false), ["minus"] = (0xBD, false), ["-"] = (0xBD, false), ["."] = (0xBE, false), ["period"] = (0xBE, false),
            [","] = (0xBC, false), ["comma"] = (0xBC, false), ["/"] = (0xBF, false), ["slash"] = (0xBF, false), [";"] = (0xBA, false), ["`"] = (0xC0, false),
            ["["] = (0xDB, false), ["]"] = (0xDD, false), ["\\"] = (0xDC, false), ["'"] = (0xDE, false),
            ["volumeup"] = (0xAF, true), ["volumedown"] = (0xAE, true), ["volumemute"] = (0xAD, true),
            ["playpause"] = (0xB3, true), ["nexttrack"] = (0xB0, true), ["prevtrack"] = (0xB1, true),
        };
        for (char c = 'a'; c <= 'z'; c++) k[c.ToString()] = ((ushort)char.ToUpper(c), false);
        for (char c = '0'; c <= '9'; c++) k[c.ToString()] = (c, false);
        for (int i = 1; i <= 12; i++) k["f" + i] = ((ushort)(0x6F + i), false);
        return k;
    }

    static string NormKeys(string keys) =>
        string.Join("+", keys.ToLowerInvariant().Replace(" ", "").Replace("cmd", "ctrl").Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p switch { "control" => "ctrl", "windows" => "win", "escape" => "esc", "return" => "enter", "del" => "delete", _ => p }));

    // never: these open a command box, wipe files for good, or reach the security screen
    static readonly HashSet<string> BlockedKeys = ["win+r", "win+x", "shift+delete", "ctrl+shift+delete", "ctrl+alt+delete", "ctrl+alt+del", "win+ctrl+shift+b"];
    // these can close things / delete: always asked
    static readonly HashSet<string> RiskyKeys = ["alt+f4", "ctrl+w", "ctrl+shift+w", "ctrl+q", "ctrl+f4", "delete", "ctrl+shift+n"];

    async Task<string> PressKeys(string keys, int times)
    {
        var norm = NormKeys(keys);
        if (BlockedKeys.Contains(norm) || norm.Contains("ctrl+alt")) return $"Blocked: {norm} isn't allowed for safety.";
        var parts = norm.Split('+');
        var codes = new List<(ushort vk, bool ext)>();
        foreach (var p in parts)
        {
            if (!Keys.TryGetValue(p, out var code)) return $"Unknown key \"{p}\".";
            codes.Add(code);
        }
        // the shortcut goes to the app you were using, not to the island
        var target = GetForegroundWindow();
        if (target == _hwnd && _prevFg != IntPtr.Zero) { Front(_prevFg); await Task.Delay(150); }
        var inputs = new List<Input>();
        for (int t = 0; t < times; t++)
        {
            foreach (var c in codes) inputs.Add(KeyEvExt(c.vk, false, c.ext));
            foreach (var c in Enumerable.Reverse(codes)) inputs.Add(KeyEvExt(c.vk, true, c.ext));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        return $"Pressed {norm}" + (times > 1 ? $" \u00D7{times}" : "");
    }

    static Input KeyEvExt(ushort vk, bool up, bool ext) =>
        new() { Type = 1, Key = new KeyIn { Vk = vk, Flags = (up ? 2u : 0u) | (ext ? 1u : 0u) } };

    // ================= Power =================
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    string _powerAction = "";
    DateTime _powerAt;
    readonly System.Windows.Threading.DispatcherTimer _powerTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _powerWarned;

    string SchedulePower(string action, double minutes)
    {
        action = action.ToLowerInvariant().Trim() switch
        {
            "shut down" or "shutdown" or "turn off" or "power off" => "shutdown",
            "restart" or "reboot" => "restart",
            "sleep" or "suspend" => "sleep",
            "hibernate" => "hibernate",
            var x => x
        };
        if (action is not ("shutdown" or "restart" or "sleep" or "hibernate")) return "Unknown power action.";
        CancelPower(quiet: true);
        // "now" still gets a few seconds so you can say "cancel"
        int secs = minutes <= 0 ? 10 : (int)Math.Min(minutes * 60, 24 * 3600);
        if (action is "shutdown" or "restart")
        {
            var flag = action == "shutdown" ? "/s" : "/r";
            var p = Process.Start(new ProcessStartInfo("shutdown.exe", $"{flag} /t {secs} /c \"Island: {action} requested by you.\"") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
            if (p?.ExitCode is int c && c != 0) return $"Windows refused ({c}). Maybe another shutdown is already scheduled.";
        }
        _powerAction = action; _powerAt = DateTime.Now.AddSeconds(secs); _powerWarned = false;
        _powerTimer.Stop(); _powerTimer.Start();
        UpdatePowerLine();
        var when = minutes <= 0 ? "in 10 seconds" : secs < 3600 ? $"in {Math.Round(secs / 60.0)} min" : $"at {_powerAt:h:mm tt}";
        return $"{char.ToUpper(action[0]) + action[1..]} {when}. Say \"cancel shutdown\" to stop it.";
    }

    string CancelPower(bool quiet = false)
    {
        bool had = _powerAction != "";
        if (_powerAction is "shutdown" or "restart" || !quiet)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000); } catch { }
        }
        _powerAction = ""; _powerTimer.Stop();
        UpdatePowerLine();
        if (!quiet && had) ShowToast("\u23FB Cancelled", Colors.White);
        return had ? "Cancelled" : "Nothing was scheduled.";
    }

    void InitPower() => _powerTimer.Tick += (_, _) => PowerTick();

    void PowerTick()
    {
        if (_powerAction == "") { _powerTimer.Stop(); return; }
        var left = _powerAt - DateTime.Now;
        if (!_powerWarned && left.TotalSeconds <= 60 && left.TotalSeconds > 5)
        {
            _powerWarned = true;
            bool f = _focus; _focus = false;
            ShowNotification("Power", $"{char.ToUpper(_powerAction[0]) + _powerAction[1..]} in 1 minute", "Save your work \u00B7 say \u201Ccancel shutdown\u201D to stop", "\uE7E8", urgent: true);
            _focus = f;
        }
        if (left <= TimeSpan.Zero && _powerAction is "sleep" or "hibernate")
        {
            var hib = _powerAction == "hibernate";
            _powerAction = ""; _powerTimer.Stop(); UpdatePowerLine();
            SetSuspendState(hib, false, false); // Windows does shutdown/restart itself
            return;
        }
        UpdatePowerLine();
    }

    // countdown in the pill (shares the live line with downloads/lyrics; power wins)
    void UpdatePowerLine()
    {
        if (_powerAction == "") { if (_dlLine?.StartsWith("\u23FB") == true) SetLiveLine(null, _lyricLine); return; }
        var left = _powerAt - DateTime.Now;
        if (left.TotalMinutes > 60) return;
        var t = left.TotalSeconds < 0 ? "now" : left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes}:{left.Seconds:00}" : $"{Math.Max(0, (int)left.TotalSeconds)}s";
        SetLiveLine($"\u23FB {char.ToUpper(_powerAction[0]) + _powerAction[1..]} in {t}", _lyricLine);
    }

    // ================= Terminal (guarded) =================
    // refused even if approved: things that damage Windows, raise privileges, hide what they run, or download-and-run
    static readonly Regex DangerousCmd = new(
        @"\b(format(\.com)?\s+[a-z]:|diskpart|bcdedit|bootrec|bcdboot|cipher\s+/w|vssadmin|wbadmin|wmic\s+shadowcopy|reagentc|takeown|icacls|cacls|" +
        @"net\s+(user|localgroup)|set-executionpolicy|invoke-expression|iex|-e(nc(odedcommand)?)?\s|frombase64string|-verb\s+runas|runas|sudo|" +
        @"reg(\.exe)?\s+(delete|add|import)|remove-itemproperty\s+.*hklm|set-itemproperty\s+.*hklm|clear-disk|initialize-disk|remove-partition|format-volume|" +
        @"stop-computer|restart-computer|shutdown|logoff|schtasks|register-scheduledtask|new-service|sc(\.exe)?\s+(create|delete|config)|" +
        @"set-mppreference|add-mppreference|disable-|netsh|downloadstring|downloadfile|start-bitstransfer|certutil|bitsadmin|mshta|rundll32|regsvr32|wscript|cscript|" +
        @"powershell(\.exe)?\s|pwsh(\.exe)?\s|cmd(\.exe)?\s+/c|invoke-webrequest[^;|]*\|\s*(iex|invoke)|irm\s[^;|]*\|\s*iex|curl[^;|]*\|\s*(sh|bash|iex))|" + // no trailing \b: several patterns end in ":" or a space
        @"(remove-item|rm|del|erase|rd|rmdir)\b[^;|]*(\s[a-z]:\\?\s*$|\s[a-z]:\\\*|c:\\windows|c:\\program|\$env:(windir|systemroot|programfiles)|system32)",
        RegexOptions.IgnoreCase);

    static string? CommandProblem(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "Empty command.";
        if (cmd.Length > 600) return "Command too long. Keep it to one simple command.";
        if (cmd.Contains('\n') || cmd.Contains('\r')) return "Only one-line commands are allowed.";
        if (DangerousCmd.IsMatch(cmd)) return "This command could damage Windows, change security settings or run hidden code, so Island won't run it. If you really need it, run it yourself in Terminal.";
        return null;
    }

    async Task<string> RunCommand(string cmd)
    {
        var problem = CommandProblem(cmd);
        if (problem != null) return "Blocked: " + problem;
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Home,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-Command",
                     "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + cmd + " | Out-String -Width 160" })
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // nothing can wait for typed input
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var done = await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
        if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch { } return "Stopped: the command took longer than 60 seconds."; }
        var output = (await outTask).Trim();
        var err = (await errTask).Trim();
        var text = output + (err != "" ? (output != "" ? "\n" : "") + "ERROR: " + err : "");
        if (text == "") text = p.ExitCode == 0 ? "(done, no output)" : $"(exit code {p.ExitCode})";
        return text.Length > 6000 ? text[..6000] + "\n…(output cut)" : text;
    }
}
