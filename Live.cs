using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace Island;

// Live activities in the pill: downloads, synced lyrics, device popups, privacy dots, rim flash
public partial class MainWindow
{
    string? _dlLine, _lyricLine;
    string? _liveLine => _dlLine ?? _lyricLine;

    void SetLiveLine(string? dl, string? lyric)
    {
        bool before = _liveLine != null;
        _dlLine = dl; _lyricLine = lyric;
        if (before != (_liveLine != null)) { _view = (View)(-1); Refresh(); }   // pill changes width
        else if (_liveLine != null && LiveText.Visibility == Visibility.Visible) LiveText.Text = _liveLine;
    }

    void StartLive()
    {
        StartDownloads();
        StartDevices();
        _lyricTimer.Tick += (_, _) => TickLyrics();
        ShowLyricsState();
    }

    // called from the 1-second tick
    void LiveTick()
    {
        PollDownloads();
        if (_ticks % 2 == 0) UpdatePrivacyDots();
    }

    // ================= Downloads (Chrome/Edge .crdownload, Firefox .part) =================
    string _dlDir = "";
    FileSystemWatcher? _dlWatch;
    DateTime _dlSeen;
    readonly Dictionary<string, (long len, DateTime changed, double speed)> _dl = new(StringComparer.OrdinalIgnoreCase);

