using System.IO.Compression;
using System.Text;
using DeepVault.Core.Catalog;
using DeepVault.Core.Util;

namespace DeepVault.Core.Carving.Formats;

public sealed class PdfFormat : CarveFormat
{
    public override string Id => "pdf";
    public override string Name => "PDF document";
    public override FileCategory Category => FileCategory.Pdf;
    public override string DefaultExtension => "pdf";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "%PDF-"u8.ToArray() };
    public override long MaxSize => 1L << 30;
    public override string Description => "Searches %%EOF markers (incl. incremental updates); each is accepted only if its startxref points to a valid xref table/stream whose entries point to real objects.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var head = src.Get(start, 8);
        if (head == null || head[5] < '1' || head[5] > '2' || head[6] != '.') return null;
        long limit = start + Math.Min(maxLength, MaxSize);
        long p = start + 8;
        long bestEnd = -1;
        bool bestVerified = false;
        int eofs = 0;
        while (p < limit)
        {
            long eof = src.Find("%%EOF"u8, p, limit);
            if (eof < 0) break;
            eofs++;
            long end = eof + 5;
            // Include the line terminator.
            int c = src.U8(end);
            if (c == '\r') { end++; if (src.U8(end) == '\n') end++; }
            else if (c == '\n') end++;
            var v = ValidateStartXref(src, start, eof);
            if (v != 0)
            {
                bestEnd = end;
                bestVerified = v == 2;
                // Continue only if an incremental update follows directly.
                if (!FollowedByPdfContent(src, end)) break;
            }
            else if (bestEnd < 0 && eofs > 64) break;
            p = eof + 5;
        }
        if (bestEnd < 0)
            return new CarveResult { Length = Math.Min(64 * 1024, limit - start), Check = StructureCheck.HeaderOnly, Extension = "pdf", Note = "no valid %%EOF/startxref found (truncated, fragmented or damaged)" };
        return new CarveResult
        {
            Length = bestEnd - start,
            Check = bestVerified ? StructureCheck.Verified : StructureCheck.Plausible,
            Extension = "pdf",
            Detail = $"PDF {(char)head[5]}.{(char)head[7]}",
            Note = bestVerified ? "cross-reference table consistent" : "startxref target found; xref entries not fully checked",
        };
    }

    private static bool FollowedByPdfContent(ICarveSource src, long p)
    {
        var b = src.Get(p, 32);
        if (b == null) return false;
        var s = Encoding.ASCII.GetString(b).TrimStart('\r', '\n', ' ', '\0');
        if (s.Length == 0) return false;
        return s.StartsWith("xref") || (char.IsDigit(s[0]) && s.Contains(" obj"));
    }

    /// <returns>0 = invalid, 1 = target looks right, 2 = xref entries verified</returns>
    private static int ValidateStartXref(ICarveSource src, long start, long eof)
    {
        long from = Math.Max(start, eof - 64);
        var tail = src.Get(from, (int)(eof - from));
        if (tail == null) return 0;
        var t = Encoding.ASCII.GetString(tail);
        int i = t.LastIndexOf("startxref", StringComparison.Ordinal);
        if (i < 0) return 0;
        var digits = new string(t[(i + 9)..].SkipWhile(char.IsWhiteSpace).TakeWhile(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out long xref) || start + xref >= eof) return 0;
        var at = src.Get(start + xref, 64);
        if (at == null) return 0;
        var s = Encoding.ASCII.GetString(at);
        if (s.StartsWith("xref"))
        {
            // Check the first entries of the first subsection: "nnnnnnnnnn ggggg n" must point at "N G obj".
            var tbl = src.Get(start + xref, (int)Math.Min(4096, src.Length - (start + xref)));
            if (tbl == null) return 1;
            var lines = Encoding.ASCII.GetString(tbl).Split('\n').Select(l => l.Trim('\r', ' ')).ToArray();
            int checkedN = 0;
            for (int k = 2; k < lines.Length - 1 && checkedN < 16; k++)
            {
                var parts = lines[k].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0].Length != 10) break;
                if (parts[2] != "n") continue;
                if (!long.TryParse(parts[0], out long off) || start + off >= eof) return 1;
                var obj = src.Get(start + off, 24);
                if (obj == null) return 1;
                var os = Encoding.ASCII.GetString(obj);
                if (!char.IsDigit(os[0]) || !os.Contains(" obj")) return 1;
                checkedN++;
            }
            return checkedN > 0 ? 2 : 1;
        }
        if (s.Length > 0 && char.IsDigit(s[0]) && s.Contains(" obj"))
        {
            var obj = src.Get(start + xref, 1024);
            return obj != null && Encoding.ASCII.GetString(obj).Contains("/XRef") ? 2 : 1;
        }
        return 0;
    }
}

