using System.Collections.Concurrent;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.Wave;

namespace Island;

// "Hey Gemini" wake word + "stop Gemini" – Windows' offline recognizer, nothing leaves the PC
public partial class MainWindow
{
    SpeechRecognitionEngine? _wake;
    bool _wakeRunning;

    static readonly string[] WakePhrases = ["hey gemini"];
    static readonly System.Text.RegularExpressions.Regex WakeRx =
        new(@"\b(hey|hi|hay|a|okay|ok|hello)\s+(gemini|jemini|gemin[aiey]|jemin[aiey]|germany|jiminy|jimmy nee|jemmy nie)\b");

    // last few recognitions, for tuning (%APPDATA%\Island\wake.log)
    static void WakeLog(string line)
    {
        try
        {
            var f = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "wake.log");
            if (File.Exists(f) && new FileInfo(f).Length > 30_000) File.Delete(f);
            File.AppendAllText(f, $"{DateTime.Now:HH:mm:ss} {line}\n");
        }
        catch { }
    }
    static readonly string[] StopPhrases = ["stop", "stop gemini", "stop google", "gemini stop", "google stop", "stop it", "stop talking", "be quiet", "shut up", "that's enough", "wait"];
    static readonly string[] EndPhrases = ["bye gemini", "bye google", "goodbye", "bye", "thank you gemini", "thanks gemini", "close gemini", "exit"];
    static readonly string[] PausePhrases = ["pause gemini", "pause google", "gemini pause", "google pause", "hold on gemini", "hold on google"];
    static readonly string[] ResumePhrases = ["continue gemini", "continue google", "gemini continue", "google continue", "resume gemini", "resume google", "continue"];

    void StartWake()
    {
        try
        {
            var info = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
            if (info == null) return; // no English speech pack installed

            _wake = new SpeechRecognitionEngine(info);
            var phrases = new Choices([.. WakePhrases, .. StopPhrases, .. EndPhrases, .. PausePhrases, .. ResumePhrases]);
            _wake.LoadGrammar(new Grammar(new GrammarBuilder(phrases) { Culture = info.Culture }) { Name = "cmd" });
            // "garbage" model: ordinary speech matches dictation instead of being forced onto a command
            _wake.LoadGrammar(new DictationGrammar { Name = "garbage" });
            try { StartBoostedMic(); } catch (Exception ex) { WakeLog("boost failed: " + ex.Message); _wake.SetInputToDefaultAudioDevice(); }
            _wake.SpeechRecognized += (_, e) =>
            {
                var raw = System.Text.RegularExpressions.Regex.Replace(e.Result.Text.ToLowerInvariant(), @"[^a-z' ]", "").Trim();
                bool isCmd = e.Result.Grammar?.Name == "cmd";
                if (isCmd || System.Text.RegularExpressions.Regex.IsMatch(raw, "gem|jem|germ|jim|mini"))
                    WakeLog($"{(isCmd ? "cmd" : "dict")} \"{raw}\" {e.Result.Confidence:0.00}");
                // "Gemini" is a rare word for this engine: accept it from the command grammar OR from dictation,
                // including its usual mishearings ("germany", "jiminy"...)
                bool wake = WakeRx.IsMatch(raw) && e.Result.Confidence >= (isCmd ? 0.5 : 0.2);
                var said = wake ? "hey gemini" : raw;
                if (!wake && !isCmd) return; // was just normal talk
                // Gemini's own voice from the speaker can say "wait", "stop"... – while it talks, only obey commands that name it
                if (_live?.Speaking == true && !said.Contains("gemini") && !said.Contains("google")) return;
                bool stop = StopPhrases.Contains(said), end = EndPhrases.Contains(said);
                bool pause = PausePhrases.Contains(said), resume = ResumePhrases.Contains(said);
                // in-session commands must be clear matches
                if (!wake && (e.Result.Confidence < 0.7 || e.Result.Words.Any(w => w.Confidence < 0.5))) return;
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_live != null)
                    {
                        if (end) StopVoice();
                        else if (pause) PauseVoice();
                        else if (_live.Paused) { if (resume || WakePhrases.Contains(said)) ResumeVoice(); }
                        else if (stop) { if (_live.Speaking) StopTalking(); else StopVoice(); }
                        return;
                    }
                    if (stop || end || pause || resume || _ai.Key == "" || _ai.LiveModel == "") return;
                    await ToggleVoice();
                });
            };
            _wake.SpeechRecognitionRejected += (_, e) => { if (System.Text.RegularExpressions.Regex.IsMatch(e.Result?.Text?.ToLowerInvariant() ?? "", "gem|jem|germ|jim|mini")) WakeLog($"rejected \"{e.Result?.Text}\" {e.Result?.Confidence:0.00} alts: " + string.Join(" | ", e.Result?.Alternates.Take(3).Select(a => a.Text) ?? [])); };
            _wake.RecognizeCompleted += (_, _) => _wakeRunning = false;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Ducker.Restore();
            ResumeWake();
        }
        catch (Exception ex) { WakeLog("start failed: " + ex.Message); _wake = null; }
    }

    async void ResumeWake()
    {
        if (_wake == null) return;
        for (int i = 0; i < 20 && _wakeRunning; i++) await Task.Delay(100);
        try { if (!_wakeRunning) { _wake.RecognizeAsync(RecognizeMode.Multiple); _wakeRunning = true; WakeLog("listening"); } } catch (Exception ex) { WakeLog("resume failed: " + ex.Message); }
    }

    // ---------- battery: wake mic only runs when it's worth it ----------
    bool _locked;

    void InitWakePower()
    {
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            _locked = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect;
            Dispatcher.BeginInvoke(UpdateWakePower);
        };
        WakeBtn.IsChecked = _ai.WakeWord;
    }

    // called every second: mic off when disabled, PC locked, or battery ≤ 20% and not charging
    void UpdateWakePower()
    {
        bool lowBat = Stats.GetSystemPowerStatus(out var p) && p.AC == 0 && p.Percent <= 20;
        bool want = _ai.WakeWord && _ai.AiEnabled && !_locked && !lowBat && _ai.Key != "" || _live != null;
        if (want == _wakeMicOn || _wakeMic == null) return;
        try { if (want) _wakeMic.StartRecording(); else _wakeMic.StopRecording(); _wakeMicOn = want; } catch { }
    }

    void OnWakeToggle(object s, System.Windows.RoutedEventArgs e)
    {
        _ai.WakeWord = WakeBtn.IsChecked == true; _ai.Save();
        UpdateWakePower();
        // easy to tap by accident (it sits next to Mute): always say what changed and how to undo it
        ShowToast(_ai.WakeWord ? "\u201CHey Gemini\u201D on" : "\u201CHey Gemini\u201D off \u00B7 tap \uD83C\uDF99 again to turn on",
                  _ai.WakeWord ? System.Windows.Media.Colors.White : System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A));
    }

    // ---------- mic → auto-gain + noise gate → recognizer (hears you from across the room) ----------
    WaveInEvent? _wakeMic;
    bool _wakeMicOn;
    double _gain = 2;

    void StartBoostedMic()
    {
        var pipe = new MicPipe();
        _wakeMic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        _wakeMic.DataAvailable += (_, e) =>
        {
            int n = e.BytesRecorded / 2;
            double sum = 0;
            for (int i = 0; i < e.BytesRecorded; i += 2) { short v = BitConverter.ToInt16(e.Buffer, i); sum += v * v; }
            double rms = Math.Sqrt(sum / Math.Max(1, n));
            // speech-level chunks steer the gain toward a healthy loudness; near-silence isn't amplified (noise gate)
            if (rms > 120) _gain = _gain * 0.85 + Math.Clamp(3500 / rms, 1, 8) * 0.15;
            double g = rms > 120 ? _gain : 1;
            var outBuf = new byte[e.BytesRecorded];
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                int v = (int)(BitConverter.ToInt16(e.Buffer, i) * g);
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                outBuf[i] = (byte)v; outBuf[i + 1] = (byte)(v >> 8);
            }
            pipe.Write(outBuf);
        };
        _wake!.SetInputToAudioStream(pipe, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        _wakeMic.StartRecording(); _wakeMicOn = true;
    }

    // endless blocking stream the recognizer reads from
    sealed class MicPipe : Stream
    {
        readonly BlockingCollection<byte[]> _q = new(50);
        byte[] _cur = []; int _pos;
        public void Write(byte[] b) { if (!_q.TryAdd(b)) { _q.TryTake(out _); _q.TryAdd(b); } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            // always fill the whole request – the recognizer stalls on short reads
            int done = 0;
            while (done < count)
            {
                if (_pos >= _cur.Length) { _cur = _q.Take(); _pos = 0; }
                int n = Math.Min(count - done, _cur.Length - _pos);
                Buffer.BlockCopy(_cur, _pos, buffer, offset + done, n);
                _pos += n; done += n;
            }
            return done;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => -1;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => 0;
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
    }
}