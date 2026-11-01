using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Island;

// Interruption-aware pop-ups: while you're busy, non-urgent pop-ups wait for a natural break
// and then arrive together as one card. Busy = typing, a full-screen app (video, game, presentation),
// a call or meeting (another app using the mic or camera), or talking with Gemini.
// Urgent things (timers, reminders, power warnings) always come through right away.
public partial class MainWindow
{
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    record Deferred(string App, string Title, string Body, string Icon, DateTime At, NotifItem? Item);
    readonly List<Deferred> _deferred = [];
    long _lastKeyTick;          // set by the keyboard hook: only the time of the last key press, never which key
    int _freeTicks;             // seconds in a row you haven't been busy
    bool _inCall; DateTime _callChecked;

    // why you're busy right now ("" = not busy)
    string BusyReason()
    {
        if (!_ai.HoldWhenBusy) return "";
        if (_live != null) return "talking with Gemini";
        if (Environment.TickCount64 - _lastKeyTick < 3000) return "typing";
        try
        {
            if (SHQueryUserNotificationState(out int s) == 0)
            {
                if (s == 4) return "presenting";
                if (s is 2 or 3) return "full screen";
            }
        }
        catch { }
        if (InCall()) return "on a call";
        return "";
    }

    // another app has the mic or camera open (Teams, Zoom, Meet in a browser, Discord...). Checked at most every 5 s.
    bool InCall()
    {
        if ((DateTime.Now - _callChecked).TotalSeconds < 5) return _inCall;
        _callChecked = DateTime.Now;
        _inCall = DeviceInUse("microphone") || DeviceInUse("webcam");
        return _inCall;
    }

    static bool DeviceInUse(string cap)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{cap}");
            if (root == null) return false;
            bool Check(RegistryKey parent)
            {
                foreach (var name in parent.GetSubKeyNames())
                {
                    if (name == "NonPackaged" || name.Contains("island.exe", StringComparison.OrdinalIgnoreCase)) continue; // the island's own mic
                    using var k = parent.OpenSubKey(name);
                    if (k?.GetValue("LastUsedTimeStart") is long start && start > 0 && k.GetValue("LastUsedTimeStop") is long stop && stop == 0) return true;
                }
                return false;
            }
            if (Check(root)) return true;
            using var np = root.OpenSubKey("NonPackaged");
            return np != null && Check(np);
        }
        catch { return false; }
    }

    // ShowNotification calls this first: true = held for later
    bool HoldIfBusy(string app, string title, string body, string icon, NotifItem? item = null)
    {
        if (BusyReason() == "") return false;
        _deferred.Add(new(app, title, body, icon, DateTime.Now, item));
        _freeTicks = 0;
        return true;
    }

    // every second: when you've been free for 2 s, deliver what waited
    void CheckBusy()
    {
        if (_deferred.Count == 0) return;
        if (BusyReason() != "") { _freeTicks = 0; return; }
        if (++_freeTicks < 2) return;
        // skip what you've already seen in the notification center, and anything older than an hour
        var due = _deferred.Where(d => d.Item?.Read != true && (DateTime.Now - d.At).TotalHours < 1).ToList();
        _deferred.Clear();
        if (due.Count == 0) return;
        if (due.Count == 1)
        {
            var d = due[0];
            ShowNotification(d.App, d.Title, d.Body, d.Icon, urgent: true); // urgent: don't re-check busy
            if (d.Item != null) NotifAppImg.Source = AppIcon(d.App);
            return;
        }
        var apps = string.Join(", ", due.GroupBy(d => d.App).Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key).Take(4));
        ShowNotification("While you were busy", $"{due.Count} notifications", apps + "  \u00B7  hover to see them", "\uEA8F", urgent: true);
    }

    void OnHoldBusyToggle(object s, RoutedEventArgs e)
    {
        _ai.HoldWhenBusy = HoldBusyBtn.IsChecked == true; _ai.Save();
        if (!_ai.HoldWhenBusy) { _freeTicks = 2; CheckBusy(); }
        ShowToast(_ai.HoldWhenBusy ? "Pop-ups wait while you're busy" : "Pop-ups show right away", Colors.White);
    }

    string BusyStatus()
    {
        var r = BusyReason();
        return (_ai.HoldWhenBusy ? "Pop-ups wait while the user is busy" : "Pop-ups are never held for busyness") +
               (r != "" ? $" (busy now: {r}, {_deferred.Count} waiting)" : "") + ".";
    }
}
