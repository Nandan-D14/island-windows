using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island;

// Phone remote over home Wi-Fi.
//  - a tiny HTTP server, only answers devices on the local network
//  - pairing = scanning a QR code that carries a random secret; the secret never travels over the network again:
//    every request is signed (HMAC-SHA256 of time + path + body), old/replayed requests are rejected
//  - everything the phone triggers goes through the same Gate() permissions as Gemini on the PC
public partial class MainWindow
{
    TcpListener? _remote;
    CancellationTokenSource? _remoteCts;
    int _remotePort;
    DateTime _phoneSeen;
    readonly List<(string who, string text)> _phoneChat = [];
    readonly JsonArray _phoneHist = [];
    bool _localGate; // local quick commands from the phone still obey permissions

    // ================= Island UI (Controls › Phone) =================
    void InitRemote()
    {
        if (_ai.RemoteOn) StartRemote();
        RenderRemote();
    }

    void OnPhoneToggle(object s, RoutedEventArgs e)
    {
        _ai.RemoteOn = !_ai.RemoteOn; _ai.Save();
        if (_ai.RemoteOn) StartRemote(); else StopRemote();
        RenderRemote();
    }

    void OnPhoneUnpair(object s, RoutedEventArgs e)
    {
        _ai.RemoteToken = NewToken(); _ai.Save();   // old phones stop working immediately
        RenderRemote();
        ShowToast("Phone unpaired \u00B7 scan the new QR", Colors.White);
    }

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    string RemoteUrl() => $"http://{LocalIp()}:{_remotePort}/";

    void RenderRemote()
    {
        bool on = _remote != null;
        PhoneOnBtn.IsChecked = on;
        PhoneQr.Source = on ? Qr(RemoteUrl() + "#" + _ai.RemoteToken) : null;
        PhoneQrOff.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PhoneQrBox.Background = on ? Brushes.White : (Brush)Resources["Tile"];
        PhoneQrOff.Foreground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        PhoneUnpairBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PhoneInfo.Text = on
            ? "1. Phone on the same Wi-Fi   2. Scan with the camera   3. Add to Home Screen.\nIf Windows asks, allow on Private networks. Anyone who scans this can control the PC — keep it to yourself."
            : _ai.RemoteOn ? "Couldn't start (port busy?)" : "Control music, volume, Gemini and routines from your phone on the same Wi-Fi.";
        UpdatePhoneStatus();
    }