/// <summary>ZIP archives and ZIP-based documents (DOCX/XLSX/PPTX, ODF, EPUB, JAR, APK).</summary>
public sealed class ZipFormat : CarveFormat
{
    public override string Id => "zip";
    public override string Name => "ZIP / Office Open XML / OpenDocument";
    public override FileCategory Category => FileCategory.Archive;
    public override string DefaultExtension => "zip";
    public override bool HasChecksums => true;
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } };
    public override long MaxSize => 4L << 30;
    /// <summary>Entries are decompressed and CRC-checked when the archive is at most this large.</summary>
    public long CrcCheckLimit { get; init; } = 256L * 1024 * 1024;
    public override string Description => "Locates the end-of-central-directory record whose offsets are consistent with the start, walks the central directory, and CRC-32-checks every entry (archives up to 256 MB).";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        var lh = src.Get(start, 30);
        if (lh == null) return null;
        int nameLen = Bytes.U16(lh, 26);
        if (Bytes.U16(lh, 4) > 63 || nameLen == 0 || nameLen > 1024) return null;
        long p = start + 30;
        while (p < limit)
        {
            long eocd = src.Find(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, p, limit);
            if (eocd < 0) break;
            var e = src.Get(eocd, 22);
            if (e == null) break;
            long entries = Bytes.U16(e, 10);
            long cdSize = Bytes.U32(e, 12);
            long cdOff = Bytes.U32(e, 16);
            int comment = Bytes.U16(e, 20);
            bool zip64 = cdOff == 0xFFFFFFFF || entries == 0xFFFF;
            if (zip64)
            {
                long loc = eocd - 20;
                if (src.Matches(loc, new byte[] { 0x50, 0x4B, 0x06, 0x07 }))
                {
                    long z64 = (long)(src.U64LE(loc + 8) ?? 0);
                    if (src.Matches(start + z64, new byte[] { 0x50, 0x4B, 0x06, 0x06 }))
                    {
                        entries = (long)(src.U64LE(start + z64 + 32) ?? 0);
                        cdSize = (long)(src.U64LE(start + z64 + 40) ?? 0);
                        cdOff = (long)(src.U64LE(start + z64 + 48) ?? 0);
                        if (start + cdOff + cdSize == start + z64) { return Finish(src, start, eocd + 22 + comment, cdOff, entries); }
                    }
                }
            }
            else if (start + cdOff + cdSize == eocd && Bytes.U16(e, 4) == 0 && Bytes.U16(e, 8) == entries)
            {
                return Finish(src, start, eocd + 22 + comment, cdOff, entries);
            }
            p = eocd + 4;
        }
        // No usable central directory: estimate the surviving part by walking local file headers,
        // so partially intact archives can still be restored and salvaged with archive repair tools.
        long q = start;
        int localEntries = 0;
        while (q + 30 <= limit && src.Matches(q, new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            long flags = src.U16LE(q + 6), csize = src.U32LE(q + 18), nl = src.U16LE(q + 26), xl = src.U16LE(q + 28);
            if ((flags & 8) != 0 || csize < 0 || nl < 0 || xl < 0) break; // sizes in a data descriptor: cannot step over
            q += 30 + nl + xl + csize;
            localEntries++;
        }
        return new CarveResult
        {
            Length = Math.Min(limit, Math.Max(q, start + 512)) - start, Check = StructureCheck.Damaged, Extension = "zip",
            Note = $"central directory not found (truncated, fragmented or overwritten); {localEntries} local entries precede the damage",
        };
    }

    private CarveResult Finish(ICarveSource src, long start, long end, long cdOff, long entries)
    {
        var names = new List<string>();
        var items = new List<(long localOff, long csize, long usize, uint crc, int method)>();
        long p = start + cdOff;
        bool cdOk = true;
        for (long i = 0; i < Math.Min(entries, 200000); i++)
        {
            var c = src.Get(p, 46);
            if (c == null || Bytes.U32(c, 0) != 0x02014B50) { cdOk = false; break; }
            int nl = Bytes.U16(c, 28), xl = Bytes.U16(c, 30), cl = Bytes.U16(c, 32);
            var nb = src.Get(p + 46, nl);
            string name = nb == null ? "" : Encoding.UTF8.GetString(nb);
            names.Add(name);
            long csize = Bytes.U32(c, 20), usize = Bytes.U32(c, 24), loff = Bytes.U32(c, 42);
            if (csize == 0xFFFFFFFF || usize == 0xFFFFFFFF || loff == 0xFFFFFFFF)
            {
                var extra = src.Get(p + 46 + nl, xl);
                if (extra != null) ParseZip64Extra(extra, ref usize, ref csize, ref loff);
            }
            items.Add((loff, csize, usize, Bytes.U32(c, 16), Bytes.U16(c, 10)));
            p += 46 + nl + xl + cl;
        }
        string ext = Classify(src, start, names, items);
        if (!cdOk)
            return new CarveResult { Length = end - start, Check = StructureCheck.Damaged, Extension = ext, Note = "central directory entries corrupted" };
        // Local headers must be where the central directory says.
        foreach (var it in items)
            if (!src.Matches(start + it.localOff, new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
                return new CarveResult { Length = end - start, Check = StructureCheck.Damaged, Extension = ext, Note = "local file headers missing (fragmented or overwritten)" };
        int crcFail = 0, crcChecked = 0;
        bool crcDone = false;
        if (end - start <= CrcCheckLimit)
        {
            crcDone = true;
            foreach (var it in items)
            {
                if (it.usize == 0 && it.csize == 0) continue;
                long lhOff = start + it.localOff;
                long nl = src.U16LE(lhOff + 26), xl = src.U16LE(lhOff + 28);
                long dataOff = lhOff + 30 + nl + xl;
                using var slice = new SliceStream(src, dataOff, it.csize);
                uint crc = 0;
                try
                {
                    Stream s = it.method switch
                    {
                        0 => slice,
                        8 => new DeflateStream(slice, CompressionMode.Decompress),
                        _ => Stream.Null,
                    };
                    if (s == Stream.Null) continue;
                    var buf = new byte[64 * 1024];
                    long total = 0;
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0 && total <= it.usize)
                    {
                        crc = Crc32.Append(crc, buf.AsSpan(0, n));
                        total += n;
                    }
                    if (crc != it.crc || total != it.usize) crcFail++;
                    crcChecked++;
                    if (s != slice) s.Dispose();
                }
                catch (InvalidDataException) { crcFail++; crcChecked++; }
            }
        }
        var check = crcFail > 0 ? StructureCheck.Damaged : crcDone ? StructureCheck.Verified : StructureCheck.Plausible;
        return new CarveResult
        {
            Length = end - start, Check = check, Extension = ext, Detail = $"{items.Count} entries",
            Note = crcFail > 0 ? $"{crcFail} of {crcChecked} entries failed CRC-32 (corrupted, overwritten or fragmented)" : crcDone ? "all entry CRC-32 checks passed" : "archive too large for CRC check",
        };
    }

    private static void ParseZip64Extra(byte[] x, ref long usize, ref long csize, ref long loff)
    {
        int p = 0;
        while (p + 4 <= x.Length)
        {
            int id = Bytes.U16(x, p), sz = Bytes.U16(x, p + 2);
            if (id == 1)
            {
                int q = p + 4;
                if (usize == 0xFFFFFFFF && q + 8 <= x.Length) { usize = Bytes.I64(x, q); q += 8; }
                if (csize == 0xFFFFFFFF && q + 8 <= x.Length) { csize = Bytes.I64(x, q); q += 8; }
                if (loff == 0xFFFFFFFF && q + 8 <= x.Length) { loff = Bytes.I64(x, q); }
                return;
            }
            p += 4 + sz;
        }
    }

    private static string Classify(ICarveSource src, long start, List<string> names, List<(long localOff, long csize, long usize, uint crc, int method)> items)
    {
        bool Has(string n) => names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        bool Pre(string n) => names.Any(x => x.StartsWith(n, StringComparison.OrdinalIgnoreCase));
        bool macro = Pre("word/vbaProject") || Pre("xl/vbaProject") || Pre("ppt/vbaProject");
        if (Has("word/document.xml")) return macro ? "docm" : "docx";
        if (Has("xl/workbook.xml")) return macro ? "xlsm" : "xlsx";
        if (Has("xl/workbook.bin")) return "xlsb";
        if (Has("ppt/presentation.xml")) return macro ? "pptm" : "pptx";
        if (Has("visio/document.xml")) return "vsdx";
        int mi = names.FindIndex(n => n == "mimetype");
        if (mi >= 0 && items[mi].method == 0 && items[mi].csize < 128)
        {
            long lhOff = start + items[mi].localOff;
            var mt = src.Get(lhOff + 30 + src.U16LE(lhOff + 26) + src.U16LE(lhOff + 28), (int)items[mi].csize);
            string m = mt == null ? "" : Encoding.ASCII.GetString(mt);
            if (m.EndsWith("opendocument.text")) return "odt";
            if (m.EndsWith("opendocument.spreadsheet")) return "ods";
            if (m.EndsWith("opendocument.presentation")) return "odp";
            if (m == "application/epub+zip") return "epub";
        }
        if (Has("AndroidManifest.xml") && Has("classes.dex")) return "apk";
        if (Has("META-INF/MANIFEST.MF")) return "jar";
        if (Has("[Content_Types].xml") && Pre("Documents/")) return "xps";
        return "zip";
    }
}

