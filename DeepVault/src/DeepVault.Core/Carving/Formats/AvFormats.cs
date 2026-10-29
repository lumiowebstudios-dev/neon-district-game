using System.Text;
using DeepVault.Core.Catalog;
using DeepVault.Core.Util;

namespace DeepVault.Core.Carving.Formats;

/// <summary>RIFF containers: WAV, AVI (incl. OpenDML AVIX extensions), WebP, ANI, RMI.</summary>
public sealed class RiffFormat : CarveFormat
{
    public override string Id => "riff";
    public override string Name => "RIFF (WAV / AVI / WebP)";
    public override FileCategory Category => FileCategory.Audio;
    public override string DefaultExtension => "wav";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "RIFF"u8.ToArray() };
    public override long MaxSize => 4L << 30;
    public override string Description => "Declared RIFF size checked against a walk of its top-level chunks.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long size = src.U32LE(start + 4);
        var form = src.Get(start + 8, 4);
        if (size < 12 || form == null) return null;
        string f = Encoding.ASCII.GetString(form);
        string ext = f switch { "WAVE" => "wav", "AVI " => "avi", "WEBP" => "webp", "ACON" => "ani", "RMID" => "rmi", "CDXA" => "dat", _ => "" };
        if (ext.Length == 0) return null;
        long end = start + 8 + size + (size & 1);
        long limit = start + Math.Min(maxLength, MaxSize);
        // Walk chunks for consistency.
        long p = start + 12;
        int chunks = 0;
        bool ok = true;
        while (p + 8 <= Math.Min(end, limit))
        {
            var id = src.Get(p, 4);
            long cl = src.U32LE(p + 4);
            if (id == null || cl < 0 || !CarveIO.IsPrintableAscii(id)) { ok = false; break; }
            chunks++;
            p += 8 + cl + (cl & 1);
            if (chunks > 100000) break;
        }
        if (chunks == 0) return null;
        bool exact = ok && (p == end || p == end - (size & 1) || p == start + 8 + size);
        // OpenDML: further RIFF 'AVIX' chunks follow a large AVI.
        if (ext == "avi")
            while (end + 12 <= limit && src.Matches(end, "RIFF"u8) && src.Matches(end + 8, "AVIX"u8))
            {
                long s2 = src.U32LE(end + 4);
                if (s2 < 4) break;
                end += 8 + s2 + (s2 & 1);
            }
        if (end > limit) return new CarveResult { Length = limit - start, Check = StructureCheck.Damaged, Extension = ext, Note = "declared size exceeds available data" };
        var cat = ext is "avi" ? "video" : "";
        return new CarveResult
        {
            Length = end - start,
            Check = exact ? StructureCheck.Verified : StructureCheck.Damaged,
            Extension = ext,
            Note = exact ? null : "chunk structure inconsistent with declared size",
            Detail = cat.Length > 0 ? null : null,
        };
    }
}

/// <summary>ISO base media file format: MP4, MOV, M4A, 3GP, HEIC/AVIF, CR3.</summary>
public sealed class IsoBmffFormat : CarveFormat
{
    private static readonly HashSet<string> TopLevel = new()
    {
        "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "pdin", "moof", "mfra", "sidx", "styp", "pnot", "prfl", "emsg", "junk", "PICT", "udta", "ssix", "prft", "mvex",
    };

