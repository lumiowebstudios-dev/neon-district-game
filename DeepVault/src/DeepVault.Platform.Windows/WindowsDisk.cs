using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using DeepVault.Core.IO;
using DeepVault.Core.Restore;
using Microsoft.Win32.SafeHandles;
using static DeepVault.Platform.Windows.NativeMethods;

namespace DeepVault.Platform.Windows;

/// <summary>
/// Read-only access to a physical disk (\\.\PhysicalDriveN). The handle is opened with GENERIC_READ
/// only, so Windows itself rejects any write through it. Reads are sector-aligned through an aligned
/// bounce buffer, as raw devices require.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsDiskSource : IBlockSource
{
    private readonly SafeFileHandle _h;
    private readonly int _ss;
    private byte* _bounce;
    private int _bounceSize;
    private readonly object _lock = new();

    public long Length { get; }
    public int SectorSize => _ss;
    public string Description { get; }

    public WindowsDiskSource(string devicePath, string description)
    {
        _h = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, 0);
        if (_h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            _h.Dispose();
            throw err == ERROR_ACCESS_DENIED
                ? new UnauthorizedAccessException("Reading a whole disk requires administrator rights. Restart DeepVault as administrator.")
                : new IOException($"Cannot open {devicePath} (Win32 error {err}).");
        }
        Description = description;
        long len = 0;
        int ss = 512;
        var geo = WindowsDiskEnumerator.QueryGeometry(_h);
        if (geo != null) { ss = geo.Value.ss; len = geo.Value.size; }
        long l2 = 0;
        if (DeviceIoControl(_h, IOCTL_DISK_GET_LENGTH_INFO, null, 0, &l2, 8, out _, 0) && l2 > 0) len = l2;
        if (len <= 0) { _h.Dispose(); throw new IOException("Could not determine the disk size."); }
        _ss = ss is 512 or 1024 or 2048 or 4096 ? ss : 512;
        Length = len;
    }

    private void EnsureBounce(int size)
    {
        if (_bounceSize >= size) return;
        if (_bounce != null) NativeMemory.AlignedFree(_bounce);
        _bounce = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        _bounceSize = size;
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length || buffer.Length == 0) return 0;
        if (buffer.Length > Length - offset) buffer = buffer.Slice(0, (int)(Length - offset));
        lock (_lock)
        {
            long alignedStart = offset / _ss * _ss;
            long alignedEnd = (offset + buffer.Length + _ss - 1) / _ss * _ss;
            // Disk lengths are whole sectors, so alignedEnd never exceeds Length.
            int total = (int)(alignedEnd - alignedStart);
            EnsureBounce(total);
            var ov = new OVERLAPPED { Offset = (uint)alignedStart, OffsetHigh = (uint)(alignedStart >> 32) };
            if (!ReadFile(_h, _bounce, (uint)total, out uint got, &ov))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new SourceReadException(offset, buffer.Length, $"Disk read error {err} at byte {offset}");
            }
            int skip = (int)(offset - alignedStart);
            int n = (int)Math.Min(buffer.Length, Math.Max(0, (long)got - skip));
            new Span<byte>(_bounce + skip, n).CopyTo(buffer);
            return n;
        }
    }

    public void Dispose()
    {
        _h.Dispose();
        if (_bounce != null) { NativeMemory.AlignedFree(_bounce); _bounce = null; }
    }
}

