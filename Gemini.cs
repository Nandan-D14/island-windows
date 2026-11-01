using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace Island;

// ---------- Settings (API key encrypted with Windows DPAPI, per-user) ----------
class AiSettings
{
    public string KeyEnc { get; set; } = "";
    public string TextModel { get; set; } = "";
    public string LiveModel { get; set; } = "";
    public bool WakeWord { get; set; } = true;
    public bool Lyrics { get; set; } = true;
    public bool AiEnabled { get; set; } = true;
    public Dictionary<string, string> Perms { get; set; } = [];
    public bool RemoteOn { get; set; }
    public string RemoteTokenEnc { get; set; } = "";
    public bool Brief { get; set; } = true;
    public string LastBrief { get; set; } = "";
    public string Accent { get; set; } = "";
    public string PillStyle { get; set; } = "Glass";
    public string Hotkey { get; set; } = "Alt";
    public int Screen { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public string UpdateRepo { get; set; } = "";
    public string LastUpdateCheck { get; set; } = "";
    public List<string> MutedApps { get; set; } = [];
    public bool Onboarded { get; set; }
    public DateTime? DndUntil { get; set; }          // island Do Not Disturb (DateTime.MaxValue = until turned off)
    public string NotifMode { get; set; } = "Popups"; // Popups · Silent · Off
    public bool HoldWhenBusy { get; set; } = true;     // pop-ups wait while you type, watch full screen or are on a call
    public string VoiceView { get; set; } = "Small";  // Small (dot + caption) · Full (orb panel)
    public string Voice { get; set; } = "";           // Gemini's voice: "" = default, Aoede, Puck, Kore, Charon
    public bool Chimes { get; set; } = true;          // soft sound when voice starts / ends
    public string ChatPopRect { get; set; } = "";     // pop-out chat window: "x,y,w,h"
    public bool ChatPopPin { get; set; } = true;      // pop-out chat stays on top
    public bool ChatPopped { get; set; }              // reopen the pop-out at start
    public bool AlwaysTop { get; set; }               // never tuck away over full-screen apps; stay on top of everything

    // phone pairing secret, encrypted with DPAPI like the API key
    [System.Text.Json.Serialization.JsonIgnore]
    public string RemoteToken
    {
        get
        {
            try { return RemoteTokenEnc == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(RemoteTokenEnc), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set => RemoteTokenEnc = value == "" ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "ai.json");

    // never serialized: only the DPAPI-encrypted KeyEnc is written to disk
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key
    {
        get
        {
            try { return KeyEnc == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(KeyEnc), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set => KeyEnc = value == "" ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public static AiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var raw = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AiSettings>(raw) ?? new();
                // older builds also wrote the key in plain text: move it into the encrypted field and rewrite the file without it
                if (JsonNode.Parse(raw)?["Key"]?.GetValue<string>() is string plain)
                {
                    if (s.KeyEnc == "" && plain != "") s.Key = plain;
                    s.Save();
                }
                return s;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

// ---------- Gemini REST (text + function calling) ----------
static class Gemini
{
    const string Base = "https://generativelanguage.googleapis.com/v1beta";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Gemini often answers 503 "high demand" for a moment – retry once quickly; Generate() then falls back to another model
    static async Task<HttpResponseMessage> SendRetry(HttpRequestMessage req)
    {
        var body = req.Content == null ? null : await req.Content.ReadAsStringAsync();
        for (int i = 0; ; i++)
        {
            var r = new HttpRequestMessage(req.Method, req.RequestUri);
            foreach (var h in req.Headers) r.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (body != null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var res = await Http.SendAsync(r);
            if (i < 1 && (int)res.StatusCode is 500 or 503) { await Task.Delay(1000); continue; }
            return res;
        }
    }

    // ---------- model fallback: free keys have a separate daily quota per model, and new models get overloaded ----------
    static readonly string[] Fallbacks = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-2.5-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash-lite"];
    static readonly Dictionary<string, DateTime> Cooldown = [];
    public static string LastModel = "";

    static async Task<string> Generate(string key, string model, JsonObject payload)
    {
        var chain = new[] { model }.Concat(Fallbacks).Distinct()
            .Where(m => !Cooldown.TryGetValue(m, out var until) || until < DateTime.Now).ToList();
        if (chain.Count == 0) chain = [model];
        int lastCode = 0; string lastErr = "", lastBody = "";
        foreach (var m in chain)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/models/{m}:generateContent")
                { Content = new StringContent(ForModel(payload, m).ToJsonString(), Encoding.UTF8, "application/json") };
                req.Headers.Add("x-goog-api-key", key);
                var res = await SendRetry(req);
                var body = await res.Content.ReadAsStringAsync();
                if (res.IsSuccessStatusCode) { LastModel = m; return body; }
                lastCode = (int)res.StatusCode; lastErr = ErrorOf(body); lastBody = body;
                // earlier turns may carry another model's thought signatures: drop them once and retry
                if (lastCode == 400 && attempt == 0 && body.Contains("ignature")) { StripSignatures(payload); continue; }
                break;
            }
            if (lastCode == 429) Cooldown[m] = DateTime.Now + RetryAfter(lastBody);      // quota used up
            else if (lastCode is 500 or 503) Cooldown[m] = DateTime.Now.AddMinutes(2); // busy
            else if (lastCode == 404) Cooldown[m] = DateTime.Now.AddHours(12);    // model not available for this key
            else throw new Exception(lastErr);                                    // a real error (bad request etc.)
        }
        throw new Exception(lastCode == 429
            ? "Gemini's free limit is used up for now (it resets daily). Add billing to your key for more."
            : lastCode is 500 or 503 ? "Gemini is overloaded right now. Try again in a minute." : lastErr);
    }

    static void StripSignatures(JsonNode? n)
    {
        if (n is JsonObject o) { o.Remove("thoughtSignature"); foreach (var kv in o.ToList()) StripSignatures(kv.Value); }
        else if (n is JsonArray a) foreach (var x in a) StripSignatures(x);
    }

    // less hidden "thinking" = answers start sooner (Gemini 3 uses a level, 2.5 a token budget)
    static JsonObject ForModel(JsonObject payload, string model)
    {
        var p = payload.DeepClone().AsObject();
        JsonObject? thinking = model.StartsWith("gemini-3") && !model.Contains("lite") ? new() { ["thinkingLevel"] = "low" }
                             : model.StartsWith("gemini-2.5-flash") && !model.Contains("lite") ? new() { ["thinkingBudget"] = 512 } : null;
        if (thinking != null)
        {
            var gc = p["generationConfig"] as JsonObject ?? new JsonObject();
            gc["thinkingConfig"] = thinking;
            p["generationConfig"] = gc;
        }
        return p;
    }

    // free keys get "retry in 46.6s" (per-minute limit) or a long delay (daily limit): skip the model exactly that long
    static TimeSpan RetryAfter(string body)
    {
        var m = System.Text.RegularExpressions.Regex.Match(body, @"retry in ([\d.]+)s|""retryDelay"":\s*""(\d+)s""");
        double sec = m.Success && double.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value,
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 60;
        return TimeSpan.FromSeconds(Math.Clamp(sec + 2, 10, 12 * 3600));
    }

    // same as Generate(), but streams: onText gets each piece of the answer as it's written
    static async Task<JsonObject> GenerateStream(string key, string model, JsonObject payload, Action<string> onText)
    {
        var chain = new[] { model }.Concat(Fallbacks).Distinct()
            .Where(m => !Cooldown.TryGetValue(m, out var until) || until < DateTime.Now).ToList();
        if (chain.Count == 0) chain = [model];
        int lastCode = 0; string lastErr = "", lastBody = "";
        foreach (var m in chain)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/models/{m}:streamGenerateContent?alt=sse")
                { Content = new StringContent(ForModel(payload, m).ToJsonString(), Encoding.UTF8, "application/json") };
                req.Headers.Add("x-goog-api-key", key);
                var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    lastCode = (int)res.StatusCode; lastErr = ErrorOf(body); lastBody = body;
                    if (lastCode == 400 && attempt == 0 && body.Contains("ignature")) { StripSignatures(payload); continue; }
                    break;
                }
                LastModel = m;
                var parts = new JsonArray();
                using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
                while (await reader.ReadLineAsync() is string line)
                {
                    if (!line.StartsWith("data:")) continue;
                    foreach (var p in JsonNode.Parse(line[5..])?["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? [])
                    {
                        var po = p!.DeepClone().AsObject();
                        if (po["text"] is JsonValue tv && po["thought"] == null)
                        {
                            var txt = tv.GetValue<string>();
                            // glue text pieces into one part (keeping any thought signature that arrives with them)
                            if (parts.Count > 0 && parts[^1] is JsonObject last && last["text"] != null && last["functionCall"] == null && last["thought"] == null)
                            {
                                last["text"] = last["text"]!.GetValue<string>() + txt;
                                if (po["thoughtSignature"] != null) last["thoughtSignature"] = po["thoughtSignature"]!.DeepClone();
                            }
                            else parts.Add(po);
                            if (txt != "") onText(txt);
                        }
                        else parts.Add(po);
                    }
                }
                return new JsonObject { ["role"] = "model", ["parts"] = parts };
            }
            if (lastCode == 429) Cooldown[m] = DateTime.Now + RetryAfter(lastBody);
            else if (lastCode is 500 or 503) Cooldown[m] = DateTime.Now.AddMinutes(2);
            else if (lastCode == 404) Cooldown[m] = DateTime.Now.AddHours(12);
            else throw new Exception(lastErr);
        }
        throw new Exception(lastCode == 429
            ? "Gemini's free limit is used up for now (it resets daily). Add billing to your key for more."
            : lastCode is 500 or 503 ? "Gemini is overloaded right now. Try again in a minute." : lastErr);
    }

    // chat turn with live text: loops through tool calls like Ask(), streaming every round
    public static async Task<string> AskStream(string key, string model, string system, JsonArray history, JsonArray tools,
                                               Func<string, JsonObject, Task<string>> exec, Action<string> onText)
    {
        for (int round = 0; round < 5; round++)
        {
            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                ["contents"] = history.DeepClone(),
                ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = tools.DeepClone() })
            };
            var content = await GenerateStream(key, model, payload, onText);
            var parts = content["parts"]!.AsArray();
            if (parts.Count == 0) return "(no answer)";
            history.Add(content.DeepClone());
            var calls = parts.Where(p => p?["functionCall"] != null).Select(p => p!["functionCall"]!).ToList();
            if (calls.Count == 0)
                return string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")).Trim();
            var responses = new JsonArray();
            foreach (var c in calls)
            {
                var name = c["name"]!.GetValue<string>();
                var result = await exec(name, c["args"]?.AsObject() ?? []);
                responses.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = result } } });
            }
            history.Add(new JsonObject { ["role"] = "user", ["parts"] = responses });
        }
        return "Done.";
    }
    // pick the newest Flash text model and newest Live (bidi) model the key can use
    public static async Task<(string text, string live)> PickModels(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/models?pageSize=1000");
        req.Headers.Add("x-goog-api-key", key);
        var res = await SendRetry(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception(ErrorOf(body));

        var models = JsonNode.Parse(body)!["models"]!.AsArray()
            .Select(m => (name: m!["name"]!.GetValue<string>().Replace("models/", ""),
                          methods: m["supportedGenerationMethods"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? []))
            .ToList();

        static double Ver(string n)
        {
            var m = System.Text.RegularExpressions.Regex.Match(n, @"gemini-(\d+(?:\.\d+)?)");
            return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
        static double Rank(string n) => Ver(n) * 10 - (n.Contains("preview") || n.Contains("exp") ? 1 : 0);

        string[] skip = ["image", "tts", "live", "audio", "lite", "embedding", "robotics", "computer"];
        var text = models.Where(m => m.methods.Contains("generateContent") && m.name.Contains("flash") && !skip.Any(m.name.Contains))
                         .OrderByDescending(m => Rank(m.name)).Select(m => m.name).FirstOrDefault() ?? "gemini-2.5-flash";
        var live = models.Where(m => m.methods.Contains("bidiGenerateContent"))
                         .OrderByDescending(m => Rank(m.name) + (m.name.Contains("native-audio") ? 0.5 : 0))
                         .Select(m => m.name).FirstOrDefault() ?? "";
        return (text, live);
    }

    // one user turn: loops through function calls until the model returns text
    public static async Task<string> Ask(string key, string model, string system, JsonArray history, JsonArray tools,
                                         Func<string, JsonObject, Task<string>> exec)
    {
        for (int round = 0; round < 5; round++)
        {
            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                ["contents"] = history.DeepClone(),
                ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = tools.DeepClone() })
            };
            var body = await Generate(key, model, payload);

            var content = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?.AsObject();
            if (content == null) return "(no answer)";
            content["role"] = "model";
            history.Add(content.DeepClone()); // keep verbatim (preserves thought signatures)

            var parts = content["parts"]?.AsArray() ?? [];
            var calls = parts.Where(p => p?["functionCall"] != null).Select(p => p!["functionCall"]!).ToList();
            if (calls.Count == 0)
                return string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")).Trim();

            var responses = new JsonArray();
            foreach (var c in calls)
            {
                var name = c["name"]!.GetValue<string>();
                var result = await exec(name, c["args"]?.AsObject() ?? []);
                responses.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = result } } });
            }
            history.Add(new JsonObject { ["role"] = "user", ["parts"] = responses });
        }
        return "Done.";
    }

    // Google Search grounding (can't be mixed with function tools in one request, so it's its own call)
    public static async Task<string> Search(string key, string model, string query)
    {
        var payload = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = query }) }),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() })
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/models/{model}:generateContent")
        { Content = new StringContent(ForModel(payload, model).ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", key);
        var res = await SendRetry(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return await WebResults(query); // grounding quota is small on free keys → free fallback
        var cand = JsonNode.Parse(body)?["candidates"]?[0];
        var text = string.Concat((cand?["content"]?["parts"]?.AsArray() ?? []).Select(p => p?["text"]?.GetValue<string>() ?? "")).Trim();
        var sources = (cand?["groundingMetadata"]?["groundingChunks"]?.AsArray() ?? [])
            .Select(c => c?["web"]?["title"]?.GetValue<string>()).Where(t => t != null).Distinct().Take(3);
        return text.Length == 0 ? "No results." : text + (sources.Any() ? "\nSources: " + string.Join(", ", sources) : "");
    }

    // Bing RSS, then DuckDuckGo Lite – raw snippets the model answers from
    static async Task<string> WebResults(string query)
    {
        var q = Uri.EscapeDataString(query);
        try
        {
            var xml = System.Xml.Linq.XDocument.Parse(await Browser($"https://www.bing.com/search?q={q}&format=rss&setlang=en"));
            var items = xml.Descendants("item").Take(6)
                .Select(i => $"- {(string?)i.Element("title")}: {(string?)i.Element("description")} ({(string?)i.Element("link")})").ToList();
            if (items.Count > 0) return "Web results:\n" + string.Join("\n", items);
        }
        catch { }
        try
        {
            var html = await Browser($"https://lite.duckduckgo.com/lite/?q={q}");
            var snips = System.Text.RegularExpressions.Regex.Matches(html, @"class='result-snippet'>\s*(.*?)\s*</td>", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Take(6).Select(m => "- " + System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "<[^>]+>", ""))).ToList();
            if (snips.Count > 0) return "Web results:\n" + string.Join("\n", snips);
        }
        catch { }
        return "Web search is unavailable right now.";
    }

    static async Task<string> Browser(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130 Safari/537.36");
        var res = await SendRetry(req);
        return await res.Content.ReadAsStringAsync();
    }

    // one-shot image question (text chat "look at my screen")
    public static async Task<string> Vision(string key, string model, byte[] jpg, string question)
    {
        var payload = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpg) } },
                    new JsonObject { ["text"] = "This is the user's screen. A red ring marks their mouse pointer: words like this/here/that refer to what is under or next to it. " + question + " Answer briefly." })
            })
        };
        var body = await Generate(key, model, payload);
        var parts = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? [];
        return string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")).Trim();
    }

    // one-shot text prompt (translate etc.)
    public static async Task<string> Quick(string key, string model, string prompt)
    {
        var payload = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) })
        };
        var body = await Generate(key, model, payload);
        var parts = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? [];
        return string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")).Trim();
    }

    public static string ErrorOf(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; } catch { return body; }
    }
}

