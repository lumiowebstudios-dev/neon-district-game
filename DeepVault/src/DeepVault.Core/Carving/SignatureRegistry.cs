using System.Text.Json;
using System.Text.Json.Serialization;
using DeepVault.Core.Carving.Formats;
using DeepVault.Core.Catalog;

namespace DeepVault.Core.Carving;

/// <summary>A user-defined signature (header and optional footer) loaded from JSON, so new formats can be added without code changes.</summary>
public sealed class CustomSignature
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("extension")] public string Extension { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "Other";
    [JsonPropertyName("header")] public string Header { get; set; } = "";
    [JsonPropertyName("headerOffset")] public int HeaderOffset { get; set; }
    [JsonPropertyName("footer")] public string? Footer { get; set; }
    [JsonPropertyName("maxSize")] public long MaxSize { get; set; } = 64L * 1024 * 1024;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class CustomSignatureFormat : CarveFormat
{
    private readonly CustomSignature _s;
    private readonly byte[] _header;
    private readonly byte[]? _footer;

    public CustomSignatureFormat(CustomSignature s)
    {
        _s = s;
        _header = Convert.FromHexString(s.Header.Replace(" ", ""));
        _footer = string.IsNullOrWhiteSpace(s.Footer) ? null : Convert.FromHexString(s.Footer.Replace(" ", ""));
        if (_header.Length < 2) throw new ArgumentException($"Custom signature '{s.Id}': header must be at least 2 bytes.");
        Category = Enum.TryParse<FileCategory>(s.Category, true, out var c) ? c : FileCategory.Other;
    }

    public override string Id => "custom:" + _s.Id;
    public override string Name => _s.Name + " (custom)";
    public override FileCategory Category { get; }
    public override string DefaultExtension => _s.Extension;
    public override int SignatureOffset => _s.HeaderOffset;
    public override IReadOnlyList<byte[]> Signatures => new[] { _header };
    public override long MaxSize => _s.MaxSize;
    public override bool EnabledByDefault => _s.Enabled;
    public override string Description => _footer != null ? "User-defined header/footer signature." : "User-defined header signature; end of file is estimated.";

    public override CarveResult? Analyze(ICarveSource src, long start, long maxLength)
    {
        long limit = start + Math.Min(maxLength, MaxSize);
        if (_footer != null)
        {
            long f = src.Find(_footer, start + _header.Length, limit);
            if (f >= 0) return new CarveResult { Length = f + _footer.Length - start, Check = StructureCheck.Plausible, Extension = _s.Extension, Note = "header and footer found" };
            return new CarveResult { Length = Math.Min(MaxSize, limit - start), Check = StructureCheck.HeaderOnly, Extension = _s.Extension, Note = "footer not found" };
        }
        // No footer: stop at the first fully-zero 4 KiB block (best effort).
        var buf = new byte[4096];
        long p = start + 4096;
        while (p < limit)
        {
            int n = src.Read(p, buf);
            if (n <= 0 || buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) < 0) break;
            p += n;
        }
        return new CarveResult { Length = Math.Min(p, limit) - start, Check = StructureCheck.HeaderOnly, Extension = _s.Extension, Note = "no end marker: size estimated" };
    }
}

public sealed class SignatureRegistry
{
    private readonly List<CarveFormat>?[] _byFirstTwo = new List<CarveFormat>?[65536];
    private readonly List<(CarveFormat f, byte[] sig)> _other = new();

    public IReadOnlyList<CarveFormat> Formats { get; }
    public IReadOnlyList<string> LoadErrors { get; }

    private SignatureRegistry(List<CarveFormat> formats, List<string> errors)
    {
        Formats = formats;
        LoadErrors = errors;
        foreach (var f in formats)
            foreach (var sig in f.Signatures)
            {
                if (f.SignatureOffset == 0 && sig.Length >= 2)
                {
                    int key = (sig[0] << 8) | sig[1];
                    (_byFirstTwo[key] ??= new()).Add(f);
                }
                else _other.Add((f, sig));
            }
    }

    public static IEnumerable<CarveFormat> BuiltInFormats() => new CarveFormat[]
    {
        new JpegFormat(), new PngFormat(), new GifFormat(), new BmpFormat(), new TiffFormat(), new RafFormat(), new PsdFormat(),
        new RiffFormat(), new IsoBmffFormat(), new MatroskaFormat(), new Mp3Format(), new FlacFormat(), new OggFormat(),
        new AsfFormat(), new FlvFormat(), new MpegTsFormat(false), new MpegTsFormat(true), new MpegPsFormat(), new MidiFormat(), new AiffFormat(),
        new PdfFormat(), new ZipFormat(), new Ole2Format(), new RtfFormat(), new PstFormat(),
        new RarFormat(), new SevenZipFormat(), new GzipFormat(), new XzFormat(), new CabFormat(), new SqliteFormat(), new PeFormat(),
        new TextMarkupFormat("xml", "XML document", "xml", FileCategory.Text, false, "<?xml version="),
        new TextMarkupFormat("html", "HTML page", "html", FileCategory.Text, false, "<!DOCTYPE html", "<!doctype html", "<!DOCTYPE HTML", "<!DOCTYPE html PUBLIC", "<html", "<HTML"),
        new TextMarkupFormat("eml", "E-mail message", "eml", FileCategory.Email, false, "Return-Path: ", "Received: from ", "Delivered-To: "),
        new TextMarkupFormat("vcf", "vCard contact", "vcf", FileCategory.Text, false, "BEGIN:VCARD"),
        new TextMarkupFormat("ics", "iCalendar", "ics", FileCategory.Text, false, "BEGIN:VCALENDAR"),
        new PlainTextFormat(),
    };

