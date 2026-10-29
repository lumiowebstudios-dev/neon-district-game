namespace DeepVault.Core.IO;

public sealed class VolumeInfo
{
    public string? DriveLetter { get; set; }
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long Offset { get; set; } = -1;
    public long Length { get; set; }
    public long FreeBytes { get; set; }
    public string Describe() => $"{DriveLetter ?? "(no letter)"} {Label} {FileSystem}".Trim();
}

/// <summary>A physical disk as presented to the user for identification before scanning.</summary>
public sealed class PhysicalDiskInfo
{
    public int Number { get; set; }
    /// <summary>Device path, e.g. \\.\PhysicalDrive0 or /dev/sda.</summary>
    public string DevicePath { get; set; } = "";
    /// <summary>Stable id used to compare source and destination ("PhysicalDrive0").</summary>
    public string Id { get; set; } = "";
    public string? Model { get; set; }
    public string? Vendor { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    public string? BusType { get; set; }
    public bool Removable { get; set; }
    public long Size { get; set; }
    public int SectorSize { get; set; } = 512;
    public int PhysicalSectorSize { get; set; } = 512;
    public bool IsSystemDisk { get; set; }
    public bool HostsDeepVault { get; set; }
    /// <summary>SMART "failure predicted" flag when the drive reports it (null = unknown).</summary>
    public bool? FailurePredicted { get; set; }
    public List<VolumeInfo> Volumes { get; set; } = new();
    public string? Error { get; set; }

    public bool LooksExternal => Removable || BusType is "USB" or "SD" or "MMC" or "1394";
    public string DisplayName => $"Disk {Number}: {Model ?? "Unknown model"} — {Util.Bytes.FormatSize(Size)}";
}

public interface IDiskEnumerator
{
    IReadOnlyList<PhysicalDiskInfo> Enumerate();
    /// <summary>Opens a disk strictly read-only. Usually requires administrator rights.</summary>
    IBlockSource OpenReadOnly(PhysicalDiskInfo disk);
    bool IsElevated { get; }
}

/// <summary>Linux block devices via /sys (used by the command-line tool for offline/live-USB recovery).</summary>
public sealed class LinuxDiskEnumerator : IDiskEnumerator
{
    public bool IsElevated => Environment.UserName == "root";

    public IReadOnlyList<PhysicalDiskInfo> Enumerate()
    {
        var list = new List<PhysicalDiskInfo>();
        if (!Directory.Exists("/sys/block")) return list;
        int n = 0;
        foreach (var dir in Directory.GetDirectories("/sys/block").OrderBy(x => x))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("zram")) continue;
            string Read(string rel) { try { return File.ReadAllText(Path.Combine(dir, rel)).Trim(); } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } }
            long.TryParse(Read("size"), out var sectors);
            int.TryParse(Read("queue/logical_block_size"), out var lbs);
            list.Add(new PhysicalDiskInfo
            {
                Number = n++, DevicePath = "/dev/" + name, Id = name, Model = Read("device/model"), Vendor = Read("device/vendor"),
                Size = sectors * 512, SectorSize = lbs > 0 ? lbs : 512, Removable = Read("removable") == "1",
            });
        }
        return list;
    }

    public IBlockSource OpenReadOnly(PhysicalDiskInfo disk) => new FileBlockSource(disk.DevicePath, disk.SectorSize);
}
