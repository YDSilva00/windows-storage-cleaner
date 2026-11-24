using System.IO;
using System.IO.Enumeration;

namespace StorageManager.Services;

public readonly record struct CleanResult(long BytesFreed, long FilesDeleted, long FilesSkipped);

/// <summary>Size calculation and safe deletion for cleanup targets.</summary>
public static class FileCleaner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Total size of everything under <paramref name="path"/> (or the file itself). Never follows links.</summary>
    public static long GetSize(string path, CancellationToken ct, Func<string, bool>? fileFilter = null)
    {
        try
        {
            if (File.Exists(path))
            {
                if (fileFilter is not null && !fileFilter(Path.GetFileName(path))) return 0;
                return new FileInfo(path).Length;
            }
            if (!Directory.Exists(path)) return 0;
            return SizeOfDirectory(path, ct, fileFilter);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long SizeOfDirectory(string path, CancellationToken ct, Func<string, bool>? fileFilter)
    {
        ct.ThrowIfCancellationRequested();
        long total = 0;
        var subDirs = new List<string>();
        try
        {
            var e = new FileSystemEnumerable<(string Name, bool Dir, long Len, FileAttributes Attr)>(path,
                static (ref FileSystemEntry x) => (x.FileName.ToString(), x.IsDirectory, x.Length, x.Attributes), Options);
            foreach (var (name, dir, len, attr) in e)
            {
                if (dir)
                {
                    if ((attr & FileAttributes.ReparsePoint) == 0) subDirs.Add(Path.Combine(path, name));
                }
                else if (fileFilter is null || fileFilter(name))
                {
                    total += len;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var d in subDirs) total += SizeOfDirectory(d, ct, fileFilter);
        return total;
    }

    /// <summary>
    /// Deletes the contents of a folder (the folder itself is kept), or a single file.
    /// Locked / in-use files are skipped. Junctions are never followed.
    /// </summary>
    public static CleanResult DeleteContents(string path, TimeSpan minAge, CancellationToken ct,
        Func<string, bool>? fileFilter = null)
    {
        long freed = 0, deleted = 0, skipped = 0;
        var cutoff = DateTime.UtcNow - minAge;

        if (File.Exists(path))
        {
            if (fileFilter is null || fileFilter(Path.GetFileName(path)))
                TryDeleteFile(new FileInfo(path), cutoff, ref freed, ref deleted, ref skipped);
            return new CleanResult(freed, deleted, skipped);
        }
        if (!Directory.Exists(path)) return default;

        DeleteDir(new DirectoryInfo(path), cutoff, isRoot: true, ct, fileFilter, ref freed, ref deleted, ref skipped);
        return new CleanResult(freed, deleted, skipped);
    }

    private static void DeleteDir(DirectoryInfo dir, DateTime cutoff, bool isRoot, CancellationToken ct,
        Func<string, bool>? fileFilter, ref long freed, ref long deleted, ref long skipped)
    {
        ct.ThrowIfCancellationRequested();
        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos("*", Options).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; return; }

        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo sub)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue; // never walk into a junction / symlink target
                DeleteDir(sub, cutoff, isRoot: false, ct, fileFilter, ref freed, ref deleted, ref skipped);
            }
            else if (entry is FileInfo file && (fileFilter is null || fileFilter(file.Name)))
            {
                TryDeleteFile(file, cutoff, ref freed, ref deleted, ref skipped);
            }
        }

        if (!isRoot && fileFilter is null)
        {
            try
            {
                dir.Refresh();
                if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDeleteFile(FileInfo file, DateTime cutoff, ref long freed, ref long deleted, ref long skipped)
    {
        try
        {
            if (file.LastWriteTimeUtc > cutoff) { skipped++; return; }
            long len = file.Length;
            if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                file.Attributes = FileAttributes.Normal;
            file.Delete();
            freed += len;
            deleted++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped++; // in use or protected
        }
    }
}