    void UpdatePhoneStatus()
    {
        if (_remote == null) { PhoneAddr.Text = ""; return; }
        var ago = DateTime.Now - _phoneSeen;
        PhoneAddr.Text = RemoteUrl() + (_phoneSeen == default ? "  \u00B7  no phone yet" : ago.TotalSeconds < 15 ? "  \u00B7  phone connected" : $"  \u00B7  last seen {(ago.TotalMinutes < 1 ? $"{ago.Seconds}s" : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes}m" : $"{(int)ago.TotalHours}h")} ago");
    }

    static ImageSource Qr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        var m = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M).ModuleMatrix;
        int n = m.Count, scale = 4, size = n * scale;
        var px = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = m[y / scale][x / scale] ? (byte)0 : (byte)255;
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, px, size);
        bmp.Freeze();
        return bmp;
    }

    static string LocalIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1))
        {
            var p = ni.GetIPProperties();
            if (!p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
            var ip = p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (ip != null && IsLan(ip)) return ip.ToString();
        }
        return "127.0.0.1";
    }

    static bool IsLan(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    // ================= Server =================
    void StartRemote()
    {
        if (_remote != null) return;
        if (string.IsNullOrEmpty(_ai.RemoteToken)) { _ai.RemoteToken = NewToken(); _ai.Save(); }
        for (int port = 8765; port < 8775 && _remote == null; port++)
        {
            try { var l = new TcpListener(IPAddress.Any, port); l.Start(); _remote = l; _remotePort = port; } catch { }
        }
        if (_remote == null) return;
        _remoteCts = new CancellationTokenSource();
        var (listener, ct) = (_remote, _remoteCts.Token);
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(ct); } catch { break; }
                _ = Task.Run(() => Handle(c));
            }
        });
    }

    void StopRemote()
    {
        _remoteCts?.Cancel();
        try { _remote?.Stop(); } catch { }
        _remote = null;
    }

    readonly Dictionary<string, DateTime> _seenSigs = [];
    readonly Dictionary<IPAddress, (int n, DateTime since)> _fails = [];

    async Task Handle(TcpClient c)
    {
        using (c)
        {
            try
            {
                var ep = (IPEndPoint)c.Client.RemoteEndPoint!;
                if (!IsLan(ep.Address)) return; // never answer the internet
                c.ReceiveTimeout = c.SendTimeout = 10000;
                var s = c.GetStream();
                var req = await ReadRequest(s);
                if (req == null) return;
                var (method, path, headers, body) = req.Value;

                // public: the page itself and its icon (no data in them)
                if (method == "GET" && (path == "/" || path.StartsWith("/?"))) { await Respond(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage)); return; }
                if (method == "GET" && path == "/manifest.json") { await Respond(s, 200, "application/manifest+json", Encoding.UTF8.GetBytes(Manifest)); return; }
                if (method == "GET" && path == "/icon.png") { await Respond(s, 200, "image/png", Icon()); return; }

                if (!path.StartsWith("/api/")) { await Respond(s, 404, "text/plain", "not found"u8.ToArray()); return; }
                if (!Authorized(ep.Address, method, path, headers, body)) { await Respond(s, 401, "application/json", """{"error":"not paired"}"""u8.ToArray()); return; }
                _phoneSeen = DateTime.Now;

                var json = body.Length > 0 ? JsonNode.Parse(body)?.AsObject() ?? [] : [];
                var result = await Dispatcher.InvokeAsync(() => Api(path, json)).Task.Unwrap();
                await Respond(s, 200, "application/json", Encoding.UTF8.GetBytes(result.ToJsonString()));
            }
            catch { }
        }
    }

    bool Authorized(IPAddress ip, string method, string path, Dictionary<string, string> h, string body)
    {
        lock (_fails)
        {
            if (_fails.TryGetValue(ip, out var f) && f.n >= 10 && (DateTime.Now - f.since).TotalMinutes < 5) return false; // brute-force lockout
        }
        bool ok = false;
        if (h.TryGetValue("x-ts", out var ts) && h.TryGetValue("x-sig", out var sig) && long.TryParse(ts, out var t)
            && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t) < 300_000)
        {
            var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_ai.RemoteToken), Encoding.UTF8.GetBytes($"{ts}\n{method} {path}\n{body}"));
            var expect = Convert.ToHexString(mac).ToLowerInvariant();
            ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expect), Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));
            if (ok)
            {
                lock (_seenSigs)
                {
                    foreach (var k in _seenSigs.Where(kv => (DateTime.Now - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList()) _seenSigs.Remove(k);
                    if (!_seenSigs.TryAdd(sig, DateTime.Now)) ok = false; // replayed request
                }
            }
        }
        lock (_fails)
        {
            if (ok) _fails.Remove(ip);
            else _fails[ip] = _fails.TryGetValue(ip, out var f) && (DateTime.Now - f.since).TotalMinutes < 5 ? (f.n + 1, f.since) : (1, DateTime.Now);
        }
        return ok;
    }

    static async Task<(string method, string path, Dictionary<string, string> headers, string body)?> ReadRequest(NetworkStream s)
    {
        var buf = new byte[16384];
        int len = 0, end = -1;
        while (end < 0)
        {
            if (len == buf.Length) return null; // headers too big
            int n = await s.ReadAsync(buf.AsMemory(len));
            if (n == 0) return null;
            len += n;
            for (int i = Math.Max(0, len - n - 3); i + 3 < len; i++)
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') { end = i; break; }
        }
        var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1)) { int i = line.IndexOf(':'); if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim(); }
        int clen = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
        if (clen > 65536) return null;
        var body = new byte[clen];
        int have = Math.Min(clen, len - end - 4);
        Array.Copy(buf, end + 4, body, 0, have);
        while (have < clen) { int n = await s.ReadAsync(body.AsMemory(have)); if (n == 0) return null; have += n; }
        return (first[0], first[1], headers, Encoding.UTF8.GetString(body));
    }

    static async Task Respond(NetworkStream s, int code, string type, byte[] body)
    {
        var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 401 ? "Unauthorized" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
    }

    static byte[]? _icon;
    static byte[] Icon()
    {
        if (_icon != null) return _icon;
        try
        {
            using var st = Application.GetResourceStream(new Uri("pack://application:,,,/logo.png")).Stream;
            using var ms = new MemoryStream(); st.CopyTo(ms); return _icon = ms.ToArray();
        }
        catch { return _icon = []; }
    }

    // ================= API (runs on the UI thread) =================
    // buttons on the phone may only call these
    static readonly HashSet<string> PhoneButtons = ["play_pause", "next_track", "previous_track", "set_volume", "mute", "set_brightness",
        "run_routine", "lock_pc", "minimize_all", "set_timer", "stop_timer", "start_focus", "stop_focus", "set_dnd"];

    async Task<JsonObject> Api(string path, JsonObject j)
    {
        switch (path)
        {
            case "/api/state": return PhoneState();
            case "/api/do":
                {
                    var tool = j["tool"]?.GetValue<string>() ?? "";
                    if (!PhoneButtons.Contains(tool)) return new() { ["error"] = "not allowed" };
                    var r = await Gate(tool, j["args"]?.AsObject().DeepClone().AsObject() ?? [], fromRoutine: true, origin: "phone");
                    return new() { ["result"] = r, ["state"] = PhoneState() };
                }
            case "/api/chat":
                {
                    var q = (j["text"]?.GetValue<string>() ?? "").Trim();
                    if (q == "") return PhoneState();
                    await PhoneChat(q.Length > 2000 ? q[..2000] : q);
                    return PhoneState();
                }
            case "/api/answer":
                {
                    // terminal commands can only be approved at the PC
                    if (_askOn && _askCat != "terminal" && j["id"]?.GetValue<int>() == _askId) AnswerAsk(j["ok"]?.GetValue<bool>() == true);
                    return PhoneState();
                }
            case "/api/voice": _ = ToggleVoice(); return PhoneState();
        }
        return new() { ["error"] = "unknown" };
    }

    async Task PhoneChat(string q)
    {
        _phoneChat.Add(("You", q));
        string answer;
        string? local;
        _localGate = true;
        try { local = await TryLocal(q); } finally { _localGate = false; }
        if (local != null) answer = local;
        else if (!_ai.AiEnabled) answer = "Gemini is switched off on the PC.";
        else if (_ai.Key == "") answer = "Add a Gemini key on the PC first.";
        else
        {
            try
            {
                _phoneHist.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = q }) });
                answer = await Gemini.Ask(_ai.Key, _ai.TextModel, SystemPrompt() + " The user is typing on their phone as a remote and may not be looking at the PC.",
                    _phoneHist, Tools, (n, a) => Dispatcher.InvokeAsync(() => Gate(n, a, origin: "phone")).Task.Unwrap());
                while (_phoneHist.Count > 24) _phoneHist.RemoveAt(0);
                if (_phoneHist.Count > 0 && _phoneHist[0]?["role"]?.GetValue<string>() != "user") _phoneHist.RemoveAt(0);
            }
            catch (Exception ex) { answer = "Error: " + ex.Message; _phoneHist.Clear(); }
        }
        _phoneChat.Add(("Gemini", answer));
        while (_phoneChat.Count > 30) _phoneChat.RemoveAt(0);
    }

    JsonObject PhoneState()
    {
        var a = Audio.Get();
        Stats.GetSystemPowerStatus(out var p);
        var t = TimerValue();
        bool timerLive = _tRunning || (!_stopwatch && _tRemaining > TimeSpan.Zero);
        return new JsonObject
        {
            ["pc"] = Environment.MachineName,
            ["hasMedia"] = _hasMedia, ["title"] = _hasMedia ? TitleText.Text : "", ["artist"] = _hasMedia ? ArtistText.Text : "", ["playing"] = _playing,
            ["vol"] = a?.vol ?? 0, ["mute"] = a?.mute ?? false,
            ["bright"] = Brightness.Supported ? Brightness.Get() ?? -1 : -1,
            ["battery"] = p.Percent <= 100 ? p.Percent : -1, ["charging"] = p.AC == 1,
            ["timer"] = timerLive ? (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss")) : "", ["focus"] = _focus,
            ["voice"] = _live != null, ["aiOn"] = _ai.AiEnabled, ["dnd"] = DndActive,
            ["ask"] = _askOn ? new JsonObject { ["id"] = _askId, ["head"] = AskHead.Text, ["what"] = AskTitle.Text, ["why"] = AskWhy.Text } : null,
            ["routines"] = new JsonArray(_routines.Select(r => (JsonNode)r.Name).ToArray()),
            ["chat"] = new JsonArray(_phoneChat.Select(c => (JsonNode)new JsonObject { ["who"] = c.who, ["text"] = c.text }).ToArray()),
            ["notifs"] = new JsonArray(_notifs.Take(15).Select(n => (JsonNode)new JsonObject { ["app"] = n.App, ["title"] = n.Title, ["body"] = n.Body, ["ago"] = Ago(n.At), ["unread"] = !n.Read }).ToArray()),
        };
    }

    const string Manifest = """{"name":"Island Remote","short_name":"Island","start_url":"/","display":"standalone","background_color":"#0b0b0f","theme_color":"#0b0b0f","icons":[{"src":"/icon.png","sizes":"256x256","type":"image/png"}]}""";

    // ================= The phone page =================
    // plain HTML + JS (no CDN). Text from the PC is always inserted with textContent (never as HTML).
    // crypto.subtle isn't available on plain http, so SHA-256/HMAC is implemented here.
    const string PhonePage = """
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no">
<meta name="theme-color" content="#0b0b0f"><meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent"><meta name="referrer" content="no-referrer">
<link rel="manifest" href="/manifest.json"><link rel="apple-touch-icon" href="/icon.png"><title>Island</title>
<style>
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;background:#0b0b0f;color:#fff;font:15px -apple-system,Segoe UI,Roboto,sans-serif;padding:calc(14px + env(safe-area-inset-top)) 14px calc(14px + env(safe-area-inset-bottom))}
.card{background:#1a1a20;border-radius:22px;padding:16px;margin-bottom:12px}
.dim{color:#8c8c95;font-size:13px}.row{display:flex;align-items:center;gap:10px}
h1{font-size:20px;margin:0}.dot{width:8px;height:8px;border-radius:4px;background:#30d158;display:inline-block}
.title{font-weight:600;font-size:17px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ctl{display:flex;justify-content:center;gap:26px;margin-top:12px}
button{background:#2a2a32;color:#fff;border:0;border-radius:16px;font:inherit;padding:10px 14px}
button:active{background:#3a3a44}.big{width:58px;height:58px;border-radius:29px;font-size:22px}
.chips{display:flex;flex-wrap:wrap;gap:8px}.chips button{padding:9px 13px;font-size:14px}
input[type=range]{width:100%;accent-color:#fff;height:28px}
.sl{display:grid;grid-template-columns:28px 1fr 34px;align-items:center;gap:8px;margin:4px 0}
#chat{max-height:38vh;overflow:auto;margin-bottom:10px}.msg{margin:6px 0;line-height:1.35}.who{color:#8c8c95;font-size:12px;margin-right:6px}
.inrow{display:flex;gap:8px}.inrow input{flex:1;background:#2a2a32;color:#fff;border:0;border-radius:16px;padding:12px;font:inherit}
#ask{position:fixed;inset:0;background:#000a;display:none;align-items:flex-end;padding:16px}
#ask .card{width:100%;border:1px solid #ff9f0a55}#ask .row{justify-content:flex-end;margin-top:14px}
.ok{background:#30d158;color:#000;font-weight:600}.no{background:#3a3a44}
#unpaired{display:none;text-align:center;padding-top:30vh}
</style></head><body>
<div id="unpaired"><h1>Not paired</h1><p class="dim">On your PC open the island &rsaquo; Controls &rsaquo; Phone, and scan the QR code with this phone's camera.</p></div>
<div id="app">
  <div class="row" style="margin-bottom:12px"><img src="/icon.png" width="30" height="30" style="border-radius:8px"><h1 id="pc">Island</h1><span class="dot" id="live"></span><span class="dim" id="bat" style="margin-left:auto"></span></div>
  <div class="card" id="media"><div class="dim" id="mstate">Nothing playing</div><div class="title" id="title"></div><div class="dim" id="artist"></div>
    <div class="ctl"><button class="big" data-do="previous_track">&#9198;</button><button class="big" id="pp" data-do="play_pause">&#9199;</button><button class="big" data-do="next_track">&#9197;</button></div></div>
  <div class="card">
    <div class="sl"><span id="mute" style="cursor:pointer">&#128266;</span><input type="range" id="vol" min="0" max="100"><span class="dim" id="volv"></span></div>
    <div class="sl" id="brow"><span>&#9728;&#65039;</span><input type="range" id="bright" min="0" max="100"><span class="dim" id="brightv"></span></div>
  </div>
  <div class="card"><div class="chips">
    <button id="voice">&#127908; Talk on PC</button>
    <button id="dnd">&#127769; Do Not Disturb</button>
    <button data-do="start_focus" data-args='{"minutes":25}'>&#9678; Focus 25</button>
    <button data-do="set_timer" data-args='{"minutes":5}'>&#9201; 5 min</button>
    <button data-do="stop_timer">Stop timer</button>
    <button data-do="minimize_all">Show desktop</button>
    <button data-do="lock_pc">&#128274; Lock PC</button>
  </div><div class="dim" id="timer" style="margin-top:10px"></div><div class="chips" id="routines" style="margin-top:10px"></div></div>
  <div class="card"><div class="dim" style="margin-bottom:6px">Notifications</div><div id="notifs"><div class="dim">None</div></div></div>
  <div class="card"><div class="dim" style="margin-bottom:6px">Gemini</div><div id="chat"></div>
    <div class="inrow"><input id="q" placeholder="Ask or tell the PC to do something" enterkeyhint="send"><button id="send">Send</button></div></div>
</div>
<div id="ask"><div class="card"><div class="dim" id="askhead">GEMINI WANTS TO</div><div class="title" id="askwhat" style="white-space:normal"></div><div class="dim" id="askwhy"></div>
  <div class="row"><button class="no" id="deny">Deny</button><button class="ok" id="allow">Allow</button></div></div></div>
<script>
"use strict";
// ---- SHA-256 / HMAC (crypto.subtle needs https, which a home PC doesn't have) ----
const K=[],H0=[];(()=>{let n=2,c=0;const f=x=>(x-Math.floor(x))*4294967296|0;while(c<64){let p=true;for(let d=2;d*d<=n;d++)if(n%d==0){p=false;break}if(p){if(c<8)H0[c]=f(Math.pow(n,1/2));K[c]=f(Math.pow(n,1/3));c++}n++}})();
function sha256(m){const l=m.length,bl=((l+9+63)>>6)<<6,b=new Uint8Array(bl);b.set(m);b[l]=0x80;const v=new DataView(b.buffer);v.setUint32(bl-4,(l*8)>>>0);v.setUint32(bl-8,Math.floor(l*8/4294967296));
const w=new Int32Array(64);let h=H0.slice();for(let i=0;i<bl;i+=64){for(let t=0;t<16;t++)w[t]=v.getInt32(i+t*4);
for(let t=16;t<64;t++){const a=w[t-15],d=w[t-2];const s0=(a>>>7|a<<25)^(a>>>18|a<<14)^(a>>>3),s1=(d>>>17|d<<15)^(d>>>19|d<<13)^(d>>>10);w[t]=(w[t-16]+s0+w[t-7]+s1)|0}
let[A,B,C,D,E,F,G,I]=h;for(let t=0;t<64;t++){const S1=(E>>>6|E<<26)^(E>>>11|E<<21)^(E>>>25|E<<7),ch=(E&F)^(~E&G),t1=(I+S1+ch+K[t]+w[t])|0,S0=(A>>>2|A<<30)^(A>>>13|A<<19)^(A>>>22|A<<10),mj=(A&B)^(A&C)^(B&C),t2=(S0+mj)|0;I=G;G=F;F=E;E=(D+t1)|0;D=C;C=B;B=A;A=(t1+t2)|0}
h=[h[0]+A|0,h[1]+B|0,h[2]+C|0,h[3]+D|0,h[4]+E|0,h[5]+F|0,h[6]+G|0,h[7]+I|0]}const o=new Uint8Array(32),ov=new DataView(o.buffer);h.forEach((x,i)=>ov.setUint32(i*4,x>>>0));return o}
const enc=s=>new TextEncoder().encode(s);
function hmac(key,msg){let k=enc(key);if(k.length>64)k=sha256(k);const kp=new Uint8Array(64);kp.set(k);const ip=new Uint8Array(64+msg.length),op=new Uint8Array(96);
for(let i=0;i<64;i++){ip[i]=kp[i]^0x36;op[i]=kp[i]^0x5c}ip.set(msg,64);op.set(sha256(ip),64);return[...sha256(op)].map(x=>x.toString(16).padStart(2,"0")).join("")}
// ---- pairing: the secret arrives in the QR (#...) and stays on this phone ----
if(location.hash.length>10){localStorage.setItem("island",location.hash.slice(1));history.replaceState(null,"","/")}
const token=localStorage.getItem("island");
const $=id=>document.getElementById(id);
function unpaired(){$("app").style.display="none";$("ask").style.display="none";$("unpaired").style.display="block"}
async function api(path,body){if(!token){unpaired();throw 0}const b=body?JSON.stringify(body):"",ts=String(Date.now()),method=body?"POST":"GET";
const r=await fetch(path,{method,headers:{"x-ts":ts,"x-sig":hmac(token,enc(ts+"\n"+method+" "+path+"\n"+b)),"content-type":"application/json"},body:body?b:undefined,cache:"no-store"});
if(r.status==401){unpaired();throw 0}return r.json()}
let busy=false,lastAsk=0,dragging=false;
function render(s){if(!s||s.error)return;if(s.state)s=s.state;$("pc").textContent=s.pc;$("live").style.background=s.aiOn?"#30d158":"#ff6b5e";
$("bat").textContent=s.battery>=0?(s.charging?"\u26A1 ":"")+s.battery+"%":"";
$("mstate").textContent=s.hasMedia?(s.playing?"Playing":"Paused"):"Nothing playing";$("title").textContent=s.title;$("artist").textContent=s.artist;$("pp").innerHTML=s.playing?"&#9208;":"&#9654;";
if(!dragging){$("vol").value=s.vol;$("bright").value=s.bright}$("volv").textContent=s.mute?"off":s.vol;$("mute").innerHTML=s.mute?"&#128263;":"&#128266;";
$("brow").style.display=s.bright>=0?"":"none";$("brightv").textContent=s.bright;
$("timer").textContent=s.timer?(s.focus?"\u25CE Focus ":"\u23F2 Timer ")+s.timer:"";$("voice").textContent=s.voice?"\u25A0 Stop voice":"\uD83C\uDFA4 Talk on PC";$("dnd").textContent=s.dnd?"\uD83C\uDF19 DND on \u00B7 tap to end":"\uD83C\uDF19 Do Not Disturb";$("dnd").onclick=()=>act("set_dnd",{on:!s.dnd,minutes:0});
const rt=$("routines");rt.replaceChildren(...s.routines.map(n=>{const b=document.createElement("button");b.textContent="\u25B6 "+n;b.onclick=()=>act("run_routine",{name:n});return b}));
const ch=$("chat"),atEnd=ch.scrollTop+ch.clientHeight>=ch.scrollHeight-20;if(!busy){ch.replaceChildren(...s.chat.map(m=>{const d=document.createElement("div");d.className="msg";const w=document.createElement("span");w.className="who";w.textContent=m.who;d.append(w,document.createTextNode(m.text));return d}));if(atEnd)ch.scrollTop=ch.scrollHeight}
const nf=$("notifs");nf.replaceChildren(...(s.notifs.length?s.notifs:[null]).map(n=>{const d=document.createElement("div");if(!n){d.className="dim";d.textContent="None";return d}d.className="msg";
const h=document.createElement("div");h.className="dim";h.textContent=n.app+" \u00B7 "+n.ago;const t=document.createElement("div");t.textContent=n.title;if(n.unread)t.style.fontWeight="600";d.append(h,t);
if(n.body){const b=document.createElement("div");b.className="dim";b.textContent=n.body;d.append(b)}return d}));
if(s.ask){$("askhead").textContent=s.ask.head;$("askwhat").textContent=s.ask.what;$("askwhy").textContent=s.ask.why;$("ask").style.display="flex";if(lastAsk!=s.ask.id){lastAsk=s.ask.id;navigator.vibrate&&navigator.vibrate(200)}}else $("ask").style.display="none"}
async function refresh(){if(document.hidden)return;try{render(await api("/api/state"))}catch(e){}}
async function act(tool,args){try{render(await api("/api/do",{tool,args:args||{}}))}catch(e){}}
document.querySelectorAll("[data-do]").forEach(b=>b.onclick=()=>act(b.dataset.do,b.dataset.args?JSON.parse(b.dataset.args):{}));
let vt;function slider(id,tool){const e=$(id);e.oninput=()=>{dragging=true;clearTimeout(vt);vt=setTimeout(()=>act(tool,{level:+e.value}),150)};e.onchange=()=>{dragging=false}}
slider("vol","set_volume");slider("bright","set_brightness");
$("mute").onclick=()=>act("mute",{on:$("volv").textContent!="off"});
$("voice").onclick=async()=>{try{render(await api("/api/voice",{}))}catch(e){}};
async function send(){const q=$("q").value.trim();if(!q)return;$("q").value="";busy=true;const ch=$("chat"),d=document.createElement("div");d.className="msg";d.textContent="\u2026";ch.append(d);ch.scrollTop=ch.scrollHeight;
try{const s=await api("/api/chat",{text:q});busy=false;render(s)}catch(e){busy=false}}
$("send").onclick=send;$("q").onkeydown=e=>{if(e.key=="Enter")send()};
$("allow").onclick=async()=>{try{render(await api("/api/answer",{id:lastAsk,ok:true}))}catch(e){}};
$("deny").onclick=async()=>{try{render(await api("/api/answer",{id:lastAsk,ok:false}))}catch(e){}};
if(!token)unpaired();else{refresh();setInterval(refresh,1500);document.addEventListener("visibilitychange",refresh)}
</script></body></html>
""";
}
