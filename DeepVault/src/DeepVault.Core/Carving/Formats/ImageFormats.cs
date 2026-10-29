using System.Text;
using DeepVault.Core.Catalog;
using DeepVault.Core.Util;

namespace DeepVault.Core.Carving.Formats;

public sealed class JpegFormat : CarveFormat
{
    public override string Id => "jpeg";
    public override string Name => "JPEG image";
    public override FileCategory Category => FileCategory.Image;
    public override string DefaultExtension => "jpg";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0xFF, 0xD8, 0xFF } };
    public override long MaxSize => 256L * 1024 * 1024;
    public override string Description => "Marker/segment walk plus entropy-coded data scan to the EOI marker; restart-marker sequence checked.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start + 2;
        bool sof = false, sos = false, dqtOrDht = false;
        int width = 0, height = 0;
        int segments = 0;
        while (p < limit)
        {
            int b = src.U8(p);
            if (b < 0) return Result(start, p, sof, sos, false, width, height, "file ends before EOI");
            if (b != 0xFF)
                return segments == 0 ? null : Result(start, p, sof, sos, false, width, height, "invalid marker structure");
            int m;
            long q = p + 1;
            do { m = src.U8(q++); } while (m == 0xFF);
            if (m < 0) return Result(start, p, sof, sos, false, width, height, "truncated");
            if (m == 0xD9) return Result(start, q, sof, sos && dqtOrDht, true, width, height, null);
            if (m == 0x00 || (m >= 0xD0 && m <= 0xD7) || m == 0x01 || m == 0xD8)
            {
                if (segments == 0) return null;
                return Result(start, p, sof, sos, false, width, height, "unexpected marker");
            }
            long len = src.U16BE(q);
            if (len < 2) return segments == 0 ? null : Result(start, p, sof, sos, false, width, height, "bad segment length");
            segments++;
            if (m is >= 0xC0 and <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
            {
                sof = true;
                height = (int)src.U16BE(q + 3);
                width = (int)src.U16BE(q + 5);
                if (width == 0 || height < 0) return Result(start, p, false, sos, false, 0, 0, "invalid frame header");
            }
            if (m == 0xDB || m == 0xC4) dqtOrDht = true;
            if (segments == 1 && !(m >= 0xE0 && m <= 0xEF) && m != 0xDB && m != 0xC4 && m != 0xFE && m != 0xDD && !(m >= 0xC0 && m <= 0xCF))
                return null; // first segment after SOI must be an APPn/DQT/DHT/SOF/COM
            p = q + len;
            if (m != 0xDA) continue;

            // Entropy-coded segment: scan until a real marker.
            sos = true;
            int expectedRst = 0;
            var buf = new byte[64 * 1024];
            bool found = false;
            while (p < limit && !found)
            {
                int n = src.Read(p, buf.AsSpan(0, (int)Math.Min(buf.Length, limit - p)));
                if (n <= 1) return Result(start, p + Math.Max(n, 0), sof, sos, false, width, height, "file ends inside image data");
                int i = 0;
                while (i < n - 1)
                {
                    int idx = buf.AsSpan(i, n - 1 - i).IndexOf((byte)0xFF);
                    if (idx < 0) { i = n - 1; break; }
                    i += idx;
                    byte nx = buf[i + 1];
                    if (nx == 0x00 || nx == 0xFF) { i += 1; continue; }
                    if (nx >= 0xD0 && nx <= 0xD7)
                    {
                        if (nx - 0xD0 != expectedRst)
                            return Result(start, p + i, sof, sos, false, width, height, "restart markers out of sequence (data is corrupted or fragmented)");
                        expectedRst = (expectedRst + 1) & 7;
                        i += 2;
                        continue;
                    }
                    if (nx == 0xD9 || nx == 0xDA || nx == 0xC4 || nx == 0xDD || nx == 0xDB || (nx >= 0xE0 && nx <= 0xEF) || nx == 0xFE || nx == 0xC2 || nx == 0xC0 || nx == 0xC1)
                    {
                        p += i;
                        found = true;
                        break;
                    }
                    return Result(start, p + i, sof, sos, false, width, height, $"invalid marker FF{nx:X2} inside image data (corrupted or fragmented)");
                }
                if (!found) p += i;
            }
        }
        return Result(start, limit, sof, sos, false, width, height, "no EOI within size limit");
    }

    private CarveResult? Result(long start, long end, bool sof, bool sos, bool complete, int w, int h, string? problem)
    {
        if (!sof && !sos && end - start < 1024) return null;
        var check = complete && sof && sos ? StructureCheck.Verified : end - start > 0 && sof ? StructureCheck.Damaged : StructureCheck.HeaderOnly;
        return new CarveResult
        {
            Length = end - start, Check = check, Extension = "jpg",
            Detail = w > 0 ? $"{w}×{h}" : null,
            Note = problem ?? (complete ? null : "incomplete"),
        };
    }
}