    public override string Id => "isobmff";
    public override string Name => "MP4 / MOV / HEIC (ISO base media)";
    public override FileCategory Category => FileCategory.Video;
    public override string DefaultExtension => "mp4";
    public override int SignatureOffset => 4;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "ftyp"u8.ToArray() };
    public override long MaxSize => 64L << 30;
    public override string Description => "Top-level box walk (ftyp/moov/mdat...). Box sizes are validated but media samples are not checksummed.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long ftypSize = src.U32BE(start);
        if (ftypSize < 8 || ftypSize > 4096) return null;
        string brand = Encoding.ASCII.GetString(src.Get(start + 8, 4) ?? Array.Empty<byte>());
        string ext = brand switch
        {
            "qt  " => "mov",
            "M4A " or "M4B " or "M4P " => "m4a",
            "M4V " or "M4VH" or "M4VP" => "m4v",
            "crx " => "cr3",
            "heic" or "heix" or "mif1" or "msf1" or "heim" or "heis" or "hevc" or "hevx" => "heic",
            "avif" or "avis" => "avif",
            _ when brand.StartsWith("3gp") || brand.StartsWith("3g2") => "3gp",
            _ => "mp4",
        };
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start;
        bool moov = false, mdat = false, meta = false;
        int boxes = 0;
        while (p + 8 <= limit)
        {
            long size = src.U32BE(p);
            var t = src.Get(p + 4, 4);
            if (t == null) break;
            string type = Encoding.ASCII.GetString(t);
            if (!TopLevel.Contains(type)) break;
            if (size == 1)
            {
                var big = src.U64BE(p + 8);
                if (big == null || big < 16 || big > (ulong)MaxSize) break;
                size = (long)big;
            }
            else if (size == 0)
            {
                // Box extends to end of file: unusable for carving bounds.
                return new CarveResult { Length = Math.Max(p - start, 8), Check = StructureCheck.HeaderOnly, Extension = ext, Note = "final box has open-ended size" };
            }
            else if (size < 8) break;
            if (type == "moov") moov = true;
            if (type == "mdat") mdat = true;
            if (type == "meta") meta = true;
            boxes++;
            p += size;
        }
        if (boxes == 0) return null;
        if (p > limit)
            return new CarveResult { Length = limit - start, Check = StructureCheck.Damaged, Extension = ext, Note = "a box extends past the readable area" };
        bool complete = (moov && mdat) || (meta && mdat && ext is "heic" or "avif") || (moov && ext == "cr3");
        return new CarveResult
        {
            Length = p - start,
            Check = complete ? StructureCheck.Plausible : StructureCheck.Damaged,
            Extension = ext,
            Detail = $"brand '{brand.Trim()}'",
            Note = complete ? "box structure consistent; media samples are not checksummed" : "required boxes (moov/mdat) missing",
        };
    }
}

public sealed class MatroskaFormat : CarveFormat
{
    public override string Id => "matroska";
    public override string Name => "Matroska / WebM";
    public override FileCategory Category => FileCategory.Video;
    public override string DefaultExtension => "mkv";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x1A, 0x45, 0xDF, 0xA3 } };
    public override long MaxSize => 64L << 30;
    public override string Description => "EBML header + Segment size; segment children walked when the size is unknown.";

    private static readonly HashSet<long> Level1 = new() { 0x114D9B74, 0x1549A966, 0x1654AE6B, 0x1F43B675, 0x1C53BB6B, 0x1941A469, 0x1043A770, 0x1254C367, 0xEC, 0xBF };

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        var (hdrSize, hdrLen, _) = ReadVint(src, start + 4, false);
        if (hdrSize < 0 || hdrSize > 1024) return null;
        long hdrEnd = start + 4 + hdrLen + hdrSize;
        var docType = src.Get(start, (int)(hdrEnd - start));
        if (docType == null) return null;
        string s = Encoding.ASCII.GetString(docType);
        string ext = s.Contains("webm") ? "webm" : s.Contains("matroska") ? "mkv" : "";
        if (ext.Length == 0) return null;
        var (segId, segIdLen, _) = ReadVint(src, hdrEnd, true);
        if (segId != 0x18538067) return null;
        var (segSize, segSizeLen, unknown) = ReadVint(src, hdrEnd + segIdLen, false);
        long dataStart = hdrEnd + segIdLen + segSizeLen;
        if (!unknown && segSize > 0)
        {
            long end = dataStart + segSize;
            if (end > limit) return new CarveResult { Length = limit - start, Check = StructureCheck.Damaged, Extension = ext, Note = "segment extends past readable area" };
            bool ok = WalkChildren(src, dataStart, end, out long reached) && reached == end;
            return new CarveResult { Length = end - start, Check = ok ? StructureCheck.Plausible : StructureCheck.Damaged, Extension = ext, Note = ok ? "element structure consistent; frames are not checksummed" : "segment children inconsistent" };
        }
        WalkChildren(src, dataStart, limit, out long last);
        return new CarveResult { Length = last - start, Check = StructureCheck.HeaderOnly, Extension = ext, Note = "segment size unknown (live recording); end estimated from element walk" };
    }

    private static bool WalkChildren(ICarveSource src, long p, long end, out long reached)
    {
        int n = 0;
        while (p < end)
        {
            var (id, idLen, _) = ReadVint(src, p, true);
            if (!Level1.Contains(id)) { reached = p; return false; }
            var (sz, szLen, unk) = ReadVint(src, p + idLen, false);
            if (sz < 0 || unk) { reached = p; return false; }
            p += idLen + szLen + sz;
            if (++n > 10_000_000) break;
        }
        reached = p;
        return true;
    }

    /// <summary>Reads an EBML variable-length integer. For IDs the marker bits are kept.</summary>
    private static (long value, int length, bool unknown) ReadVint(ICarveSource src, long off, bool keepMarker)
    {
        int first = src.U8(off);
        if (first <= 0) return (-1, 1, false);
        int len = 1;
        while (len <= 8 && (first & (0x80 >> (len - 1))) == 0) len++;
        if (len > 8) return (-1, 1, false);
        var b = src.Get(off, len);
        if (b == null) return (-1, len, false);
        long v = keepMarker ? first : first & ((0x80 >> (len - 1)) - 1);
        bool allOnes = (first & ((0x80 >> (len - 1)) - 1)) == ((0x80 >> (len - 1)) - 1);
        for (int i = 1; i < len; i++)
        {
            v = (v << 8) | b[i];
            if (b[i] != 0xFF) allOnes = false;
        }
        return (v, len, !keepMarker && allOnes);
    }
}

