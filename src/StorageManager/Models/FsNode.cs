using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace StorageManager.Models;

public enum NodeKind
{
    Directory,
    File,
    /// <summary>Aggregate of many small files in one folder (keeps memory low on huge drives).</summary>
    SmallFiles,
    /// <summary>Junction / symlink - not followed, so space is not double counted.</summary>
    ReparsePoint,
    /// <summary>Folder that could not be read (needs admin, or SYSTEM-only ACL).</summary>
    AccessDenied,
}

/// <summary>
/// One entry in the scanned file system tree. Sizes are "size on disk" for cloud placeholders,
/// compressed and sparse files, so OneDrive online-only files don't show up as huge.
/// </summary>
public sealed class FsNode : INotifyPropertyChanged
{
    private long _size;
    private bool _isExpanded;
    private bool _isSelected;

    public FsNode(string name, NodeKind kind, FsNode? parent)
    {
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public NodeKind Kind { get; }
    public FsNode? Parent { get; private set; }

    /// <summary>Only set for the root node; children compute their path from the parent chain.</summary>
    public string? RootPath { get; init; }

    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            OnPropertyChanged(nameof(Size));
            OnPropertyChanged(nameof(PercentOfParent));
        }
    }

    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public int InaccessibleCount { get; set; }

    /// <summary>Mutable list used while scanning; replaced by <see cref="Children"/> afterwards.</summary>
    internal List<FsNode>? ScanChildren { get; set; }

    public ObservableCollection<FsNode> Children { get; private set; } = [];

    public bool IsDirectoryLike => Kind is NodeKind.Directory;
    public bool CanDelete => Kind is NodeKind.Directory or NodeKind.File;

    public double PercentOfParent =>
        Parent is { Size: > 0 } p ? (double)Size / p.Size * 100.0 : 100.0;

    public string FullPath
    {
        get
        {
            if (RootPath is not null) return RootPath;
            if (Kind == NodeKind.SmallFiles) return Parent?.FullPath ?? string.Empty;
            return Path.Combine(Parent?.FullPath ?? string.Empty, Name);
        }
    }

    public string Icon => Kind switch
    {
        NodeKind.Directory => "",     // folder
        NodeKind.File => "",          // document
        NodeKind.SmallFiles => "",    // list
        NodeKind.ReparsePoint => "",  // link
        NodeKind.AccessDenied => "",  // lock
        _ => "",
    };

    public string DisplayName => Kind switch
    {
        NodeKind.SmallFiles => $"[{FileCount:N0} files smaller than 1 MB]",
        NodeKind.ReparsePoint => $"{Name}  (link - not followed)",
        NodeKind.AccessDenied => $"{Name}  (access denied - run as administrator)",
        _ => Name,
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
    }

    /// <summary>Sorts children by size (largest first) and freezes them into an observable collection.</summary>
    internal void FinalizeChildren()
    {
        if (ScanChildren is null) return;
        ScanChildren.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        Children = new ObservableCollection<FsNode>(ScanChildren);
        ScanChildren = null;
    }

    /// <summary>Removes this node from the tree and subtracts its size from every ancestor.</summary>
    public void Detach()
    {
        var parent = Parent;
        if (parent is null) return;
        parent.Children.Remove(this);
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size -= Size;
            if (Kind == NodeKind.File) p.FileCount--;
            else p.FileCount -= FileCount;
        }
        Parent = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
