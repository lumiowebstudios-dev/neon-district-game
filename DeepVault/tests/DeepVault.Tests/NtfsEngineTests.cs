using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.FileSystems.Ntfs;
using DeepVault.Core.IO;
using DeepVault.Core.Partitions;
using DeepVault.Tests.Infrastructure;

namespace DeepVault.Tests;

/// <summary>Test A (NTFS): files deleted through a real NTFS driver must be recovered byte-exactly.</summary>
public class NtfsEngineTests
{
    [Fact]
    public void Recovers_deleted_ntfs_files_with_names_paths_and_exact_content()
    {
        var fx = Fixtures.Get("ntfs_deleted");
        using var src = new FileBlockSource(fx.ImagePath);
        var reader = new ResilientReader(src, null);
        var pt = PartitionTableParser.Parse(src);
        var part = Assert.Single(pt.Partitions);
        Assert.Equal(FsKind.Ntfs, part.FileSystem);
        Assert.Equal(fx.PartitionOffset, part.Offset);

        var sink = new ListSink();
        var res = new NtfsScanner(src, reader, sink, part.Offset, part.Boot!, 1, "DVTEST").Run(null, CancellationToken.None);
        Assert.True(res.Completed);

        foreach (var exp in fx.Files.Where(f => f.Deleted))
        {
            var found = sink.Files.Where(f => f.Name == exp.Name && f.IsDeleted == true).ToList();
            Assert.True(found.Count >= 1, $"{exp.Name} not found");
            var f = found.First();
            Assert.Equal(exp.Path, f.Path);
            Assert.Equal(exp.Size, f.Size);
            using var cs = new ContentStream(reader, f.Layout);
            var hash = Fixtures.Sha256(cs);
            if (f.OverwrittenFraction is > 0)
                Assert.NotEqual(Confidence.High, f.Confidence); // overwritten content must not claim high confidence
            else
                Assert.True(exp.Sha256 == hash, $"{exp.Name}: content hash mismatch (layout {f.Layout.Kind}, frags {f.Layout.FragmentCount})");
            if (exp.Fragmented) Assert.True(f.Layout.FragmentCount > 1, "expected a fragmented layout");
            if (exp.Resident) Assert.Equal(LayoutKind.NtfsResident, f.Layout.Kind);
            if (exp.NtfsCompressed) Assert.Equal(LayoutKind.NtfsCompressed, f.Layout.Kind);
        }
        // Existing files are excluded by default.
        Assert.DoesNotContain(sink.Files, f => f.Name == "budget.xlsx");
    }
}
