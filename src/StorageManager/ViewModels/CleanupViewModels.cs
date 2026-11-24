using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageManager.Converters;
using StorageManager.Services;

namespace StorageManager.ViewModels;

public sealed record LocationInfo(string Path, long Size)
{
    public string SizeText => ByteFormat.Format(Size);
}

public sealed partial class CleanupItemViewModel : ObservableObject
{
    private readonly Func<string, bool>? _filter;

    public CleanupItemViewModel(CleanupDefinition def)
    {
        Definition = def;
        _filter = SpaceHogCatalog.BuildFilter(def.FilePatterns);
        IsSelected = def.Safety == Safety.Safe && def.Method != CleanMethod.None;
    }

    public CleanupDefinition Definition { get; }
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string Group => Definition.Group;
    public Safety Safety => Definition.Safety;
    public string SafetyText => Definition.SafetyText;
    public string? Advice => Definition.Advice;
    public string? FixLabel => Definition.FixLabel;
    public bool HasFix => Definition.FixLabel is not null;
    public bool CanClean => Definition.Method != CleanMethod.None;
    public bool NeedsAdminWarning => Definition.NeedsAdmin && !SystemHelper.IsAdministrator;

    public ObservableCollection<LocationInfo> Locations { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? Size { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string SizeText => IsBusy ? "measuring…" : Size switch
    {
        null when Definition.CustomSize is not null && Definition.NeedsAdmin && !SystemHelper.IsAdministrator => "needs admin",
        null when Definition.CustomSize is not null => "unknown",
        null => "—",
        0 when Definition.NeedsAdmin && !SystemHelper.IsAdministrator => "needs admin",
        0 => "0 B",
        long s => ByteFormat.Format(s),
    };

    public async Task MeasureAsync(CancellationToken ct)
    {
        IsBusy = true;
        Status = null;
        try
        {
            var (size, locations) = await Task.Run(() => Measure(ct), ct);
            Size = size;
            Locations.Clear();
            foreach (var l in locations.OrderByDescending(l => l.Size).Take(25)) Locations.Add(l);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private (long? Size, List<LocationInfo> Locations) Measure(CancellationToken ct)
    {
        var d = Definition;
        if (d.CustomSize is not null) return (d.CustomSize(ct), []);

        var locs = new List<LocationInfo>();
        long total = 0;
        foreach (var path in ExistingPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (_filter is not null && d.Method == CleanMethod.None && Directory.Exists(path))
            {
                // List the individual matching files (e.g. each ext4.vhdx) so the user sees exactly where they are.
                foreach (var f in FindFiles(path, ct))
                {
                    locs.Add(new LocationInfo(f.FullName, f.Length));
                    total += f.Length;
                }
            }
            else
            {
                long s = FileCleaner.GetSize(path, ct, _filter);
                if (s > 0) locs.Add(new LocationInfo(path, s));
                total += s;
            }
        }
        return (total, locs);
    }

    private IEnumerable<FileInfo> FindFiles(string dir, CancellationToken ct)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 6,
        };
        foreach (var pattern in Definition.FilePatterns!)
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(dir).EnumerateFiles(pattern, opts).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return f;
            }
        }
    }

    private IEnumerable<string> ExistingPaths() =>
        Definition.Paths.SelectMany(SpaceHogCatalog.ExpandPath).Distinct(StringComparer.OrdinalIgnoreCase);

    public async Task<CleanResult> CleanAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            CleanResult result;
            if (Definition.Method == CleanMethod.RecycleBin)
            {
                long before = Size ?? SystemHelper.GetRecycleBin(null).Size;
                bool ok = await Task.Run(() => SystemHelper.EmptyRecycleBin(null), ct);
                result = ok ? new CleanResult(before, 0, 0) : new CleanResult(0, 0, 1);
            }
            else
            {
                result = await Task.Run(() =>
                {
                    long freed = 0, del = 0, skip = 0;
                    foreach (var p in ExistingPaths().ToList())
                    {
                        var r = FileCleaner.DeleteContents(p, Definition.MinAge, ct, _filter);
                        freed += r.BytesFreed; del += r.FilesDeleted; skip += r.FilesSkipped;
                    }
                    return new CleanResult(freed, del, skip);
                }, ct);
            }
            Status = $"Freed {ByteFormat.Format(result.BytesFreed)}" +
                     (result.FilesDeleted > 0 ? $" ({result.FilesDeleted:N0} files)" : string.Empty) +
                     (result.FilesSkipped > 0 ? $" • {result.FilesSkipped:N0} skipped (in use, too new or protected)" : string.Empty);
            return result;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Open()
    {
        var first = Locations.FirstOrDefault()?.Path ?? ExistingPaths().FirstOrDefault();
        if (first is not null) SystemHelper.OpenInExplorer(first);
        else Status = "No folder found for this item on this PC.";
    }

