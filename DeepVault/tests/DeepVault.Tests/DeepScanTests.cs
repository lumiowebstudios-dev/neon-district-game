using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;
using DeepVault.Core.Scanning;
using DeepVault.Tests.Infrastructure;
using Xunit.Abstractions;

namespace DeepVault.Tests;

/// <summary>End-to-end "Deep Scan — All Applicable Methods" through the orchestrator and project database.</summary>
public class DeepScanTests
{
    private readonly ITestOutputHelper _out;
    public DeepScanTests(ITestOutputHelper o) => _out = o;

    internal static (ProjectDatabase db, ScanSummary sum) Scan(IBlockSource src, ScanOptions? opt = null, IProgress<ScanProgress>? p = null, CancellationToken ct = default, string? projectPath = null)
    {
        var path = projectPath ?? Path.Combine(Fixtures.NewTempDir("proj"), "scan" + ProjectDatabase.FileExtension);
        var db = File.Exists(path) ? ProjectDatabase.Open(path) : ProjectDatabase.Create(path);
        var sum = new ScanOrchestrator(src, db, opt ?? new ScanOptions()).Run(p, ct);
        return (db, sum);
    }

    /// <summary>Matches each tracked file against the catalogue by content hash and records the outcome.</summary>
    private ScenarioReport Evaluate(string scenario, FixtureImage fx, ProjectDatabase db, IBlockSource src, ScanSummary sum, IEnumerable<FixtureFile> tracked)
    {
        var reader = new ResilientReader(src, null);
        var all = db.QueryFiles(new FileQuery { Limit = 1_000_000, View = ResultView.All });
        var hashes = new Dictionary<long, string>();
        string HashOf(FoundFile f)
        {
            if (!hashes.TryGetValue(f.Id, out var h))
            {
                using var cs = new ContentStream(reader, f.Layout);
                hashes[f.Id] = h = Fixtures.Sha256(cs);
            }
            return h;
        }
        var rep = new ScenarioReport
        {
            Scenario = scenario, Setup = fx.Scenario, Methods = string.Join(", ", sum.MethodsRun), Outcome = sum.Outcome.ToString(), TotalCandidates = all.Count,
        };
        foreach (var exp in tracked)
        {
            rep.TrackedFiles++;
            var byName = all.Where(f => f.Name == exp.Name).ToList();
            var sizeMatch = all.Where(f => f.Size == exp.Size).ToList();
            var exact = byName.Concat(sizeMatch).Distinct().FirstOrDefault(f => HashOf(f) == exp.Sha256);
            if (exact != null)
            {
                rep.Detected++; rep.Reconstructed++; rep.VerifiedByHash++;
                rep.Details.Add($"{exp.Name}: recovered byte-exact by {exact.Engines} as '{exact.DisplayPath}' ({exact.Structure}, {exact.Confidence})");
            }
            else if (byName.Count > 0)
            {
                rep.Detected++;
                var f = byName[0];
                bool flagged = f.State is RecoveryState.PartialOrDamaged or RecoveryState.Failed || f.Confidence <= Confidence.Low || f.Structure == StructureCheck.Damaged;
                if (flagged) rep.PartialOrFlagged++;
                else rep.Failures.Add($"{exp.Name}: content differs but the candidate is NOT flagged as damaged ({f.Structure}, {f.Confidence})");
                rep.Details.Add($"{exp.Name}: metadata found by {f.Engines} but content not byte-exact; flagged={flagged} ({f.Notes})");
            }
            else
            {
                rep.NotRecovered++;
                rep.Details.Add($"{exp.Name}: not recovered");
            }
        }
        foreach (var r in db.GetRegions()) rep.SkippedRegions.Add($"{r.Kind} @{r.Offset} len {r.Length}: {r.Note}");
        foreach (var d in rep.Details) _out.WriteLine(d);
        return rep;
    }