[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsDiskEnumerator : IDiskEnumerator
{
    public bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Folder whose disk should be flagged as hosting DeepVault's own data (e.g. the project folder).</summary>
    public string? WorkFolder { get; set; }

    internal static (long size, int ss)? QueryGeometry(SafeFileHandle h)
    {
        var buf = stackalloc byte[256];
        if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, buf, 256, out _, 0)) return null;
        var g = *(DISK_GEOMETRY*)buf;
        long size = *(long*)(buf + sizeof(DISK_GEOMETRY));
        return (size, g.BytesPerSector);
    }

    private static string? ReadAnsi(byte* b, uint len, uint off)
    {
        if (off == 0 || off >= len) return null;
        int end = (int)off;
        while (end < len && b[end] != 0) end++;
        var s = Encoding.ASCII.GetString(b + off, end - (int)off).Trim();
        return s.Length == 0 ? null : s;
    }

    public IReadOnlyList<PhysicalDiskInfo> Enumerate()
    {
        var volumes = EnumerateVolumes();
        string? sysLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        string? workLetter = WorkFolder != null ? Path.GetPathRoot(Path.GetFullPath(WorkFolder))?.TrimEnd('\\') : null;
        string? appLetter = Path.GetPathRoot(AppContext.BaseDirectory)?.TrimEnd('\\');
        var list = new List<PhysicalDiskInfo>();
        for (int n = 0; n < 64; n++)
        {
            string path = $@"\\.\PhysicalDrive{n}";
            using var h = CreateFile(path, NO_ACCESS, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (h.IsInvalid) continue;
            var d = new PhysicalDiskInfo { Number = n, DevicePath = path, Id = $"PhysicalDrive{n}" };
            var q = new STORAGE_PROPERTY_QUERY { PropertyId = 0, QueryType = 0 };
            var buf = stackalloc byte[4096];
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, &q, (uint)sizeof(STORAGE_PROPERTY_QUERY), buf, 4096, out uint ret, 0) && ret >= 36)
            {
                // STORAGE_DEVICE_DESCRIPTOR
                d.Removable = buf[10] != 0;
                d.Vendor = ReadAnsi(buf, ret, *(uint*)(buf + 12));
                d.Model = ReadAnsi(buf, ret, *(uint*)(buf + 16));
                d.Firmware = ReadAnsi(buf, ret, *(uint*)(buf + 20));
                d.SerialNumber = ReadAnsi(buf, ret, *(uint*)(buf + 24));
                d.BusType = BusName(*(int*)(buf + 28));
                if (d.Vendor != null && d.Model != null && !d.Model.StartsWith(d.Vendor, StringComparison.OrdinalIgnoreCase)) d.Model = d.Vendor + " " + d.Model;
            }
            var geo = QueryGeometry(h);
            if (geo != null) { d.Size = geo.Value.size; d.SectorSize = geo.Value.ss; }
            var align = new STORAGE_PROPERTY_QUERY { PropertyId = 6, QueryType = 0 };
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, &align, (uint)sizeof(STORAGE_PROPERTY_QUERY), buf, 4096, out ret, 0) && ret >= 20)
                d.PhysicalSectorSize = *(int*)(buf + 16);
            STORAGE_PREDICT_FAILURE pf;
            if (DeviceIoControl(h, IOCTL_STORAGE_PREDICT_FAILURE, null, 0, &pf, (uint)sizeof(STORAGE_PREDICT_FAILURE), out _, 0))
                d.FailurePredicted = pf.PredictFailure != 0;
            foreach (var (letter, disk, vol) in volumes)
                if (disk == n) d.Volumes.Add(vol);
            d.IsSystemDisk = d.Volumes.Any(v => string.Equals(v.DriveLetter, sysLetter, StringComparison.OrdinalIgnoreCase));
            d.HostsDeepVault = d.Volumes.Any(v => string.Equals(v.DriveLetter, workLetter, StringComparison.OrdinalIgnoreCase) || string.Equals(v.DriveLetter, appLetter, StringComparison.OrdinalIgnoreCase));
            d.Volumes.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            list.Add(d);
        }
        return list;
    }

    /// <summary>Maps each lettered volume to the physical disk(s) it lives on.</summary>
    internal static List<(string letter, int disk, VolumeInfo vol)> EnumerateVolumes()
    {
        var res = new List<(string, int, VolumeInfo)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string letter = drive.Name.TrimEnd('\\');
            if (letter.Length != 2) continue;
            foreach (var (disk, off, len) in DiskExtents(letter))
            {
                var v = new VolumeInfo { DriveLetter = letter, Offset = off, Length = len };
                try
                {
                    if (drive.IsReady)
                    {
                        v.Label = drive.VolumeLabel;
                        v.FileSystem = drive.DriveFormat;
                        v.FreeBytes = drive.AvailableFreeSpace;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                res.Add((letter, disk, v));
            }
        }
        return res;
    }

    internal static List<(int disk, long offset, long length)> DiskExtents(string letter)
    {
        var res = new List<(int, long, long)>();
        using var h = CreateFile($@"\\.\{letter}", NO_ACCESS, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return res;
        var buf = stackalloc byte[1024];
        if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, 1024, out uint ret, 0) || ret < 8) return res;
        int count = *(int*)buf;
        for (int i = 0; i < count && 8 + (i + 1) * 24 <= ret; i++)
        {
            byte* e = buf + 8 + i * 24;
            res.Add((*(int*)e, *(long*)(e + 8), *(long*)(e + 16)));
        }
        return res;
    }

    public IBlockSource OpenReadOnly(PhysicalDiskInfo disk) => new WindowsDiskSource(disk.DevicePath, disk.DisplayName);

    /// <summary>Relaunches DeepVault elevated (UAC prompt). Returns false if the user declined.</summary>
    public static bool RestartElevated(string? args = null)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args ?? "") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

/// <summary>Destination inspection with physical-disk mapping, so DeepVault can refuse to restore onto the source disk.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDestinationInspector : IDestinationInspector
{
    private readonly BasicDestinationInspector _basic = new();

    public DestinationInfo Inspect(string folder)
    {
        var info = _basic.Inspect(folder);
        var letter = Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\');
        if (letter is { Length: 2 })
        {
            foreach (var (disk, _, _) in WindowsDiskEnumerator.DiskExtents(letter)) info.PhysicalDisks.Add($"PhysicalDrive{disk}");
            if (GetDiskFreeSpace(letter + "\\", out uint spc, out uint bps, out _, out _)) info.ClusterSize = (int)(spc * bps);
        }
        else if (folder.StartsWith(@"\\")) info.BusType = "Network";
        return info;
    }
}
