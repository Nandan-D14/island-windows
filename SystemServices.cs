using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Island;

// ---------- Master volume via Core Audio (no extra libraries) ----------
static class Audio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float l, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float l, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float l);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float l);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float l, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float l);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float l);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }

    static IAudioEndpointVolume? _ep;
    static DateTime _fetched;
    static Guid _ctx = Guid.Empty;

    static IAudioEndpointVolume? Ep()
    {
        // re-acquire periodically so switching to headphones is picked up
        if (_ep == null || (DateTime.Now - _fetched).TotalSeconds > 5)
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0, 1, out var dev) == 0)
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
                    _ep = (IAudioEndpointVolume)o;
                }
            }
            catch { _ep = null; }
            _fetched = DateTime.Now;
        }
        return _ep;
    }

    public static (int vol, bool mute)? Get()
    {
        var ep = Ep();
        if (ep == null || ep.GetMasterVolumeLevelScalar(out var v) != 0) return null;
        ep.GetMute(out var m);
        return ((int)Math.Round(v * 100), m);
    }

    public static void SetVolume(int v) => Ep()?.SetMasterVolumeLevelScalar(Math.Clamp(v, 0, 100) / 100f, ref _ctx);
    public static void SetMute(bool m) => Ep()?.SetMute(m, ref _ctx);
}

// ---------- Built-in display brightness (laptops) via WMI ----------
static class Brightness
{
    public static bool Supported { get; private set; } = true;

    public static int? Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        Supported = false;
        return null;
    }

    public static void Set(int v)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get()) o.InvokeMethod("WmiSetBrightness", new object[] { 1, (byte)Math.Clamp(v, 0, 100) });
        }
        catch { }
    }

    // event-driven: zero cost until brightness actually changes
    public static ManagementEventWatcher? Watch(Action<int> onChange)
    {
        try
        {
            var w = new ManagementEventWatcher(new ManagementScope(@"root\WMI"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            w.EventArrived += (_, e) => onChange(Convert.ToInt32(e.NewEvent["Brightness"]));
            w.Start();
            return w;
        }
        catch { Supported = false; return null; }
    }
}

// ---------- CPU / RAM / Network / Battery ----------
static class Stats
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus
    {
        public uint Length, Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    [StructLayout(LayoutKind.Sequential)]
    public struct PowerStatus { public byte AC, Flag, Percent, Saver; public int Life, Full; }
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out PowerStatus s);

    static long _idle, _total, _rx, _tx;
    static DateTime _netAt;

    public static int Cpu()
    {
        GetSystemTimes(out var idle, out var k, out var u);
        long total = k + u, dIdle = idle - _idle, dTotal = total - _total;
        _idle = idle; _total = total;
        return dTotal <= 0 ? 0 : (int)Math.Clamp(100 - dIdle * 100 / dTotal, 0, 100);
    }

    public static (int load, double usedGb, double totalGb) Ram()
    {
        var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        GlobalMemoryStatusEx(ref m);
        return ((int)m.Load, (m.Total - m.Avail) / 1073741824.0, m.Total / 1073741824.0);
    }

    public static (double down, double up) Net()
    {
        long rx = 0, tx = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var st = n.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent;
        }
        var now = DateTime.Now; var dt = (now - _netAt).TotalSeconds;
        (double, double) r = _netAt == default || dt <= 0 ? (0, 0) : ((rx - _rx) / dt, (tx - _tx) / dt);
        _rx = rx; _tx = tx; _netAt = now;
        return r;
    }

    public static string Speed(double bps) =>
        bps >= 1048576 ? $"{bps / 1048576:0.0} MB/s" : bps >= 1024 ? $"{bps / 1024:0} KB/s" : $"{bps:0} B/s";
}