    [Fact]
    public void DeepScan_ntfs_deleted_files_are_recovered_and_correlated_with_carving()
    {
        var fx = Fixtures.Get("ntfs_deleted");
        string before = Fixtures.Sha256File(fx.ImagePath);
        using var src = new FileBlockSource(fx.ImagePath);
        var (db, sum) = Scan(src);
        using var _ = db;
        Assert.Equal(ScanOutcome.Completed, sum.Outcome);
        var rep = Evaluate("A1 NTFS deleted files (deep scan)", fx, db, src, sum, fx.Files.Where(f => f.Deleted));
        rep.Limitations.Add("Deleted files whose clusters are reused would be flagged as overwritten; none were reused in this image.");
        rep.Save();
        Assert.Empty(rep.Failures);
        Assert.Equal(rep.TrackedFiles, rep.VerifiedByHash);
        // The carver found the same JPEG; it must be merged into the named NTFS result, not listed twice.
        var jpg = db.QueryFiles(new FileQuery { Search = "holiday_beach.jpg" }).Single();
        Assert.True(jpg.Engines.HasFlag(EngineId.NtfsMft) && jpg.Engines.HasFlag(EngineId.SignatureCarving), jpg.Engines.ToString());
        Assert.True(db.CountView(ResultView.Duplicates) > 0);
        // Test F: the source image was not modified by the scan.
        src.Dispose();
        Assert.Equal(before, Fixtures.Sha256File(fx.ImagePath));
    }

    [Fact]
    public void DeepScan_fat32_and_exfat()
    {
        foreach (var (name, scen) in new[] { ("fat32_deleted", "A2 FAT32 deleted files (deep scan)"), ("exfat_deleted", "A3 exFAT deleted files (deep scan)") })
        {
            var fx = Fixtures.Get(name);
            using var src = new FileBlockSource(fx.ImagePath);
            var (db, sum) = Scan(src);
            using var _ = db;
            var rep = Evaluate(scen, fx, db, src, sum, fx.Files.Where(f => f.Deleted));
            if (name.StartsWith("fat")) rep.Limitations.Add("FAT erases the first character of deleted 8.3-only names; they are shown as '_'.");
            rep.Save();
            Assert.Empty(rep.Failures);
            Assert.Equal(rep.TrackedFiles, rep.VerifiedByHash);
        }
    }

    [Fact]
    public void DeepScan_after_quick_reformat_recovers_old_files_with_original_names()
    {
        var fx = Fixtures.Get("reformatted_ntfs");
        using var src = new FileBlockSource(fx.ImagePath);
        var (db, sum) = Scan(src);
        using var _ = db;
        var rep = Evaluate("E1 Quick-reformatted NTFS (Windows reinstall simulation)", fx, db, src, sum, fx.Files);
        rep.Limitations.Add("Files whose MFT records and clusters were reused by the new file system cannot be recovered.");
        rep.Limitations.Add("Old folder records were overwritten by the new MFT, so recovered files are grouped as $PreviousFileSystem\\Folder #N.");
        rep.Save();
        Assert.Empty(rep.Failures);
        // All media/document samples must come back byte-exact, with their original names via orphan MFT records.
        foreach (var n in new[] { "holiday_beach.jpg", "diagram.png", "invoice_2019.pdf", "thesis.docx", "song.mp3", "old_report.doc" })
        {
            var f = db.QueryFiles(new FileQuery { Search = n }).FirstOrDefault(x => x.Name == n);
            Assert.True(f != null && f.Engines.HasFlag(EngineId.OrphanMftRecords), $"{n} not recovered by name");
        }
        Assert.True(rep.VerifiedByHash >= 7, $"only {rep.VerifiedByHash} verified");
    }

    [Fact]
    public void DeepScan_finds_lost_partitions_and_their_files_without_modifying_the_image()
    {
        var fx = Fixtures.Get("lost_partitions");
        string before = Fixtures.Sha256File(fx.ImagePath);
        using (var src = new FileBlockSource(fx.ImagePath))
        {
            var (db, sum) = Scan(src);
            using var _ = db;
            var parts = db.LoadPartitions();
            foreach (var p in parts) _out.WriteLine($"{p.Status} {p.FileSystem} @{p.Offset} len {p.Length} {p.Evidence}");
            foreach (var exp in fx.Partitions!)
                Assert.Contains(parts, p => p.Status == "Candidate" && p.Offset == exp.Offset && p.FileSystem == exp.Fs);
            var rep = Evaluate("C1 Lost partitions (MBR, GPT and NTFS boot sector wiped)", fx, db, src, sum, fx.Files);
            rep.Details.Insert(0, $"Lost volumes found: {string.Join(", ", parts.Where(p => p.Status == "Candidate").Select(p => $"{p.FileSystem}@{p.Offset} ({p.Evidence})"))}");
            rep.Save();
            Assert.Empty(rep.Failures);
            Assert.Equal(rep.TrackedFiles, rep.VerifiedByHash);
            // Files of lost volumes keep their names and folders.
            Assert.Contains(db.QueryFiles(new FileQuery { Search = "thesis.docx" }), f => f.Path == "\\Work");
            // The lost volume's own MFT must not also be reported as a "previous file system".
            Assert.Single(db.QueryFiles(new FileQuery { Search = "thesis.docx" }));
            Assert.Equal(fx.Files.Count, db.CountView(ResultView.All));
        }
        Assert.Equal(before, Fixtures.Sha256File(fx.ImagePath));
    }

