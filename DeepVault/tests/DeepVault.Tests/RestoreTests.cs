using DeepVault.Core.Catalog;
using DeepVault.Core.IO;
using DeepVault.Core.Restore;
using DeepVault.Core.Scanning;
using DeepVault.Tests.Infrastructure;

namespace DeepVault.Tests;

public sealed class FakeInspector : IDestinationInspector
{
    public long Free { get; set; } = 10L << 30;
    public List<string> Disks { get; set; } = new() { "PhysicalDrive9" };
    public string FileSystem { get; set; } = "NTFS";
    public DestinationInfo Inspect(string folder) => new()
    {
        Folder = folder, VolumeRoot = Path.GetPathRoot(Path.GetFullPath(folder)) ?? "/", Label = "USB500", FileSystem = FileSystem,
        TotalBytes = 500_000_000_000, FreeBytes = Free, PhysicalDisks = Disks, IsRemovable = true,
    };
}

/// <summary>Simulates a destination that runs out of space after a number of bytes.</summary>
public sealed class FullingTarget : IRestoreTarget
{
    private readonly FileSystemRestoreTarget _fs = new();
    private long _budget;
    public FullingTarget(long budget) => _budget = budget;
    public Stream CreateNew(string path) => new Limited(_fs.CreateNew(path), this);
    public void MoveNoOverwrite(string f, string t) => _fs.MoveNoOverwrite(f, t);
    public bool Exists(string p) => _fs.Exists(p);
    public void CreateDirectory(string p) => _fs.CreateDirectory(p);
    public void Delete(string p) => _fs.Delete(p);
    public Stream OpenRead(string p) => _fs.OpenRead(p);
    public void SetTimes(string p, DateTime? c, DateTime? m, DateTime? a) { }

    private sealed class Limited : Stream
    {
        private readonly Stream _s; private readonly FullingTarget _t;
        public Limited(Stream s, FullingTarget t) { _s = s; _t = t; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_t._budget < count) throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
            _t._budget -= count;
            _s.Write(buffer, offset, count);
        }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _s.Length; public override long Position { get => _s.Position; set => _s.Position = value; }
        public override void Flush() => _s.Flush();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool d) { if (d) _s.Dispose(); base.Dispose(d); }
    }
}

/// <summary>Test E: destination safety — no overwrites, space checks, source-disk refusal, disk-full handling.</summary>
public class RestoreTests
{
    private static (ProjectDatabase db, FileBlockSource src, FixtureImage fx) Scanned()
    {
        var fx = Fixtures.Get("ntfs_deleted");
        var src = new FileBlockSource(fx.ImagePath);
        var (db, _) = DeepScanTests.Scan(src, new ScanOptions { Methods = ScanMethods.Quick });
        return (db, src, fx);
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(p => p, Fixtures.Sha256File);

    [Fact]
    public void Restores_verified_copies_without_touching_existing_destination_files()
    {
        var (db, src, fx) = Scanned();
        using var _ = db; using var __ = src;
        var dest = Fixtures.NewTempDir("dest");
        // Pre-existing user data with colliding names.
        Directory.CreateDirectory(Path.Combine(dest, "Image"));
        File.WriteAllText(Path.Combine(dest, "Image", "holiday_beach.jpg"), "existing user file - must survive");
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "existing");
        var before = Snapshot(dest);

        var files = db.QueryFiles(new FileQuery { Limit = 10000, OnlyDeleted = true }).Where(f => fx.Files.Any(x => x.Name == f.Name)).ToList();
        var svc = new RestoreService(src, new SourceIdentity { Kind = "Image", Path = fx.ImagePath, Size = src.Length }, new FakeInspector(), db);
        var req = new RestoreRequest { Files = files, DestinationFolder = dest, CreateSessionFolder = false, PreserveFolders = false };
        var pf = svc.Preflight(req);
        Assert.True(pf.CanProceed, string.Join(";", pf.Blockers));
        var res = svc.Run(req, null, CancellationToken.None);

        foreach (var (path, hash) in before) Assert.Equal(hash, Fixtures.Sha256File(path)); // nothing pre-existing changed
        Assert.True(File.Exists(Path.Combine(dest, "Image", "holiday_beach (2).jpg")));
        Assert.Empty(Directory.GetFiles(dest, "*.dvpart", SearchOption.AllDirectories));
        foreach (var exp in fx.Files.Where(f => f.Deleted))
        {
            var item = res.Items.Single(i => i.OriginalPath.EndsWith("\\" + exp.Name));
            Assert.Equal(RestoreOutcome.Verified, item.Outcome);
            Assert.Equal(exp.Sha256, Fixtures.Sha256File(item.TargetPath!));
        }
        Assert.True(File.Exists(res.ReportHtml) && File.Exists(res.ReportCsv));
        Assert.Equal(RecoveryState.RestoreVerified, db.GetFile(files[0].Id)!.State);
        Assert.Single(db.GetRestoreRuns());
        new ScenarioReport
        {
            Scenario = "E1 Restore to a destination with existing files", Setup = "ntfs_deleted scanned; destination pre-populated with a same-named file",
            TrackedFiles = files.Count, Reconstructed = res.Items.Count(i => i.TargetPath != null), VerifiedByHash = res.Count(RestoreOutcome.Verified),
            Outcome = "existing files unchanged; collision renamed to 'holiday_beach (2).jpg'; every restored file SHA-256 verified",
        }.Save();
    }