// ---------- Wi-Fi / Bluetooth radios ----------
static class Radios
{
    public static async Task<Windows.Devices.Radios.Radio?> Find(Windows.Devices.Radios.RadioKind kind)
    {
        try
        {
            if (await Windows.Devices.Radios.Radio.RequestAccessAsync() != Windows.Devices.Radios.RadioAccessStatus.Allowed) return null;
            return (await Windows.Devices.Radios.Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch { return null; }
    }

    public static async Task<bool> Toggle(Windows.Devices.Radios.RadioKind kind, bool on)
    {
        var r = await Find(kind);
        if (r == null) return false;
        return await r.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off)
               == Windows.Devices.Radios.RadioAccessStatus.Allowed;
    }
}

// ---------- Windows notifications ----------
// Reads Windows' own notification list (works even when banners are hidden by Do Not Disturb).
// Falls back to watching toast windows with UI Automation if notification access is turned off.
record ToastItem(uint Id, string App, string Aumid, string Title, string Body, DateTime At, byte[]? Logo, bool Fresh);

static class Toasts
{
    static Windows.UI.Notifications.Management.UserNotificationListener? _listener;
    static readonly Dictionary<string, byte[]?> Logos = [];

    public static async void Watch(Action<ToastItem> onToast, Func<bool> slow)
    {
        try
        {
            _listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            if (await _listener.RequestAccessAsync() != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) _listener = null;
        }
        catch { _listener = null; }
        if (_listener == null) { WatchWindows(onToast); return; }

        var seen = new HashSet<uint>();
        bool first = true;
        while (true)
        {
            try
            {
                var list = await _listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
                foreach (var n in list.OrderBy(n => n.CreationTime))
                {
                    if (!seen.Add(n.Id)) continue;
                    var b = n.Notification?.Visual?.GetBinding(Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
                    var texts = b?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
                    if (texts.Count == 0) continue;
                    string app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notification", aumid = n.AppInfo?.AppUserModelId ?? "";
                    onToast(new ToastItem(n.Id, app, aumid, texts[0], string.Join("  ", texts.Skip(1)), n.CreationTime.LocalDateTime, await Logo(n, aumid), !first));
                }
                seen.IntersectWith(list.Select(n => n.Id)); // forget ones Windows has cleared
                first = false;
            }
            catch { }
            await Task.Delay(slow() ? 4000 : 1500);
        }
    }

    // app logo straight from Windows (works for Store apps like WhatsApp too), cached per app
    static async Task<byte[]?> Logo(Windows.UI.Notifications.UserNotification n, string aumid)
    {
        if (Logos.TryGetValue(aumid, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            var r = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
            if (r != null)
            {
                using var s = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(await r.OpenReadAsync());
                using var ms = new System.IO.MemoryStream(); await s.CopyToAsync(ms); bytes = ms.ToArray();
            }
        }
        catch { }
        return Logos[aumid] = bytes;
    }

    // dismiss in Windows too, so the island and Action Center stay in sync
    public static void Remove(uint id) { try { if (id != 0) _listener?.RemoveNotification(id); } catch { } }
    public static void ClearAll() { try { _listener?.ClearNotifications(); } catch { } }

    static void WatchWindows(Action<ToastItem> onToast)
    {
        Task.Run(() =>
        {
            try
            {
                Automation.AddAutomationEventHandler(WindowPattern.WindowOpenedEvent, AutomationElement.RootElement,
                    TreeScope.Children, (s, _) =>
                    {
                        try
                        {
                            if (s is not AutomationElement el) return;
                            var c = el.Current;
                            if (c.ClassName != "Windows.UI.Core.CoreWindow" ||
                                !c.Name.Contains("notification", StringComparison.OrdinalIgnoreCase)) return;
                            Thread.Sleep(150); // let the toast finish rendering its text
                            var texts = el.FindAll(TreeScope.Descendants,
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                                .Cast<AutomationElement>().Select(t => t.Current.Name)
                                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
                            if (texts.Count == 0) return;
                            // typical order: app name, title, body
                            string app = texts.Count >= 3 ? texts[0] : "Notification";
                            string title = texts.Count >= 3 ? texts[1] : texts[0];
                            string body = texts.Count >= 3 ? string.Join("  ", texts.Skip(2)) : texts.ElementAtOrDefault(1) ?? "";
                            onToast(new ToastItem(0, app, "", title, body, DateTime.Now, null, true));
                        }
                        catch { }
                    });
            }
            catch { }
        });
    }
}