/// <summary>Read-only stream over a range of a carve source.</summary>
public sealed class SliceStream : Stream
{
    private readonly ICarveSource _src;
    private readonly long _start;
    private readonly long _len;
    private long _pos;
    public SliceStream(ICarveSource src, long start, long len) { _src = src; _start = start; _len = Math.Max(0, len); }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _len;
    public override long Position { get => _pos; set => _pos = value; }
    public long BytesRead { get; private set; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= _len) return 0;
        int n = _src.Read(_start + _pos, buffer.AsSpan(offset, (int)Math.Min(count, _len - _pos)));
        _pos += n;
        BytesRead += n;
        return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _len + offset;
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>OLE2 compound files: legacy Word/Excel/PowerPoint, Outlook .msg, Visio, MSI.</summary>
public sealed class Ole2Format : CarveFormat
{
    public override string Id => "ole2";
    public override string Name => "OLE2 compound file (DOC / XLS / PPT / MSG)";
    public override FileCategory Category => FileCategory.Document;
    public override string DefaultExtension => "doc";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 } };
    public override long MaxSize => 2L << 30;
    public override string Description => "Size computed from the sector allocation table (FAT, via DIFAT); directory and stream chains validated. No content checksums.";

    private const uint EndOfChain = 0xFFFFFFFE, Free = 0xFFFFFFFF;

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var h = src.Get(start, 512);
        if (h == null || Bytes.U16(h, 0x1C) != 0xFFFE) return null;
        int shift = Bytes.U16(h, 0x1E);
        if (shift != 9 && shift != 12) return null;
        int ss = 1 << shift;
        long nFat = Bytes.U32(h, 0x2C);
        long firstDir = Bytes.U32(h, 0x30);
        long firstDifat = Bytes.U32(h, 0x44);
        long nDifat = Bytes.U32(h, 0x48);
        long limit = Math.Min(maxLength, MaxSize);
        if (nFat == 0 || nFat > limit / ss / (ss / 4) + 2) return null;
        long Off(long sec) => start + (sec + 1) * ss;

        var fatSectors = new List<long>();
        for (int i = 0; i < 109 && fatSectors.Count < nFat; i++)
        {
            uint s = Bytes.U32(h, 0x4C + i * 4);
            if (s == Free) break;
            fatSectors.Add(s);
        }
        long dif = firstDifat;
        for (long k = 0; k < nDifat && fatSectors.Count < nFat; k++)
        {
            if (dif >= 0xFFFFFFFA) break;
            var d = src.Get(Off(dif), ss);
            if (d == null) return Damaged(ss, "DIFAT unreadable");
            for (int i = 0; i < ss / 4 - 1 && fatSectors.Count < nFat; i++) fatSectors.Add(Bytes.U32(d, i * 4));
            dif = Bytes.U32(d, ss - 4);
        }
        if (fatSectors.Count != nFat) return Damaged(ss, "FAT sector list incomplete");
        var fat = new uint[nFat * (ss / 4)];
        for (int i = 0; i < fatSectors.Count; i++)
        {
            if ((fatSectors[i] + 2) * ss > limit) return Damaged(ss, "FAT sector out of range");
            var fs = src.Get(Off(fatSectors[i]), ss);
            if (fs == null) return Damaged(ss, "FAT unreadable");
            for (int j = 0; j < ss / 4; j++) fat[i * (ss / 4) + j] = Bytes.U32(fs, j * 4);
        }
        long maxUsed = -1;
        for (long i = fat.Length - 1; i >= 0; i--) if (fat[i] != Free) { maxUsed = i; break; }
        if (maxUsed < 0) return null;
        long length = (maxUsed + 2) * ss;
        if (length > limit) return Damaged(ss, "declared size exceeds readable area");

        // Directory: root entry must be first and be named "Root Entry".
        var names = new List<string>();
        long sec = firstDir;
        int guard = 0;
        bool rootOk = false, chainsOk = true;
        while (sec < EndOfChain - 4 && sec < fat.Length && guard++ < 100000)
        {
            var dir = src.Get(Off(sec), ss);
            if (dir == null) break;
            for (int e = 0; e < ss / 128; e++)
            {
                int nl = Bytes.U16(dir, e * 128 + 64);
                int type = dir[e * 128 + 66];
                if (nl < 2 || nl > 64 || type == 0) continue;
                string name = Encoding.Unicode.GetString(dir, e * 128, nl - 2);
                if (guard == 1 && e == 0) rootOk = type == 5 && name == "Root Entry";
                names.Add(name);
                if (type == 2)
                {
                    long size = Bytes.U32(dir, e * 128 + 120);
                    long st = Bytes.U32(dir, e * 128 + 116);
                    if (size >= Bytes.U32(h, 0x38)) chainsOk &= ChainOk(fat, st, (size + ss - 1) / ss);
                }
            }
            sec = fat[sec];
        }
        if (!rootOk) return guard <= 1 ? null : Damaged(ss, "root directory entry not found");
        string ext = names.Contains("WordDocument") ? "doc"
            : names.Contains("Workbook") || names.Contains("Book") ? "xls"
            : names.Contains("PowerPoint Document") ? "ppt"
            : names.Any(n => n.StartsWith("__substg1.0_")) ? "msg"
            : names.Contains("VisioDocument") ? "vsd"
            : names.Contains("\u0005SummaryInformation") && names.Count <= 4 ? "ole"
            : "ole";
        if (ext == "ole" && names.Any(n => n.Length > 0 && n[0] >= 0x3800 && n[0] < 0x4840)) ext = "msi";
        return new CarveResult
        {
            Length = length,
            Check = chainsOk ? StructureCheck.Verified : StructureCheck.Damaged,
            Extension = ext,
            Detail = $"{names.Count} directory entries",
            Note = chainsOk ? "allocation table and stream chains consistent (content itself has no checksum)" : "stream sector chains inconsistent (corrupted or fragmented)",
        };
    }

    private static bool ChainOk(uint[] fat, long s, long expected)
    {
        long n = 0;
        var seen = new HashSet<long>();
        while (s < EndOfChain - 4)
        {
            if (s >= fat.Length || !seen.Add(s)) return false;
            n++;
            s = fat[s];
            if (n > expected + 1) return false;
        }
        return s == EndOfChain && n == expected;
    }

    private static CarveResult Damaged(int ss, string why) => new() { Length = ss * 8, Check = StructureCheck.Damaged, Extension = "doc", Note = why };
}

