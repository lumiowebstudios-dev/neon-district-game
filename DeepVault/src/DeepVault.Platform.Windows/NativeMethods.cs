using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DeepVault.Platform.Windows;

/// <summary>
/// The complete set of Win32 calls DeepVault makes against disks. Deliberately minimal and
/// read-only: devices are opened with GENERIC_READ (or no access at all, for queries) and only
/// query IOCTLs are defined. There is no binding for any write, lock, dismount or layout API.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint NO_ACCESS = 0;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // Query-only control codes.
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public nint Internal;
        public nint InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public nint hEvent;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle h, byte* buffer, uint toRead, out uint read, OVERLAPPED* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;   // 0 = StorageDeviceProperty, 6 = StorageAccessAlignmentProperty
        public int QueryType;    // 0 = PropertyStandardQuery
        public byte Additional;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_NUMBER
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY
    {
        public long Cylinders;
        public int MediaType;
        public int TracksPerCylinder;
        public int SectorsPerTrack;
        public int BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PREDICT_FAILURE
    {
        public int PredictFailure;
        public fixed byte VendorSpecific[512];
    }

    public static string BusName(int bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
        11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "FileBackedVirtual", 16 => "Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "Unknown",
    };
}