    /// <summary>Builds the registry from the built-in analyzers plus optional custom JSON definitions.</summary>
    public static SignatureRegistry Create(string? customJsonPath = null)
    {
        var list = BuiltInFormats().ToList();
        var errors = new List<string>();
        if (customJsonPath != null && File.Exists(customJsonPath))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<CustomFile>(File.ReadAllText(customJsonPath));
                foreach (var s in doc?.Signatures ?? new())
                {
                    try { list.Add(new CustomSignatureFormat(s)); }
                    catch (Exception e) when (e is FormatException or ArgumentException) { errors.Add($"{s.Id}: {e.Message}"); }
                }
            }
            catch (JsonException e) { errors.Add($"{Path.GetFileName(customJsonPath)}: {e.Message}"); }
        }
        return new SignatureRegistry(list, errors);
    }

    /// <summary>Formats whose signature matches at the start of <paramref name="sector"/>.</summary>
    public void Match(ReadOnlySpan<byte> sector, ISet<string> enabled, List<CarveFormat> output)
    {
        output.Clear();
        if (sector.Length < 8) return;
        var bucket = _byFirstTwo[(sector[0] << 8) | sector[1]];
        if (bucket != null)
            foreach (var f in bucket)
            {
                if (!enabled.Contains(f.Id)) continue;
                foreach (var sig in f.Signatures)
                    if (sig.Length <= sector.Length && sector.StartsWith(sig)) { output.Add(f); break; }
            }
        foreach (var (f, sig) in _other)
        {
            if (!enabled.Contains(f.Id) || output.Contains(f)) continue;
            int o = f.SignatureOffset;
            if (o + sig.Length <= sector.Length && sector.Slice(o, sig.Length).SequenceEqual(sig)) output.Add(f);
        }
        if (enabled.Contains("text") && output.Count == 0)
        {
            var t = Formats.First(x => x.Id == "text");
            byte b = sector[0];
            if (b >= 0x20 && b < 0x7F) output.Add(t);
        }
    }

    public ISet<string> DefaultEnabled() => Formats.Where(f => f.EnabledByDefault).Select(f => f.Id).ToHashSet();

    private sealed class CustomFile
    {
        [JsonPropertyName("signatures")] public List<CustomSignature> Signatures { get; set; } = new();
    }
}

/// <summary>Validates content reconstructed by metadata engines using the carving analyzers.</summary>
public static class FormatValidator
{
    private static readonly Lazy<SignatureRegistry> Registry = new(() => SignatureRegistry.Create());

    public sealed record Validation(StructureCheck Check, string? Note, string? DetectedExtension);

    /// <summary>
    /// Checks the first bytes against all known signatures and, when one matches, runs its analyzer over
    /// the whole content. Returns NotChecked when no analyzer applies.
    /// </summary>
    public static Validation Validate(ICarveSource content, string? extension)
    {
        var head = new byte[512];
        int n = content.Read(0, head);
        if (n < 8) return new Validation(StructureCheck.NotChecked, null, null);
        var reg = Registry.Value;
        var matches = new List<CarveFormat>();
        reg.Match(head.AsSpan(0, n), reg.Formats.Where(f => f.Id != "text").Select(f => f.Id).ToHashSet(), matches);
        foreach (var f in matches)
        {
            var r = f.SafeAnalyze(content, 0, content.Length);
            if (r == null) continue;
            var check = r.Check;
            string? note = r.Note;
            if (check is StructureCheck.Verified or StructureCheck.Plausible && r.Length > content.Length)
            {
                check = StructureCheck.Damaged;
                note = "content is shorter than its internal structure requires";
            }
            return new Validation(check, note, r.Extension);
        }
        if (extension != null && ExpectsSignature(extension) && Bytes0(head) )
            return new Validation(StructureCheck.Damaged, "content is all zeros at the start (overwritten or wiped)", null);
        if (extension != null && ExpectsSignature(extension))
            return new Validation(StructureCheck.Damaged, $"content does not start with a valid .{extension} header (overwritten or wrong clusters)", null);
        return new Validation(StructureCheck.NotChecked, null, null);
    }

    private static bool Bytes0(byte[] b) => b.AsSpan().IndexOfAnyExcept((byte)0) < 0;

    private static readonly HashSet<string> SignedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "psd", "cr2", "nef", "arw", "dng", "orf", "rw2", "raf", "pef",
        "mp4", "m4v", "mov", "m4a", "3gp", "heic", "avif", "cr3", "mkv", "webm", "avi", "wav", "mp3", "flac", "ogg", "opus", "wmv", "wma", "flv", "mid",
        "pdf", "docx", "xlsx", "pptx", "docm", "xlsm", "pptm", "odt", "ods", "odp", "epub", "doc", "xls", "ppt", "msg", "rtf", "pst",
        "zip", "rar", "7z", "gz", "tgz", "xz", "cab", "sqlite", "db",
    };

    public static bool ExpectsSignature(string ext) => SignedExtensions.Contains(ext);
}
