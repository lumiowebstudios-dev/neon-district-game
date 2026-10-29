using System.IO.Compression;
using System.Text;
using DeepVault.Core.Catalog;
using DeepVault.Core.Util;

namespace DeepVault.Core.Carving.Formats;

public sealed class RarFormat : CarveFormat
{
    public override string Id => "rar";
    public override string Name => "RAR archive (v4 and v5)";
    public override FileCategory Category => FileCategory.Archive;
    public override string DefaultExtension => "rar";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "Rar!\u001A\u0007"u8.ToArray() };
    public override long MaxSize => 8L << 30;
    public override string Description => "Block walk with header CRCs verified, to the end-of-archive block. (Validator not tested against real RAR files: no redistributable RAR creator was available.)";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        int v = src.U8(start + 6);
        long limit = start + Math.Min(maxLength, MaxSize);
        if (v == 0) return Rar4(src, start, limit);
        if (v == 1 && src.U8(start + 7) == 0) return Rar5(src, start, limit);
        return null;
    }

    private static CarveResult? Rar4(ICarveSource src, long start, long limit)
    {
        long p = start + 7;
        int blocks = 0, badCrc = 0;
        while (p + 7 <= limit)
        {
            var h = src.Get(p, 7);
            if (h == null) break;
            int crc = Bytes.U16(h, 0), type = h[2], flags = Bytes.U16(h, 3), size = Bytes.U16(h, 5);
            if (type < 0x72 || type > 0x7B || size < 7) break;
            long add = 0;
            if ((flags & 0x8000) != 0 || type == 0x74 || type == 0x7A)
            {
                add = src.U32LE(p + 7);
                if (type == 0x74 && (flags & 0x100) != 0) add |= src.U32LE(p + 36) << 32;
            }
            var hb = src.Get(p + 2, size - 2);
            if (hb == null) break;
            if (type != 0x7B && (Crc32.Compute(hb) & 0xFFFF) != crc) badCrc++;
            blocks++;
            p += size + add;
            if (type == 0x7B)
                return new CarveResult { Length = p - start, Check = badCrc == 0 ? StructureCheck.Verified : StructureCheck.Damaged, Extension = "rar", Note = badCrc > 0 ? $"{badCrc} header CRC errors" : "header CRCs consistent" };
        }
        if (blocks == 0) return null;
        return new CarveResult { Length = p - start, Check = StructureCheck.HeaderOnly, Extension = "rar", Note = "end-of-archive block not found" };
    }

    private static (long v, int len) Vint(ICarveSource src, long off)
    {
        long v = 0;
        for (int i = 0; i < 10; i++)
        {
            int b = src.U8(off + i);
            if (b < 0) return (-1, 0);
            v |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return (v, i + 1);
        }
        return (-1, 0);
    }

    private static CarveResult? Rar5(ICarveSource src, long start, long limit)
    {
        long p = start + 8;
        int blocks = 0, badCrc = 0;
        while (p + 6 <= limit)
        {
            long crc = src.U32LE(p);
            var (hsize, hl) = Vint(src, p + 4);
            if (hsize <= 0 || hsize > 2 * 1024 * 1024) break;
            var hb = src.Get(p + 4, (int)(hl + hsize));
            if (hb == null) break;
            if (Crc32.Compute(hb) != crc) { badCrc++; break; }
            long q = p + 4 + hl;
            var (type, tl) = Vint(src, q); q += tl;
            var (flags, fl) = Vint(src, q); q += fl;
            if ((flags & 1) != 0) { var (_, el) = Vint(src, q); q += el; }
            long data = 0;
            if ((flags & 2) != 0) data = Vint(src, q).v;
            blocks++;
            p += 4 + hl + hsize + data;
            if (type == 5)
                return new CarveResult { Length = p - start, Check = StructureCheck.Verified, Extension = "rar", Note = "header CRCs consistent" };
        }
        if (blocks == 0) return null;
        return new CarveResult { Length = p - start, Check = badCrc > 0 ? StructureCheck.Damaged : StructureCheck.HeaderOnly, Extension = "rar", Note = badCrc > 0 ? "header CRC error" : "end-of-archive block not found" };
    }
}

public sealed class SevenZipFormat : CarveFormat
{
    public override string Id => "7z";
    public override string Name => "7-Zip archive";
    public override FileCategory Category => FileCategory.Archive;
    public override string DefaultExtension => "7z";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C } };
    public override long MaxSize => 64L << 30;
    public override string Description => "Start-header CRC and next-header CRC verified; size = 32 + next-header offset + size. Packed data is not decompressed.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var h = src.Get(start, 32);
        if (h == null || h[6] != 0) return null;
        if (Crc32.Compute(h.AsSpan(12, 20)) != Bytes.U32(h, 8)) return null;
        long off = (long)Bytes.U64(h, 12), size = (long)Bytes.U64(h, 20);
        uint nhCrc = Bytes.U32(h, 28);
        long len = 32 + off + size;
        if (off < 0 || size <= 0 || size > 256 * 1024 * 1024 || len > Math.Min(maxLength, MaxSize))
            return new CarveResult { Length = 32, Check = StructureCheck.Damaged, Extension = "7z", Note = "header offsets out of range" };
        var nh = src.Get(start + 32 + off, (int)size);
        bool ok = nh != null && Crc32.Compute(nh) == nhCrc;
        return new CarveResult { Length = len, Check = ok ? StructureCheck.Verified : StructureCheck.Damaged, Extension = "7z", Note = ok ? "archive header CRCs consistent; packed data not decoded" : "end header CRC mismatch (truncated, fragmented or overwritten)" };
    }
}

