using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace UberDisk.Core.Drives;

public enum MediaKind { Unknown, Ssd, NvmeSsd, Hdd, Removable, Network, Optical, Ram }

/// <summary>What the drive rail shows about a drive before (and while) it is scanned.</summary>
public sealed record DriveMeta(
    string Root,          // "C:\"
    string Letter,        // "C:"
    string Label,
    MediaKind Kind,
    long TotalBytes,
    long FreeBytes,
    bool IsReady)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public string KindText => Kind switch
    {
        MediaKind.NvmeSsd => "NVMe SSD",
        MediaKind.Ssd => "SSD",
        MediaKind.Hdd => "HDD",
        MediaKind.Removable => "Removable",
        MediaKind.Network => "Network",
        MediaKind.Optical => "Optical",
        MediaKind.Ram => "RAM disk",
        _ => "Local disk",
    };

    /// <summary>Drives the background queue scans on its own. Network and optical drives only scan when picked.</summary>
    public bool AutoScan => IsReady && Kind is not (MediaKind.Network or MediaKind.Optical);

    /// <summary>Spinning disks get few threads: parallel reads only make them seek.</summary>
    public int SuggestedThreads => Kind switch
    {
        MediaKind.Hdd or MediaKind.Optical => 2,
        MediaKind.Network => 8,
        // One and a half threads per physical core: opening folders waits on the file system as much as it uses the
        // CPU. Measured on an 8-core / 16-thread PC, NVMe C: with 597k files and 197k folders, warm cache, three
        // rounds: 8 threads 3.45 to 3.96 s, 12 threads 3.12 to 3.20 s, 16 threads 2.95 to 3.28 s (more variable).
        _ => Math.Clamp(CpuInfo.PhysicalCores * 3 / 2, 4, 16),
    };

    public static IReadOnlyList<DriveMeta> List()
    {
        var list = new List<DriveMeta>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { list.Add(From(d)); }
            catch (Exception) { /* drive vanished while listing */ }
        }
        return list;
    }

    /// <summary>Fresh used/free space for a known drive, keeping its detected type (no hardware probe).</summary>
    public static DriveMeta Refresh(DriveMeta m)
    {
        var d = new DriveInfo(m.Root);
        if (!d.IsReady) return m with { IsReady = false };
        return m with { TotalBytes = d.TotalSize, FreeBytes = d.TotalFreeSpace, IsReady = true };
    }

    public static DriveMeta From(DriveInfo d)
    {
        bool ready = d.IsReady;
        string letter = d.Name.TrimEnd('\\', '/');
        string label = "";
        long total = 0, free = 0;
        if (ready)
        {
            try { label = d.VolumeLabel; } catch { }
            try { total = d.TotalSize; free = d.TotalFreeSpace; } catch { ready = false; }
        }
        if (string.IsNullOrWhiteSpace(label))
            label = d.DriveType switch
            {
                DriveType.Removable => "USB drive",
                DriveType.Network => "Network",
                DriveType.CDRom => "CD/DVD",
                _ => "Local disk",
            };

        var kind = d.DriveType switch
        {
            DriveType.Removable => MediaKind.Removable,
            DriveType.Network => MediaKind.Network,
            DriveType.CDRom => MediaKind.Optical,
            DriveType.Ram => MediaKind.Ram,
            DriveType.Fixed => OperatingSystem.IsWindows() ? StorageProbe.Detect(letter) : MediaKind.Unknown,
            _ => MediaKind.Unknown,
        };
        return new DriveMeta(d.Name, letter, label, kind, total, free, ready);
    }
}

/// <summary>
/// Asks the storage driver whether a volume's disk has a seek penalty (HDD) and what bus it uses (NVMe).
/// Opening the volume with zero access rights needs no admin.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class StorageProbe
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int BusTypeNvme = 0x11;
    private const int BusTypeUsb = 0x07;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    public static MediaKind Detect(string letter)
    {
        try
        {
            using var h = CreateFileW(@"\\.\" + letter, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return MediaKind.Unknown;

            byte* buf = stackalloc byte[1024];
            uint got;

            int bus = -1;
            var q = new STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty };
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, &q, (uint)sizeof(STORAGE_PROPERTY_QUERY), buf, 1024, &got, IntPtr.Zero) && got >= 32)
                bus = *(int*)(buf + 28); // STORAGE_DEVICE_DESCRIPTOR.BusType

            bool? seekPenalty = null;
            q = new STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty };
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, &q, (uint)sizeof(STORAGE_PROPERTY_QUERY), buf, 1024, &got, IntPtr.Zero) && got >= 9)
                seekPenalty = buf[8] != 0; // DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty

            if (bus == BusTypeNvme) return MediaKind.NvmeSsd;
            if (seekPenalty == true) return MediaKind.Hdd;
            if (seekPenalty == false) return bus == BusTypeUsb ? MediaKind.Removable : MediaKind.Ssd;
            return MediaKind.Unknown;
        }
        catch
        {
            return MediaKind.Unknown;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize,
        void* outBuf, uint outSize, uint* returned, IntPtr overlapped);
}

/// <summary>Physical core count (not hyperthreads). Falls back to the logical count off Windows or on error.</summary>
public static class CpuInfo
{
    private static readonly Lazy<int> _cores = new(Count);
    public static int PhysicalCores => _cores.Value;

    private static int Count()
    {
        int logical = Environment.ProcessorCount;
        if (!OperatingSystem.IsWindows()) return logical;
        try
        {
            const int RelationProcessorCore = 0;
            int len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len <= 0) return logical;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return logical;
                // Variable-size records: { int Relationship; int Size; ... }. One record per physical core.
                int cores = 0;
                for (int off = 0; off + 8 <= len;)
                {
                    int size = Marshal.ReadInt32(buf, off + 4);
                    if (size <= 0) break;
                    if (Marshal.ReadInt32(buf, off) == RelationProcessorCore) cores++;
                    off += size;
                }
                return cores > 0 ? cores : logical;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return logical; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int returnedLength);
}
