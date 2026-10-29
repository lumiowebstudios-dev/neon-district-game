using DeepVault.Core.IO;
using DeepVault.Core.Util;

namespace DeepVault.Core.Partitions;

public enum PartitionScheme { None, Mbr, Gpt }

public enum PartitionStatus
{
    /// <summary>Listed in the partition table and its boot sector is valid.</summary>
    Confirmed,
    /// <summary>Listed in the partition table but no recognised/valid file system was found.</summary>
    InTableNoFileSystem,
    /// <summary>Not in the partition table; found by scanning for boot sectors. Unconfirmed until analysed.</summary>
    Candidate,
    /// <summary>No partition table; the whole device is a single volume ("superfloppy").</summary>
    WholeDevice,
}

public sealed class PartitionInfo
{
    public int Index { get; set; }
    public PartitionScheme Scheme { get; set; }
    public PartitionStatus Status { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public string TypeDescription { get; set; } = "";
    public string TypeId { get; set; } = "";
    public string? Name { get; set; }
    public FsKind FileSystem { get; set; }
    public BootSectorInfo? Boot { get; set; }
    /// <summary>For candidates: how the partition was found ("primary boot sector", "backup boot sector").</summary>
    public string? Evidence { get; set; }

    public long End => Offset + Length;

    public override string ToString() =>
        $"#{Index} {Scheme} {Status} @{Offset} len={Bytes.FormatSize(Length)} {TypeDescription} {FileSystemProbe.Describe(FileSystem)}";
}

public sealed class PartitionTableResult
{
    public PartitionScheme Scheme { get; set; }
    public List<PartitionInfo> Partitions { get; } = new();
    public List<string> Issues { get; } = new();
    public Guid? DiskGuid { get; set; }
    public uint? MbrSignature { get; set; }
}

/// <summary>Read-only partition table analysis (MBR with extended/logical partitions, and GPT).</summary>
public static class PartitionTableParser
{
    private static readonly Dictionary<Guid, string> GptTypes = new()
    {
        [new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")] = "Basic data",
        [new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")] = "EFI System",
        [new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")] = "Microsoft Reserved",
        [new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")] = "Windows Recovery",
        [new Guid("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3")] = "LDM metadata",
        [new Guid("AF9B60A0-1431-4F62-BC68-3311714A69AD")] = "LDM data",
        [new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D")] = "Storage Spaces",
        [new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4")] = "Linux filesystem",
        [new Guid("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F")] = "Linux swap",
        [new Guid("48465300-0000-11AA-AA11-00306543ECAC")] = "Apple HFS+",
        [new Guid("7C3457EF-0000-11AA-AA11-00306543ECAC")] = "Apple APFS",
    };

    private static string MbrTypeName(byte t) => t switch
    {
        0x01 => "FAT12",
        0x04 or 0x06 or 0x0E => "FAT16",
        0x05 or 0x0F or 0x85 => "Extended",
        0x07 => "NTFS/exFAT/HPFS",
        0x0B or 0x0C => "FAT32",
        0x11 or 0x14 or 0x16 or 0x1B or 0x1C or 0x1E => "Hidden FAT",
        0x17 => "Hidden NTFS",
        0x27 => "Windows Recovery (hidden NTFS)",
        0x42 => "Windows dynamic disk (LDM)",
        0x82 => "Linux swap",
        0x83 => "Linux",
        0xEE => "GPT protective",
        0xEF => "EFI System",
        _ => $"Type 0x{t:X2}",
    };

    public static PartitionTableResult Parse(IBlockSource src)
    {
        var result = new PartitionTableResult();
        byte[] mbr;
        try { mbr = src.ReadBytes(0, 512); }
        catch (IOException ex)
        {
            result.Issues.Add($"Sector 0 is unreadable: {ex.Message}");
            return result;
        }

        bool hasMbrSig = mbr[510] == 0x55 && mbr[511] == 0xAA;
        bool protective = hasMbrSig && Enumerable.Range(0, 4).Any(i => mbr[0x1BE + i * 16 + 4] == 0xEE);

        // Try GPT at LBA1 for both 512 and 4096-byte sectors (also covers a damaged protective MBR).
        foreach (int ss in new[] { src.SectorSize, 512, 4096 }.Distinct())
        {
            if (TryParseGpt(src, ss, result)) return result;
        }
        if (protective) result.Issues.Add("Protective MBR present but no valid GPT header/backup was found.");

        if (!hasMbrSig)
        {
            result.Issues.Add("No MBR boot signature at sector 0.");
            TryWholeDevice(src, result);
            return result;
        }

        // A volume boot sector at LBA 0 means no partition table ("superfloppy" layout).
        var asBoot = FileSystemProbe.Parse(mbr);
        if (asBoot != null && FileSystemProbe.ValidateVolume(src, 0, asBoot))
        {
            TryWholeDevice(src, result);
            return result;
        }

        result.Scheme = PartitionScheme.Mbr;
        result.MbrSignature = Bytes.U32(mbr, 0x1B8);
        int idx = 1;
        for (int i = 0; i < 4; i++)
        {
            var e = mbr.AsSpan(0x1BE + i * 16, 16);
            byte type = e[4];
            long lba = Bytes.U32(e, 8);
            long count = Bytes.U32(e, 12);
            if (type == 0 || count == 0) continue;
            if (type is 0x05 or 0x0F or 0x85)
            {
                ParseExtended(src, lba, result, ref idx);
                continue;
            }
            AddPartition(src, result, idx++, PartitionScheme.Mbr, lba * 512, count * 512, MbrTypeName(type), $"0x{type:X2}", null);
        }
        if (result.Partitions.Count == 0) result.Issues.Add("MBR contains no partition entries.");
        return result;
    }

    private static void TryWholeDevice(IBlockSource src, PartitionTableResult result)
    {
        var bs = FileSystemProbe.Probe(src, 0);
        if (bs != null && FileSystemProbe.ValidateVolume(src, 0, bs))
        {
            result.Scheme = PartitionScheme.None;
            result.Partitions.Add(new PartitionInfo
            {
                Index = 1, Scheme = PartitionScheme.None, Status = PartitionStatus.WholeDevice,
                Offset = 0, Length = Math.Min(bs.TotalBytes + (bs.Kind == FsKind.Ntfs ? bs.BytesPerSector : 0), src.Length),
                FileSystem = bs.Kind, Boot = bs, TypeDescription = "Whole device (no partition table)",
            });
        }
    }

    private static void ParseExtended(IBlockSource src, long extStartLba, PartitionTableResult result, ref int idx)
    {
        long ebrLba = extStartLba;
        var seen = new HashSet<long>();
        for (int guard = 0; guard < 128 && seen.Add(ebrLba); guard++)
        {
            byte[] ebr;
            try { ebr = src.ReadBytes(ebrLba * 512, 512); }
            catch (IOException) { result.Issues.Add($"Extended boot record at LBA {ebrLba} is unreadable."); return; }
            if (ebr[510] != 0x55 || ebr[511] != 0xAA) { result.Issues.Add($"Invalid EBR at LBA {ebrLba}."); return; }
            var e1 = ebr.AsSpan(0x1BE, 16);
            var e2 = ebr.AsSpan(0x1CE, 16);
            if (e1[4] != 0 && Bytes.U32(e1, 12) != 0)
                AddPartition(src, result, idx++, PartitionScheme.Mbr, (ebrLba + Bytes.U32(e1, 8)) * 512,
                    (long)Bytes.U32(e1, 12) * 512, MbrTypeName(e1[4]) + " (logical)", $"0x{e1[4]:X2}", null);
            if (e2[4] == 0 || Bytes.U32(e2, 12) == 0) return;
            ebrLba = extStartLba + Bytes.U32(e2, 8);
        }
    }

    private static bool TryParseGpt(IBlockSource src, int ss, PartitionTableResult result)
    {
        if (src.Length < ss * 3L) return false;
        var primary = ReadGptHeader(src, ss, ss);
        byte[]? header = primary;
        string? usedCopy = "primary";
        if (header == null)
        {
            // Backup header lives at the last LBA.
            long lastLba = src.Length / ss - 1;
            header = ReadGptHeader(src, lastLba * ss, ss);
            usedCopy = "backup";
            if (header == null) return false;
            result.Issues.Add("Primary GPT header is missing or corrupt; the backup GPT header was used.");
        }
        long entriesLba = Bytes.I64(header, 0x48);
        int count = (int)Bytes.U32(header, 0x50);
        int esize = (int)Bytes.U32(header, 0x54);
        uint entriesCrc = Bytes.U32(header, 0x58);
        if (count <= 0 || count > 4096 || esize < 128 || esize > 4096) return false;
        byte[] entries;
        try { entries = src.ReadBytes(entriesLba * ss, count * esize); }
        catch (IOException) { result.Issues.Add("GPT partition entry array is unreadable."); return false; }
        if (Crc32.Compute(entries) != entriesCrc)
            result.Issues.Add($"GPT partition entry array CRC mismatch ({usedCopy} copy); entries may be damaged.");
        result.Scheme = PartitionScheme.Gpt;
        result.DiskGuid = new Guid(header.AsSpan(0x38, 16));
        int idx = 1;
        for (int i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * esize, esize);
            var type = new Guid(e.Slice(0, 16));
            if (type == Guid.Empty) continue;
            long first = Bytes.I64(e, 32);
            long last = Bytes.I64(e, 40);
            if (last < first) { result.Issues.Add($"GPT entry {i} has an invalid LBA range."); continue; }
            string name = System.Text.Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
            AddPartition(src, result, idx++, PartitionScheme.Gpt, first * ss, (last - first + 1) * ss,
                GptTypes.TryGetValue(type, out var tn) ? tn : "Unknown type", type.ToString(), name);
        }
        return true;
    }

    private static byte[]? ReadGptHeader(IBlockSource src, long offset, int ss)
    {
        try
        {
            var h = src.ReadBytes(offset, ss);
            if (!h.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) return null;
            int hsize = (int)Bytes.U32(h, 0x0C);
            if (hsize < 92 || hsize > ss) return null;
            uint crc = Bytes.U32(h, 0x10);
            var copy = h.AsSpan(0, hsize).ToArray();
            copy[0x10] = copy[0x11] = copy[0x12] = copy[0x13] = 0;
            if (Crc32.Compute(copy) != crc) return null;
            return h;
        }
        catch (IOException) { return null; }
    }

    private static void AddPartition(IBlockSource src, PartitionTableResult result, int idx, PartitionScheme scheme,
        long offset, long length, string typeDesc, string typeId, string? name)
    {
        var p = new PartitionInfo
        {
            Index = idx, Scheme = scheme, Offset = offset, Length = length,
            TypeDescription = typeDesc, TypeId = typeId, Name = string.IsNullOrEmpty(name) ? null : name,
        };
        if (offset + 512 > src.Length)
        {
            result.Issues.Add($"Partition {idx} starts beyond the end of the disk.");
            p.Status = PartitionStatus.InTableNoFileSystem;
        }
        else
        {
            var bs = FileSystemProbe.Probe(src, offset);
            if (bs != null && FileSystemProbe.ValidateVolume(src, offset, bs))
            {
                p.Boot = bs;
                p.FileSystem = bs.Kind;
                p.Status = PartitionStatus.Confirmed;
            }
            else
            {
                p.Status = PartitionStatus.InTableNoFileSystem;
                // NTFS: try the backup boot sector at the end of the partition.
                var backup = FileSystemProbe.Probe(src, offset + length - 512);
                if (backup is { Kind: FsKind.Ntfs } && FileSystemProbe.ValidateVolume(src, offset, backup))
                {
                    p.Boot = backup;
                    p.FileSystem = FsKind.Ntfs;
                    p.Status = PartitionStatus.Confirmed;
                    result.Issues.Add($"Partition {idx}: primary NTFS boot sector damaged; using the backup boot sector.");
                }
            }
        }
        result.Partitions.Add(p);
    }
}
