using System.Text;
using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.IO;
using DeepVault.Core.Util;

namespace DeepVault.Core.Preview;

public enum PreviewKind { None, Image, Text, Pdf, Hex }

public sealed class PreviewData
{
    public PreviewKind Kind { get; set; }
    /// <summary>Bytes for the viewer (image/PDF bytes, or the raw prefix for hex). Held in memory only.</summary>
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public string? Text { get; set; }
    public string? HexDump { get; set; }
    public bool Truncated { get; set; }
    public bool HadReadErrors { get; set; }
    public string? Message { get; set; }
    public string? EncodingName { get; set; }
}

/// <summary>
/// Produces previews entirely in memory: nothing is written to disk (no temp files), nothing is
/// executed, archives are not extracted. Content is read straight from the source via the layout.
/// </summary>
public sealed class PreviewService
{
    public const long MaxImageBytes = 64L * 1024 * 1024;
    public const long MaxPdfBytes = 64L * 1024 * 1024;
    public const int MaxTextBytes = 512 * 1024;
    public const int HexBytes = 4096;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "ico", "webp", "heic", "heif", "jxr", "wdp" };
    private static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase) { "cr2", "nef", "nrw", "arw", "dng", "orf", "rw2", "pef", "srw", "raf" };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "log", "md", "ini", "cfg", "csv", "xml", "json", "html", "htm", "rtf", "srt", "vcf", "ics", "eml", "yaml", "yml", "nfo", "svg", "plist",
        "c", "h", "cpp", "hpp", "cs", "java", "py", "js", "ts", "tsx", "jsx", "php", "rb", "go", "rs", "swift", "kt", "sh", "ps1", "bat", "cmd", "sql", "css", "scss", "vb", "pl", "lua",
    };

    private readonly ResilientReader _reader;
    public PreviewService(ResilientReader reader) => _reader = reader;

    public static PreviewKind KindFor(FoundFile f)
    {
        if (ImageExt.Contains(f.Extension) || RawExt.Contains(f.Extension)) return PreviewKind.Image;
        if (f.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Pdf;
        if (TextExt.Contains(f.Extension) || f.Category == FileCategory.Text || f.Category == FileCategory.SourceCode) return PreviewKind.Text;
        return PreviewKind.Hex;
    }

    public PreviewData Load(FoundFile f)
    {
        var kind = KindFor(f);
        var p = new PreviewData { Kind = kind };
        if (f.Size == 0) { p.Kind = PreviewKind.None; p.Message = "Empty file."; return p; }
        if (f.State == RecoveryState.Failed) { p.Kind = PreviewKind.None; p.Message = "No preview: the location of this file's content is unknown."; return p; }
        using var cs = new ContentStream(_reader, f.Layout);
        switch (kind)
        {
            case PreviewKind.Image when RawExt.Contains(f.Extension):
            {
                var all = ReadUpTo(cs, MaxImageBytes, out bool trunc);
                var jpg = ExtractLargestEmbeddedJpeg(all);
                if (jpg != null) { p.Bytes = jpg; p.Message = "Showing the camera's embedded JPEG preview."; }
                else { p.Kind = PreviewKind.Hex; p.Bytes = all.AsSpan(0, Math.Min(all.Length, HexBytes)).ToArray(); p.HexDump = Hex(p.Bytes); p.Message = "No embedded preview found in this RAW file."; }
                p.Truncated = trunc;
                break;
            }
            case PreviewKind.Image:
                if (f.Size > MaxImageBytes) { p.Kind = PreviewKind.Hex; p.Message = $"Image larger than {Bytes.FormatSize(MaxImageBytes)}: showing raw bytes only."; goto default; }
                p.Bytes = ReadUpTo(cs, MaxImageBytes, out bool t1);
                p.Truncated = t1;
                break;
            case PreviewKind.Pdf:
                if (f.Size > MaxPdfBytes) { p.Kind = PreviewKind.Hex; p.Message = $"PDF larger than {Bytes.FormatSize(MaxPdfBytes)}: showing raw bytes only."; goto default; }
                p.Bytes = ReadUpTo(cs, MaxPdfBytes, out bool t2);
                p.Truncated = t2;
                var pdf = PdfTextExtractor.Extract(p.Bytes);
                p.Text = pdf.Text;
                p.Message = pdf.Encrypted ? "This PDF is encrypted: its text cannot be previewed."
                    : pdf.Text.Length == 0 ? $"No extractable text ({pdf.PageCount} page(s)). It may be scanned images, damaged, or use filters DeepVault does not decode. Restore it to view it."
                    : pdf.LooksUnreadable ? "Text preview may be garbled: this PDF uses embedded font encodings."
                    : $"Text preview of a {pdf.PageCount}-page PDF (layout and images are not shown).";
                break;
            case PreviewKind.Text:
            {
                var b = ReadUpTo(cs, MaxTextBytes, out bool t3);
                p.Truncated = t3;
                (p.Text, p.EncodingName) = DecodeText(b);
                if (f.Extension.Equals("rtf", StringComparison.OrdinalIgnoreCase)) p.Text = RtfToPlain(p.Text);
                break;
            }
            default:
            {
                var b = ReadUpTo(cs, HexBytes, out bool t4);
                p.Bytes = b;
                p.HexDump = Hex(b);
                p.Truncated = t4;
                p.Message ??= "No visual preview for this type. Showing the first bytes so you can judge whether the content looks intact (all zeros usually means overwritten).";
                break;
            }
        }
        p.HadReadErrors = cs.HadReadErrors;
        if (cs.LayoutError != null) p.Message = cs.LayoutError;
        if (f.Layout.FragmentCount > 1 && f.Engines == EngineId.SignatureCarving) p.Message = (p.Message + " ").TrimStart() + "Carved files are assumed contiguous.";
        if (f.State == RecoveryState.PartialOrDamaged || f.Structure == StructureCheck.Damaged)
            p.Message = ((p.Message ?? "") + " This candidate is damaged or partly overwritten: the preview may be incomplete or misleading.").Trim();
        if (p.HadReadErrors) p.Message = ((p.Message ?? "") + " Some sectors were unreadable and are shown as zeros.").Trim();
        return p;
    }

    private static byte[] ReadUpTo(Stream s, long max, out bool truncated)
    {
        long len = Math.Min(s.Length, max);
        truncated = s.Length > max;
        var b = new byte[len];
        int pos = 0;
        while (pos < len)
        {
            int n = s.Read(b, pos, (int)(len - pos));
            if (n <= 0) break;
            pos += n;
        }
        return pos == len ? b : b.AsSpan(0, pos).ToArray();
    }

    public static (string text, string encoding) DecodeText(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), "UTF-8 (BOM)");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, (b.Length - 2) & ~1), "UTF-16 LE");
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, (b.Length - 2) & ~1), "UTF-16 BE");
        // UTF-16 without BOM: many zero bytes at odd positions.
        int zOdd = 0, n = Math.Min(b.Length, 4096);
        for (int i = 1; i < n; i += 2) if (b[i] == 0) zOdd++;
        if (n > 16 && zOdd > n / 4) return (Encoding.Unicode.GetString(b, 0, b.Length & ~1), "UTF-16 LE");
        try { return (new UTF8Encoding(false, true).GetString(b), "UTF-8"); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(b), "Windows-1252 / Latin-1"); }
    }

    /// <summary>Very small RTF-to-text conversion for preview only (control words removed).</summary>
    public static string RtfToPlain(string rtf)
    {
        var sb = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '{') { depth++; continue; }
            if (c == '}') { depth--; continue; }
            if (c == '\\')
            {
                if (i + 1 < rtf.Length && (rtf[i + 1] == '{' || rtf[i + 1] == '}' || rtf[i + 1] == '\\')) { sb.Append(rtf[++i]); continue; }
                int j = i + 1;
                while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                string word = rtf[(i + 1)..j];
                while (j < rtf.Length && (char.IsDigit(rtf[j]) || rtf[j] == '-')) j++;
                if (j < rtf.Length && rtf[j] == ' ') j++;
                if (word is "par" or "line") sb.Append('\n');
                else if (word == "tab") sb.Append('\t');
                i = j - 1;
                continue;
            }
            if (c is '\r' or '\n') continue;
            if (depth <= 1 || sb.Length > 0) sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Hex(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int k = 0; k < 16; k++) sb.Append(i + k < b.Length ? b[i + k].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int k = 0; k < 16 && i + k < b.Length; k++) { byte c = b[i + k]; sb.Append(c >= 32 && c < 127 ? (char)c : '.'); }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Finds the largest complete JPEG embedded in a RAW file (camera previews), validated with the JPEG analyzer.</summary>
    public static byte[]? ExtractLargestEmbeddedJpeg(byte[] data)
    {
        var src = new ByteArrayCarveSource(data);
        var jpeg = new Formats_JpegProxy();
        byte[]? best = null;
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;
            var r = jpeg.Analyze(src, i, data.Length - i);
            if (r is { Check: StructureCheck.Verified } && (best == null || r.Length > best.Length))
                best = data.AsSpan(i, (int)r.Length).ToArray();
            if (r is { Check: StructureCheck.Verified }) i += (int)r.Length - 1;
        }
        return best;
    }

    private sealed class Formats_JpegProxy
    {
        private readonly Carving.Formats.JpegFormat _f = new();
        public CarveResult? Analyze(ICarveSource s, long start, long max) => _f.SafeAnalyze(s, start, max);
    }
}