public sealed class RtfFormat : CarveFormat
{
    public override string Id => "rtf";
    public override string Name => "Rich Text Format";
    public override FileCategory Category => FileCategory.Document;
    public override string DefaultExtension => "rtf";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "{\\rtf"u8.ToArray() };
    public override long MaxSize => 256L * 1024 * 1024;
    public override string Description => "Brace-depth tracking (escapes and \\bin data honoured) until the document's closing brace.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        var buf = new byte[64 * 1024];
        long p = start;
        int depth = 0;
        while (p < limit)
        {
            int n = src.Read(p, buf.AsSpan(0, (int)Math.Min(buf.Length, limit - p)));
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                byte c = buf[i];
                if (c == 0) return new CarveResult { Length = p + i - start, Check = StructureCheck.Damaged, Extension = "rtf", Note = "NUL byte before closing brace (truncated or fragmented)" };
                if (c == '\\' && i + 1 < n && (buf[i + 1] == '{' || buf[i + 1] == '}' || buf[i + 1] == '\\')) { i++; continue; }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                    return new CarveResult { Length = p + i + 1 - start, Check = StructureCheck.Plausible, Extension = "rtf", Note = "braces balanced" };
            }
            p += n;
        }
        return new CarveResult { Length = p - start, Check = StructureCheck.Damaged, Extension = "rtf", Note = "closing brace not found" };
    }
}

