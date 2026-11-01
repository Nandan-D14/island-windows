using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Island;

// Do Not Disturb + notification modes.
//  DND (island): no pop-ups, notifications are kept and summarised when it ends. Reminders, timers and power warnings still come through.
//  Mode: Pop-ups (normal) · Silent (list + badge, no pop-ups) · Off (the island ignores Windows notifications).
//  Windows' own Do Not Disturb has no public on/off API: the island shows its state and opens its settings.
public partial class MainWindow
{
    bool DndActive => _ai.DndUntil is DateTime u && u > DateTime.Now;
    DateTime _dndSince;

    void SetDnd(double minutes, bool untilTomorrow = false)
    {
        bool was = DndActive;
        _ai.DndUntil = untilTomorrow ? DateTime.Today.AddDays(1).AddHours(8)
                     : minutes > 0 ? DateTime.Now.AddMinutes(minutes) : DateTime.MaxValue;
        _ai.Save();
        if (!was) _dndSince = DateTime.Now;
        RenderDnd();
        ShowToast("\uD83C\uDF19 Do Not Disturb " + DndText(), Color.FromRgb(0xBF, 0x5A, 0xF2));
    }

    void EndDnd(bool expired = false)
    {
        if (_ai.DndUntil == null) return;
        _ai.DndUntil = null; _ai.Save();
        RenderDnd();
        ShowToast(expired ? "Do Not Disturb ended" : "Do Not Disturb off", Colors.White);
        _ = Task.Delay(3500).ContinueWith(_ => Dispatcher.Invoke(() => FocusCatchUp(_dndSince == default ? DateTime.Now.AddHours(-12) : _dndSince)));
    }

    string DndText() => _ai.DndUntil is not DateTime u ? "off"
        : u == DateTime.MaxValue ? "on"
        : u.Date == DateTime.Today ? $"until {u:h:mm tt}"
        : $"until {u:ddd h:mm tt}";

    // from the 1-second tick
    void CheckDnd()
    {
        if (_ai.DndUntil is DateTime u && u <= DateTime.Now) EndDnd(expired: true);
        if (_ticks % 10 == 0) UpdateWinDnd();
    }

    void RenderDnd()
    {
        bool on = DndActive;
        DndBtn.IsChecked = on;
        IdleDnd.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DndState.Text = "\uD83C\uDF19 Do Not Disturb " + DndText();
        DndBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DndMoon.Foreground = on ? new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)) : (Brush)Resources["Dim"];
        var kind = _ai.DndUntil is not DateTime u || !on ? "" : u == DateTime.MaxValue ? "on" : u.Date > DateTime.Today ? "morning" : "hour";
        Seg(Dnd60Btn, kind == "hour"); Seg(DndTmrBtn, kind == "morning"); Seg(DndOnBtn, kind == "on");
        Seg(ModePopBtn, _ai.NotifMode is not ("Silent" or "Off"));
        Seg(ModeSilentBtn, _ai.NotifMode == "Silent");
        Seg(ModeOffBtn, _ai.NotifMode == "Off");
        NotifModeHint.Text = _ai.NotifMode switch
        {
            "Silent" => "Silent: notifications are listed here, no pop-ups.",
            "Off" => "Off: the island ignores Windows notifications.",
            _ => on ? "Held while Do Not Disturb is on. Reminders and timers still come through." : ""
        };
        NotifModeHint.Visibility = NotifModeHint.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        HoldBusyBtn.IsChecked = _ai.HoldWhenBusy;
    }

    static void Seg(Button b, bool on) => b.Tag = on ? "on" : null;

    void OnNotifOptions(object s, RoutedEventArgs e) =>
        NotifOptions.Visibility = NotifOptions.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    void OnDndToggle(object s, RoutedEventArgs e)
    {
        if (DndActive) EndDnd(); else SetDnd(0);
    }

    void OnDndFor(object s, RoutedEventArgs e)
    {
        var tag = (string)((Button)s).CommandParameter;
        if (tag == "tomorrow") SetDnd(0, untilTomorrow: true); else SetDnd(double.Parse(tag));
    }

    void OnDndOff(object s, RoutedEventArgs e) => EndDnd();

    void OnNotifMode(object s, RoutedEventArgs e) => SetNotifMode((string)((Button)s).CommandParameter);

    void SetNotifMode(string mode)
    {
        _ai.NotifMode = mode; _ai.Save();
        RenderDnd();
        ShowToast(mode switch { "Silent" => "Notifications: silent", "Off" => "Notifications: off", _ => "Notifications: pop-ups" }, Colors.White);
    }

    // ---- Windows' own Do Not Disturb (read only: Windows has no public switch for it) ----
    [DllImport("ntdll.dll")] static extern int NtQueryWnfStateData(ref ulong name, IntPtr type, IntPtr scope, out uint stamp, byte[] buf, ref uint size);
    int _winDnd = -1; // 0 off, 1 priority only, 2 alarms only

    void UpdateWinDnd()
    {
        try
        {
            ulong n = 0x0D83063EA3BF1C75UL; // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
            var b = new byte[4]; uint size = 4;
            _winDnd = NtQueryWnfStateData(ref n, IntPtr.Zero, IntPtr.Zero, out _, b, ref size) == 0 ? (int)BitConverter.ToUInt32(b) : -1;
        }
        catch { _winDnd = -1; }
        WinDndText.Text = _winDnd switch
        {
            0 => "Windows Do Not Disturb: off",
            1 => "Windows Do Not Disturb: on (priority only)",
            2 => "Windows Do Not Disturb: on (alarms only)",
            _ => "Windows Do Not Disturb: unknown"
        };
    }

    void OnWinDndSettings(object s, RoutedEventArgs e) { try { Shell("ms-settings:notifications"); } catch { } }

    string DndStatus() =>
        $"Island Do Not Disturb: {DndText()}. Notification mode: {_ai.NotifMode}. " + WinDndText.Text + ". " + BusyStatus();
}
