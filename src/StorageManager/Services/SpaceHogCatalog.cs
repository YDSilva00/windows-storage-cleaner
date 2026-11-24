using System.IO;
using System.IO.Enumeration;

namespace StorageManager.Services;

public enum Safety
{
    /// <summary>Cache / junk. Deleting is safe; apps recreate it.</summary>
    Safe,
    /// <summary>Can be reduced, but only via the right Windows tool or with some care.</summary>
    Caution,
    /// <summary>Never delete by hand. Shown so you understand where the space went.</summary>
    DoNotDelete,
}

public enum CleanMethod
{
    /// <summary>App deletes the contents of the matching folders / files.</summary>
    DeleteContents,
    /// <summary>Empty the Recycle Bin via the shell API.</summary>
    RecycleBin,
    /// <summary>Not cleaned by the app - user follows the "Fix" action / advice.</summary>
    None,
}

public enum Area
{
    JunkCleaner,
    HiddenSpace,
}

public sealed class CleanupDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public required Area Area { get; init; }
    public required string Description { get; init; }
    public Safety Safety { get; init; } = Safety.Safe;
    public CleanMethod Method { get; init; } = CleanMethod.DeleteContents;

    /// <summary>Path patterns. Environment variables are expanded; a "*" segment matches any folder.</summary>
    public string[] Paths { get; init; } = [];

    /// <summary>Optional file name filter (e.g. only *.vhdx). Simple wildcard patterns.</summary>
    public string[]? FilePatterns { get; init; }

    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public bool NeedsAdmin { get; init; }

    /// <summary>Custom size calculation (used instead of <see cref="Paths"/>). Returns null when unknown.</summary>
    public Func<CancellationToken, long?>? CustomSize { get; init; }

    /// <summary>Label + action for the "Fix" button (runs a command, opens a settings page...).</summary>
    public string? FixLabel { get; init; }
    public string? FixCommand { get; init; }
    public bool FixElevated { get; init; }
    /// <summary>When true, FixCommand is opened with the shell (ms-settings:, .exe, .cpl) instead of run in a console.</summary>
    public bool FixIsShellOpen { get; init; }

    /// <summary>Extra how-to text (commands to copy etc.).</summary>
    public string? Advice { get; init; }

    public string SafetyText => Safety switch
    {
        Safety.Safe => "Safe to clean",
        Safety.Caution => "Use the right tool",
        _ => "Do not delete manually",
    };
}

public static class SpaceHogCatalog
{
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string Sys => SystemHelper.SystemDrive;

    public static IReadOnlyList<CleanupDefinition> All { get; } = Build();