    [RelayCommand]
    private void OpenLocation(LocationInfo? loc)
    {
        if (loc is not null) SystemHelper.OpenInExplorer(loc.Path);
    }

    [RelayCommand]
    private void CopyAdvice()
    {
        var text = Advice ?? Definition.FixCommand ?? string.Empty;
        if (Locations.Count > 0) text += "\r\n\r\nFound on this PC:\r\n" + string.Join("\r\n", Locations.Select(l => $"{l.Path}  ({l.SizeText})"));
        try { Clipboard.SetText(text); Status = "Copied to clipboard."; } catch { }
    }

    [RelayCommand]
    private void Fix()
    {
        var d = Definition;
        if (d.FixCommand is null) return;
        switch (d.FixCommand)
        {
            case "copy-advice":
                CopyAdviceCommand.Execute(null);
                return;
            case "open-first":
                OpenCommand.Execute(null);
                return;
        }

        if (d.FixIsShellOpen)
        {
            SystemHelper.ShellOpen(d.FixCommand);
            return;
        }

        var confirm = MessageBox.Show(
            $"This will run the following command{(d.FixElevated ? " as administrator" : string.Empty)}:\n\n{d.FixCommand}\n\n" +
            (d.Advice is null ? string.Empty : d.Advice + "\n\n") + "Continue?",
            d.FixLabel ?? "Run command", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        if (d.FixCommand.StartsWith("cleanmgr", StringComparison.OrdinalIgnoreCase))
        {
            // GUI tool - no console needed.
            var parts = d.FixCommand.Split(' ', 2);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "")
                    { UseShellExecute = true, Verb = d.FixElevated ? "runas" : "" });
            }
            catch (System.ComponentModel.Win32Exception) { Status = "Cancelled."; }
            return;
        }

        if (!SystemHelper.RunCommandInConsole(d.FixCommand, d.FixElevated))
            Status = "Cancelled.";
        else
            Status = "Command started in a console window. Click 'Scan' again when it finishes.";
    }
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public CleanupViewModel(Area area)
    {
        Area = area;
        foreach (var d in SpaceHogCatalog.All.Where(d => d.Area == area))
        {
            var vm = new CleanupItemViewModel(d);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CleanupItemViewModel.Size) or nameof(CleanupItemViewModel.IsSelected))
                    RecalcTotals();
            };
            Items.Add(vm);
        }
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupItemViewModel.Group)));
    }

    public Area Area { get; }
    public ObservableCollection<CleanupItemViewModel> Items { get; } = [];
    public ICollectionView View { get; }
    public bool IsJunkCleaner => Area == Area.JunkCleaner;
    public bool IsAdmin => SystemHelper.IsAdministrator;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanSelectedCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial long TotalFound { get; set; }
    [ObservableProperty] public partial long SelectedTotal { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Click Scan to measure each location.";
    [ObservableProperty] public partial bool HasScanned { get; set; }

    private void RecalcTotals()
    {
        TotalFound = Items.Sum(i => i.Size ?? 0);
        SelectedTotal = Items.Where(i => i.IsSelected && i.CanClean).Sum(i => i.Size ?? 0);
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = "Measuring…";
        try
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(Items.Select(async i =>
            {
                await gate.WaitAsync(_cts.Token);
                try { await i.MeasureAsync(_cts.Token); }
                finally { gate.Release(); }
            }));
            HasScanned = true;
            StatusText = IsJunkCleaner
                ? $"Found {ByteFormat.Format(TotalFound)} of junk and caches. Tick the items you want to remove, then click Clean."
                : $"These locations use {ByteFormat.Format(TotalFound)} in total. Expand an item to see how to shrink it safely.";
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CleanSelectedAsync()
    {
        var targets = Items.Where(i => i.IsSelected && i.CanClean).ToList();
        if (targets.Count == 0) { StatusText = "Nothing selected."; return; }

        var list = string.Join("\n", targets.Select(t => $" • {t.Title}  ({t.SizeText})"));
        if (MessageBox.Show($"Delete the contents of these locations?\n\n{list}\n\nClose your browsers and the listed apps first. Files that are in use are skipped.",
                "Confirm clean-up", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        long freed = 0;
        try
        {
            foreach (var t in targets)
            {
                StatusText = $"Cleaning {t.Title}…";
                var r = await t.CleanAsync(_cts.Token);
                freed += r.BytesFreed;
                await t.MeasureAsync(_cts.Token);
            }
            StatusText = $"Done. Freed {ByteFormat.Format(freed)}.";
        }
        catch (OperationCanceledException) { StatusText = $"Cancelled. Freed {ByteFormat.Format(freed)} so far."; }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void SelectAll(string? value)
    {
        bool select = !string.Equals(value, "False", StringComparison.OrdinalIgnoreCase);
        foreach (var i in Items.Where(i => i.CanClean)) i.IsSelected = select;
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();
}
