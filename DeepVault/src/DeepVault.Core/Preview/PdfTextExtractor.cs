using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepVault.Core.Preview;

/// <summary>
/// Safe, minimal PDF text extraction for previews: decodes Flate content streams and collects the
/// strings shown by text operators (Tj, TJ, ', "). It never runs scripts, renders, or follows links.
/// Layout, images and text drawn with embedded custom-encoded fonts are not reproduced.
/// </summary>
public static class PdfTextExtractor
{
    public sealed record Result(string Text, int PageCount, int StreamsDecoded, bool Encrypted, bool LooksUnreadable);

    private static readonly Regex PageRx = new(@"/Type\s*/Page[^s]", RegexOptions.Compiled);

    public static Result Extract(byte[] pdf, int maxChars = 200_000)
    {
        var latin = Encoding.Latin1.GetString(pdf);
        bool encrypted = latin.Contains("/Encrypt");
        int pages = PageRx.Matches(latin).Count;
        var sb = new StringBuilder();
        int decoded = 0;
        int pos = 0;
        while (sb.Length < maxChars)
        {
            int s = latin.IndexOf("stream", pos, StringComparison.Ordinal);
            if (s < 0) break;
            int dictStart = latin.LastIndexOf("<<", s, Math.Min(s, 4096), StringComparison.Ordinal);
            string dict = dictStart >= 0 ? latin[dictStart..s] : "";
            int dataStart = s + 6;
            if (dataStart < latin.Length && latin[dataStart] == '\r') dataStart++;
            if (dataStart < latin.Length && latin[dataStart] == '\n') dataStart++;
            int end = latin.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (end < 0) break;
            pos = end + 9;
            if (dict.Contains("/Subtype/Image") || dict.Contains("/Subtype /Image") || dict.Contains("/FontFile")) continue;
            byte[] data = pdf.AsSpan(dataStart, end - dataStart).ToArray();
            if (dict.Contains("/FlateDecode"))
            {
                data = Inflate(data);
                if (data.Length == 0) continue;
            }
            else if (dict.Contains("/Filter")) continue; // other filters are not decoded
            decoded++;
            ExtractOperators(Encoding.Latin1.GetString(data), sb, maxChars);
        }
        var text = sb.ToString().Trim();
        int printable = text.Count(c => !char.IsControl(c) || c == '\n');
        bool unreadable = text.Length > 0 && text.Count(c => char.IsLetterOrDigit(c) || c == ' ') < text.Length / 2;
        return new Result(text, pages, decoded, encrypted, unreadable || (printable == 0 && decoded > 0));
    }

    private static byte[] Inflate(byte[] data)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var o = new MemoryStream();
            var buf = new byte[65536];
            int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0)
            {
                o.Write(buf, 0, n);
                if (o.Length > 16 * 1024 * 1024) break;
            }
            return o.ToArray();
        }
        catch (InvalidDataException) { return Array.Empty<byte>(); }
    }

    private static void ExtractOperators(string c, StringBuilder sb, int maxChars)
    {
        int i = 0;
        bool inText = false;
        var pending = new StringBuilder();
        while (i < c.Length && sb.Length < maxChars)
        {
            char ch = c[i];
            if (ch == '(') { pending.Append(ReadLiteral(c, ref i)); continue; }
            if (ch == '<' && i + 1 < c.Length && c[i + 1] != '<') { pending.Append(ReadHex(c, ref i)); continue; }
            if (char.IsLetter(ch) || ch == '\'' || ch == '"' || ch == '*')
            {
                int s = i;
                while (i < c.Length && (char.IsLetter(c[i]) || c[i] == '*' || c[i] == '\'' || c[i] == '"')) i++;
                string op = c[s..i];
                switch (op)
                {
                    case "BT": inText = true; pending.Clear(); break;
                    case "ET": inText = false; NewLine(sb); pending.Clear(); break;
                    case "Tj" or "TJ" when inText: sb.Append(pending); pending.Clear(); break;
                    case "'" or "\"" when inText: NewLine(sb); sb.Append(pending); pending.Clear(); break;
                    case "T*" or "Td" or "TD" when inText: NewLine(sb); pending.Clear(); break;
                    default: if (op != "Tf" && op != "Tc" && op != "Tw" && op != "Tz" && op != "TL") pending.Clear(); break;
                }
                continue;
            }
            if (ch == '[' || ch == ']' || char.IsDigit(ch) || ch == '-' || ch == '.' || char.IsWhiteSpace(ch) || ch == '/') { i++; continue; }
            i++;
        }
    }

    private static void NewLine(StringBuilder sb) { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); }

    private static string ReadLiteral(string c, ref int i)
    {
        var sb = new StringBuilder();
        int depth = 0;
        for (; i < c.Length; i++)
        {
            char ch = c[i];
            if (ch == '\\' && i + 1 < c.Length)
            {
                char n = c[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': break;
                    case 't': sb.Append('\t'); break;
                    case '(' or ')' or '\\': sb.Append(n); break;
                    default:
                        if (n >= '0' && n <= '7')
                        {
                            int v = 0, k = 0;
                            while (k < 3 && i < c.Length && c[i] >= '0' && c[i] <= '7') { v = v * 8 + (c[i] - '0'); i++; k++; }
                            i--;
                            sb.Append((char)v);
                        }
                        break;
                }
                continue;
            }
            if (ch == '(') { if (depth++ > 0) sb.Append(ch); continue; }
            if (ch == ')') { if (--depth == 0) { i++; break; } sb.Append(ch); continue; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string ReadHex(string c, ref int i)
    {
        int end = c.IndexOf('>', i);
        if (end < 0) { i = c.Length; return ""; }
        var hex = new string(c[(i + 1)..end].Where(Uri.IsHexDigit).ToArray());
        i = end + 1;
        if (hex.Length % 2 == 1) hex += "0";
        var bytes = Convert.FromHexString(hex);
        // Two-byte (CID) strings are common; treat as UTF-16BE when that looks plausible.
        if (bytes.Length >= 2 && bytes.Length % 2 == 0 && bytes.Where((b, k) => k % 2 == 0).All(b => b == 0)) return Encoding.BigEndianUnicode.GetString(bytes);
        return Encoding.Latin1.GetString(bytes);
    }
}
