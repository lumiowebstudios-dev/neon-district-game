using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;
using DeepVault.Tests.Infrastructure;
using Xunit.Abstractions;

namespace DeepVault.Tests;

/// <summary>Test B / Test D: carving without any file-system metadata, including damaged candidates.</summary>
public class CarvingTests
{
    private readonly ITestOutputHelper _out;
    public CarvingTests(ITestOutputHelper output) => _out = output;

    private static (ListSink sink, ResilientReader reader, FileBlockSource src) Carve(string fixture)
    {
        var fx = Fixtures.Get(fixture);
        var src = new FileBlockSource(fx.ImagePath);
        var reader = new ResilientReader(src, null);
        var sink = new ListSink();
        var engine = new CarvingEngine(src, reader, sink, SignatureRegistry.Create());
        engine.Run(new[] { new Extent(0, src.Length) }, 0, null, null, CancellationToken.None);
        return (sink, reader, src);
    }

    [Fact]
    public void Carves_intact_files_byte_exactly_without_metadata()
    {
        var fx = Fixtures.Get("carve_raw");
        var (sink, reader, src) = Carve("carve_raw");
        using var _ = src;
        var failures = new List<string>();
        foreach (var exp in fx.Files.Where(f => f.Expect == "exact"))
        {
            var hit = sink.Files.FirstOrDefault(f => f.FirstOffset == exp.Offset);
            if (hit == null) { failures.Add($"{exp.Name}: not found at {exp.Offset}"); continue; }
            using var cs = new ContentStream(reader, hit.Layout);
            var hash = Fixtures.Sha256(cs);
            _out.WriteLine($"{exp.Name,-22} -> {hit.Name} {hit.Size} {hit.Structure} {(hash == exp.Sha256 ? "HASH OK" : "HASH MISMATCH")} {hit.Notes}");
            if (hit.Size != exp.Size || hash != exp.Sha256) failures.Add($"{exp.Name}: size {hit.Size} vs {exp.Size}, hash {(hash == exp.Sha256 ? "ok" : "mismatch")}");
            else if (hit.Structure is not (StructureCheck.Verified or StructureCheck.Plausible) && !(hit.Structure == StructureCheck.HeaderOnly && hit.Category == FileCategory.Text))
                failures.Add($"{exp.Name}: structure {hit.Structure}");
            if (FileTypes.ExtensionOf(exp.Name) is var e && e != hit.Extension && !(e == "jpg" && hit.Extension == "jpg"))
                _out.WriteLine($"   extension detected as {hit.Extension} (expected {e})");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Damaged_candidates_are_never_reported_as_verified()
    {
        var fx = Fixtures.Get("carve_raw");
        var (sink, _, src) = Carve("carve_raw");
        using var __ = src;
        foreach (var exp in fx.Files.Where(f => f.Expect != "exact"))
        {
            var hit = sink.Files.FirstOrDefault(f => f.FirstOffset == exp.Offset);
            _out.WriteLine($"{exp.Name}: {(hit == null ? "not reported" : $"{hit.Structure} {hit.State} {hit.Notes}")}");
            if (hit == null) { Assert.NotEqual("damaged", exp.Expect); continue; }
            Assert.NotEqual(StructureCheck.Verified, hit.Structure);
            Assert.NotEqual(Confidence.High, hit.Confidence);
            if (exp.Expect == "damaged") Assert.Equal(StructureCheck.Damaged, hit.Structure);
        }
    }
}

public class CustomSignatureTests
{
    [Fact]
    public void Sample_custom_signature_file_loads_and_carves_header_footer_files()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DeepVault.sln"))) dir = Path.GetDirectoryName(dir)!;
        var reg = SignatureRegistry.Create(Path.Combine(dir, "docs", "custom-signatures.sample.json"));
        Assert.Empty(reg.LoadErrors);
        Assert.Contains(reg.Formats, f => f.Id == "custom:eps");
        var eps = System.Text.Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF\n" + new string('x', 3000) + "\n%%EOF");
        var img = new byte[64 * 1024];
        eps.CopyTo(img, 4096);
        var sink = new ListSink();
        using var src = new Core.IO.SubRangeSource(new MemSource(img), 0, img.Length);
        new CarvingEngine(src, new ResilientReader(src, null), sink, reg).Run(new[] { new Extent(0, img.Length) }, 0, null, null, default);
        var hit = Assert.Single(sink.Files, f => f.Extension == "eps");
        Assert.Equal(4096, hit.FirstOffset);
        Assert.Equal(eps.Length, hit.Size);
    }

    private sealed class MemSource : Core.IO.IBlockSource
    {
        private readonly byte[] _b;
        public MemSource(byte[] b) => _b = b;
        public long Length => _b.Length;
        public int SectorSize => 512;
        public string Description => "mem";
        public int Read(long o, Span<byte> buf) { int n = (int)Math.Min(buf.Length, _b.Length - o); _b.AsSpan((int)o, n).CopyTo(buf); return n; }
        public void Dispose() { }
    }
}