public sealed class GzipFormat : CarveFormat
{
    public override string Id => "gzip";
    public override string Name => "gzip";
    public override FileCategory Category => FileCategory.Archive;
    public override string DefaultExtension => "gz";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x1F, 0x8B, 0x08 } };
    public override long MaxSize => 4L << 30;
    public long MaxDecompressed { get; init; } = 8L << 30;
    public override string Description => "Fully decompressed; the trailer CRC-32 and size must match the decompressed data. Concatenated members are followed.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start;
        int members = 0;
        string ext = "gz";
        while (p + 18 <= limit && src.Matches(p, new byte[] { 0x1F, 0x8B, 0x08 }))
        {
            int flags = src.U8(p + 3);
            if ((flags & 0xE0) != 0) break;
            long q = p + 10;
            if ((flags & 4) != 0) q += 2 + src.U16LE(q);
            if ((flags & 8) != 0) { long z = src.Find(new byte[] { 0 }, q, Math.Min(q + 4096, limit)); if (z < 0) break; q = z + 1; }
            if ((flags & 16) != 0) { long z = src.Find(new byte[] { 0 }, q, Math.Min(q + 65536, limit)); if (z < 0) break; q = z + 1; }
            if ((flags & 2) != 0) q += 2;
            using var slice = new SliceStream(src, q, limit - q);
            uint crc = 0;
            long total = 0;
            var first = new byte[512];
            try
            {
                using var ds = new DeflateStream(slice, CompressionMode.Decompress, true);
                var buf = new byte[128 * 1024];
                int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    if (total < 512) Array.Copy(buf, 0, first, (int)total, (int)Math.Min(n, 512 - total));
                    crc = Crc32.Append(crc, buf.AsSpan(0, n));
                    total += n;
                    if (total > MaxDecompressed) return members > 0 ? Done(start, p, ext, true, members) : null;
                }
            }
            catch (InvalidDataException)
            {
                if (members == 0 && total == 0) return null;
                return new CarveResult { Length = Math.Min(q + slice.BytesRead, limit) - start, Check = StructureCheck.Damaged, Extension = ext, Note = "compressed data corrupted (deflate error)" };
            }
            if (members == 0 && total >= 262 && Encoding.ASCII.GetString(first, 257, 5) == "ustar") ext = "tar.gz";
            // The deflate stream ended somewhere before q+BytesRead; find the trailer that matches CRC and size.
            var trailer = new byte[8];
            BitConverter.TryWriteBytes(trailer.AsSpan(0, 4), crc);
            BitConverter.TryWriteBytes(trailer.AsSpan(4, 4), (uint)total);
            long searchFrom = Math.Max(q, q + slice.BytesRead - 128 * 1024 - 64);
            long t = src.Find(trailer, searchFrom, Math.Min(q + slice.BytesRead + 8, limit));
            if (t < 0)
                return new CarveResult { Length = Math.Min(q + slice.BytesRead, limit) - start, Check = StructureCheck.Damaged, Extension = ext, Note = "CRC-32/size trailer does not match the decompressed data" };
            members++;
            p = t + 8;
        }
        if (members == 0) return null;
        return Done(start, p, ext, false, members);
    }

    private static CarveResult Done(long s, long e, string ext, bool capped, int members) => new()
    {
        Length = e - s, Check = capped ? StructureCheck.Plausible : StructureCheck.Verified, Extension = ext,
        Note = capped ? "decompression capped" : members > 1 ? $"{members} members, all CRC-32 verified" : "CRC-32 verified",
    };
}

public sealed class XzFormat : CarveFormat
{
    public override string Id => "xz";
    public override string Name => "xz";
    public override FileCategory Category => FileCategory.Archive;
    public override string DefaultExtension => "xz";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 } };
    public override long MaxSize => 8L << 30;
    public override string Description => "Stream header/footer CRCs verified and the index located via the footer's backward size.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var h = src.Get(start, 12);
        if (h == null || Crc32.Compute(h.AsSpan(6, 2)) != Bytes.U32(h, 8)) return null;
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start + 12;
        while (p < limit)
        {
            long yz = src.Find("YZ"u8, p, limit);
            if (yz < 0) break;
            if ((yz - start - 10) % 4 == 0)
            {
                var f = src.Get(yz - 10, 12);
                if (f != null && f[8] == h[6] && f[9] == h[7] && Crc32.Compute(f.AsSpan(4, 6)) == Bytes.U32(f, 0))
                {
                    long backward = ((long)Bytes.U32(f, 4) + 1) * 4;
                    long index = yz - 10 - backward;
                    if (index > start + 12 && src.U8(index) == 0)
                        return new CarveResult { Length = yz + 2 - start, Check = StructureCheck.Verified, Extension = "xz", Note = "stream footer and index consistent" };
                }
            }
            p = yz + 1;
        }
        return new CarveResult { Length = 12, Check = StructureCheck.HeaderOnly, Extension = "xz", Note = "stream footer not found" };
    }
}

