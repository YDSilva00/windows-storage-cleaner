using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageManager.Services;

namespace StorageManager.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        RefreshDrives();
    }

    public AnalyzerViewModel Analyzer { get; } = new();
    public CleanupViewModel Junk { get; } = new(Area.JunkCleaner);
    public CleanupViewModel Hidden { get; } = new(Area.HiddenSpace);
    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public bool IsAdmin => SystemHelper.IsAdministrator;
    public string AdminText => IsAdmin
        ? "Running as administrator - all locations can be measured."
        : "Running without admin rights - protected folders (restore points, Windows Temp, Installer...) can't be measured.";

    [ObservableProperty] public partial int SelectedTab { get; set; }

    [RelayCommand]
    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveViewModel.GetFixedDrives()) Drives.Add(d);
    }

    [RelayCommand]
    private void AnalyzeDrive(DriveViewModel? drive)
    {
        if (drive is null) return;
        SelectedTab = 1;
        Analyzer.ScanDrive(drive.Root);
    }

    [RelayCommand]
    private void GoToTab(string? index)
    {
        if (int.TryParse(index, out var i)) SelectedTab = i;
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (SystemHelper.RestartAsAdministrator())
            Application.Current.Shutdown();
    }

    [RelayCommand]
    private static void OpenStorageSettings() => SystemHelper.ShellOpen("ms-settings:storagesense");
}