public sealed class PstFormat : CarveFormat
{
    public override string Id => "pst";
    public override string Name => "Outlook data file (PST/OST)";
    public override FileCategory Category => FileCategory.Email;
    public override string DefaultExtension => "pst";
    public override IReadOnlyList<byte[]> Signatures { get; } = new[] { "!BDN"u8.ToArray() };
    public override long MaxSize => 64L << 30;
    public override string Description => "End-of-file offset from the PST header (ANSI and Unicode). No checksums.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long client = src.U16LE(start + 8);
        long ver = src.U16LE(start + 10);
        if (client != 0x4D53 && client != 0x4F53) return null; // "SM" (pst) or "SO" (ost)
        long eof;
        if (ver is 14 or 15) eof = src.U32LE(start + 0xA8);
        else if (ver is 23 or 36) eof = (long)(src.U64LE(start + 0xB8) ?? 0);
        else return null;
        if (eof < 512 || eof > Math.Min(maxLength, MaxSize)) return null;
        return new CarveResult { Length = eof, Check = StructureCheck.Plausible, Extension = client == 0x4F53 ? "ost" : "pst", Detail = ver >= 23 ? "Unicode" : "ANSI" };
    }
}

/// <summary>Text-based formats with a recognisable opening (XML, HTML, e-mail, vCard, iCalendar).</summary>
public sealed class TextMarkupFormat : CarveFormat
{
    private readonly string _id, _name, _ext;
    private readonly FileCategory _cat;
    private readonly byte[][] _sigs;
    private readonly bool _caseInsensitive;

