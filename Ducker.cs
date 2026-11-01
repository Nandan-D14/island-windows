using System.IO;
using NAudio.CoreAudioApi;

namespace Island;

// turns other apps (music, videos) down while talking to Gemini, then restores them.
// The original levels are also written to disk, so if the island is closed or crashes while ducked,
// the next start puts everyone's volume back (otherwise Windows remembers the 12%).
static class Ducker
{
    static readonly Dictionary<uint, float> _saved = [];
    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "duck.txt");

    public static void Duck(float to = 0.12f)
    {
        try
        {
            using var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            uint me = (uint)Environment.ProcessId;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                uint pid = s.GetProcessID;
                if (pid == 0 || pid == me || s.IsSystemSoundsSession || _saved.ContainsKey(pid)) continue;
                float v = s.SimpleAudioVolume.Volume;
                if (v <= to) continue;
                _saved[pid] = v;
                s.SimpleAudioVolume.Volume = to;
            }
            Persist();
        }
        catch { }
    }

    public static void Restore()
    {
        if (_saved.Count == 0) return;
        try
        {
            using var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
                if (_saved.TryGetValue(sessions[i].GetProcessID, out var v)) sessions[i].SimpleAudioVolume.Volume = v;
        }
        catch { }
        _saved.Clear();
        Persist();
    }

    // at startup: a previous run left apps turned down -> bring them back
    public static void RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var line in File.ReadAllLines(File_))
            {
                var p = line.Split(' ');
                if (p.Length == 2 && uint.TryParse(p[0], out var pid) && float.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var v))
                    _saved[pid] = v;
            }
            Restore();
            // anything still sitting at exactly the duck level (process restarted with a new id) goes back to full
            using var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
                if (sessions[i].GetProcessID != 0 && Math.Abs(sessions[i].SimpleAudioVolume.Volume - 0.12f) < 0.005f) sessions[i].SimpleAudioVolume.Volume = 1f;
        }
        catch { }
        try { File.Delete(File_); } catch { }
    }

    static void Persist()
    {
        try
        {
            if (_saved.Count == 0) { File.Delete(File_); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllLines(File_, _saved.Select(kv => $"{kv.Key} {kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        }
        catch { }
    }
}
