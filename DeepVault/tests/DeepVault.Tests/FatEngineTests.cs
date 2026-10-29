using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.FileSystems.ExFat;
using DeepVault.Core.FileSystems.Fat;
using DeepVault.Core.IO;
using DeepVault.Core.Partitions;
using DeepVault.Tests.Infrastructure;
using Xunit.Abstractions;

namespace DeepVault.Tests;

/// <summary>Test A for FAT32 (deleted with mtools) and exFAT (deleted through exfat-fuse).</summary>
public class FatEngineTests
{
    private readonly ITestOutputHelper _out;
    public FatEngineTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Recovers_deleted_fat32_files_including_lfn_names_and_deleted_directory()
    {
        var fx = Fixtures.Get("fat32_deleted");
        using var src = new FileBlockSource(fx.ImagePath);
        var reader = new ResilientReader(src, null);
        var part = Assert.Single(PartitionTableParser.Parse(src).Partitions);
        Assert.Equal(FsKind.Fat32, part.FileSystem);
        var sink = new ListSink();
        var res = new FatScanner(src, reader, sink, part.Offset, part.Boot!, "DVFAT").Run(null, CancellationToken.None);
        Assert.True(res.Completed);
        foreach (var f in sink.Files) _out.WriteLine($"{f.DisplayPath} {f.Size} {f.Structure} {f.Confidence} {f.Notes}");
        foreach (var exp in fx.Files.Where(f => f.Deleted))
        {
            // A deleted 8.3-only entry loses its first character for good; DeepVault shows it as '_'.
            var f = sink.Files.SingleOrDefault(x => x.Name == exp.Name)
                    ?? sink.Files.SingleOrDefault(x => x.Name.StartsWith('_') && x.Name[1..] == exp.Name[1..] && x.Notes.Contains("First character"));
            Assert.True(f != null, $"{exp.Name} not found");
            Assert.Equal(exp.Path, f!.Path);
            Assert.Equal(exp.Size, f.Size);
            using var cs = new ContentStream(reader, f.Layout);
            Assert.Equal(exp.Sha256, Fixtures.Sha256(cs));
        }
        Assert.DoesNotContain(sink.Files, f => f.Name == "slides.pptx");
    }

    [Fact]
    public void Recovers_deleted_exfat_files()
    {
        var fx = Fixtures.Get("exfat_deleted");
        using var src = new FileBlockSource(fx.ImagePath);
        var reader = new ResilientReader(src, null);
        var part = Assert.Single(PartitionTableParser.Parse(src).Partitions);
        Assert.Equal(FsKind.ExFat, part.FileSystem);
        var sink = new ListSink();
        var res = new ExFatScanner(reader, sink, part.Offset, part.Boot!, "DVEXFAT").Run(null, CancellationToken.None);
        Assert.True(res.Completed);
        foreach (var f in sink.Files) _out.WriteLine($"{f.DisplayPath} {f.Size} {f.Structure} {f.Confidence} {f.Notes}");
        foreach (var exp in fx.Files.Where(f => f.Deleted))
        {
            var f = sink.Files.SingleOrDefault(x => x.Name == exp.Name);
            Assert.True(f != null, $"{exp.Name} not found");
            Assert.Equal(exp.Path, f!.Path);
            using var cs = new ContentStream(reader, f.Layout);
            Assert.Equal(exp.Sha256, Fixtures.Sha256(cs));
        }
    }
}