public sealed class Mp3Format : CarveFormat
{
    public override string Id => "mp3";
    public override string Name => "MP3 audio";
    public override FileCategory Category => FileCategory.Audio;
    public override string DefaultExtension => "mp3";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[]
    {
        "ID3"u8.ToArray(), new byte[] { 0xFF, 0xFB }, new byte[] { 0xFF, 0xFA }, new byte[] { 0xFF, 0xF3 }, new byte[] { 0xFF, 0xF2 }, new byte[] { 0xFF, 0xE3 },
    };
    public override long MaxSize => 512L * 1024 * 1024;
    public override string Description => "ID3v2 tag + MPEG audio frame chain walk (frame lengths computed from each header) + optional ID3v1 tag.";

    private static readonly int[,] Bitrates =
    {
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 }, // V1 L1
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 },   // V1 L2
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 },    // V1 L3
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 },   // V2 L1
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 },        // V2 L2/L3
    };

    internal static int FrameLength(ReadOnlySpan<byte> h)
    {
        if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return -1;
        int ver = (h[1] >> 3) & 3; // 0=2.5, 2=2, 3=1
        int layer = (h[1] >> 1) & 3; // 1=III, 2=II, 3=I
        if (ver == 1 || layer == 0) return -1;
        int bri = h[2] >> 4, sri = (h[2] >> 2) & 3, pad = (h[2] >> 1) & 1;
        if (bri == 0 || bri == 15 || sri == 3) return -1;
        int[] sr1 = { 44100, 48000, 32000 };
        int sr = sr1[sri] >> (ver == 3 ? 0 : ver == 2 ? 1 : 2);
        int row = ver == 3 ? (layer == 3 ? 0 : layer == 2 ? 1 : 2) : (layer == 3 ? 3 : 4);
        int br = Bitrates[row, bri] * 1000;
        if (layer == 3) return (12 * br / sr + pad) * 4;
        if (layer == 1 && ver != 3) return 72 * br / sr + pad;
        return 144 * br / sr + pad;
    }

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start;
        bool id3 = false;
        if (src.Matches(start, "ID3"u8))
        {
            var h = src.Get(start, 10);
            if (h == null || h[3] > 4 || ((h[6] | h[7] | h[8] | h[9]) & 0x80) != 0) return null;
            long sz = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
            p = start + 10 + sz + ((h[5] & 0x10) != 0 ? 10 : 0);
            id3 = true;
            // Some encoders pad after the tag; skip zero padding.
            for (int i = 0; i < 4096 && src.U8(p) == 0; i++) p++;
        }
        int frames = 0;
        var hb = new byte[4];
        while (p + 4 <= limit && src.Fill(p, hb))
        {
            int len = FrameLength(hb);
            if (len <= 4) break;
            if (p + len > limit) break;
            frames++;
            p += len;
        }
        int minFrames = id3 ? 4 : 16;
        if (frames < minFrames) return null;
        if (src.Matches(p, "TAG"u8)) p += 128;
        if (src.Matches(p, "APETAGEX"u8)) { long apeSize = src.U32LE(p + 12); if (apeSize > 0 && apeSize < 1 << 24) p += 32 + apeSize; }
        if (p > limit) p = limit;
        return new CarveResult { Length = p - start, Check = StructureCheck.Plausible, Extension = "mp3", Detail = $"{frames} frames", Note = "frame chain intact; audio data has no checksum" };
    }
}

