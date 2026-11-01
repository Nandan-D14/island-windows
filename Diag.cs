using System.IO;
using NAudio.Wave;

namespace Island;

// Support aid: `Island.exe --health` runs the full health check (plus a 2-second mic level test)
// and writes the result to %TEMP%\island-health.txt, then exits.
public partial class MainWindow
{
    async Task HealthToFile()
    {
        var lines = new List<string> { $"Island {AppVersion.ToString(3)} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        try
        {
            var rows = new List<HealthRow>
            {
                await InternetRow(), await ChatRow(), await VoiceRow(),
                InstallRow(), MicRow(), WakeRow(), SpeakerRow(), ErrorsRow(),
            };
            lines.AddRange(rows.Select(r => $"{r.State,-5} {r.Name}: {r.Detail}"));
            lines.Add($"state: wakeWord={_ai.WakeWord} aiEnabled={_ai.AiEnabled} wakeEngine={(_wake != null)} wakeMicOn={_wakeMicOn} locked={_locked} hotkey={_ai.Hotkey} text={_ai.TextModel} live={_ai.LiveModel}");
            lines.Add("mic devices: " + string.Join(" | ", Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => $"{i}: {WaveInEvent.GetCapabilities(i).ProductName}")));
            lines.Add(await MicLevel());
        }
        catch (Exception ex) { lines.Add("diag failed: " + ex); }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "island-health.txt"), lines);
        System.Windows.Application.Current.Shutdown();
    }

    // records 2 s from the device the wake word uses and reports how loud it was
    static async Task<string> MicLevel()
    {
        try
        {
            using var mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
            double peak = 0, sum = 0; long n = 0;
            mic.DataAvailable += (_, e) =>
            {
                for (int i = 0; i < e.BytesRecorded; i += 2)
                {
                    double v = Math.Abs((double)BitConverter.ToInt16(e.Buffer, i));
                    peak = Math.Max(peak, v); sum += v * v; n++;
                }
            };
            mic.StartRecording();
            await Task.Delay(2000);
            mic.StopRecording();
            double rms = Math.Sqrt(sum / Math.Max(1, n));
            return $"mic level (2 s): samples={n} rms={rms:0} peak={peak:0}" + (n == 0 ? "  → NO AUDIO" : peak < 30 ? "  → SILENT (muted or wrong mic?)" : "");
        }
        catch (Exception ex) { return "mic level: failed – " + ex.Message; }
    }
}