// ---------- Gemini Live (real-time voice over WebSocket) ----------
sealed class LiveSession : IDisposable
{
    ClientWebSocket _ws = new();
    readonly SemaphoreSlim _send = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    WaveInEvent? _mic;
    WaveOutEvent? _out;
    BufferedWaveProvider? _buf;

    public event Action<string>? UserText, ModelText, Status;
    public event Action? TurnDone, Closed;
    public event Action<float>? Level, OutLevel;
    public Func<string, JsonObject, Task<string>>? Exec;
    public DateTime LastActivity = DateTime.Now;
    public bool Speaking => _buf != null && _buf.BufferedDuration.TotalMilliseconds > 60;
    bool _drop;        // after an interrupt: discard the rest of the current model reply
    int _loud;         // consecutive mic chunks that sound like the user while the model is speaking
    public bool Paused; // session stays open, mic audio isn't sent
    long _muteUntil;     // ignore the mic briefly (our own start chime)
    public void MuteMic(int ms) => _muteUntil = Environment.TickCount64 + ms;
    public event Action? BargedIn;

    // ---- interruption-aware listening ----
    // While Gemini talks its own voice leaks from the speakers into the mic. Instead of a fixed loudness bar,
    // learn how much echo this PC has (mic level vs. playback level) and the room's noise floor, and only treat
    // sound clearly above both as you talking. Gemini dips its voice at the first sign of you and stops if you
    // keep going; the half second before that is kept so your first word isn't lost.
    static float _echoK = 0.5f;   // mic level per unit of playback level (learned, kept for the app's lifetime)
    static float _noise = 0.01f;  // background level when nobody talks
    float _outPeak;               // recent playback loudness (the speaker plays a little after we read it)
    readonly Queue<byte[]> _preroll = new();
    Meter? _meter;