public sealed class FlacFormat : CarveFormat
{
    public override string Id => "flac";
    public override string Name => "FLAC audio";
    public override FileCategory Category => FileCategory.Audio;
    public override string DefaultExtension => "flac";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "fLaC"u8.ToArray() };
    public override long MaxSize => 2L << 30;
    public override string Description => "Metadata blocks, then every frame located by header CRC-8 and verified with its CRC-16 until STREAMINFO's sample count is reached.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start + 4;
        long totalSamples = -1;
        int maxFrame = 0, minBlock = 0, sampleRate = 0, bps = 0;
        for (int i = 0; i < 1000; i++)
        {
            int hdr = src.U8(p);
            long len = src.U24BE(p + 1);
            if (hdr < 0 || len < 0) return null;
            int type = hdr & 0x7F;
            if (i == 0)
            {
                if (type != 0 || len != 34) return null;
                var si = src.Get(p + 4, 34);
                if (si == null) return null;
                minBlock = (si[0] << 8) | si[1];
                maxFrame = (si[7] << 16) | (si[8] << 8) | si[9];
                sampleRate = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
                bps = (((si[12] & 1) << 4) | (si[13] >> 4)) + 1;
                totalSamples = ((long)(si[13] & 0x0F) << 32) | ((long)si[14] << 24) | ((long)si[15] << 16) | ((long)si[16] << 8) | si[17];
                if (sampleRate == 0 || minBlock < 16) return null;
            }
            else if (type == 127) return null;
            p += 4 + len;
            if ((hdr & 0x80) != 0) break;
        }
        // Frames.
        long samples = 0;
        int frames = 0;
        int crcErrors = 0;
        int frameCap = maxFrame > 0 ? maxFrame + 64 : 16 * 1024 * 1024;
        var buf = new byte[Math.Min(frameCap + 32, 32 * 1024 * 1024)];
        while (p < limit && (totalSamples <= 0 || samples < totalSamples))
        {
            int n = src.Read(p, buf.AsSpan(0, (int)Math.Min(buf.Length, limit - p)));
            if (n < 6) break;
            int blockSize = ParseFrameHeader(buf.AsSpan(0, n), out int hdrLen);
            if (blockSize <= 0) break;
            // Find frame end: CRC-16 over the whole frame equals 0, and the next frame header is valid (or the stream ends).
            ushort crc = 0;
            int end = -1;
            for (int i = 0; i < n; i++)
            {
                crc = Crc16(crc, buf[i]);
                if (i < hdrLen + 2 || crc != 0) continue;
                int fl = i + 1;
                bool last = totalSamples > 0 && samples + blockSize >= totalSamples;
                if (last || (fl + 6 <= n && ParseFrameHeader(buf.AsSpan(fl, n - fl), out _) > 0)) { end = fl; break; }
            }
            if (end < 0) { crcErrors++; break; }
            samples += blockSize;
            frames++;
            p += end;
        }
        if (frames == 0) return null;
        bool complete = totalSamples > 0 ? samples >= totalSamples && crcErrors == 0 : crcErrors == 0;
        return new CarveResult
        {
            Length = p - start,
            Check = complete ? StructureCheck.Verified : StructureCheck.Damaged,
            Extension = "flac",
            Detail = $"{sampleRate} Hz, {bps}-bit, {(sampleRate > 0 ? samples / (double)sampleRate : 0):0.0}s",
            Note = complete ? null : "frame CRC check failed or stream ended early (corrupted, truncated or fragmented)",
        };
    }

    private static int ParseFrameHeader(ReadOnlySpan<byte> b, out int len)
    {
        len = 0;
        if (b.Length < 6 || b[0] != 0xFF || (b[1] & 0xFE) != 0xF8) return -1;
        int bsCode = b[2] >> 4, srCode = b[2] & 0xF, ch = b[3] >> 4, ss = (b[3] >> 1) & 7;
        if (srCode == 15 || ch > 10 || ss == 3 || ss == 7 || (b[3] & 1) != 0 || bsCode == 0) return -1;
        int p = 4;
        // UTF-8-like coded number
        int c = b[p];
        int extra = c < 0x80 ? 0 : c >= 0xFE ? 6 : c >= 0xFC ? 5 : c >= 0xF8 ? 4 : c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : -1;
        if (extra < 0) return -1;
        p += 1 + extra;
        if (p + 3 > b.Length) return -1;
        int bs = bsCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (bsCode - 2),
            6 => b[p] + 1,
            7 => ((b[p] << 8) | b[p + 1]) + 1,
            _ => 256 << (bsCode - 8),
        };
        if (bsCode == 6) p += 1;
        else if (bsCode == 7) p += 2;
        if (srCode == 12) p += 1;
        else if (srCode is 13 or 14) p += 2;
        if (p >= b.Length) return -1;
        byte crc8 = 0;
        for (int i = 0; i < p; i++) crc8 = Crc8(crc8, b[i]);
        if (crc8 != b[p]) return -1;
        len = p + 1;
        return bs;
    }

    private static byte Crc8(byte crc, byte v)
    {
        crc ^= v;
        for (int i = 0; i < 8; i++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        return crc;
    }

    private static readonly ushort[] Crc16Table = BuildCrc16();
    private static ushort[] BuildCrc16()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
            t[i] = (ushort)c;
        }
        return t;
    }
    private static ushort Crc16(ushort crc, byte v) => (ushort)((crc << 8) ^ Crc16Table[(crc >> 8) ^ v]);
}

