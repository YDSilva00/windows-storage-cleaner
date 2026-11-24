using System.IO;
using System.Runtime.InteropServices;

namespace StorageManager.Services;

internal static partial class NativeMethods
{
    // File attributes used by cloud providers (OneDrive, Google Drive, Dropbox) for online-only placeholders.
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [Flags]
    public enum RecycleFlags : uint
    {
        NoConfirmation = 0x1,
        NoProgressUI = 0x2,
        NoSound = 0x4,
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, RecycleFlags dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    /// <summary>Real allocated size for compressed / sparse files. Returns null on failure.</summary>
    public static long? GetCompressedFileSize(string path)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        if (low == 0xFFFFFFFF && Marshal.GetLastPInvokeError() != 0) return null;
        return ((long)high << 32) | low;
    }

    public static (long Size, long Items) QueryRecycleBin(string? root)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(root, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }
}
