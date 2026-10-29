using DeepVault.Core.IO;
using DeepVault.Core.Util;

namespace DeepVault.Core.Partitions;

public enum FsKind { Unknown = 0, Ntfs = 1, Fat12 = 2, Fat16 = 3, Fat32 = 4, ExFat = 5 }

/// <summary>Geometry parsed from a volume boot sector.</summary>
public sealed record BootSectorInfo(
    FsKind Kind,
    int BytesPerSector,
    int ClusterSize,
    long TotalBytes,
    string? Label,
    ulong SerialNumber)
{
    /// <summary>Copy of the 512-byte boot sector this info was parsed from.</summary>
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    /// <summary>Offset of the sector that holds a backup copy of this boot sector, relative to volume start (-1 if none).</summary>
    public long BackupBootOffset => Kind switch
    {
        FsKind.Ntfs => TotalBytes, // NTFS keeps its backup in the sector after the last counted sector
        FsKind.Fat32 => 6L * BytesPerSector,
        FsKind.ExFat => 12L * BytesPerSector,
        _ => -1,
    };
}

public static class FileSystemProbe
{
    /// <summary>Parses a sector as a volume boot record. Returns null if it is not a recognised boot sector.</summary>
    public static BootSectorInfo? Parse(ReadOnlySpan<byte> s)
    {
        var r = ParseCore(s);
        return r == null ? null : r with { Raw = s.Slice(0, 512).ToArray() };
    }

    private static BootSectorInfo? ParseCore(ReadOnlySpan<byte> s)
    {
        if (s.Length < 512) return null;
        if (s[510] != 0x55 || s[511] != 0xAA) return null;

        if (s.Slice(3, 8).SequenceEqual("NTFS    "u8))
        {
            int bps = Bytes.U16(s, 0x0B);
            int spcRaw = s[0x0D];
            if (!IsPow2(bps) || bps < 256 || bps > 4096) return null;
            // Sectors per cluster > 128 is stored as negative power of two (2^-n) on large-cluster volumes.
            long spc = spcRaw <= 0x80 ? spcRaw : 1L << (256 - spcRaw);
            if (spc == 0 || !IsPow2(spc)) return null;
            long total = Bytes.I64(s, 0x28);
            long mftLcn = Bytes.I64(s, 0x30);
            if (total <= 0 || mftLcn <= 0) return null;
            long cs = bps * spc;
            if (cs > 2 * 1024 * 1024) return null;
            if (mftLcn * cs > total * bps) return null;
            return new BootSectorInfo(FsKind.Ntfs, bps, (int)cs, total * bps, null, Bytes.U64(s, 0x48));
        }

        if (s.Slice(3, 8).SequenceEqual("EXFAT   "u8))
        {
            int bpsShift = s[0x6C];
            int spcShift = s[0x6D];
            if (bpsShift < 9 || bpsShift > 12 || spcShift > 25 - bpsShift) return null;
            long len = Bytes.I64(s, 0x48);
            if (len <= 0) return null;
            int bps = 1 << bpsShift;
            return new BootSectorInfo(FsKind.ExFat, bps, bps << spcShift, len * bps, null, Bytes.U32(s, 0x64));
        }

        // FAT: validate BPB rather than relying on the (optional) type string.
        if (s[0] != 0xEB && s[0] != 0xE9) return null;
        {
            int bps = Bytes.U16(s, 0x0B);
            int spc = s[0x0D];
            int reserved = Bytes.U16(s, 0x0E);
            int nFats = s[0x10];
            int rootEntries = Bytes.U16(s, 0x11);
            long total16 = Bytes.U16(s, 0x13);
            int media = s[0x15];
            int fatSz16 = Bytes.U16(s, 0x16);
            long total32 = Bytes.U32(s, 0x20);
            if (!IsPow2(bps) || bps < 512 || bps > 4096) return null;
            if (spc == 0 || !IsPow2(spc)) return null;
            if (reserved == 0 || nFats < 1 || nFats > 2) return null;
            if (media != 0xF0 && media < 0xF8) return null;
            long totalSectors = total16 != 0 ? total16 : total32;
            if (totalSectors == 0) return null;
            long fatSz = fatSz16 != 0 ? fatSz16 : Bytes.U32(s, 0x24);
            if (fatSz == 0) return null;
            long rootDirSectors = (rootEntries * 32L + bps - 1) / bps;
            long dataSectors = totalSectors - (reserved + nFats * fatSz + rootDirSectors);
            if (dataSectors <= 0) return null;
            long clusters = dataSectors / spc;
            FsKind kind = clusters < 4085 ? FsKind.Fat12 : clusters < 65525 ? FsKind.Fat16 : FsKind.Fat32;
            if (kind == FsKind.Fat32 && fatSz16 != 0) return null;
            if (kind != FsKind.Fat32 && rootEntries == 0) return null;
            int labelOff = kind == FsKind.Fat32 ? 0x47 : 0x2B;
            int serialOff = kind == FsKind.Fat32 ? 0x43 : 0x27;
            int sigOff = kind == FsKind.Fat32 ? 0x42 : 0x26;
            string? label = null;
            ulong serial = 0;
            if (s[sigOff] == 0x29)
            {
                label = System.Text.Encoding.ASCII.GetString(s.Slice(labelOff, 11)).Trim();
                serial = Bytes.U32(s, serialOff);
                if (label == "NO NAME") label = null;
            }
            return new BootSectorInfo(kind, bps, bps * spc, totalSectors * bps, label, serial);
        }
    }

