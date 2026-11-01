using System.IO;
using Microsoft.Win32;

namespace Island;

public partial class App : System.Windows.Application
{
    static readonly string Log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "error.log");
    static Mutex? _single;

    public App()
    {
        // install / update / uninstall happen before anything else (see Installer.cs)
        if (HandleSetup(Environment.GetCommandLineArgs().Skip(1).ToArray())) Environment.Exit(0);

        // only one island at a time (startup entry + manual launch would otherwise double up)
        _single = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out bool first);
        if (!first) Environment.Exit(0);

        var startup = AiSettings.Load().StartWithWindows;
        Task.Run(() => RegisterStartup(startup)); // in the background: never slows down the island appearing

        // never let one bad event kill the island; log it instead
        DispatcherUnhandledException += (_, e) => { Write(e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
    }

    // Start with Windows (on by default). Uses a Task Scheduler logon task: Windows starts Run-key apps one by one
    // (up to 30 s each), which on a busy PC delayed the island by minutes. Falls back to the Run key if the task
    // can't be created. Turned off in Controls › Island.
    public static void SetStartup(bool on) => Task.Run(() => RegisterStartup(on));

    const string TaskName = "DynamicIsland";

    static void RegisterStartup(bool on)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            // development builds (…\bin\Debug, …\bin\Release) never take over the startup entry
            if (exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)) return;
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (!on)
            {
                run?.DeleteValue("DynamicIsland", false);
                Schtasks($"/Delete /TN \"{TaskName}\" /F");
                return;
            }
            if (EnsureTask(exe)) { run?.DeleteValue("DynamicIsland", false); return; } // fast path works: drop the slow entry
            var value = $"\"{exe}\"";
            if (run?.GetValue("DynamicIsland") as string != value) run?.SetValue("DynamicIsland", value);
        }
        catch { }
    }

    // (re)create the logon task only when it's missing or points at a different exe
    static bool EnsureTask(string exe)
    {
        var (code, xml) = Schtasks($"/Query /TN \"{TaskName}\" /XML");
        if (code == 0 && xml.Contains("<Command>" + System.Security.SecurityElement.Escape(exe) + "</Command>")) return true;
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var esc = System.Security.SecurityElement.Escape;
        var task = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts the Dynamic Island when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{esc(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{esc(exe)}</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "island-task.xml");
        try
        {
            File.WriteAllText(file, task, System.Text.Encoding.Unicode);
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F").code == 0;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    static (int code, string output) Schtasks(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, ""); }
            return (p.ExitCode, output);
        }
        catch { return (-1, ""); }
    }

    static void Write(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
            File.AppendAllText(Log, $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}
