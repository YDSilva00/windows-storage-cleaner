using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using StorageManager.Models;

namespace StorageManager.Services;

public sealed class ExtensionStat
{
    public required string Extension { get; init; }
    public long Size;
    public long Count;
    public long TotalSize => Size;
    public long FileCount => Count;
}

public sealed class ScanResult
{
    public required FsNode Root { get; init; }
    public required IReadOnlyList<FsNode> LargestFiles { get; init; }
    public required IReadOnlyList<ExtensionStat> Extensions { get; init; }
    public required IReadOnlyList<FsNode> OldLargeFiles { get; init; }
    public long InaccessibleFolders { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Live counters read by the UI on a timer while a scan is running.</summary>
public sealed class ScanProgress
{
    public long Files;
    public long Folders;
    public long Bytes;
    public volatile string CurrentPath = string.Empty;
}

/// <summary>
/// Fast, parallel directory-size scanner (WinDirStat / WizTree style).
/// - Includes hidden and system files (what Explorer hides).
/// - Does not follow junctions / symlinks (avoids double counting "Application Data" style links).
/// - Uses size-on-disk for cloud placeholders, compressed and sparse files.
/// - Files below <see cref="SmallFileThreshold"/> are aggregated per folder to keep memory bounded.
/// </summary>
public sealed class DiskScanner
{
    public const long SmallFileThreshold = 1L * 1024 * 1024;
    private const int ParallelDepth = 3;
    private const int TopFileCount = 500;

    private readonly ScanProgress _progress;
    private readonly CancellationToken _ct;
    private readonly ConcurrentBag<FsNode> _bigFiles = [];
    private readonly ConcurrentDictionary<string, ExtensionStat> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private long _inaccessible;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,          // include Hidden + System
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    private DiskScanner(ScanProgress progress, CancellationToken ct)
    {
        _progress = progress;
        _ct = ct;
    }

    public static Task<ScanResult> ScanAsync(string rootPath, ScanProgress progress, CancellationToken ct) =>
        Task.Run(() => new DiskScanner(progress, ct).Run(rootPath), ct);

    private ScanResult Run(string rootPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rootPath = Path.GetFullPath(rootPath);
        var root = new FsNode(rootPath, NodeKind.Directory, null) { RootPath = rootPath };
        ScanDirectory(root, rootPath, 0);
        _ct.ThrowIfCancellationRequested();

        FinalizeTree(root, 0);

        var largest = _bigFiles.OrderByDescending(f => f.Size).Take(TopFileCount).ToList();
        var cutoff = DateTime.UtcNow.AddYears(-1);
        var old = _bigFiles.Where(f => f.Size >= 50 * 1024 * 1024 && f.LastWriteUtc < cutoff)
                           .OrderByDescending(f => f.Size).Take(TopFileCount).ToList();
        var ext = _extensions.Values.OrderByDescending(e => e.Size).ToList();

        return new ScanResult
        {
            Root = root,
            LargestFiles = largest,
            OldLargeFiles = old,
            Extensions = ext,
            InaccessibleFolders = _inaccessible,
            Elapsed = sw.Elapsed,
        };
    }

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes, DateTime LastWriteUtc);

    private void ScanDirectory(FsNode node, string path, int depth)
    {
        _ct.ThrowIfCancellationRequested();
        _progress.CurrentPath = path;

        var children = new List<FsNode>();
        var subDirs = new List<(FsNode Node, string Path)>();
        long smallSize = 0, smallCount = 0, fileCount = 0, size = 0;
        DateTime lastWrite = DateTime.MinValue;

        try
        {
            var enumerable = new FileSystemEnumerable<Entry>(path,
                static (ref FileSystemEntry e) => new Entry(
                    e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes, e.LastWriteTimeUtc.UtcDateTime),
                Options);

            foreach (var e in enumerable)
            {
                if (e.IsDirectory)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        children.Add(new FsNode(e.Name, NodeKind.ReparsePoint, node));
                        continue;
                    }
                    var dir = new FsNode(e.Name, NodeKind.Directory, node) { LastWriteUtc = e.LastWriteUtc };
                    children.Add(dir);
                    subDirs.Add((dir, Path.Combine(path, e.Name)));
                    continue;
                }

                long len = GetSizeOnDisk(path, e);
                fileCount++;
                size += len;
                if (e.LastWriteUtc > lastWrite) lastWrite = e.LastWriteUtc;
                Interlocked.Increment(ref _progress.Files);
                Interlocked.Add(ref _progress.Bytes, len);
                AddExtension(e.Name, len);

                if (len >= SmallFileThreshold)
                {
                    var file = new FsNode(e.Name, NodeKind.File, node)
                    {
                        Size = len,
                        FileCount = 1,
                        LastWriteUtc = e.LastWriteUtc,
                    };
                    children.Add(file);
                    _bigFiles.Add(file);
                }
                else
                {
                    smallSize += len;
                    smallCount++;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Mark the whole folder as unreadable. Parent replaces the node kind below.
            node.InaccessibleCount = 1;
            Interlocked.Increment(ref _inaccessible);
        }

        if (smallCount > 0)
        {
            children.Add(new FsNode("(small files)", NodeKind.SmallFiles, node)
            {
                Size = smallSize,
                FileCount = smallCount,
            });
        }

        Interlocked.Increment(ref _progress.Folders);

        // Recurse - in parallel near the top of the tree where folders are big and independent.
        if (depth < ParallelDepth && subDirs.Count > 1)
        {
            Parallel.ForEach(subDirs, new ParallelOptions { CancellationToken = _ct },
                sd => ScanDirectory(sd.Node, sd.Path, depth + 1));
        }
        else
        {
            foreach (var sd in subDirs) ScanDirectory(sd.Node, sd.Path, depth + 1);
        }

        // Replace unreadable sub folders with "access denied" markers and roll sizes up.
        long folderCount = 0;
        int inaccessible = node.InaccessibleCount;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            if (c.Kind == NodeKind.Directory)
            {
                if (c.InaccessibleCount == 1 && c.ScanChildren is { Count: 0 })
                {
                    children[i] = new FsNode(c.Name, NodeKind.AccessDenied, node) { InaccessibleCount = 1 };
                    inaccessible++;
                    continue;
                }
                size += c.Size;
                fileCount += c.FileCount;
                folderCount += c.FolderCount + 1;
                inaccessible += c.InaccessibleCount;
                if (c.LastWriteUtc > lastWrite) lastWrite = c.LastWriteUtc;
            }
        }

        node.ScanChildren = children;
        node.Size = size;
        node.FileCount = fileCount;
        node.FolderCount = folderCount;
        node.InaccessibleCount = inaccessible;
        if (lastWrite > node.LastWriteUtc) node.LastWriteUtc = lastWrite;
    }

    private static long GetSizeOnDisk(string dir, in Entry e)
    {
        var a = e.Attributes;
        // Cloud "online-only" files take (almost) no space locally.
        if ((a & (NativeMethods.RecallOnDataAccess | NativeMethods.RecallOnOpen | FileAttributes.Offline)) != 0)
            return 0;
        if ((a & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            return NativeMethods.GetCompressedFileSize(Path.Combine(dir, e.Name)) ?? e.Length;
        return e.Length;
    }

    private void AddExtension(string name, long len)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext) || ext.Length > 12) ext = "(no extension)";
        var stat = _extensions.GetOrAdd(ext, static k => new ExtensionStat { Extension = k.ToLowerInvariant() });
        Interlocked.Add(ref stat.Size, len);
        Interlocked.Increment(ref stat.Count);
    }

    private void FinalizeTree(FsNode node, int depth)
    {
        node.FinalizeChildren();
        var dirs = node.Children.Where(c => c.Kind == NodeKind.Directory).ToList();
        if (depth < ParallelDepth)
            Parallel.ForEach(dirs, d => FinalizeTree(d, depth + 1));
        else
            foreach (var d in dirs) FinalizeTree(d, depth + 1);
    }
}