public sealed class PngFormat : CarveFormat
{
    public override string Id => "png";
    public override string Name => "PNG image";
    public override FileCategory Category => FileCategory.Image;
    public override string DefaultExtension => "png";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } };
    public override long MaxSize => 512L * 1024 * 1024;
    public override string Description => "Chunk walk to IEND with every chunk CRC-32 verified.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start + 8;
        int chunks = 0, badCrc = 0, w = 0, h = 0;
        var hdr = new byte[8];
        var buf = new byte[64 * 1024];
        while (p + 12 <= limit)
        {
            if (!src.Fill(p, hdr)) break;
            long len = Bytes.U32BE(hdr, 0);
            var type = hdr.AsSpan(4, 4);
            if (len > int.MaxValue || !IsChunkType(type)) break;
            if (chunks == 0)
            {
                if (!type.SequenceEqual("IHDR"u8) || len != 13) return null;
                w = (int)src.U32BE(p + 8);
                h = (int)src.U32BE(p + 12);
            }
            if (p + 12 + len > limit) break;
            // CRC over type + data
            uint crc = Crc32.Append(0, type);
            long d = p + 8, remain = len;
            while (remain > 0)
            {
                int n = (int)Math.Min(buf.Length, remain);
                if (!src.Fill(d, buf.AsSpan(0, n))) return Done(start, d, chunks, badCrc + 1, false, w, h);
                crc = Crc32.Append(crc, buf.AsSpan(0, n));
                d += n;
                remain -= n;
            }
            long stored = src.U32BE(p + 8 + len);
            if (stored != crc) badCrc++;
            chunks++;
            bool iend = type.SequenceEqual("IEND"u8);
            p += 12 + len;
            if (iend) return Done(start, p, chunks, badCrc, true, w, h);
        }
        if (chunks == 0) return null;
        return Done(start, p, chunks, badCrc, false, w, h);
    }

    private static bool IsChunkType(ReadOnlySpan<byte> t)
    {
        foreach (var c in t) if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return false;
        return true;
    }

    private static CarveResult Done(long start, long end, int chunks, int bad, bool iend, int w, int h) => new()
    {
        Length = end - start,
        Check = iend && bad == 0 ? StructureCheck.Verified : StructureCheck.Damaged,
        Extension = "png",
        Detail = $"{w}×{h}",
        Note = bad > 0 ? $"{bad} chunk(s) failed CRC (content corrupted, overwritten or fragmented)" : iend ? null : "IEND not found (truncated or fragmented)",
    };
}

