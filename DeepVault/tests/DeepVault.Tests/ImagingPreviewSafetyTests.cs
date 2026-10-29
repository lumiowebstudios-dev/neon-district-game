using System.Text.RegularExpressions;
using DeepVault.Core.Catalog;
using DeepVault.Core.Imaging;
using DeepVault.Core.IO;
using DeepVault.Core.Preview;
using DeepVault.Core.Scanning;
using DeepVault.Tests.Infrastructure;

namespace DeepVault.Tests;

public class ImagingTests
{
    [Fact]
    public void Images_a_disk_with_bad_sectors_mapped_and_refuses_when_space_is_insufficient()
    {
        var fx = Fixtures.Get("lost_partitions");
        using var inner = new FileBlockSource(fx.ImagePath);
        using var src = new FaultyBlockSource(new FileBlockSource(fx.ImagePath), (5 * 1024 * 1024, 4096));
        var id = new SourceIdentity { Kind = "Image", Path = fx.ImagePath, Size = src.Length };
        var dir = Fixtures.NewTempDir("img");

        var small = new DiskImager(src, id, new FakeInspector { Free = 10 * 1024 * 1024 });
        var pf = small.Preflight(new ImageRequest { ImagePath = Path.Combine(dir, "a.img") });
        Assert.Contains(pf.Blockers, b => b.Contains("Not enough space"));
        Assert.Throws<InvalidOperationException>(() => small.Run(new ImageRequest { ImagePath = Path.Combine(dir, "a.img") }, null, default));
        Assert.False(File.Exists(Path.Combine(dir, "a.img")));

        // Interrupted and resumed imaging produces the same image as a single pass.
        var imager = new DiskImager(src, id, new FakeInspector());
        var path = Path.Combine(dir, "disk.img");
        using var cts = new CancellationTokenSource();
        var m1 = imager.Run(new ImageRequest { ImagePath = path, ChunkSize = 1 << 20 }, new SyncProgress<(long, long, long)>(p => { if (p.Item1 > 20 << 20) cts.Cancel(); }), cts.Token);
        Assert.False(m1.Complete);
        var m2 = imager.Run(new ImageRequest { ImagePath = path, ChunkSize = 1 << 20 }, null, default);
        Assert.True(m2.Complete);
        Assert.Equal(m2.Sha256, Fixtures.Sha256File(path));
        Assert.Single(m2.UnreadableRanges);
        Assert.Equal(5L * 1024 * 1024, m2.UnreadableRanges[0][0]);
        // Everything outside the bad range is identical to the source.
        var a = File.ReadAllBytes(path); var b = File.ReadAllBytes(fx.ImagePath);
        for (int i = 0; i < a.Length; i += 4096)
            if (i < 5 * 1024 * 1024 || i >= 5 * 1024 * 1024 + 4096) Assert.True(a.AsSpan(i, 4096).SequenceEqual(b.AsSpan(i, 4096)), $"diff at {i}");
        // Never overwrites an existing complete image.
        Assert.Contains(imager.Preflight(new ImageRequest { ImagePath = path }).Blockers, x => x.Contains("never overwrites"));
    }
}

