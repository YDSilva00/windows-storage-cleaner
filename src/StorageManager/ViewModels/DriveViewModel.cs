using System.IO;

namespace StorageManager.ViewModels;

public sealed class DriveViewModel
{
    public DriveViewModel(DriveInfo d)
    {
        Root = d.Name;
        Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
        Total = d.TotalSize;
        Free = d.AvailableFreeSpace;
        Format = d.DriveFormat;
        IsSystem = string.Equals(d.Name, Services.SystemHelper.SystemDrive, StringComparison.OrdinalIgnoreCase);
    }

    public string Root { get; }
    public string Label { get; }
    public string Format { get; }
    public long Total { get; }
    public long Free { get; }
    public long Used => Total - Free;
    public double PercentUsed => Total > 0 ? (double)Used / Total * 100 : 0;
    public bool IsSystem { get; }
    public string Title => $"{Label} ({Root.TrimEnd('\\')})" + (IsSystem ? "  •  Windows" : string.Empty);

    public static List<DriveViewModel> GetFixedDrives()
    {
        var list = new List<DriveViewModel>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                    list.Add(new DriveViewModel(d));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(d => d.IsSystem).ThenBy(d => d.Root).ToList();
    }
}