public sealed class OggFormat : CarveFormat
{
    public override string Id => "ogg";
    public override string Name => "Ogg (Vorbis / Opus / Theora)";
    public override FileCategory Category => FileCategory.Audio;
    public override string DefaultExtension => "ogg";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "OggS\0\u0002"u8.ToArray() };
    public override long MaxSize => 4L << 30;
    public override string Description => "Page walk with every page CRC verified until all logical streams reach end-of-stream.";

    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start;
        var open = new HashSet<uint>();
        int pages = 0, bad = 0;
        string ext = "ogg";
        var hdr = new byte[27 + 255];
        var page = new byte[27 + 255 + 255 * 255];
        while (p + 27 <= limit)
        {
            if (!src.Fill(p, hdr.AsSpan(0, 27)) || !hdr.AsSpan(0, 4).SequenceEqual("OggS"u8) || hdr[4] != 0) break;
            int nseg = hdr[26];
            if (!src.Fill(p + 27, hdr.AsSpan(27, nseg))) break;
            int body = 0;
            for (int i = 0; i < nseg; i++) body += hdr[27 + i];
            int plen = 27 + nseg + body;
            if (p + plen > limit || !src.Fill(p, page.AsSpan(0, plen))) break;
            uint stored = Bytes.U32(page, 22);
            page[22] = page[23] = page[24] = page[25] = 0;
            uint crc = 0;
            for (int i = 0; i < plen; i++) crc = (crc << 8) ^ Table[((crc >> 24) ^ page[i]) & 0xFF];
            if (crc != stored) { bad++; break; }
            uint serial = Bytes.U32(page, 14);
            byte type = page[5];
            if ((type & 2) != 0)
            {
                open.Add(serial);
                var b = page.AsSpan(27 + nseg, Math.Min(body, 16));
                if (pages == 0 && b.IndexOf("OpusHead"u8) >= 0) ext = "opus";
                if (pages == 0 && b.IndexOf("theora"u8) >= 0) ext = "ogv";
            }
            else if (pages == 0) return null; // first page must begin a stream
            pages++;
            p += plen;
            if ((type & 4) != 0)
            {
                open.Remove(serial);
                if (open.Count == 0) return new CarveResult { Length = p - start, Check = StructureCheck.Verified, Extension = ext, Detail = $"{pages} pages" };
            }
        }
        if (pages == 0) return null;
        return new CarveResult { Length = p - start, Check = StructureCheck.Damaged, Extension = ext, Note = bad > 0 ? "page CRC mismatch (corrupted or fragmented)" : "end-of-stream page not found" };
    }
}