    static bool IsPartial(string p) =>
        p.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    void StartDownloads()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            { _dlDir = System.Runtime.InteropServices.Marshal.PtrToStringUni(p) ?? ""; System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); }
            if (!Directory.Exists(_dlDir)) _dlDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(_dlDir)) return;
            _dlWatch = new FileSystemWatcher(_dlDir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            _dlWatch.Created += (_, e) => { if (IsPartial(e.FullPath)) _dlSeen = DateTime.Now; };
            _dlWatch.Changed += (_, e) => { if (IsPartial(e.FullPath)) _dlSeen = DateTime.Now; };
            _dlWatch.Renamed += (_, e) =>
            {
                if (IsPartial(e.FullPath)) { _dlSeen = DateTime.Now; return; }
                if (!IsPartial(e.OldFullPath)) return;
                var name = e.Name ?? "";
                Dispatcher.BeginInvoke(() => { _dl.Remove(e.OldFullPath); PollDownloads(); ShowToast("\u2193 Downloaded  " + name, Color.FromRgb(0x30, 0xD1, 0x58)); });
            };
        }
        catch { }
    }

    void PollDownloads()
    {
        if (_dlDir == "" || (DateTime.Now - _dlSeen).TotalSeconds > 30) { if (_dlLine != null) { _dl.Clear(); SetLiveLine(null, _lyricLine); } return; }
        var now = DateTime.Now;
        var live = new List<(string path, long len, double speed)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dlDir).Where(IsPartial))
            {
                long len; try { len = new FileInfo(f).Length; } catch { continue; }
                if (!_dl.TryGetValue(f, out var prev)) { _dl[f] = (len, now, 0); continue; }
                if (len != prev.len)
                {
                    double sec = Math.Max(0.5, (now - prev.changed).TotalSeconds);
                    double sp = (len - prev.len) / sec;
                    _dl[f] = (len, now, prev.speed == 0 ? sp : prev.speed * 0.6 + sp * 0.4);
                }
                var cur = _dl[f];
                if ((now - cur.changed).TotalSeconds < 20) live.Add((f, cur.len, cur.speed)); // stalled/paused ones drop out
            }
            foreach (var k in _dl.Keys.Where(k => !File.Exists(k)).ToList()) _dl.Remove(k);
        }
        catch { }
        if (live.Count == 0) { SetLiveLine(null, _lyricLine); return; }
        var top = live.OrderByDescending(x => x.speed).First();
        var name = System.IO.Path.GetFileNameWithoutExtension(top.path);
        if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)) name = "Downloading";
        var more = live.Count > 1 ? $"  +{live.Count - 1}" : "";
        SetLiveLine($"\u2193 {name}  {Size(top.len)}  \u00B7  {Size((long)top.speed)}/s{more}", _lyricLine);
    }

    static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{b / 1024.0:0} KB";

    // ================= Synced lyrics (lrclib.net, free, no key) =================
    readonly DispatcherTimer _lyricTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    List<(double t, string line)>? _lyrics;
    string _lyricKey = "";
    static readonly HttpClient LyricHttp = MakeLyricHttp();

    static HttpClient MakeLyricHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Island/1.0 (dynamic island for Windows)");
        return h;
    }

    void ShowLyricsState()
    {
        LyricsBtn.Foreground = _ai.Lyrics ? Brushes.White : (Brush)Resources["Dim"];
        LyricsBtn.Opacity = _ai.Lyrics ? 1 : 0.6;
    }

    void OnLyricsToggle(object s, RoutedEventArgs e)
    {
        _ai.Lyrics = !_ai.Lyrics; try { _ai.Save(); } catch { }
        ShowLyricsState();
        _lyricKey = "";
        if (_ai.Lyrics) _ = LoadLyrics(); else UpdateLyricState();
    }

    async Task LoadLyrics()
    {
        string title = _hasMedia ? TitleText.Text : "", artist = _hasMedia ? ArtistText.Text : "";
        var key = title + "\u0001" + artist;
        if (key == _lyricKey) { UpdateLyricState(); return; }
        _lyricKey = key; _lyrics = null; UpdateLyricState();
        if (!_ai.Lyrics || title == "" || artist == "") return;
        try
        {
            // strip "(Official Video)" etc. that browsers add
            var clean = Regex.Replace(title, @"\s*[\(\[][^\)\]]*(official|video|audio|lyric|visualizer|remaster)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase).Trim();
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(clean)}&artist_name={Uri.EscapeDataString(artist.Replace(" - Topic", ""))}";
            var arr = JsonNode.Parse(await LyricHttp.GetStringAsync(url))?.AsArray();
            double dur = _timeline?.EndTime.TotalSeconds ?? 0;
            var best = arr?.Where(x => !string.IsNullOrEmpty(x?["syncedLyrics"]?.GetValue<string>()))
                          .OrderBy(x => dur > 0 ? Math.Abs((x?["duration"]?.GetValue<double>() ?? 0) - dur) : 0).FirstOrDefault();
            if (best == null || key != _lyricKey) return;
            if (dur > 0 && Math.Abs((best["duration"]?.GetValue<double>() ?? dur) - dur) > 8) return; // probably a different song
            var lines = new List<(double, string)>();
            foreach (Match m in Regex.Matches(best["syncedLyrics"]!.GetValue<string>(), @"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", RegexOptions.Multiline))
                lines.Add((int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[3].Value.Trim()));
            _lyrics = lines.Count > 0 ? lines : null;
        }
        catch { }
        UpdateLyricState();
    }

    // run the lyric timer only while a song with lyrics is actually playing
    void UpdateLyricState()
    {
        bool on = _ai.Lyrics && _lyrics != null && _playing && _hasMedia && _timeline != null && _timeline.EndTime > TimeSpan.Zero;
        if (on) { if (!_lyricTimer.IsEnabled) { _lyricTimer.Start(); TickLyrics(); } }
        else { _lyricTimer.Stop(); if (_lyricLine != null) SetLiveLine(_dlLine, null); }
    }

    void TickLyrics()
    {
        if (_lyrics == null || _timeline == null) return;
        var pos = _timeline.Position + (_playing ? DateTimeOffset.Now - _timeline.LastUpdatedTime : TimeSpan.Zero);
        double t = pos.TotalSeconds + 0.25;
        string line = "";
        foreach (var l in _lyrics) { if (l.t > t) break; line = l.line; }
        SetLiveLine(_dlLine, line == "" ? "\u266A" : "\u266A  " + line);
    }

    // ================= Bluetooth device popups =================
    DeviceWatcher? _btWatch;
    readonly Dictionary<string, (string name, bool on)> _bt = [];
    bool _btReady;
    const string ConnProp = "System.Devices.Aep.IsConnected", ContProp = "System.Devices.Aep.ContainerId";

    void StartDevices()
    {
        try
        {
            // paired classic + LE Bluetooth endpoints; we react when IsConnected flips
            const string aqs = "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            _btWatch = DeviceInformation.CreateWatcher(aqs, [ConnProp, ContProp], DeviceInformationKind.AssociationEndpoint);
            _btWatch.Added += (_, d) => Dispatcher.BeginInvoke(() => _bt[d.Id] = (d.Name, d.Properties.TryGetValue(ConnProp, out var c) && c is true));
            _btWatch.Updated += (w, u) => Dispatcher.BeginInvoke(() =>
            {
                if (!_btReady || !_bt.TryGetValue(u.Id, out var cur) || !u.Properties.TryGetValue(ConnProp, out var c) || c is not bool on || on == cur.on) return;
                _bt[u.Id] = (cur.name, on);
                Guid? cont = u.Properties.TryGetValue(ContProp, out var g) && g is Guid gg ? gg : null;
                _ = DevicePopup(cur.name, on, cont);
            });
            _btWatch.EnumerationCompleted += (_, _) => Dispatcher.BeginInvoke(() => _btReady = true);
            _btWatch.Start();
        }
        catch { }
    }

    async Task DevicePopup(string name, bool on, Guid? container)
    {
        var n = name.ToLowerInvariant();
        string icon = Regex.IsMatch(n, "bud|pod|head|ear|wh-|wf-|dope|beats|jbl|boat|nothing ear|sound") ? "\uE7F6"
                    : n.Contains("mouse") ? "\uE962" : n.Contains("keyboard") ? "\uE765"
                    : Regex.IsMatch(n, "phone|galaxy|pixel|redmi|oneplus|vivo|oppo|realme") ? "\uE8EA"
                    : n.Contains("controller") || n.Contains("xbox") ? "\uE7FC" : "\uE702";
        string body = on ? "Connected" : "Disconnected";
        if (on && container != null)
        {
            await Task.Delay(2500); // battery level shows up shortly after connecting
            var pct = await DeviceBattery(container.Value);
            if (pct != null) body += $"  \u00B7  {pct}%";
        }
        ShowNotification("Bluetooth", name, body, icon);
    }

    static async Task<int?> DeviceBattery(Guid container)
    {
        const string bat = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        try
        {
            var devs = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", [bat], DeviceInformationKind.Device);
            foreach (var d in devs)
                if (d.Properties.TryGetValue(bat, out var v) && v is byte b && b <= 100) return b;
        }
        catch { }
        return null;
    }

    // ================= Privacy dots (camera / mic in use by any app) =================
    static readonly string SelfKey = (Environment.ProcessPath ?? "").Replace('\\', '#');

    static bool CapInUse(string cap)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{cap}");
            if (root == null) return false;
            static bool Active(RegistryKey k) => k.GetValue("LastUsedTimeStop") is long stop && stop == 0 && k.GetValue("LastUsedTimeStart") is long start && start != 0;
            foreach (var n in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(n);
                if (k == null) continue;
                if (n == "NonPackaged")
                {
                    foreach (var app in k.GetSubKeyNames())
                    {
                        if (string.Equals(app, SelfKey, StringComparison.OrdinalIgnoreCase)) continue; // the island's own wake-word mic
                        using var a = k.OpenSubKey(app);
                        if (a != null && Active(a)) return true;
                    }
                }
                else if (Active(k)) return true;
            }
        }
        catch { }
        return false;
    }

    void UpdatePrivacyDots()
    {
        bool cam = _cam == null && CapInUse("webcam"), mic = CapInUse("microphone") || _live is { Paused: false }; // orange also while Gemini hears you
        CamDot.Visibility = cam ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        ShareDot.Visibility = _sharing ? Visibility.Visible : Visibility.Collapsed;
        PhoneDot.Visibility = _remote != null && (DateTime.Now - _phoneSeen).TotalSeconds < 15 ? Visibility.Visible : Visibility.Collapsed; // blue = phone remote active
    }

    // ================= Rim flash (charging / low battery) =================
    Brush? _rim;

    void FlashRim(Color c)
    {
        _rim ??= Pill.BorderBrush;
        var b = new SolidColorBrush(c);
        Pill.BorderBrush = b;
        var a = new ColorAnimation(c, Color.FromArgb(0x30, c.R, c.G, c.B), TimeSpan.FromMilliseconds(700))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) };
        a.Completed += (_, _) => Pill.BorderBrush = _rim;
        b.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }
}