    [Fact]
    public void DeepScan_carving_only_image_report()
    {
        var fx = Fixtures.Get("carve_raw");
        using var src = new FileBlockSource(fx.ImagePath);
        var (db, sum) = Scan(src);
        using var _ = db;
        var rep = Evaluate("B1 Signature carving without any file system", fx, db, src, sum, fx.Files.Where(f => f.Expect == "exact"));
        foreach (var d in fx.Files.Where(f => f.Expect != "exact"))
        {
            var hit = db.QueryFiles(new FileQuery { Limit = 100000 }).FirstOrDefault(f => f.FirstOffset == d.Offset);
            rep.Details.Add($"{d.Name} (deliberately damaged): {(hit == null ? "not reported" : $"{hit.Structure}/{hit.State}: {hit.Notes}")}");
            if (hit != null && hit.Structure == StructureCheck.Verified) rep.Failures.Add($"{d.Name}: damaged file reported as Verified");
        }
        rep.Limitations.Add("Carved files have no original names or folders (named DV_<sector>.<ext>).");
        rep.Limitations.Add("Fragmented files are detected as damaged only for formats with checksums (PNG/ZIP/FLAC/Ogg/gzip...).");
        rep.Save();
        Assert.Empty(rep.Failures);
        Assert.Equal(rep.TrackedFiles, rep.VerifiedByHash);
    }

    [Fact]
    public void Unreadable_sectors_are_recorded_skipped_and_flagged_not_hidden()
    {
        var fx = Fixtures.Get("carve_raw");
        var jpg = fx.Files.First(f => f.Name == "family_large.jpg");
        long badStart = jpg.Offset!.Value + 64 * 1024;
        using var src = new FaultyBlockSource(new FileBlockSource(fx.ImagePath), (badStart, 8192), (20L * 1024 * 1024, 64 * 512));
        var (db, sum) = Scan(src, new ScanOptions { StopWhenDriveFailing = false });
        using var _ = db;
        Assert.Equal(ScanOutcome.CompletedWithGaps, sum.Outcome);
        Assert.False(sum.IsExhaustive);
        var regions = db.GetRegions();
        Assert.Contains(regions, r => r.Kind == RegionKind.Unreadable && r.Offset <= badStart && r.Offset + r.Length >= badStart + 8192);
        Assert.Contains(regions, r => r.Kind == RegionKind.SkippedAfterErrors);
        var hit = db.QueryFiles(new FileQuery { Limit = 100000 }).First(f => f.FirstOffset == jpg.Offset);
        Assert.Equal(RecoveryState.PartialOrDamaged, hit.State);
        Assert.NotEqual(Confidence.High, hit.Confidence);
        var rep = new ScenarioReport
        {
            Scenario = "D1 Unreadable sectors", Setup = "carve_raw image behind a fault injector: 16 bad sectors inside a JPEG, 64 bad sectors elsewhere",
            Methods = string.Join(", ", sum.MethodsRun), Outcome = sum.Outcome.ToString(), TrackedFiles = 1, Detected = 1, PartialOrFlagged = 1,
            Details = { $"JPEG overlapping bad sectors: {hit.State}, {hit.Confidence}: {hit.Notes}" },
            SkippedRegions = regions.Select(r => $"{r.Kind} @{r.Offset} len {r.Length}").ToList(),
        };
        rep.Save();
    }

    [Fact]
    public void Drive_health_threshold_stops_the_scan_resumably()
    {
        var fx = Fixtures.Get("carve_raw");
        using var src = new FaultyBlockSource(new FileBlockSource(fx.ImagePath), (4L * 1024 * 1024, 2 * 1024 * 1024));
        var (db, sum) = Scan(src, new ScanOptions { BadSectorStopThreshold = 100, ConsecutiveBadSectorsBeforeSkip = 1000 });
        using var _ = db;
        Assert.Equal(ScanOutcome.StoppedDriveHealth, sum.Outcome);
        Assert.Equal("StoppedDriveHealth", db.GetMeta("scan_status"));
        Assert.Contains(db.GetIssues(), i => i.issue.Message.Contains("failing"));
    }