public sealed class GifFormat : CarveFormat
{
    public override string Id => "gif";
    public override string Name => "GIF image";
    public override FileCategory Category => FileCategory.Image;
    public override string DefaultExtension => "gif";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "GIF87a"u8.ToArray(), "GIF89a"u8.ToArray() };
    public override long MaxSize => 128L * 1024 * 1024;
    public override string Description => "Block walk (extensions, image descriptors, LZW sub-blocks) to the trailer byte.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        int w = (int)src.U16LE(start + 6), h = (int)src.U16LE(start + 8);
        int flags = src.U8(start + 10);
        if (w <= 0 || h <= 0 || flags < 0) return null;
        long p = start + 13;
        if ((flags & 0x80) != 0) p += 3L * (1 << ((flags & 7) + 1));
        int images = 0;
        while (p < limit)
        {
            int b = src.U8(p);
            if (b < 0) break;
            if (b == 0x3B)
                return new CarveResult { Length = p + 1 - start, Check = images > 0 ? StructureCheck.Verified : StructureCheck.Damaged, Extension = "gif", Detail = $"{w}×{h}" };
            if (b == 0x21)
            {
                p = SkipSubBlocks(src, p + 2, limit);
                if (p < 0) break;
            }
            else if (b == 0x2C)
            {
                int lf = src.U8(p + 9);
                if (lf < 0) break;
                p += 10;
                if ((lf & 0x80) != 0) p += 3L * (1 << ((lf & 7) + 1));
                int minCode = src.U8(p);
                if (minCode < 2 || minCode > 12) break;
                p = SkipSubBlocks(src, p + 1, limit);
                if (p < 0) break;
                images++;
            }
            else break;
        }
        if (images == 0 && p - start < 64) return null;
        return new CarveResult { Length = Math.Min(p, limit) - start, Check = StructureCheck.Damaged, Extension = "gif", Detail = $"{w}×{h}", Note = "trailer not found (truncated or fragmented)" };
    }

    private static long SkipSubBlocks(ICarveSource src, long p, long limit)
    {
        while (p < limit)
        {
            int n = src.U8(p);
            if (n < 0) return -1;
            p += 1 + n;
            if (n == 0) return p;
        }
        return -1;
    }
}

public sealed class BmpFormat : CarveFormat
{
    public override string Id => "bmp";
    public override string Name => "Windows bitmap";
    public override FileCategory Category => FileCategory.Image;
    public override string DefaultExtension => "bmp";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "BM"u8.ToArray() };
    public override long MaxSize => 512L * 1024 * 1024;
    public override string Description => "Header fields cross-checked (declared size vs. dimensions and bit depth). No checksums.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var h = src.Get(start, 54);
        if (h == null) return null;
        long size = Bytes.U32(h, 2);
        uint reserved = Bytes.U32(h, 6);
        long dataOff = Bytes.U32(h, 10);
        uint dib = Bytes.U32(h, 14);
        if (reserved != 0 || dib is not (12 or 40 or 52 or 56 or 64 or 108 or 124)) return null;
        int w = dib == 12 ? Bytes.U16(h, 18) : (int)Bytes.U32(h, 18);
        int ht = dib == 12 ? Bytes.U16(h, 20) : Math.Abs((int)Bytes.U32(h, 22));
        int bpp = dib == 12 ? Bytes.U16(h, 24) : Bytes.U16(h, 28);
        uint comp = dib == 12 ? 0 : Bytes.U32(h, 30);
        if (w <= 0 || ht <= 0 || w > 65535 || ht > 65535 || bpp is not (1 or 4 or 8 or 16 or 24 or 32)) return null;
        if (dataOff < 14 + dib || size < dataOff || size > MaxSize || size > maxLength) return null;
        long rowBytes = ((long)w * bpp + 31) / 32 * 4;
        long expected = dataOff + rowBytes * ht;
        if (comp == 0 && Math.Abs(expected - size) > 4) return null;
        return new CarveResult { Length = size, Check = StructureCheck.Plausible, Extension = "bmp", Detail = $"{w}×{ht}, {bpp}-bit" };
    }
}

