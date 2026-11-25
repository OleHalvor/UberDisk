using System.IO.Enumeration;

namespace UberDisk.Core.Scanning;

/// <summary>
/// Fallback reader built on .NET's file system enumeration. Used on non-Windows (tests) and as a
/// backup. Size on disk is estimated by rounding the length up to the cluster size.
/// </summary>
public sealed class PortableDirectoryReader : IDirectoryReader
{
    private readonly long _cluster;

    public PortableDirectoryReader(long clusterSize = 4096) => _cluster = clusterSize;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    public bool TryRead(string path, List<RawEntry> sink)
    {
        try
        {
            long cluster = _cluster;
            var e = new FileSystemEnumerable<RawEntry>(path, (ref FileSystemEntry fe) =>
            {
                bool dir = fe.IsDirectory;
                long len = dir ? 0 : fe.Length;
                long alloc = len == 0 ? 0 : (len + cluster - 1) / cluster * cluster;
                bool reparse = (fe.Attributes & FileAttributes.ReparsePoint) != 0;
                return new RawEntry(fe.FileName.ToString(), dir, alloc, reparse);
            }, Options);
            foreach (var r in e) sink.Add(r);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