public sealed class CabFormat : CarveFormat
{
    public override string Id => "cab";
    public override string Name => "Microsoft Cabinet";
    public override FileCategory Category => FileCategory.Archive;
    public override string DefaultExtension => "cab";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "MSCF\0\0\0\0"u8.ToArray() };
    public override long MaxSize => 2L << 30;
    public override string Description => "Cabinet size from the CFHEADER. No checksums.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long size = src.U32LE(start + 8);
        long filesOff = src.U32LE(start + 16);
        int minor = src.U8(start + 24), major = src.U8(start + 25);
        if (major != 1 || minor != 3 || size < 44 || filesOff >= size || size > Math.Min(maxLength, MaxSize)) return null;
        return new CarveResult { Length = size, Check = StructureCheck.Plausible, Extension = "cab" };
    }
}

public sealed class SqliteFormat : CarveFormat
{
    public override string Id => "sqlite";
    public override string Name => "SQLite database";
    public override FileCategory Category => FileCategory.Database;
    public override string DefaultExtension => "sqlite";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "SQLite format 3\0"u8.ToArray() };
    public override long MaxSize => 16L << 30;
    public override string Description => "Page size × page count from the header (only trusted when the header's version-valid-for counter matches).";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var h = src.Get(start, 104);
        if (h == null) return null;
        long ps = Bytes.U16BE(h, 16);
        if (ps == 1) ps = 65536;
        if (ps < 512 || (ps & (ps - 1)) != 0 || h[21] != 64 || h[22] != 32 || h[23] != 32) return null;
        long pages = Bytes.U32BE(h, 28);
        bool valid = Bytes.U32BE(h, 92) == Bytes.U32BE(h, 24) && pages > 0;
        int btree = src.U8(start + 100);
        bool firstPageOk = btree is 0x0D or 0x05;
        if (!valid)
            return new CarveResult { Length = ps, Check = StructureCheck.HeaderOnly, Extension = "sqlite", Note = "database size field not valid (legacy writer); only the first page is certain" };
        long len = pages * ps;
        if (len > Math.Min(maxLength, MaxSize)) return null;
        return new CarveResult { Length = len, Check = firstPageOk ? StructureCheck.Plausible : StructureCheck.Damaged, Extension = "sqlite", Detail = $"{pages} pages of {ps} bytes" };
    }
}

/// <summary>Windows PE executables (EXE/DLL/SYS). Opt-in: system disks contain very many.</summary>
public sealed class PeFormat : CarveFormat
{
    public override string Id => "pe";
    public override string Name => "Windows executable (EXE/DLL)";
    public override FileCategory Category => FileCategory.Executable;
    public override string DefaultExtension => "exe";
    public override bool EnabledByDefault => false;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "MZ"u8.ToArray() };
    public override long MaxSize => 1L << 30;
    public override string Description => "Size from the section table and certificate directory. Recovered programs are never executed by DeepVault.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long lfanew = src.U32LE(start + 0x3C);
        if (lfanew < 0x40 || lfanew > 4096 || !src.Matches(start + lfanew, "PE\0\0"u8)) return null;
        long coff = start + lfanew + 4;
        long nsec = src.U16LE(coff + 2), optSize = src.U16LE(coff + 16), chars = src.U16LE(coff + 18);
        if (nsec is <= 0 or > 96 || optSize < 0) return null;
        long opt = coff + 20;
        long magic = src.U16LE(opt);
        if (magic != 0x10B && magic != 0x20B) return null;
        long end = 0;
        long st = opt + optSize;
        for (int i = 0; i < nsec; i++)
        {
            long raw = src.U32LE(st + i * 40 + 20), rsz = src.U32LE(st + i * 40 + 16);
            if (raw < 0 || rsz < 0) return null;
            if (rsz > 0) end = Math.Max(end, raw + rsz);
        }
        long ddBase = opt + (magic == 0x20B ? 112 : 96);
        long certOff = src.U32LE(ddBase + 4 * 8), certSize = src.U32LE(ddBase + 4 * 8 + 4);
        if (certOff > 0 && certSize > 0) end = Math.Max(end, certOff + certSize);
        if (end <= lfanew || end > Math.Min(maxLength, MaxSize)) return null;
        return new CarveResult { Length = end, Check = StructureCheck.Plausible, Extension = (chars & 0x2000) != 0 ? "dll" : "exe" };
    }
}