    private static List<CleanupDefinition> Build()
    {
        string[] chromiumCache(string userData) =>
        [
            $@"{userData}\*\Cache",
            $@"{userData}\*\Code Cache",
            $@"{userData}\*\GPUCache",
            $@"{userData}\*\Service Worker\CacheStorage",
            $@"{userData}\*\Service Worker\ScriptCache",
            $@"{userData}\ShaderCache",
            $@"{userData}\GrShaderCache",
        ];

        return
        [
            // ───────────── Junk cleaner: Windows ─────────────
            new()
            {
                Id = "user-temp", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Your temporary files (%TEMP%)",
                Description = "Leftovers from installers, updates and apps. Often several GB. Files changed in the last 24 hours are skipped because they may still be in use.",
                Paths = [@"%TEMP%"], MinAge = TimeSpan.FromDays(1),
            },
            new()
            {
                Id = "windows-temp", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = @"Windows temporary files (C:\Windows\Temp)",
                Description = "System-wide temp folder used by services and installers. Needs administrator rights.",
                Paths = [@"%WINDIR%\Temp"], MinAge = TimeSpan.FromDays(1), NeedsAdmin = true,
            },
            new()
            {
                Id = "recycle-bin", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Recycle Bin",
                Description = "Deleted files still use disk space until the Recycle Bin is emptied. The hidden $Recycle.Bin folder is invisible in Explorer.",
                Method = CleanMethod.RecycleBin,
                CustomSize = _ => SystemHelper.GetRecycleBin(null).Size,
            },
            new()
            {
                Id = "crash-dumps", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Crash dumps & memory dumps",
                Description = "Written when Windows or an app crashes. MEMORY.DMP and LiveKernelReports can be as large as your RAM (8-64 GB). Only needed when debugging a crash.",
                Paths = [@"%LOCALAPPDATA%\CrashDumps", @"%WINDIR%\Minidump", @"%WINDIR%\MEMORY.DMP", @"%WINDIR%\LiveKernelReports"],
                NeedsAdmin = true,
            },
            new()
            {
                Id = "wer", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Windows Error Reporting files",
                Description = "Queued and archived crash reports.",
                Paths = [@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive", @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue",
                         @"%PROGRAMDATA%\Microsoft\Windows\WER\Temp", @"%LOCALAPPDATA%\Microsoft\Windows\WER"],
                NeedsAdmin = true,
            },
            new()
            {
                Id = "update-download", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Windows Update download cache",
                Description = @"C:\Windows\SoftwareDistribution\Download holds update packages that are already installed. Windows downloads them again if needed.",
                Paths = [@"%WINDIR%\SoftwareDistribution\Download"], MinAge = TimeSpan.FromDays(2), NeedsAdmin = true,
            },
            new()
            {
                Id = "delivery-opt", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Delivery Optimization cache",
                Description = "Copies of updates kept to share with other PCs. Can reach several GB.",
                Paths = [@"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache",
                         @"%WINDIR%\SoftwareDistribution\DeliveryOptimization"],
                NeedsAdmin = true,
                FixLabel = "Clear with PowerShell", FixCommand = "powershell -NoProfile -Command Delete-DeliveryOptimizationCache -Force", FixElevated = true,
            },
            new()
            {
                Id = "cbs-logs", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Old Windows setup / servicing logs",
                Description = @"CBS and DISM logs in C:\Windows\Logs. A broken update can make CBS.log grow to tens of GB. Only files older than 7 days are removed.",
                Paths = [@"%WINDIR%\Logs\CBS", @"%WINDIR%\Logs\DISM", @"%WINDIR%\Logs\MoSetup", @"%WINDIR%\Panther"],
                FilePatterns = ["*.log", "*.cab", "*.etl"], MinAge = TimeSpan.FromDays(7), NeedsAdmin = true,
            },
            new()
            {
                Id = "thumbs", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "Thumbnail & icon cache",
                Description = "Explorer rebuilds thumbnails automatically. Files in use are skipped.",
                Paths = [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"], FilePatterns = ["thumbcache_*.db", "iconcache_*.db"],
            },
            new()
            {
                Id = "shader-cache", Area = Area.JunkCleaner, Group = "Windows junk",
                Title = "DirectX / GPU shader caches",
                Description = "Games and the GPU driver rebuild these. NVIDIA and AMD caches can grow to several GB.",
                Paths = [@"%LOCALAPPDATA%\D3DSCache", @"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache",
                         @"%PROGRAMDATA%\NVIDIA Corporation\NV_Cache", @"%LOCALAPPDATA%\AMD\DxCache", @"%LOCALAPPDATA%\AMD\DxcCache",
                         @"%LOCALAPPDATA%\AMD\GLCache", @"%LOCALAPPDATA%\Intel\ShaderCache"],
            },

            // ───────────── Junk cleaner: Browsers ─────────────
            new()
            {
                Id = "chrome", Area = Area.JunkCleaner, Group = "Browser caches",
                Title = "Google Chrome cache",
                Description = "Cached web pages and scripts for all profiles. Close Chrome first. Passwords, history and cookies are NOT touched.",
                Paths = chromiumCache(@"%LOCALAPPDATA%\Google\Chrome\User Data"),
            },
            new()
            {
                Id = "edge", Area = Area.JunkCleaner, Group = "Browser caches",
                Title = "Microsoft Edge cache",
                Description = "Cached web data for all Edge profiles. Close Edge first. Passwords, history and cookies are NOT touched.",
                Paths = chromiumCache(@"%LOCALAPPDATA%\Microsoft\Edge\User Data"),
            },
            new()
            {
                Id = "brave", Area = Area.JunkCleaner, Group = "Browser caches",
                Title = "Brave cache",
                Description = "Cached web data for Brave profiles.",
                Paths = chromiumCache(@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data"),
            },
            new()
            {
                Id = "firefox", Area = Area.JunkCleaner, Group = "Browser caches",
                Title = "Firefox cache",
                Description = "Disk cache for Firefox profiles.",
                Paths = [@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2", @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\startupCache"],
            },

            // ───────────── Junk cleaner: Developer ─────────────
            new()
            {
                Id = "nuget", Area = Area.JunkCleaner, Group = "Developer caches",
                Title = "NuGet package caches (.NET)",
                Description = @"%USERPROFILE%\.nuget\packages and the HTTP cache. Packages download again on the next build. Same as 'dotnet nuget locals all --clear'.",
                Paths = [@"%USERPROFILE%\.nuget\packages", @"%LOCALAPPDATA%\NuGet\v3-cache", @"%LOCALAPPDATA%\NuGet\plugins-cache"],
            },
            new()
            {
                Id = "npm", Area = Area.JunkCleaner, Group = "Developer caches",
                Title = "npm / Yarn / pnpm caches (Node.js)",
                Description = "Downloaded package tarballs. Re-downloaded when needed.",
                Paths = [@"%LOCALAPPDATA%\npm-cache", @"%APPDATA%\npm-cache", @"%LOCALAPPDATA%\Yarn\Cache", @"%LOCALAPPDATA%\pnpm-cache"],
            },
            new()
            {
                Id = "python", Area = Area.JunkCleaner, Group = "Developer caches",
                Title = "pip / uv / conda caches (Python)",
                Description = "Downloaded wheels and packages.",
                Paths = [@"%LOCALAPPDATA%\pip\cache", @"%LOCALAPPDATA%\uv\cache", @"%USERPROFILE%\anaconda3\pkgs", @"%USERPROFILE%\miniconda3\pkgs"],
            },
            new()
            {
                Id = "java", Area = Area.JunkCleaner, Group = "Developer caches",
                Title = "Gradle / Maven caches (Java, Android)",
                Description = "Downloaded dependencies and build caches. Gradle caches often reach 10+ GB.",
                Paths = [@"%USERPROFILE%\.gradle\caches", @"%USERPROFILE%\.gradle\wrapper\dists", @"%USERPROFILE%\.m2\repository"],
            },
            new()
            {
                Id = "other-dev", Area = Area.JunkCleaner, Group = "Developer caches",
                Title = "Go / Rust / VS Code caches",
                Description = "Go build cache, Cargo registry cache, VS Code cached extensions and data.",
                Paths = [@"%LOCALAPPDATA%\go-build", @"%USERPROFILE%\.cargo\registry\cache", @"%APPDATA%\Code\Cache",
                         @"%APPDATA%\Code\CachedData", @"%APPDATA%\Code\CachedExtensionVSIXs", @"%APPDATA%\Code\Code Cache"],
            },

            // ───────────── Junk cleaner: Apps ─────────────
            new()
            {
                Id = "chat-apps", Area = Area.JunkCleaner, Group = "App caches",
                Title = "Teams / Discord / Slack caches",
                Description = "Cached images, videos and web data. Close the apps first.",
                Paths = [@"%APPDATA%\Microsoft\Teams\Cache", @"%APPDATA%\Microsoft\Teams\Service Worker\CacheStorage",
                         @"%APPDATA%\Microsoft\Teams\GPUCache", @"%APPDATA%\discord\Cache", @"%APPDATA%\discord\Code Cache",
                         @"%APPDATA%\discord\GPUCache", @"%APPDATA%\Slack\Cache", @"%APPDATA%\Slack\Service Worker\CacheStorage",
                         @"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache"],
            },
            new()
            {
                Id = "spotify", Area = Area.JunkCleaner, Group = "App caches",
                Title = "Spotify cache",
                Description = "Streamed songs Spotify keeps locally. Can be 10 GB. Downloaded playlists are kept in a separate folder.",
                Paths = [@"%LOCALAPPDATA%\Spotify\Storage", @"%LOCALAPPDATA%\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalCache\Spotify\Data"],
            },

            // ───────────── Hidden space: system ─────────────
            new()
            {
                Id = "hiberfil", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = "Hibernation file (hiberfil.sys)",
                Description = "A hidden protected file at the root of C:. It can be 40-75% of your RAM (e.g. 6-12 GB on a 16 GB PC). It is used by Hibernate and Fast Startup.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                CustomSize = _ => SystemHelper.GetRootSystemFileSize(Sys, "hiberfil.sys"),
                FixLabel = "Turn off hibernation", FixCommand = "powercfg /h off", FixElevated = true,
                Advice = "powercfg /h off  - deletes hiberfil.sys (you lose Hibernate and Fast Startup)\r\npowercfg /h /type reduced  - smaller file, keeps Fast Startup\r\npowercfg /h on  - turn it back on",
            },
            new()
            {
                Id = "pagefile", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = "Page file & swap file (pagefile.sys, swapfile.sys)",
                Description = "Virtual memory. Windows manages its size, usually 2-24 GB. Never delete it. You can move it to another drive or set a fixed smaller size.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                CustomSize = _ => SystemHelper.GetRootSystemFileSize(Sys, "pagefile.sys") + SystemHelper.GetRootSystemFileSize(Sys, "swapfile.sys"),
                FixLabel = "Open virtual memory settings", FixCommand = "SystemPropertiesPerformance.exe", FixIsShellOpen = true,
                Advice = "Advanced tab > Virtual memory > Change > untick 'Automatically manage' > pick D: > 'System managed size' > Set > pick C: > 'No paging file' (or a small custom size) > Set > reboot.",
            },
            new()
            {
                Id = "restore-points", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = "System Restore points / shadow copies",
                Description = "Stored in the hidden 'System Volume Information' folder, which even administrators can't open. This is a very common reason that 'C: is full but I can't see why'. Needs admin to measure.",
                Safety = Safety.Caution, Method = CleanMethod.None, NeedsAdmin = true,
                CustomSize = _ => SystemHelper.GetShadowStorage(Sys)?.Used,
                FixLabel = "Open System Protection", FixCommand = "SystemPropertiesProtection.exe", FixIsShellOpen = true,
                Advice = "System Protection > select C: > Configure > lower 'Max Usage' (5-10% is plenty) or 'Delete' old points.\r\nCommand (admin): vssadmin list shadowstorage\r\nLimit: vssadmin resize shadowstorage /for=C: /on=C: /maxsize=10%",
            },
            new()
            {
                Id = "winsxs", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = @"Component store (C:\Windows\WinSxS)",
                Description = "Old versions of system files kept after updates. Explorer overstates its size because many files are hard links to System32. NEVER delete by hand. DISM can clean it safely.",
                Safety = Safety.DoNotDelete, Method = CleanMethod.None,
                Paths = [@"%WINDIR%\WinSxS"],
                FixLabel = "Clean up with DISM (admin)", FixCommand = "Dism.exe /Online /Cleanup-Image /StartComponentCleanup", FixElevated = true,
                Advice = "Analyze real size: Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore\r\nClean: Dism.exe /Online /Cleanup-Image /StartComponentCleanup\r\nMore aggressive (removes the ability to uninstall updates): add /ResetBase",
            },
            new()
            {
                Id = "windows-old", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = "Previous Windows installation & upgrade leftovers",
                Description = @"Windows.old, $WINDOWS.~BT, $Windows.~WS and ESD folders left after a Windows upgrade. They are often 10-30 GB and you can only roll back with them for 10 days.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%SYSTEMDRIVE%\Windows.old", @"%SYSTEMDRIVE%\$WINDOWS.~BT", @"%SYSTEMDRIVE%\$Windows.~WS", @"%SYSTEMDRIVE%\ESD", @"%SYSTEMDRIVE%\$GetCurrent", @"%SYSTEMDRIVE%\$SysReset"],
                FixLabel = "Open Disk Cleanup (system files)", FixCommand = "cleanmgr.exe /d C", FixElevated = true,
                Advice = "In Disk Cleanup tick 'Previous Windows installation(s)', 'Windows upgrade log files' and 'Temporary Windows installation files'. Or use Settings > System > Storage > Temporary files.",
            },
            new()
            {
                Id = "driverstore", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = "Old driver packages (DriverStore)",
                Description = @"C:\Windows\System32\DriverStore\FileRepository keeps every driver version ever installed. GPU drivers alone can add 1 GB or more per update.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%WINDIR%\System32\DriverStore\FileRepository"],
                FixLabel = "Open Disk Cleanup (system files)", FixCommand = "cleanmgr.exe /d C", FixElevated = true,
                Advice = "Disk Cleanup > Clean up system files > tick 'Device driver packages'.\r\nAdvanced: pnputil /enum-drivers  then  pnputil /delete-driver oemXX.inf (only old versions).",
            },
            new()
            {
                Id = "installer", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = @"Windows Installer cache (C:\Windows\Installer)",
                Description = "A hidden folder of .msi/.msp files needed to repair, update or uninstall programs. Do NOT delete it by hand or apps will fail to uninstall. Uninstalling apps you don't use (Office, Visual Studio, SQL Server) shrinks it.",
                Safety = Safety.DoNotDelete, Method = CleanMethod.None, NeedsAdmin = true,
                Paths = [@"%WINDIR%\Installer"],
                FixLabel = "Open Installed apps", FixCommand = "ms-settings:appsfeatures", FixIsShellOpen = true,
            },
            new()
            {
                Id = "package-cache", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = @"Installer package cache (C:\ProgramData\Package Cache)",
                Description = "Setup files kept by Visual Studio, .NET, SQL Server, VC++ runtimes, etc. Needed for repair and uninstall. To shrink it, uninstall old SDKs and runtimes you no longer use.",
                Safety = Safety.DoNotDelete, Method = CleanMethod.None,
                Paths = [@"%PROGRAMDATA%\Package Cache", @"%PROGRAMDATA%\Microsoft\VisualStudio\Packages"],
                FixLabel = "Open Installed apps", FixCommand = "ms-settings:appsfeatures", FixIsShellOpen = true,
            },
            new()
            {
                Id = "search-index", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = "Windows Search index",
                Description = @"C:\ProgramData\Microsoft\Search\Data. It is usually small, but it can balloon to 50+ GB when it gets corrupted. Rebuilding the index fixes that.",
                Safety = Safety.Caution, Method = CleanMethod.None, NeedsAdmin = true,
                Paths = [@"%PROGRAMDATA%\Microsoft\Search\Data"],
                FixLabel = "Open Indexing Options", FixCommand = "control.exe srchadmin.dll", FixIsShellOpen = true,
                Advice = "Indexing Options > Advanced > Rebuild. You can also click Modify and remove big folders you never search in.",
            },
            new()
            {
                Id = "reserved", Area = Area.HiddenSpace, Group = "Hidden Windows system files",
                Title = "Reserved Storage (about 7 GB)",
                Description = "Windows keeps about 7 GB reserved for updates. It's invisible everywhere and is counted as 'used'. The app can't measure it. Click Fix to check whether it is on.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                CustomSize = _ => null,
                FixLabel = "Check state (admin)", FixCommand = "DISM.exe /Online /Get-ReservedStorageState", FixElevated = true,
                Advice = "Disable (admin): DISM.exe /Online /Set-ReservedStorageState /State:Disabled\r\nRe-enable: DISM.exe /Online /Set-ReservedStorageState /State:Enabled",
            },

            // ───────────── Hidden space: virtual disks ─────────────
            new()
            {
                Id = "wsl", Area = Area.HiddenSpace, Group = "Virtual machines, WSL & Docker",
                Title = "WSL Linux disks (ext4.vhdx)",
                Description = "WSL virtual disks grow but NEVER shrink by themselves, even after you delete files inside Linux. This is one of the most common hidden 50-200 GB space hogs.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%LOCALAPPDATA%\Packages\*\LocalState", @"%LOCALAPPDATA%\wsl"],
                FilePatterns = ["*.vhdx"],
                FixLabel = "Copy compact commands", FixCommand = "copy-advice",
                Advice = "PowerShell (admin):\r\nwsl --shutdown\r\nOptimize-VHD -Path \"<path to ext4.vhdx>\" -Mode Full\r\n\r\nWindows Home (no Optimize-VHD):\r\nwsl --shutdown\r\ndiskpart\r\n  select vdisk file=\"<path to ext4.vhdx>\"\r\n  attach vdisk readonly\r\n  compact vdisk\r\n  detach vdisk\r\n\r\nWSL 2.x: wsl --manage <distro> --set-sparse true\r\nMove to D: wsl --export Ubuntu D:\\ubuntu.tar ; wsl --unregister Ubuntu ; wsl --import Ubuntu D:\\WSL\\Ubuntu D:\\ubuntu.tar",
            },
            new()
            {
                Id = "docker", Area = Area.HiddenSpace, Group = "Virtual machines, WSL & Docker",
                Title = "Docker Desktop disk image",
                Description = "Docker images, containers and build cache live in one virtual disk that never shrinks automatically. Running 'docker system prune' alone does not give the space back to Windows.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%LOCALAPPDATA%\Docker\wsl", @"%PROGRAMDATA%\DockerDesktop\vm-data"],
                FilePatterns = ["*.vhdx"],
                FixLabel = "Copy cleanup commands", FixCommand = "copy-advice",
                Advice = "docker system prune -a --volumes   (deletes unused images, containers, volumes)\r\ndocker builder prune -a\r\nThen quit Docker Desktop, run: wsl --shutdown\r\nOptimize-VHD -Path \"<path to docker_data.vhdx>\" -Mode Full\r\nOr Docker Desktop > Troubleshoot > Clean / Purge data. Settings > Resources lets you move the disk image to D:.",
            },
            new()
            {
                Id = "vms", Area = Area.HiddenSpace, Group = "Virtual machines, WSL & Docker",
                Title = "Hyper-V / VirtualBox / VMware / Android emulator disks",
                Description = "Virtual machine disks, including ones left behind by deleted VMs. People often find forgotten 40 GB files here.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%PROGRAMDATA%\Microsoft\Windows\Virtual Hard Disks", @"%PUBLIC%\Documents\Hyper-V\Virtual hard disks",
                         @"%USERPROFILE%\VirtualBox VMs", @"%USERPROFILE%\Documents\Virtual Machines", @"%USERPROFILE%\.android\avd"],
                FixLabel = "Open folder", FixCommand = "open-first",
                Advice = "Delete VMs from inside Hyper-V Manager / VirtualBox / VMware / Android Studio Device Manager, so their disks are removed properly.",
            },

            // ───────────── Hidden space: user data ─────────────
            new()
            {
                Id = "iphone-backup", Area = Area.HiddenSpace, Group = "Large personal data",
                Title = "iPhone / iPad backups (iTunes, Apple Devices)",
                Description = "Each device backup can be 10-100 GB and is hidden deep inside AppData.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%APPDATA%\Apple Computer\MobileSync\Backup", @"%USERPROFILE%\Apple\MobileSync\Backup"],
                FixLabel = "Open folder", FixCommand = "open-first",
                Advice = "Delete old backups from iTunes / Apple Devices > Manage Backups, or move them to another drive.",
            },
            new()
            {
                Id = "outlook", Area = Area.HiddenSpace, Group = "Large personal data",
                Title = "Outlook offline mailbox (.ost / .pst)",
                Description = "Offline copies of your mailbox. These can be 10-50 GB.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%LOCALAPPDATA%\Microsoft\Outlook", @"%USERPROFILE%\Documents\Outlook Files"],
                FilePatterns = ["*.ost", "*.pst"],
                FixLabel = "Open folder", FixCommand = "open-first",
                Advice = "Outlook > Account Settings > Change > lower 'Download email for the past ...' (e.g. 1 year). The .ost then rebuilds smaller.",
            },
            new()
            {
                Id = "downloads", Area = Area.HiddenSpace, Group = "Large personal data",
                Title = "Downloads folder",
                Description = "Old installers, ISOs and archives pile up here. Review and delete what you don't need.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%USERPROFILE%\Downloads"],
                FixLabel = "Open folder", FixCommand = "open-first",
            },
            new()
            {
                Id = "onedrive", Area = Area.HiddenSpace, Group = "Large personal data",
                Title = "OneDrive files kept on this device",
                Description = "Files marked 'Always keep on this device' use local space. Right-click a folder > 'Free up space' to make them online-only. Online-only files are counted as 0 bytes by this app.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%OneDrive%", @"%OneDriveCommercial%"],
                FixLabel = "Open folder", FixCommand = "open-first",
            },
            new()
            {
                Id = "games", Area = Area.HiddenSpace, Group = "Large personal data",
                Title = "Game libraries (Steam, Epic, Xbox)",
                Description = "Installed games are often 50-150 GB each. Every launcher can move games to another drive.",
                Safety = Safety.Caution, Method = CleanMethod.None,
                Paths = [@"%ProgramFiles(x86)%\Steam\steamapps", @"%ProgramFiles%\Epic Games", @"%SYSTEMDRIVE%\XboxGames"],
                FixLabel = "Open folder", FixCommand = "open-first",
                Advice = "Steam: Settings > Storage > move. Epic: uninstall then reinstall to D:. Xbox app: Settings > Install options.",
            },
        ];
    }

    /// <summary>Expands environment variables and "*" folder wildcards into existing paths.</summary>
    public static IEnumerable<string> ExpandPath(string pattern)
    {
        var expanded = Environment.ExpandEnvironmentVariables(pattern);
        if (expanded.Contains('%')) yield break; // undefined variable (e.g. OneDrive not installed)
        if (!expanded.Contains('*'))
        {
            if (Directory.Exists(expanded) || File.Exists(expanded)) yield return expanded;
            yield break;
        }

        var parts = expanded.Split('\\');
        IEnumerable<string> current = [parts[0] + "\\"];
        for (int i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            if (part.Contains('*'))
            {
                current = current.SelectMany(dir =>
                {
                    try
                    {
                        return Directory.EnumerateDirectories(dir, part,
                            new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
                    }
                    catch { return []; }
                }).ToList();
            }
            else
            {
                current = current.Select(d => Path.Combine(d, part)).ToList();
            }
        }
        foreach (var p in current)
            if (Directory.Exists(p) || File.Exists(p)) yield return p;
    }

    public static Func<string, bool>? BuildFilter(string[]? patterns)
    {
        if (patterns is null || patterns.Length == 0) return null;
        return name => patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }
}
