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