    public static BootSectorInfo? Probe(IBlockSource src, long offset)
    {
        if (offset < 0 || offset + 512 > src.Length) return null;
        try
        {
            var b = src.ReadBytes(offset, 512);
            return Parse(b);
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Checks that a volume that supposedly starts at <paramref name="volStart"/> is internally
    /// consistent (e.g. its MFT or FAT is where the boot sector says). Used to reject false positives
    /// and to decide whether a found boot sector is a primary or a backup copy.
    /// </summary>
    public static bool ValidateVolume(IBlockSource src, long volStart, BootSectorInfo bs)
    {
        try
        {
            switch (bs.Kind)
            {
                case FsKind.Ntfs:
                {
                    var boot = bs.Raw.Length >= 512 ? bs.Raw : src.ReadBytes(volStart, 512);
                    long mftLcn = Bytes.I64(boot, 0x30);
                    long mftOff = volStart + mftLcn * bs.ClusterSize;
                    if (mftOff + 1024 > src.Length) return false;
                    var rec = src.ReadBytes(mftOff, 1024);
                    return rec.AsSpan(0, 4).SequenceEqual("FILE"u8);
                }
                case FsKind.Fat12:
                case FsKind.Fat16:
                case FsKind.Fat32:
                {
                    var boot = bs.Raw.Length >= 512 ? bs.Raw : src.ReadBytes(volStart, 512);
                    int reserved = Bytes.U16(boot, 0x0E);
                    byte media = boot[0x15];
                    long fatOff = volStart + (long)reserved * bs.BytesPerSector;
                    if (fatOff + 4 > src.Length) return false;
                    var fat = src.ReadBytes(fatOff, 4);
                    return fat[0] == media && fat[1] == 0xFF && fat[2] == 0xFF;
                }
                case FsKind.ExFat:
                {
                    var boot = bs.Raw.Length >= 512 ? bs.Raw : src.ReadBytes(volStart, 512);
                    if (!boot.AsSpan(3, 8).SequenceEqual("EXFAT   "u8)) return false;
                    long fatOffSectors = Bytes.U32(boot, 0x50);
                    var fat = src.ReadBytes(volStart + fatOffSectors * bs.BytesPerSector, 8);
                    return fat[0] == 0xF8 && fat[1] == 0xFF && fat[2] == 0xFF && fat[3] == 0xFF;
                }
            }
        }
        catch (IOException) { }
        return false;
    }

    private static bool IsPow2(long v) => v > 0 && (v & (v - 1)) == 0;

    public static string Describe(FsKind k) => k switch
    {
        FsKind.Ntfs => "NTFS",
        FsKind.Fat12 => "FAT12",
        FsKind.Fat16 => "FAT16",
        FsKind.Fat32 => "FAT32",
        FsKind.ExFat => "exFAT",
        _ => "Unknown",
    };
}