    public TextMarkupFormat(string id, string name, string ext, FileCategory cat, bool caseInsensitive, params string[] sigs)
    {
        _id = id; _name = name; _ext = ext; _cat = cat; _caseInsensitive = caseInsensitive;
        var list = new List<byte[]>();
        foreach (var s in sigs)
        {
            list.Add(Encoding.ASCII.GetBytes(s));
            if (caseInsensitive) list.Add(Encoding.ASCII.GetBytes(s.ToUpperInvariant()));
        }
        _sigs = list.Distinct(new ByteArrayComparer()).ToArray();
    }

    public override string Id => _id;
    public override string Name => _name;
    public override FileCategory Category => _cat;
    public override string DefaultExtension => _ext;
    public override IReadOnlyList<byte[]> Signatures => _sigs;
    public override long MaxSize => 64L * 1024 * 1024;
    public override string Description => "Text run from the signature until the first byte that cannot be text (usually the zero slack after the file). No checksums.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long end = TextEnd(src, start, start + Math.Min(maxLength, MaxSize));
        if (end - start < 16) return null;
        string ext = _ext;
        if (_id == "xml")
        {
            var head = src.Get(start, (int)Math.Min(512, end - start));
            var hs = head == null ? "" : Encoding.UTF8.GetString(head);
            if (hs.Contains("<svg", StringComparison.OrdinalIgnoreCase)) ext = "svg";
            else if (hs.Contains("<plist")) ext = "plist";
        }
        return new CarveResult { Length = end - start, Check = StructureCheck.HeaderOnly, Extension = ext, Note = "text files have no end marker; end taken at the first non-text byte" };
    }

    internal static long TextEnd(ICarveSource src, long start, long limit)
    {
        var buf = new byte[64 * 1024];
        long p = start;
        while (p < limit)
        {
            int n = src.Read(p, buf.AsSpan(0, (int)Math.Min(buf.Length, limit - p)));
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                byte c = buf[i];
                bool ok = c >= 0x20 || c == '\n' || c == '\r' || c == '\t' || c == 0x0C;
                if (c >= 0x80)
                {
                    // Accept well-formed UTF-8 sequences.
                    int len = c >= 0xF0 && c < 0xF5 ? 4 : c >= 0xE0 ? 3 : c >= 0xC2 && c < 0xE0 ? 2 : 0;
                    if (len == 0 || i + len > n) return p + i;
                    for (int k = 1; k < len; k++) if ((buf[i + k] & 0xC0) != 0x80) return p + i;
                    i += len - 1;
                    continue;
                }
                if (!ok || c == 0x7F) return p + i;
            }
            p += n;
        }
        return p;
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? a, byte[]? b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] o) => o.Length > 0 ? o[0] * 31 + o.Length : 0;
    }
}