/// <summary>TIFF and the TIFF-based camera RAW formats (CR2, NEF, ARW, DNG, PEF, SRW, ORF, RW2...).</summary>
public sealed class TiffFormat : CarveFormat
{
    public override string Id => "tiff";
    public override string Name => "TIFF image / TIFF-based camera RAW";
    public override FileCategory Category => FileCategory.Image;
    public override string DefaultExtension => "tif";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[]
    {
        new byte[] { 0x49, 0x49, 0x2A, 0x00 }, new byte[] { 0x4D, 0x4D, 0x00, 0x2A },
        new byte[] { 0x49, 0x49, 0x52, 0x4F }, new byte[] { 0x49, 0x49, 0x55, 0x00 },
    };
    public override long MaxSize => 1L << 30;
    public override string Description => "IFD chain walk; file length is the furthest byte referenced by strips, tiles, sub-IFDs, EXIF and embedded previews. No checksums.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var hdr = src.Get(start, 16);
        if (hdr == null) return null;
        bool le = hdr[0] == 0x49;
        long Rd16(long o) => le ? src.U16LE(o) : src.U16BE(o);
        long Rd32(long o) => le ? src.U32LE(o) : src.U32BE(o);
        long first = Rd32(start + 4);
        long limit = Math.Min(maxLength, MaxSize);
        if (first < 8 || first >= limit) return null;
        string ext = "tif";
        if (hdr[2] == 0x52) ext = "orf";
        else if (hdr[2] == 0x55) ext = "rw2";
        else if (hdr[8] == 'C' && hdr[9] == 'R') ext = "cr2";

        long maxEnd = 8;
        var queue = new Queue<long>();
        var seen = new HashSet<long>();
        queue.Enqueue(first);
        int ifds = 0;
        int w = 0, h = 0;
        string? make = null;
        bool dng = false;
        int[] typeSize = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4 };
        var stripOffsets = new List<long>();
        var stripCounts = new List<long>();
        while (queue.Count > 0 && ifds < 64)
        {
            long ifd = queue.Dequeue();
            if (ifd <= 0 || ifd >= limit || !seen.Add(ifd)) continue;
            long count = Rd16(start + ifd);
            if (count <= 0 || count > 1000) { if (ifds == 0) return null; continue; }
            ifds++;
            maxEnd = Math.Max(maxEnd, ifd + 2 + count * 12 + 4);
            List<long>? offs = null, cnts = null;
            for (int i = 0; i < count; i++)
            {
                long e = start + ifd + 2 + i * 12;
                int tag = (int)Rd16(e);
                int type = (int)Rd16(e + 2);
                long n = Rd32(e + 4);
                if (type <= 0 || type >= typeSize.Length || n < 0) { if (ifds == 1 && i == 0) return null; continue; }
                long bytes = n * typeSize[type];
                long valOff = bytes > 4 ? Rd32(e + 8) : -1;
                if (valOff >= 0)
                {
                    if (valOff + bytes > limit) continue;
                    maxEnd = Math.Max(maxEnd, valOff + bytes);
                }
                long V(int idx)
                {
                    long at = valOff >= 0 ? start + valOff : e + 8;
                    return type == 3 ? Rd16(at + idx * 2) : Rd32(at + idx * 4);
                }
                switch (tag)
                {
                    case 256: w = (int)V(0); break;
                    case 257: h = (int)V(0); break;
                    case 271 when valOff >= 0 || bytes <= 4:
                    {
                        var mb = src.Get(valOff >= 0 ? start + valOff : e + 8, (int)Math.Min(bytes, 32));
                        if (mb != null) make = Encoding.ASCII.GetString(mb).TrimEnd('\0').Trim().ToUpperInvariant();
                        break;
                    }
                    case 273 or 324 or 513:
                        offs = new List<long>();
                        for (int k = 0; k < Math.Min(n, 100000); k++) offs.Add(V(k));
                        break;
                    case 279 or 325 or 514:
                        cnts = new List<long>();
                        for (int k = 0; k < Math.Min(n, 100000); k++) cnts.Add(V(k));
                        break;
                    case 330 or 34665 or 34853 or 40965:
                        for (int k = 0; k < Math.Min(n, 32); k++) queue.Enqueue(V(k));
                        break;
                    case 50706: dng = true; break;
                }
                if (offs != null && cnts != null)
                {
                    for (int k = 0; k < Math.Min(offs.Count, cnts.Count); k++)
                        if (offs[k] > 0 && offs[k] + cnts[k] <= limit) maxEnd = Math.Max(maxEnd, offs[k] + cnts[k]);
                    stripOffsets.AddRange(offs);
                    stripCounts.AddRange(cnts);
                    offs = cnts = null;
                }
            }
            long next = Rd32(start + ifd + 2 + count * 12);
            if (next > 0) queue.Enqueue(next);
        }
        if (ifds == 0) return null;
        if (ext == "tif")
        {
            if (dng) ext = "dng";
            else if (make != null)
            {
                if (make.StartsWith("NIKON")) ext = "nef";
                else if (make.StartsWith("SONY")) ext = "arw";
                else if (make.StartsWith("PENTAX")) ext = "pef";
                else if (make.StartsWith("SAMSUNG")) ext = "srw";
                else if (make.StartsWith("CANON")) ext = "cr2";
            }
        }
        if (maxEnd <= 8 || maxEnd > limit) return null;
        return new CarveResult
        {
            Length = maxEnd, Check = StructureCheck.Plausible, Extension = ext,
            Detail = w > 0 ? $"{w}×{h}" + (make != null ? $", {make}" : "") : make,
            Note = "TIFF has no checksums: size derived from internal offsets",
        };
    }
}

