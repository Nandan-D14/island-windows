using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Island;

// Built-in installer: Island.exe installs itself into %LOCALAPPDATA%\Programs\Island (no admin), adds a Start menu
// shortcut and a "Settings › Apps" entry, and removes all of it again with --uninstall.
//   Island.exe                 from anywhere else → asks to install (or just run once)
//   Island.exe --update        silent install over the existing copy (used by one-click updates)
//   Island.exe --uninstall     removes the island (add --quiet to skip the questions and keep your data)
public partial class App
{
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Island");
    public static string InstalledExe => Path.Combine(InstallDir, "Island.exe");
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");
    static readonly string ShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Island.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland";

    static string Me => Environment.ProcessPath ?? "";
    static bool IsDevBuild => Me.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || Me.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    static bool IsInstalledCopy => string.Equals(Path.GetFullPath(Me), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
    static string MyVersion => (typeof(App).Assembly.GetName().Version ?? new Version(1, 0)).ToString(3);

    // runs before anything else; true = this process has done its job and should exit
    static bool HandleSetup(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        try { if (File.Exists(InstalledExe + ".old")) File.Delete(InstalledExe + ".old"); } catch { }
        if (args.Contains("--uninstall")) { Uninstall(quiet); return true; }
        if (IsInstalledCopy) { Task.Run(Register); return false; } // keep the Apps entry's version current
        if (IsDevBuild || args.Contains("--portable") || Me == "") return false;

        if (!args.Contains("--update"))
        {
            string? have = File.Exists(InstalledExe) ? FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion : null;
            var msg = have != null
                ? $"Island {have} is already installed.\n\nReplace it with version {MyVersion}?\n\nYes = update   ·   No = just run this copy"
                : "Install Island on this PC?\n\nIt goes into your user folder (no admin needed), appears in the Start menu and starts with Windows. " +
                  "You can remove it any time from Settings › Apps.\n\nYes = install   ·   No = just run it once";
            var r = MessageBox.Show(msg, "Island", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return true;
            if (r == MessageBoxResult.No) return false; // portable run
        }
        try
        {
            Install();
            Process.Start(new ProcessStartInfo(InstalledExe, args.Contains("--update") ? "--updated" : "--installed") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Write(ex);
            if (!quiet) MessageBox.Show("Couldn't install Island:\n" + ex.Message, "Island", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return true;
    }

    static void Install()
    {
        bool stopped = StopOtherIslands();
        Directory.CreateDirectory(InstallDir);
        var old = InstalledExe + ".old";
        Exception? last = null;
        // Right after the old island exits, Windows keeps its exe locked for a moment ("access denied" / "in use").
        // A running or locked exe can't be overwritten, but it *can* be renamed: move it aside, then copy the new one in.
        for (int i = 0; i < 40; i++)
        {
            try
            {
                if (File.Exists(InstalledExe))
                {
                    File.SetAttributes(InstalledExe, FileAttributes.Normal); // a read-only copy can't be replaced
                    try { if (File.Exists(old)) { File.SetAttributes(old, FileAttributes.Normal); File.Delete(old); } } catch { }
                    if (!File.Exists(old)) File.Move(InstalledExe, old);
                    else File.Delete(InstalledExe);
                }
                File.Copy(Me, InstalledExe, true);
                last = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (i == 10) StopOtherIslands(force: true); // still locked: try harder to close the old one
                Thread.Sleep(250);
            }
        }
        if (last != null)
        {
            // put the old version back so the island still works
            try { if (!File.Exists(InstalledExe) && File.Exists(old)) File.Move(old, InstalledExe); } catch { }
            throw new IOException(last.Message + "\n\nClose Island in Task Manager (or restart the PC) and run the new version again.", last);
        }
        try { File.Delete(old); } catch { } // still in use by the old island: deleted on the next start
        Register();
        if (!stopped && !QuietArg)
            MessageBox.Show("Island was updated.\nThe old version is still running (it was started as administrator), so restart the PC or end Island in Task Manager to use the new one.",
                            "Island", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    static bool QuietArg => Environment.GetCommandLineArgs().Contains("--quiet");

    // Start menu shortcut + Settings › Apps entry (both per-user, no admin)
    static void Register()
    {
        try
        {
            if (!File.Exists(ShortcutPath))
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic lnk = sh.CreateShortcut(ShortcutPath);
                lnk.TargetPath = InstalledExe; lnk.WorkingDirectory = InstallDir; lnk.Description = "Dynamic Island for Windows";
                lnk.Save();
            }
        }
        catch (Exception ex) { Write(ex); }
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", "Island");
            k.SetValue("DisplayVersion", MyVersion);
            k.SetValue("Publisher", "Island");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            if (File.Exists(InstalledExe)) k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex) { Write(ex); }
    }

    static void Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Uninstall Island?", "Island", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StopOtherIslands();
        RegisterStartup(false); // removes the logon task + Run entry
        try { File.Delete(ShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        bool wipe = !quiet && Directory.Exists(DataDir) && MessageBox.Show(
            "Also delete your Island data?\n\nSettings, Gemini key, notes, memory, to-dos and the activity log.\nNo = keep them for a future reinstall.",
            "Island", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (wipe) { try { Directory.Delete(DataDir, true); } catch { } }
        // a running exe can't delete itself: a hidden helper removes the folder a moment after we exit
        if (Directory.Exists(InstallDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }
        if (!quiet) MessageBox.Show("Island was removed." + (wipe ? "" : "\nYour data is kept in " + DataDir), "Island", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // true = no other island is left running
    static bool StopOtherIslands(bool force = false)
    {
        var others = Process.GetProcessesByName("Island").Where(p => p.Id != Environment.ProcessId).ToList();
        foreach (var p in others)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        if (force)
        {
            try
            {
                using var tk = Process.Start(new ProcessStartInfo("taskkill.exe", $"/F /T /FI \"PID ne {Environment.ProcessId}\" /IM Island.exe")
                { CreateNoWindow = true, UseShellExecute = false })!;
                tk.WaitForExit(5000);
            }
            catch { }
        }
        foreach (var p in others)
        {
            try { p.WaitForExit(5000); } catch { }
        }
        return Process.GetProcessesByName("Island").All(p => p.Id == Environment.ProcessId);
    }
}
