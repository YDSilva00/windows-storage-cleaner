using System.IO;

namespace StorageManager.Services;

/// <summary>Stops the user from deleting things that would break Windows or installed apps.</summary>
public static class PathGuard
{
    private static string Norm(string p) =>
        string.IsNullOrWhiteSpace(p) ? "<none>" : Path.GetFullPath(p).TrimEnd('\\');

    private static readonly string Windows = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string ProgramFiles = Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private static readonly string ProgramFilesX86 = Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    private static readonly string ProgramData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    private static readonly string UserProfile = Norm(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static readonly string UsersRoot = Norm(Path.GetDirectoryName(UserProfile) ?? @"C:\Users");
    private static readonly string AppData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    private static readonly string LocalAppData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static readonly string[] RootSystemNames =
    [
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT",
        "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "Config.Msi",
    ];

    public static bool IsProtected(string path, out string reason)
    {
        var p = Norm(path);
        reason = string.Empty;

        if (Path.GetPathRoot(p)?.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase) == true)
        { reason = "This is the root of a drive."; return true; }

        if (IsSameOrUnder(p, Windows))
        { reason = "Files inside the Windows folder must be cleaned with Windows tools (Disk Cleanup, DISM). See the 'Hidden Space' tab."; return true; }

        string[] exact = [ProgramFiles, ProgramFilesX86, ProgramData, UsersRoot, UserProfile, AppData, LocalAppData,
                          Path.Combine(LocalAppData, "Packages"), Path.Combine(ProgramFiles, "WindowsApps")];
        if (exact.Any(e => e.Equals(p, StringComparison.OrdinalIgnoreCase)))
        { reason = "This is an essential system or user folder. Delete specific items inside it instead."; return true; }

        if (IsSameOrUnder(p, Path.Combine(ProgramFiles, "WindowsApps")))
        { reason = "Microsoft Store apps must be uninstalled from Settings > Apps."; return true; }

        var parent = Path.GetDirectoryName(p);
        if (parent is not null && Path.GetPathRoot(p)?.TrimEnd('\\').Equals(parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true
            && RootSystemNames.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        { reason = "This is a Windows system file or folder. Use the 'Hidden Space' tab for the proper way to shrink it."; return true; }

        return false;
    }

    /// <summary>A softer warning for places where deletion is allowed but may break an app.</summary>
    public static string? GetWarning(string path)
    {
        var p = Norm(path);
        if (IsSameOrUnder(p, ProgramFiles) || IsSameOrUnder(p, ProgramFilesX86))
            return "This is inside Program Files. Uninstall apps from Settings > Apps instead of deleting their folders.";
        if (IsSameOrUnder(p, ProgramData))
            return "This is inside ProgramData. Apps keep settings and licenses here.";
        if (IsSameOrUnder(p, AppData) || IsSameOrUnder(p, LocalAppData))
            return "This is app data. Deleting it can reset or break the app it belongs to (caches are usually fine).";
        if (p.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
            return "This is a virtual disk (WSL / Docker / VM). Deleting it destroys everything inside that Linux distro or VM.";
        return null;
    }

    private static bool IsSameOrUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}