/// <summary>Plain text without a signature. Opt-in: on a system disk this finds very large numbers of fragments.</summary>
public sealed class PlainTextFormat : CarveFormat
{
    public override string Id => "text";
    public override string Name => "Plain text (no signature, experimental)";
    public override FileCategory Category => FileCategory.Text;
    public override string DefaultExtension => "txt";
    public override bool EnabledByDefault => false;
    public override IReadOnlyList<byte[]> Signatures { get; } = Array.Empty<byte[]>();
    public override long MaxSize => 16L * 1024 * 1024;
    public override string Description => "Any sector that begins with at least 256 bytes of printable text. Produces many fragments; enable only when looking for lost text documents.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        var head = src.Get(start, 256);
        if (head == null) return null;
        int letters = 0;
        foreach (var c in head)
        {
            if (c < 0x09 || (c > 0x0D && c < 0x20) || c >= 0x7F) return null;
            if (char.IsLetter((char)c) || c == ' ') letters++;
        }
        if (letters < 180) return null;
        long end = TextMarkupFormat.TextEnd(src, start, start + Math.Min(maxLength, MaxSize));
        return new CarveResult { Length = end - start, Check = StructureCheck.HeaderOnly, Extension = "txt", Note = "unsigned text: boundaries are estimates" };
    }
}