    [Fact]
    public void Default_restore_uses_a_new_session_folder_and_preserves_original_folders()
    {
        var (db, src, fx) = Scanned();
        using var _ = db; using var __ = src;
        var dest = Fixtures.NewTempDir("dest");
        var files = db.QueryFiles(new FileQuery { Search = "holiday_beach" });
        var res = new RestoreService(src, new SourceIdentity(), new FakeInspector(), db).Run(new RestoreRequest { Files = files, DestinationFolder = dest }, null, default);
        var item = Assert.Single(res.Items);
        Assert.StartsWith(Path.Combine(dest, "DeepVault Recovery "), item.TargetPath);
        Assert.EndsWith(Path.Combine("Photos", "2019", "holiday_beach.jpg"), item.TargetPath);
    }

    [Fact]
    public void Skip_policy_skips_collisions()
    {
        var (db, src, _) = Scanned();
        using var __ = db; using var ___ = src;
        var dest = Fixtures.NewTempDir("dest");
        Directory.CreateDirectory(Path.Combine(dest, "Image"));
        File.WriteAllText(Path.Combine(dest, "Image", "holiday_beach.jpg"), "x");
        var files = db.QueryFiles(new FileQuery { Search = "holiday_beach" });
        var res = new RestoreService(src, new SourceIdentity(), new FakeInspector(), db).Run(
            new RestoreRequest { Files = files, DestinationFolder = dest, CreateSessionFolder = false, PreserveFolders = false, Collisions = CollisionPolicy.Skip }, null, default);
        Assert.Equal(RestoreOutcome.Skipped, Assert.Single(res.Items).Outcome);
        Assert.Equal("x", File.ReadAllText(Path.Combine(dest, "Image", "holiday_beach.jpg")));
    }

    [Fact]
    public void Insufficient_space_blocks_before_anything_is_written()
    {
        var (db, src, _) = Scanned();
        using var __ = db; using var ___ = src;
        var dest = Fixtures.NewTempDir("dest");
        var files = db.QueryFiles(new FileQuery { Limit = 1000, OnlyDeleted = true });
        var svc = new RestoreService(src, new SourceIdentity(), new FakeInspector { Free = 100_000 }, db);
        var req = new RestoreRequest { Files = files, DestinationFolder = dest };
        var pf = svc.Preflight(req);
        Assert.False(pf.EnoughSpace);
        Assert.Contains(pf.Blockers, b => b.Contains("Not enough free space"));
        Assert.Throws<InvalidOperationException>(() => svc.Run(req, null, default));
        Assert.Empty(Directory.GetFileSystemEntries(dest));
    }

    [Fact]
    public void Destination_on_the_source_disk_is_refused()
    {
        var (db, src, _) = Scanned();
        using var __ = db; using var ___ = src;
        var svc = new RestoreService(src, new SourceIdentity { Kind = "PhysicalDisk" }, new FakeInspector { Disks = new() { "PhysicalDrive0" } }, db)
        { SourceDisks = new[] { "PhysicalDrive0" } };
        var pf = svc.Preflight(new RestoreRequest { Files = db.QueryFiles(new FileQuery()), DestinationFolder = Fixtures.NewTempDir("d") });
        Assert.True(pf.DestinationOnSourceDisk);
        Assert.False(pf.CanProceed);
    }

    [Fact]
    public void Fat32_destination_rejects_files_over_4GB()
    {
        var (db, src, _) = Scanned();
        using var __ = db; using var ___ = src;
        var huge = new FoundFile { Name = "big.mkv", Size = 5L << 30, Layout = Core.Content.DataLayout.Contiguous(0, 5L << 30) };
        var svc = new RestoreService(new FakeLargeSource(), new SourceIdentity(), new FakeInspector { FileSystem = "FAT32" }, null);
        var pf = svc.Preflight(new RestoreRequest { Files = new() { huge }, DestinationFolder = Fixtures.NewTempDir("d") });
        Assert.Contains(pf.NotRestorable, n => n.reason.Contains("4 GB"));
    }

    private sealed class FakeLargeSource : IBlockSource
    {
        public long Length => 1L << 40; public int SectorSize => 512; public string Description => "fake";
        public int Read(long offset, Span<byte> buffer) { buffer.Clear(); return buffer.Length; }
        public void Dispose() { }
    }

    [Fact]
    public void Destination_full_during_restore_stops_cleanly_and_deletes_only_its_partial_file()
    {
        var (db, src, fx) = Scanned();
        using var __ = db; using var ___ = src;
        var dest = Fixtures.NewTempDir("dest");
        File.WriteAllText(Path.Combine(dest, "existing.txt"), "keep me");
        var files = db.QueryFiles(new FileQuery { Limit = 1000, OnlyDeleted = true, Sort = SortField.Size }).Where(f => fx.Files.Any(x => x.Name == f.Name)).ToList();
        var res = new RestoreService(src, new SourceIdentity(), new FakeInspector(), db, new FullingTarget(60_000)).Run(
            new RestoreRequest { Files = files, DestinationFolder = dest }, null, default);
        Assert.True(res.StoppedDestinationFull);
        Assert.Contains(res.Items, i => i.Outcome == RestoreOutcome.Failed && i.Message.StartsWith("Destination is full"));
        Assert.Contains(res.Items, i => i.Outcome == RestoreOutcome.NotAttempted);
        Assert.Empty(Directory.GetFiles(dest, "*.dvpart", SearchOption.AllDirectories));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(dest, "existing.txt")));
        new ScenarioReport
        {
            Scenario = "E2 Destination fills up during restore", Setup = "simulated destination with a 60 kB budget",
            TrackedFiles = files.Count, VerifiedByHash = res.Count(RestoreOutcome.Verified),
            Outcome = $"stopped cleanly: {res.Count(RestoreOutcome.Verified)} verified, {res.Count(RestoreOutcome.Failed)} failed (partial removed), {res.Count(RestoreOutcome.NotAttempted)} not attempted; existing files intact",
        }.Save();
    }
}