public class PreviewTests
{
    [Fact]
    public void Previews_are_produced_in_memory_for_images_text_pdf_and_raw_bytes()
    {
        var fx = Fixtures.Get("ntfs_deleted");
        using var src = new FileBlockSource(fx.ImagePath);
        var (db, _) = DeepScanTests.Scan(src, new ScanOptions { Methods = ScanMethods.Quick });
        using var __ = db;
        var svc = new PreviewService(new ResilientReader(src, null));
        var jpg = svc.Load(db.QueryFiles(new FileQuery { Search = "holiday_beach.jpg" }).Single());
        Assert.Equal(PreviewKind.Image, jpg.Kind);
        Assert.Equal(0xFF, jpg.Bytes[0]);
        var txt = svc.Load(db.QueryFiles(new FileQuery { Search = "notes.txt" }).Single());
        Assert.Equal(PreviewKind.Text, txt.Kind);
        Assert.StartsWith("Note 0:", txt.Text);
        var pdf = svc.Load(db.QueryFiles(new FileQuery { Search = "invoice_2019.pdf" }).Single());
        Assert.Equal(PreviewKind.Pdf, pdf.Kind);
        Assert.Contains("invoice 2019", pdf.Text);
        var rtf = svc.Load(db.QueryFiles(new FileQuery { Search = "letter.rtf" }).Single());
        Assert.Contains("Dear friend", rtf.Text);
        var mp3 = svc.Load(db.QueryFiles(new FileQuery { Search = "song.mp3" }).Single());
        Assert.Equal(PreviewKind.Hex, mp3.Kind);
        Assert.StartsWith("00000000  49 44 33", mp3.HexDump);
    }
}

/// <summary>Test F: static guarantees that the raw-disk component cannot write to the source.</summary>
public class SourceSafetyTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DeepVault.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("DeepVault.sln not found");
    }

    [Fact]
    public void Block_source_interfaces_expose_no_write_operations()
    {
        foreach (var t in new[] { typeof(IBlockSource), typeof(FileBlockSource), typeof(VhdBlockSource), typeof(SubRangeSource), typeof(ResilientReader) })
            Assert.DoesNotContain(t.GetMethods(), m => m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase) && m.DeclaringType == t);
        Assert.Throws<NotSupportedException>(() => new Core.Content.ContentStream(new ResilientReader(new FileBlockSource(Fixtures.Get("carve_raw").ImagePath), null), new()).Write(new byte[1], 0, 1));
    }

    [Fact]
    public void Raw_disk_access_code_opens_devices_read_only_and_never_issues_write_or_repair_requests()
    {
        var dir = Path.Combine(RepoRoot(), "src", "DeepVault.Platform.Windows");
        var code = string.Join("\n", Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(File.ReadAllText));
        Assert.Contains("GENERIC_READ", code);
        foreach (var forbidden in new[] { "GENERIC_WRITE", "GENERIC_ALL", "WriteFile(", "FSCTL_LOCK_VOLUME", "FSCTL_DISMOUNT_VOLUME", "IOCTL_DISK_SET_DRIVE_LAYOUT", "IOCTL_DISK_CREATE_DISK",
                     "IOCTL_DISK_DELETE_DRIVE_LAYOUT", "IOCTL_DISK_FORMAT", "IOCTL_DISK_GROW_PARTITION", "IOCTL_DISK_UPDATE_PROPERTIES", "FileAccess.Write", "FileAccess.ReadWrite", "DeviceIoControlWrite" })
            Assert.DoesNotContain(forbidden, code);
        // Every IOCTL used must be a query.
        foreach (Match m in Regex.Matches(code, @"(IOCTL_[A-Z_]+|FSCTL_[A-Z_]+)\s*="))
            Assert.Matches("GET|QUERY|PREDICT_FAILURE", m.Groups[1].Value);
        var core = string.Join("\n", Directory.GetFiles(Path.Combine(RepoRoot(), "src", "DeepVault.Core", "IO"), "*.cs").Select(File.ReadAllText));
        Assert.DoesNotContain("FileAccess.Write", core);
        Assert.DoesNotContain("FileAccess.ReadWrite", core);
    }

    [Fact]
    public void Preview_and_scan_engines_contain_no_file_writing_code()
    {
        var root = Path.Combine(RepoRoot(), "src", "DeepVault.Core");
        foreach (var sub in new[] { "Preview", "Carving", "FileSystems", "Partitions", "Content", "IO" })
        {
            var code = string.Join("\n", Directory.GetFiles(Path.Combine(root, sub), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
            foreach (var w in new[] { "File.Write", "File.Create", "FileMode.Create", "File.Open(", "Path.GetTempFileName", "Process.Start" })
                Assert.DoesNotContain(w, code);
        }
    }
}