public sealed class AsfFormat : CarveFormat
{
    private static readonly byte[] VideoMedia = new Guid("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B").ToByteArray();
    private static readonly byte[] FileProps = new Guid("8CABDCA1-A947-11CF-8EE4-00C00C205365").ToByteArray();
    public override string Id => "asf";
    public override string Name => "Windows Media (WMV/WMA/ASF)";
    public override FileCategory Category => FileCategory.Video;
    public override string DefaultExtension => "wmv";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray() };
    public override long MaxSize => 16L << 30;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var hsz = src.U64LE(start + 16);
        if (hsz is null or < 30 or > 16 * 1024 * 1024) return null;
        var header = src.Get(start, (int)hsz.Value);
        if (header == null) return null;
        int fp = header.AsSpan().IndexOf(FileProps);
        if (fp < 0 || fp + 48 > header.Length) return null;
        long fileSize = (long)Bytes.U64(header, fp + 40);
        if (fileSize < (long)hsz || fileSize > Math.Min(maxLength, MaxSize)) return null;
        string ext = header.AsSpan().IndexOf(VideoMedia) >= 0 ? "wmv" : "wma";
        return new CarveResult { Length = fileSize, Check = StructureCheck.Plausible, Extension = ext };
    }
}

public sealed class FlvFormat : CarveFormat
{
    public override string Id => "flv";
    public override string Name => "Flash video";
    public override FileCategory Category => FileCategory.Video;
    public override string DefaultExtension => "flv";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "FLV\u0001"u8.ToArray() };
    public override long MaxSize => 8L << 30;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long hdr = src.U32BE(start + 5);
        if (hdr != 9 || src.U32BE(start + 9) != 0) return null;
        long p = start + 13;
        int tags = 0;
        while (p + 11 <= limit)
        {
            int type = src.U8(p);
            long dsz = src.U24BE(p + 1);
            if ((type & 0x1F) is not (8 or 9 or 18) || dsz < 0) break;
            long prev = src.U32BE(p + 11 + dsz);
            if (prev != 11 + dsz) break;
            p += 11 + dsz + 4;
            tags++;
        }
        if (tags < 2) return null;
        return new CarveResult { Length = p - start, Check = StructureCheck.Plausible, Extension = "flv", Detail = $"{tags} tags" };
    }
}

/// <summary>MPEG transport streams: 188-byte .ts packets, or 192-byte .m2ts/.mts camcorder packets (sync byte at +4).</summary>
public sealed class MpegTsFormat : CarveFormat
{
    private readonly int _pkt;
    private readonly int _sync;
    public MpegTsFormat(bool m2ts) { _pkt = m2ts ? 192 : 188; _sync = m2ts ? 4 : 0; }
    public override string Id => _pkt == 192 ? "m2ts" : "mpegts";
    public override string Name => _pkt == 192 ? "MPEG transport stream, 192-byte (camcorder .MTS/.M2TS)" : "MPEG transport stream (.ts)";
    public override FileCategory Category => FileCategory.Video;
    public override string DefaultExtension => _pkt == 192 ? "mts" : "ts";
    public override int SignatureOffset => _sync;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x47 } };
    public override long MaxSize => 16L << 30;
    public override string Description => "Requires 64 consecutive packet sync bytes, then follows the packet chain. Payload is not checksummed.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        for (int i = 1; i < 64; i++)
            if (src.U8(start + _sync + (long)i * _pkt) != 0x47) return null;
        // A TS packet's PID field must not be all-ones-noise: check the transport error bit is clear on the first packet.
        if ((src.U8(start + _sync + 1) & 0x80) != 0) return null;
        long p = start;
        var buf = new byte[_pkt * 2048];
        while (p < limit)
        {
            int n = src.Read(p, buf.AsSpan(0, (int)Math.Min(buf.Length, limit - p)));
            int full = n / _pkt;
            int i = 0;
            for (; i < full; i++) if (buf[i * _pkt + _sync] != 0x47) break;
            p += (long)i * _pkt;
            if (i < full || full == 0 || n < buf.Length) break;
        }
        return new CarveResult { Length = p - start, Check = StructureCheck.Plausible, Extension = DefaultExtension, Note = "packet sync intact; payload not checksummed" };
    }
}

