using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;

namespace StorageManager.Services;

public static class SystemHelper
{
    public static bool IsAdministrator { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string SystemDrive { get; } =
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    /// <summary>Relaunches the app elevated. Returns false if the user cancelled the UAC prompt.</summary>
    public static bool RestartAsAdministrator()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC cancelled
        }
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { /* ignore */ }
    }

    public static void ShellOpen(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>Runs a command in a visible console window (elevated when requested) so the user sees the output.</summary>
    public static bool RunCommandInConsole(string command, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/k {command}")
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty,
            };
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Space used by System Restore points / Volume Shadow Copies on a drive (lives in "System Volume Information").</summary>
    public static (long Used, long Max)? GetShadowStorage(string driveRoot)
    {
        try
        {
            string letter = driveRoot.TrimEnd('\\');
            using var volSearch = new ManagementObjectSearcher(
                $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter = '{letter}'");
            string? deviceId = volSearch.Get().Cast<ManagementObject>().Select(m => m["DeviceID"] as string).FirstOrDefault();
            if (deviceId is null) return null;

            using var search = new ManagementObjectSearcher("SELECT AllocatedSpace, UsedSpace, MaxSpace, Volume FROM Win32_ShadowStorage");
            foreach (ManagementObject mo in search.Get())
            {
                var volRef = mo["Volume"] as string ?? string.Empty;
                // Volume is a reference like: Win32_Volume.DeviceID="\\\\?\\Volume{guid}\\"
                if (volRef.Replace(@"\\", @"\").Contains(deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    long used = Convert.ToInt64(mo["AllocatedSpace"] ?? mo["UsedSpace"] ?? 0L);
                    long max = Convert.ToInt64(mo["MaxSpace"] ?? 0L);
                    return (used, max);
                }
            }
            return (0, 0);
        }
        catch
        {
            return null; // requires admin
        }
    }

    public static (long Size, long Items) GetRecycleBin(string? driveRoot) => NativeMethods.QueryRecycleBin(driveRoot);

    public static bool EmptyRecycleBin(string? driveRoot) =>
        NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, driveRoot,
            NativeMethods.RecycleFlags.NoConfirmation | NativeMethods.RecycleFlags.NoSound) is 0 or 1 /* S_FALSE: already empty */
        || NativeMethods.QueryRecycleBin(driveRoot).Size == 0;

    /// <summary>Reads hidden protected files at the drive root (hiberfil.sys, pagefile.sys, swapfile.sys...).</summary>
    public static long GetRootSystemFileSize(string driveRoot, string fileName)
    {
        try
        {
            var opts = new System.IO.EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            return new DirectoryInfo(driveRoot).EnumerateFiles(fileName, opts).Sum(f => f.Length);
        }
        catch { return 0; }
    }
}