public sealed class RafFormat : CarveFormat
{
    public override string Id => "raf";
    public override string Name => "Fujifilm RAW";
    public override FileCategory Category => FileCategory.RawPhoto;
    public override string DefaultExtension => "raf";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "FUJIFILMCCD-RAW "u8.ToArray() };
    public override long MaxSize => 1L << 30;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long end = 0;
        for (int i = 0; i < 3; i++)
        {
            long off = src.U32BE(start + 84 + i * 8);
            long len = src.U32BE(start + 88 + i * 8);
            if (off < 0 || len < 0) return null;
            end = Math.Max(end, off + len);
        }
        if (end < 160 || end > Math.Min(maxLength, MaxSize)) return null;
        return new CarveResult { Length = end, Check = StructureCheck.Plausible, Extension = "raf" };
    }
}

public sealed class PsdFormat : CarveFormat
{
    public override string Id => "psd";
    public override string Name => "Photoshop document";
    public override FileCategory Category => FileCategory.Image;
    public override string DefaultExtension => "psd";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x38, 0x42, 0x50, 0x53, 0x00 } };
    public override long MaxSize => 2L << 30;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long ver = src.U16BE(start + 4);
        if (ver is not (1 or 2)) return null;
        bool psb = ver == 2;
        long channels = src.U16BE(start + 12), h = src.U32BE(start + 14), w = src.U32BE(start + 18), depth = src.U16BE(start + 22);
        if (channels is < 1 or > 56 || h <= 0 || w <= 0 || depth is not (1 or 8 or 16 or 32)) return null;
        long p = start + 26;
        long cm = src.U32BE(p); if (cm < 0) return null; p += 4 + cm;
        long ir = src.U32BE(p); if (ir < 0) return null; p += 4 + ir;
        long lm = psb ? (long)(src.U64BE(p) ?? 0) : src.U32BE(p); p += (psb ? 8 : 4) + lm;
        long comp = src.U16BE(p); p += 2;
        long rowBytes = (w * depth + 7) / 8;
        if (comp == 0) p += rowBytes * h * channels;
        else if (comp == 1)
        {
            long rows = h * channels;
            if (rows > 10_000_000) return null;
            long sum = 0;
            int cs = psb ? 4 : 2;
            var tbl = src.Get(p, (int)Math.Min(rows * cs, 64 * 1024 * 1024));
            if (tbl == null) return null;
            for (long i = 0; i < rows; i++) sum += psb ? Bytes.U32BE(tbl, (int)i * 4) : Bytes.U16BE(tbl, (int)i * 2);
            p += rows * cs + sum;
        }
        else return new CarveResult { Length = p - start, Check = StructureCheck.HeaderOnly, Extension = ver == 2 ? "psb" : "psd", Note = "ZIP-compressed image data: end not computable" };
        long len = p - start;
        if (len > Math.Min(maxLength, MaxSize)) return null;
        return new CarveResult { Length = len, Check = StructureCheck.Plausible, Extension = ver == 2 ? "psb" : "psd", Detail = $"{w}×{h}" };
    }
}