public sealed class MpegPsFormat : CarveFormat
{
    public override string Id => "mpegps";
    public override string Name => "MPEG program stream (.mpg/.vob)";
    public override FileCategory Category => FileCategory.Video;
    public override string DefaultExtension => "mpg";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x00, 0x00, 0x01, 0xBA } };
    public override long MaxSize => 8L << 30;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start;
        int packs = 0;
        var h = new byte[16];
        while (p + 4 <= limit && src.Fill(p, h.AsSpan(0, 4)) && h[0] == 0 && h[1] == 0 && h[2] == 1)
        {
            int code = h[3];
            if (code == 0xB9) { p += 4; return Done(start, p, packs, true); }
            if (code == 0xBA)
            {
                if (!src.Fill(p, h.AsSpan(0, 14))) break;
                if ((h[4] & 0xC0) == 0x40) p += 14 + (h[13] & 7); // MPEG-2
                else if ((h[4] & 0xF0) == 0x20) p += 12;           // MPEG-1
                else break;
                packs++;
            }
            else if (code >= 0xBB)
            {
                long len = src.U16BE(p + 4);
                if (len < 0) break;
                p += 6 + len;
            }
            else break;
        }
        if (packs < 4) return null;
        return Done(start, p, packs, false);
    }

    private static CarveResult Done(long s, long e, int packs, bool endCode) => new()
    {
        Length = e - s, Check = endCode ? StructureCheck.Plausible : StructureCheck.HeaderOnly, Extension = "mpg",
        Detail = $"{packs} packs", Note = endCode ? null : "program end code not found; end estimated from pack chain",
    };
}

public sealed class MidiFormat : CarveFormat
{
    public override string Id => "midi";
    public override string Name => "MIDI";
    public override FileCategory Category => FileCategory.Audio;
    public override string DefaultExtension => "mid";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "MThd\0\0\0\u0006"u8.ToArray() };
    public override long MaxSize => 64L << 20;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long tracks = src.U16BE(start + 10);
        if (tracks is <= 0 or > 1024) return null;
        long p = start + 14;
        for (int i = 0; i < tracks; i++)
        {
            if (!src.Matches(p, "MTrk"u8)) return i == 0 ? null : new CarveResult { Length = p - start, Check = StructureCheck.Damaged, Extension = "mid" };
            long len = src.U32BE(p + 4);
            if (len < 0) return null;
            p += 8 + len;
            if (p - start > Math.Min(maxLength, MaxSize)) return null;
        }
        return new CarveResult { Length = p - start, Check = StructureCheck.Plausible, Extension = "mid" };
    }
}

public sealed class AiffFormat : CarveFormat
{
    public override string Id => "aiff";
    public override string Name => "AIFF audio";
    public override FileCategory Category => FileCategory.Audio;
    public override string DefaultExtension => "aiff";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "FORM"u8.ToArray() };
    public override long MaxSize => 4L << 30;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long size = src.U32BE(start + 4);
        var form = src.Get(start + 8, 4);
        if (form == null || size < 12) return null;
        string f = Encoding.ASCII.GetString(form);
        if (f != "AIFF" && f != "AIFC") return null;
        long end = 8 + size + (size & 1);
        if (end > Math.Min(maxLength, MaxSize)) return null;
        return new CarveResult { Length = end, Check = StructureCheck.Plausible, Extension = f == "AIFC" ? "aifc" : "aiff" };
    }
}