    [Fact]
    public void Cancelled_scan_resumes_and_produces_the_same_results_as_an_uninterrupted_scan()
    {
        var fx = Fixtures.Get("carve_raw");
        using var src = new FileBlockSource(fx.ImagePath);
        var opt = new ScanOptions { CheckpointInterval = 256 * 1024, ReadChunkSize = 128 * 1024 };
        var (refDb, refSum) = Scan(src, opt);
        var expected = refDb.QueryFiles(new FileQuery { Limit = 100000, View = ResultView.All }).Select(f => (f.FirstOffset, f.Size, f.Name)).OrderBy(x => x).ToList();
        refDb.Dispose();

        var path = Path.Combine(Fixtures.NewTempDir("resume"), "r" + ProjectDatabase.FileExtension);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ScanProgress>(p => { if (p.Unit == "bytes" && p.Done > 1536L * 1024) cts.Cancel(); });
        var (db1, s1) = Scan(src, opt, progress, cts.Token, path);
        Assert.Equal(ScanOutcome.Cancelled, s1.Outcome);
        Assert.False(s1.IsExhaustive);
        Assert.Contains(db1.GetRegions(), r => r.Kind == RegionKind.NotReachedCancelled);
        long partial = db1.CountFiles(new FileQuery());
        db1.Dispose();

        var (db2, s2) = Scan(src, opt, null, default, path); // reopen and resume
        using var _ = db2;
        Assert.Equal(ScanOutcome.Completed, s2.Outcome);
        Assert.Contains(db2.GetIssues(), i => i.issue.Message.StartsWith("Resuming"));
        var got = db2.QueryFiles(new FileQuery { Limit = 100000, View = ResultView.All }).Select(f => (f.FirstOffset, f.Size, f.Name)).OrderBy(x => x).ToList();
        _out.WriteLine($"partial={partial} final={got.Count} expected={expected.Count}");
        Assert.True(partial < expected.Count);
        Assert.Equal(expected, got);
        new ScenarioReport
        {
            Scenario = "F2 Cancel and resume", Setup = "carve_raw: deep scan cancelled after 1.5 MiB, project reopened and resumed",
            Outcome = $"{s1.Outcome} then {s2.Outcome}", TotalCandidates = got.Count,
            Details = { $"{partial} candidates before cancellation; {got.Count} after resume; uninterrupted scan: {expected.Count}; identical sets: {expected.SequenceEqual(got)}" },
        }.Save();
    }

    [Fact]
    public void Deleted_file_whose_data_never_reached_the_disk_is_flagged_before_restore()
    {
        // Windows can delete a file while its data is still in the write cache: the MFT record survives,
        // the clusters stay zero. The scan must not present such a file as recoverable.
        var fx = Fixtures.Get("ntfs_deleted");
        var copy = Path.Combine(Fixtures.NewTempDir("zeroed"), "z.img");
        File.Copy(fx.ImagePath, copy);
        long off, len;
        using (var src0 = new FileBlockSource(copy))
        {
            var (db0, _) = Scan(src0, new ScanOptions { Methods = ScanMethods.Quick });
            var f0 = db0.QueryFiles(new FileQuery { Search = "holiday_beach.jpg" }).Single();
            off = f0.FirstOffset; len = f0.Size;
            db0.Dispose();
        }
        using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write)) { fs.Position = off; fs.Write(new byte[len]); }
        using var src = new FileBlockSource(copy);
        var (db, _) = Scan(src);
        using var __ = db;
        var f = db.QueryFiles(new FileQuery { Search = "holiday_beach.jpg", View = ResultView.All }).Single();
        Assert.Equal(StructureCheck.Damaged, f.Structure);
        Assert.Equal(RecoveryState.PartialOrDamaged, f.State);
        Assert.Contains("zeros", f.Notes);
        // Intact deleted files are confirmed by the same check.
        var png = db.QueryFiles(new FileQuery { Search = "diagram.png" }).Single();
        Assert.Equal(StructureCheck.Verified, png.Structure);
    }
}