    // stop Gemini mid-sentence (tap orb, double-tap Alt, or talking over it)
    public void Interrupt()
    {
        _drop = true; _dropAt = DateTime.Now;
        _buf?.ClearBuffer();
        if (_meter != null) _meter.Gain = 1;
    }
    DateTime _dropAt;

    // voice fallbacks if the main Live model is busy / out of quota
    static readonly string[] LiveFallbacks = ["gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-latest"];
    string _key = "", _system = "";
    public string Voice = "";   // prebuilt voice name, "" = model default
    JsonArray _tools = [];
    string? _handle;          // session resumption handle: lets a dropped call continue where it left off
    bool _disposed;
    public string Model { get; private set; } = "";

    public async Task Start(string key, string model, string system, JsonArray tools)
    {
        _key = key; _system = system; _tools = tools;
        string err = "Live session rejected";
        foreach (var m in new[] { model }.Concat(LiveFallbacks).Distinct())
        {
            var (ok, e) = await Connect(m);
            if (ok) { err = ""; break; }
            err = e;
        }
        if (err != "") throw new Exception(err);

        _buf = new BufferedWaveProvider(new WaveFormat(24000, 16, 1), TimeSpan.FromSeconds(90)) { DiscardOnBufferOverflow = true };
        _out = new WaveOutEvent { BufferMilliseconds = 60, NumberOfBuffers = 2 };
        _meter = new Meter(_buf, l => { _outPeak = Math.Max(l, _outPeak * 0.8f); OutLevel?.Invoke(l); });
        _out.Init(_meter); _out.Play();

        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        _mic.DataAvailable += OnMic;
        _mic.StartRecording();

        _ = Task.Run(ReceiveLoop);
    }

