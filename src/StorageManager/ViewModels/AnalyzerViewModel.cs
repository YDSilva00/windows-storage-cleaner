using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using StorageManager.Converters;
using StorageManager.Models;
using StorageManager.Services;

namespace StorageManager.ViewModels;

public sealed partial class AnalyzerViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private ScanProgress? _progress;
    private CancellationTokenSource? _cts;

    public AnalyzerViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => UpdateProgress();
        ScanPath = SystemHelper.SystemDrive;
    }

    public ObservableCollection<FsNode> RootItems { get; } = [];
    public ObservableCollection<FsNode> LargestFiles { get; } = [];
    public ObservableCollection<FsNode> OldLargeFiles { get; } = [];
    public ObservableCollection<ExtensionStat> Extensions { get; } = [];
    public ObservableCollection<FsNode> TopFolders { get; } = [];

    [ObservableProperty] public partial string ScanPath { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelCommand))]
    public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "Pick a drive or folder and click Scan.";
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial FsNode? SelectedNode { get; set; }

    // Summary of "where did my space go"
    [ObservableProperty] public partial long DriveUsed { get; set; }
    [ObservableProperty] public partial long ScannedTotal { get; set; }
    [ObservableProperty] public partial long HiddenSystemFiles { get; set; }
    [ObservableProperty] public partial long? ShadowCopies { get; set; }
    [ObservableProperty] public partial long Unexplained { get; set; }
    [ObservableProperty] public partial int InaccessibleFolders { get; set; }
    [ObservableProperty] public partial bool IsDriveScan { get; set; }
    [ObservableProperty] public partial string SummaryHint { get; set; } = string.Empty;

    public bool IsAdmin => SystemHelper.IsAdministrator;

    [RelayCommand]
    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to analyze" };
        if (Directory.Exists(ScanPath)) dlg.InitialDirectory = ScanPath;
        if (dlg.ShowDialog() == true) ScanPath = dlg.FolderName;
    }

    public void ScanDrive(string root)
    {
        ScanPath = root;
        if (ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var path = ScanPath?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            ProgressText = "That folder does not exist.";
            return;
        }
        if (path.Length == 2 && path[1] == ':') path += "\\";

        IsScanning = true;
        HasResult = false;
        RootItems.Clear(); LargestFiles.Clear(); OldLargeFiles.Clear(); Extensions.Clear(); TopFolders.Clear();
        SelectedNode = null;

        _progress = new ScanProgress();
        _cts = new CancellationTokenSource();
        _timer.Start();
        try
        {
            var result = await DiskScanner.ScanAsync(path, _progress, _cts.Token);
            ApplyResult(result, path);
            ProgressText = $"Scanned {result.Root.FileCount:N0} files in {result.Root.FolderCount:N0} folders " +
                           $"({ByteFormat.Format(result.Root.Size)}) in {result.Elapsed.TotalSeconds:0.0} s." +
                           (result.InaccessibleFolders > 0 ? $"  {result.InaccessibleFolders:N0} folders could not be read." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText = "Scan failed: " + ex.Message;
        }
        finally
        {
            _timer.Stop();
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void UpdateProgress()
    {
        if (_progress is null) return;
        ProgressText = $"Scanning…  {Interlocked.Read(ref _progress.Files):N0} files, " +
                       $"{ByteFormat.Format(Interlocked.Read(ref _progress.Bytes))}   —   {_progress.CurrentPath}";
    }

    private void ApplyResult(ScanResult r, string path)
    {
        r.Root.IsExpanded = true;
        r.Root.IsSelected = true;
        RootItems.Add(r.Root);
        foreach (var f in r.LargestFiles) LargestFiles.Add(f);
        foreach (var f in r.OldLargeFiles) OldLargeFiles.Add(f);
        foreach (var e in r.Extensions.Take(200)) Extensions.Add(e);
        foreach (var d in CollectTopFolders(r.Root, 200)) TopFolders.Add(d);

        ScannedTotal = r.Root.Size;
        InaccessibleFolders = (int)r.InaccessibleFolders;

        var driveRoot = Path.GetPathRoot(path) ?? path;
        IsDriveScan = string.Equals(Path.GetFullPath(path).TrimEnd('\\'), driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        try
        {
            var di = new DriveInfo(driveRoot);
            DriveUsed = di.TotalSize - di.TotalFreeSpace;
        }
        catch { DriveUsed = 0; }

        // hiberfil/pagefile/swapfile are already counted by the scan (we read hidden files) - shown separately for clarity.
        HiddenSystemFiles = SystemHelper.GetRootSystemFileSize(driveRoot, "hiberfil.sys")
                          + SystemHelper.GetRootSystemFileSize(driveRoot, "pagefile.sys")
                          + SystemHelper.GetRootSystemFileSize(driveRoot, "swapfile.sys");
        ShadowCopies = SystemHelper.GetShadowStorage(driveRoot)?.Used;
        Unexplained = IsDriveScan ? Math.Max(0, DriveUsed - ScannedTotal - (ShadowCopies ?? 0)) : 0;

        var hint = new List<string>();
        if (IsDriveScan && Unexplained > 2L * 1024 * 1024 * 1024)
        {
            hint.Add($"{ByteFormat.Format(Unexplained)} of used space was not found in any readable folder.");
            if (!IsAdmin) hint.Add("Restart as administrator to read protected folders (WindowsApps, other users' profiles, System Volume Information size).");
            if (ShadowCopies is null) hint.Add("Restore point size can only be measured as administrator.");
            hint.Add("Other usual suspects: NTFS metadata ($MFT), Reserved Storage (~7 GB). Check the 'Hidden Space' tab.");
        }
        SummaryHint = string.Join(" ", hint);
        HasResult = true;
    }

    /// <summary>Largest folders that do not mostly consist of one big sub folder - a quick "where is it" list.</summary>
    private static List<FsNode> CollectTopFolders(FsNode root, int count)
    {
        var all = new List<FsNode>();
        void Walk(FsNode n, int depth)
        {
            foreach (var c in n.Children)
            {
                if (c.Kind != NodeKind.Directory) continue;
                if (depth >= 1) all.Add(c);
                if (depth < 6) Walk(c, depth + 1);
            }
        }
        Walk(root, 0);
        // Keep folders where no single child dominates (>80 %), so we point at the real location.
        return all.Where(f => f.Size > 100L * 1024 * 1024 &&
                              !f.Children.Any(ch => ch.Kind == NodeKind.Directory && ch.Size > f.Size * 0.8))
                  .OrderByDescending(f => f.Size).Take(count).ToList();
    }

    // ─────────────── item actions ───────────────

    [RelayCommand]
    private void OpenInExplorer(FsNode? node)
    {
        node ??= SelectedNode;
        if (node is not null) SystemHelper.OpenInExplorer(node.FullPath);
    }

    [RelayCommand]
    private void CopyPath(FsNode? node)
    {
        node ??= SelectedNode;
        if (node is null) return;
        try { Clipboard.SetText(node.FullPath); } catch { }
    }

    [RelayCommand]
    private Task RecycleAsync(FsNode? node) => DeleteAsync(node ?? SelectedNode, permanent: false);

    [RelayCommand]
    private Task DeletePermanentlyAsync(FsNode? node) => DeleteAsync(node ?? SelectedNode, permanent: true);

    private static bool IsSameOrUnder(FsNode n, FsNode ancestor)
    {
        for (var p = n; p is not null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    private async Task DeleteAsync(FsNode? node, bool permanent)
    {
        if (node is null || !node.CanDelete) return;
        var path = node.FullPath;

        if (PathGuard.IsProtected(path, out var reason))
        {
            MessageBox.Show($"Storage Manager will not delete this item:\n\n{path}\n\n{reason}",
                "Protected location", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        var warn = PathGuard.GetWarning(path);
        var msg = (permanent ? "PERMANENTLY delete" : "Move to Recycle Bin") +
                  $":\n\n{path}\n\nSize: {ByteFormat.Format(node.Size)}" +
                  (node.Kind == NodeKind.Directory ? $"  ({node.FileCount:N0} files)" : string.Empty) +
                  (warn is null ? string.Empty : "\n\n⚠ " + warn) +
                  (permanent ? "\n\nThis cannot be undone." : "\n\nNote: space is only freed after the Recycle Bin is emptied.");
        if (MessageBox.Show(msg, "Confirm delete", MessageBoxButton.YesNo,
                warn is null ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
            await Task.Run(() =>
            {
                if (node.Kind == NodeKind.Directory)
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
                else
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
            });
            // Drop the node and anything below it from the side lists before detaching it from the tree.
            foreach (var list in new[] { LargestFiles, OldLargeFiles, TopFolders })
                foreach (var stale in list.Where(n => IsSameOrUnder(n, node)).ToList())
                    list.Remove(stale);
            node.Detach();
            ScannedTotal = RootItems.FirstOrDefault()?.Size ?? 0;
            ProgressText = $"Deleted {path} ({ByteFormat.Format(node.Size)}).";
        }
        catch (OperationCanceledException) { ProgressText = "Delete cancelled."; }
        catch (Exception ex)
        {
            MessageBox.Show("Could not delete:\n" + ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
