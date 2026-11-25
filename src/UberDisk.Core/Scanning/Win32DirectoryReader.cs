using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace UberDisk.Core.Scanning;

/// <summary>
/// Reads a folder with GetFileInformationByHandleEx(FileFullDirectoryInfo) into a 128 KB buffer per call.
/// Each call returns hundreds of entries with the real allocated size (size on disk) straight from NTFS,
/// so there is no per-file system call: compression, sparse files and cloud placeholders are counted
/// correctly, and the call needs no admin rights. Paths use the \\?\ prefix, so long paths and Unicode
/// names work.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class Win32DirectoryReader : IDirectoryReader
{
    private const int BufferSize = 128 * 1024;

    [ThreadStatic] private static byte[]? t_buffer;

    public bool TryRead(string path, List<RawEntry> sink)
    {
        using var h = Native.CreateFileW(ToLongPath(path), Native.FILE_LIST_DIRECTORY,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return false;

        var buf = t_buffer ??= GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);
        fixed (byte* p = buf)
        {
            bool first = true;
            while (true)
            {
                int cls = first ? Native.FileFullDirectoryRestartInfo : Native.FileFullDirectoryInfo;
                first = false;
                if (!Native.GetFileInformationByHandleEx(h, cls, p, BufferSize))
                {
                    int err = Marshal.GetLastWin32Error();
                    // ERROR_NO_MORE_FILES ends the listing. Anything else on the first call means we can't list it.
                    return err == Native.ERROR_NO_MORE_FILES || sink.Count > 0 || err == Native.ERROR_FILE_NOT_FOUND;
                }

                int offset = 0;
                while (true)
                {
                    var e = (Native.FILE_FULL_DIR_INFO*)(p + offset);
                    int nameLen = (int)e->FileNameLength / 2;
                    char* name = (char*)((byte*)e + Native.FileNameOffset);
                    if (!IsDots(name, nameLen))
                    {
                        uint attr = e->FileAttributes;
                        bool dir = (attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                        bool reparse = (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                        if (dir)
                            sink.Add(new RawEntry(new string(name, 0, nameLen), true, 0, reparse));
                        else
                            sink.Add(new RawEntry(null, false, e->AllocationSize, reparse, NameArena.Add(name, nameLen), nameLen));
                    }
                    if (e->NextEntryOffset == 0) break;
                    offset += (int)e->NextEntryOffset;
                }
            }
        }
    }

    private static bool IsDots(char* n, int len) =>
        (len == 1 && n[0] == '.') || (len == 2 && n[0] == '.' && n[1] == '.');

    internal static string ToLongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    private static class Native
    {
        public const uint FILE_LIST_DIRECTORY = 0x0001;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        public const int FileFullDirectoryInfo = 14;
        public const int FileFullDirectoryRestartInfo = 15;
        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_NO_MORE_FILES = 18;
        public const int FileNameOffset = 68;

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_FULL_DIR_INFO
        {
            public uint NextEntryOffset;
            public uint FileIndex;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public long EndOfFile;
            public long AllocationSize;
            public uint FileAttributes;
            public uint FileNameLength;
            public uint EaSize;
            // WCHAR FileName[] follows at offset 68
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, void* buffer, int size);
    }
}