    // open a socket + send setup; resumes the previous conversation when we have a handle
    async Task<(bool ok, string err)> Connect(string model)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("x-goog-api-key", _key);
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            to.CancelAfter(TimeSpan.FromSeconds(12));
            await ws.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"), to.Token);
            var old = _ws; _ws = ws;
            try { old.Abort(); old.Dispose(); } catch { }
            await Send(new JsonObject
            {
                ["setup"] = new JsonObject
                {
                    ["model"] = "models/" + model,
                    ["generationConfig"] = Voice == ""
                        ? new JsonObject { ["responseModalities"] = new JsonArray("AUDIO") }
                        : new JsonObject
                        {
                            ["responseModalities"] = new JsonArray("AUDIO"),
                            ["speechConfig"] = new JsonObject { ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = Voice } } }
                        },
                    ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = _system }) },
                    ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = _tools.DeepClone() }),
                    ["inputAudioTranscription"] = new JsonObject(),
                    ["outputAudioTranscription"] = new JsonObject(),
                    ["sessionResumption"] = _handle != null ? new JsonObject { ["handle"] = _handle } : new JsonObject(),
                    // your speech interrupts Gemini; a short pause (or a breath) doesn't end your turn
                    ["realtimeInputConfig"] = new JsonObject
                    {
                        ["automaticActivityDetection"] = new JsonObject { ["prefixPaddingMs"] = 300, ["silenceDurationMs"] = 800 },
                        ["activityHandling"] = "START_OF_ACTIVITY_INTERRUPTS"
                    },
                    ["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() } // long chats don't hit the context limit
                }
            });
            var first = await Receive(to.Token);
            if (first?["setupComplete"] == null) return (false, ws.CloseStatusDescription ?? "Live session rejected");
            Model = model;
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // health check: can voice connect? (setup only — no mic, no audio, closed right away)
    public static async Task<(bool ok, string model, string err)> Probe(string key, string model)
    {
        var s = new LiveSession { _key = key, _system = "test", _tools = [] };
        string mainErr = "";
        try
        {
            foreach (var m in new[] { model }.Concat(LiveFallbacks).Distinct())
            {
                var (ok, err) = await s.Connect(m);
                if (ok) return (true, m, "");
                if (m == model) mainErr = err;
            }
            return (false, "", mainErr == "" ? "rejected" : mainErr);
        }
        finally { s.Dispose(); }
    }

    // the server drops long sessions (and networks blip): reconnect quietly and carry on
    async Task<bool> Reconnect()
    {
        for (int i = 0; i < 3 && !_disposed; i++)
        {
            Status?.Invoke("One sec, reconnecting…");
            await Task.Delay(i == 0 ? 300 : 1500 * i);
            if (_disposed) return false;
            var (ok, _) = await Connect(Model);
            if (!ok && _handle != null) { _handle = null; (ok, _) = await Connect(Model); } // handle expired: fresh session
            if (ok) { _buf?.ClearBuffer(); _drop = false; Status?.Invoke(""); return true; }
        }
        return false;
    }

    void OnMic(object? s, WaveInEventArgs e)
    {
        // RMS level for the orb animation
        double sum = 0; int n = e.BytesRecorded / 2;
        for (int i = 0; i < e.BytesRecorded; i += 2) { short v = BitConverter.ToInt16(e.Buffer, i); sum += v * v; }
        var level = (float)Math.Min(1, Math.Sqrt(sum / Math.Max(1, n)) / 6000);
        Level?.Invoke(level);
        if (Paused || Environment.TickCount64 < _muteUntil) return;

        if (Speaking)
        {
            var chunk = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
            _preroll.Enqueue(chunk);
            while (_preroll.Count > 6) _preroll.Dequeue(); // last ~600 ms
            float echo = _echoK * _outPeak;
            float bar = Math.Max(_noise * 4 + 0.03f, echo * 2.2f + 0.04f);
            if (level <= bar)
            {
                // only Gemini (and the room): learn the echo, let its voice come back up
                if (_outPeak > 0.05f)
                {
                    float r = Math.Clamp(level / _outPeak, 0.02f, 3f);
                    _echoK += (r - _echoK) * (r > _echoK ? 0.05f : 0.02f);
                }
                _loud = Math.Max(0, _loud - 1);
                if (_loud == 0 && _meter != null) _meter.Gain = 1;
                return;
            }
            _loud++;
            if (_meter != null) _meter.Gain = 0.3f; // first sign of you: Gemini gets quieter right away
            if (_loud < 3) return;                  // ~300 ms of you talking = a real interruption
            _loud = 0;
            Interrupt();
            BargedIn?.Invoke();
            // send what you said while it was talking, so the server hears the start of your sentence
            while (_preroll.Count > 0)
            {
                var pre = _preroll.Dequeue();
                _ = Send(new JsonObject { ["realtimeInput"] = new JsonObject { ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pre), ["mimeType"] = "audio/pcm;rate=16000" } } });
            }
            return;
        }
        _preroll.Clear();
        _loud = 0;
        // room noise floor: falls fast, rises slowly (speech doesn't drag it up)
        _noise += (level - _noise) * (level < _noise ? 0.1f : 0.005f);
        var b64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
        _ = Send(new JsonObject { ["realtimeInput"] = new JsonObject { ["audio"] = new JsonObject { ["data"] = b64, ["mimeType"] = "audio/pcm;rate=16000" } } });
    }

    // screen share: one JPEG frame as realtime video input
    public Task SendFrame(byte[] jpg) => Send(new JsonObject
    {
        ["realtimeInput"] = new JsonObject { ["video"] = new JsonObject { ["data"] = Convert.ToBase64String(jpg), ["mimeType"] = "image/jpeg" } }
    });

    // a short text note to the model mid-session (e.g. "sharing stopped")
    public Task SendText(string text) => Send(new JsonObject
    {
        ["realtimeInput"] = new JsonObject { ["text"] = text }
    });

    async Task ReceiveLoop()
    {
        while (!_disposed)
        {
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    var msg = await Receive(_cts.Token);
                    if (msg == null) break;
                    LastActivity = DateTime.Now;
                    // safety: never stay muted if the server didn't confirm the interrupt
                    if (_drop && (DateTime.Now - _dropAt).TotalSeconds > 4) _drop = false;

                    if (msg["sessionResumptionUpdate"] is JsonObject ru && ru["resumable"]?.GetValue<bool>() == true && ru["newHandle"]?.GetValue<string>() is string h)
                        _handle = h;
                    if (msg["goAway"] != null) break; // server is about to close: reconnect now instead of mid-sentence

                    if (msg["serverContent"] is JsonObject sc)
                {
                    if (sc["interrupted"]?.GetValue<bool>() == true) { _buf?.ClearBuffer(); _drop = false; }
                    if (sc["inputTranscription"]?["text"]?.GetValue<string>() is string ut) UserText?.Invoke(ut);
                    if (!_drop && sc["outputTranscription"]?["text"]?.GetValue<string>() is string mt) ModelText?.Invoke(mt);
                    foreach (var p in sc["modelTurn"]?["parts"]?.AsArray() ?? [])
                        if (!_drop && p?["inlineData"]?["data"]?.GetValue<string>() is string audio)
                        {
                            var bytes = Convert.FromBase64String(audio);
                            _buf?.AddSamples(bytes, 0, bytes.Length);
                        }
                    if (sc["turnComplete"]?.GetValue<bool>() == true) { _drop = false; TurnDone?.Invoke(); }
                }
                else if (msg["toolCall"]?["functionCalls"] is JsonArray calls && Exec != null)
                {
                    var responses = new JsonArray();
                    foreach (var c in calls)
                    {
                        var name = c!["name"]!.GetValue<string>();
                        var result = await Exec(name, c["args"]?.AsObject() ?? []);
                        responses.Add(new JsonObject { ["id"] = c["id"]?.GetValue<string>(), ["name"] = name, ["response"] = new JsonObject { ["result"] = result } });
                    }
                    await Send(new JsonObject { ["toolResponse"] = new JsonObject { ["functionResponses"] = responses } });
                }
                }
            }
            catch { }
            if (_disposed || !await Reconnect()) break;
        }
        if (!_disposed) Closed?.Invoke();
    }

    async Task<JsonNode?> Receive(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray()); // server sends JSON as text or binary frames
    }

    async Task Send(JsonObject o)
    {
        if (_ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString());
        await _send.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        catch { }
        finally { _send.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _mic?.StopRecording(); _mic?.Dispose(); } catch { }
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        try { _cts.Cancel(); _ws.Abort(); _ws.Dispose(); } catch { }
    }

    // passes playback audio through and reports its loudness (orb follows Gemini's voice)
    sealed class Meter(IWaveProvider src, Action<float> report) : IWaveProvider
    {
        public float Gain = 1;  // Gemini's voice dips while you start to talk
        float _g = 1;
        public WaveFormat WaveFormat => src.WaveFormat;
        public int Read(Span<byte> buffer)
        {
            int r = src.Read(buffer);
            double sum = 0; int n = r / 2;
            for (int i = 0; i + 1 < r; i += 2)
            {
                short v = BitConverter.ToInt16(buffer.Slice(i, 2));
                sum += v * v;
                // glide toward the target gain (no clicks)
                _g += (Gain - _g) * 0.004f;
                if (_g < 0.999f) BitConverter.TryWriteBytes(buffer.Slice(i, 2), (short)(v * _g));
            }
            report((float)Math.Min(1, Math.Sqrt(sum / Math.Max(1, n)) / 5000));
            return r;
        }
    }
}
