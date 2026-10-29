using System.Diagnostics;
using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.FileSystems.ExFat;
using DeepVault.Core.FileSystems.Fat;
using DeepVault.Core.FileSystems.Ntfs;
using DeepVault.Core.IO;
using DeepVault.Core.Logging;
using DeepVault.Core.Partitions;
using DeepVault.Core.Util;

namespace DeepVault.Core.Scanning;

/// <summary>
/// "Deep Scan — All Applicable Methods": runs partition analysis, file-system metadata engines,
/// one sequential raw pass (carving + lost-volume + orphan-MFT detection), analysis of lost volumes,
/// reconstruction from previous-file-system records and cross-engine correlation. Each phase is
/// checkpointed in the project database so an interrupted scan resumes instead of starting over.
/// </summary>
public sealed class ScanOrchestrator
{
    public const string PhasePartitions = "partitions", PhaseMetadata = "metadata", PhaseRaw = "raw", PhaseLost = "lostvolumes", PhaseOrphans = "orphans", PhaseCorrelate = "correlate";

    private readonly IBlockSource _src;
    private readonly ProjectDatabase _db;
    private readonly ScanOptions _opt;
    private readonly ILog _log;
    private ResilientReader _reader = null!;
    private BadRegionTracker _bad = null!;
    private CancellationTokenSource _healthCts = new();
    private ScanProgress _p = new();
    private IProgress<ScanProgress>? _progress;
    private readonly Stopwatch _clock = new();

    private sealed class VolumeState
    {
        public PartitionInfo Part = null!;
        public int Unit;
        public string Label = "";
        public NtfsVolumeResult? Ntfs;
        public FatVolumeResult? Fat;
        public ExFatVolumeResult? ExFat;
    }

    public ScanOrchestrator(IBlockSource source, ProjectDatabase db, ScanOptions options, ILog? log = null)
    {
        _src = source;
        _db = db;
        _opt = options;
        _log = log ?? NullLog.Instance;
    }

    public ScanSummary Run(IProgress<ScanProgress>? progress, CancellationToken userCt)
    {
        _progress = progress;
        _clock.Start();
        var summary = new ScanSummary();
        _bad = new BadRegionTracker(_db);
        _reader = new ResilientReader(_src, _bad, _opt.ToReadPolicy());
        _healthCts = new CancellationTokenSource();
        _reader.HealthWarning += n =>
        {
            _p.DriveHealthWarning = true;
            _db.AddIssue(new ScanIssue(IssueSeverity.Error, EngineId.None,
                $"{n} unreadable sectors so far. The drive may be failing: continuing to scan can make it worse. Consider stopping and seeking professional help or imaging the drive with dedicated tools."));
            _log.Warn($"Drive health warning after {n} bad sectors");
            if (_opt.StopWhenDriveFailing) _healthCts.Cancel();
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCt, _healthCts.Token);
        var ct = linked.Token;
        _db.SetMetaJson("scan_options", _opt);
        _db.SetMeta("scan_status", "running");
        _db.SetMeta("scan_started", DateTime.UtcNow.ToString("O"));
        var phases = PlannedPhases();
        _p.PhaseCount = phases.Count;
        _log.Info($"Scan started: methods={_opt.Methods}, scope={_opt.Scope}, source length={_src.Length}");

        try
        {
            var registry = SignatureRegistry.Create(_opt.CustomSignaturesPath);
            foreach (var e in registry.LoadErrors) _db.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.SignatureCarving, $"Custom signature ignored: {e}"));

            // 1. Partitions (always needed to understand the disk; cheap and read-only).
            Enter(0, "Analysing partition table");
            var table = PartitionTableParser.Parse(_src);
            foreach (var i in table.Issues) _db.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.PartitionScan, i));
            var volumes = table.Partitions.Where(p => p.Boot != null).Select((p, i) => new VolumeState { Part = p, Unit = p.Index, Label = $"Partition {p.Index} ({FileSystemProbe.Describe(p.FileSystem)})" }).ToList();
            _db.SavePartitions(table.Partitions);
            _db.SetMeta("partition_scheme", table.Scheme.ToString());
            _db.Checkpoint(PhasePartitions, "done", 0);
            summary.MethodsRun.Add("Partition table analysis");

            // 2. File-system metadata.
            int phaseIdx = 1;
            if (_opt.Methods.HasFlag(ScanMethods.FileSystemMetadata))
            {
                Enter(phaseIdx++, "Reading file-system metadata");
                bool done = _db.GetPhase(PhaseMetadata)?.Status == "done";
                if (!done) _db.RollbackPhase(PhaseMetadata);
                _db.CurrentPhase = PhaseMetadata;
                foreach (var v in volumes)
                {
                    ct.ThrowIfCancellationRequested();
                    AnalyseVolume(v, includeExisting: _opt.IncludeExistingFiles, prepareOnly: done, ct);
                }
                _db.Checkpoint(PhaseMetadata, "done", 0);
                summary.MethodsRun.Add("File-system metadata (NTFS / FAT / exFAT)");
            }
            else
            {
                foreach (var v in volumes) AnalyseVolume(v, false, prepareOnly: true, ct);
                summary.MethodsSkipped.Add("File-system metadata");
            }

            // 3. Raw pass.
            bool wantRaw = _opt.Methods.HasFlag(ScanMethods.SignatureCarving) || _opt.Methods.HasFlag(ScanMethods.LostPartitionSearch) || _opt.Methods.HasFlag(ScanMethods.OrphanMftRecords);
            var lost = new List<PartitionInfo>();
            if (wantRaw)
            {
                Enter(phaseIdx++, "Deep scan of raw sectors");
                RunRawPass(volumes, table, registry, lost, summary, ct);
            }

            // 4. Lost volumes.
            if (_opt.Methods.HasFlag(ScanMethods.LostPartitionSearch))
            {
                Enter(phaseIdx++, "Analysing lost volumes");
                var lostStates = new List<VolumeState>();
                var phase = _db.GetPhase(PhaseLost);
                if (phase?.Status != "done") _db.RollbackPhase(PhaseLost);
                _db.CurrentPhase = PhaseLost;
                int unit = 100;
                foreach (var lp in lost)
                {
                    ct.ThrowIfCancellationRequested();
                    var v = new VolumeState { Part = lp, Unit = unit++, Label = $"Lost {FileSystemProbe.Describe(lp.FileSystem)} volume @ {Bytes.FormatSize(lp.Offset)}" };
                    AnalyseVolume(v, includeExisting: true, prepareOnly: phase?.Status == "done", ct);
                    lostStates.Add(v);
                }
                volumes.AddRange(lostStates);
                _db.SavePartitions(table.Partitions.Concat(lost));
                _db.Checkpoint(PhaseLost, "done", 0);
                summary.LostPartitions = lost.Count;
                summary.MethodsRun.Add($"Lost partition search ({lost.Count} found)");
            }

            // 5. Orphan MFT records.
            if (_opt.Methods.HasFlag(ScanMethods.OrphanMftRecords))
            {
                Enter(phaseIdx++, "Rebuilding files from previous file-system records");
                if (_db.GetPhase(PhaseOrphans)?.Status != "done")
                {
                    _db.RollbackPhase(PhaseOrphans);
                    _db.CurrentPhase = PhaseOrphans;
                    var geoms = volumes.Where(v => v.Ntfs != null).Select(v => new VolumeGeometry(v.Part.Offset, v.Part.Length, v.Ntfs!.ClusterSize, v.Label,
                        v.Ntfs.Bitmap != null ? v.Ntfs.IsClusterAllocated : null)).ToList();
                    var eng = new OrphanMftEngine(_src, _reader, _db, _db, geoms, _opt.ValidateUpTo);
                    eng.AnalysedMftExtents.AddRange(volumes.Where(v => v.Unit >= 100 && v.Ntfs != null).SelectMany(v => v.Ntfs!.MftExtents));
                    eng.Run(EngineProgressAdapter(), ct);
                    foreach (var f in eng.Findings) { summary.Findings.Add(f); _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.OrphanMftRecords, f)); }
                    _db.Checkpoint(PhaseOrphans, "done", 0);
                }
                summary.MethodsRun.Add("Previous file-system MFT records");
            }

            // 6. Correlation / deduplication, then content checks of named candidates.
            Enter(phaseIdx, "Combining results and checking content");
            _db.CurrentPhase = PhaseCorrelate;
            summary.Correlated = _db.CorrelateCarvedWithMetadata();
            CheckContent(ct);
            _db.Checkpoint(PhaseCorrelate, "done", summary.Correlated);

            summary.BadSectors = _reader.TotalBadSectors;
            bool gaps = summary.BadSectors > 0 || _db.GetRegions().Any(r => r.Kind is RegionKind.Unreadable or RegionKind.SkippedAfterErrors);
            summary.Outcome = gaps ? ScanOutcome.CompletedWithGaps : ScanOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            summary.Outcome = _healthCts.IsCancellationRequested && !userCt.IsCancellationRequested ? ScanOutcome.StoppedDriveHealth : ScanOutcome.Cancelled;
            _db.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.None, summary.Outcome == ScanOutcome.StoppedDriveHealth
                ? "Scan stopped automatically because the drive returned many read errors. Results so far are kept; the scan is NOT complete."
                : "Scan cancelled by the user. Results so far are kept; the scan is NOT complete and can be resumed."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            summary.Outcome = ScanOutcome.Failed;
            summary.Error = ex.Message;
            _db.AddIssue(new ScanIssue(IssueSeverity.Error, EngineId.None, $"Scan failed: {ex.Message}"));
            _log.Error("Scan failed", ex);
        }
        finally
        {
            _db.Commit();
        }
        summary.Duration = _clock.Elapsed;
        summary.BadSectors = _reader.TotalBadSectors;
        summary.FilesFound = _db.CountFiles(new FileQuery { View = ResultView.All });
        _db.SetMeta("scan_status", summary.Outcome.ToString());
        _db.SetMeta("scan_finished", DateTime.UtcNow.ToString("O"));
        _db.SetMetaJson("scan_summary", summary);
        try
        {
            var rp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_db.Path))!, $"{Path.GetFileNameWithoutExtension(_db.Path)}-scan-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            ScanReport.Write(_db, rp);
            _db.SetMeta("scan_report", rp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn($"Scan report not written: {ex.Message}"); }
        _log.Info($"Scan finished: {summary.Outcome}, files={summary.FilesFound}, badSectors={summary.BadSectors}, duration={summary.Duration}");
        return summary;
    }

    /// <summary>
    /// Reads deleted candidates found by file-system metadata (in disk order) and checks their content:
    /// files up to the validation limit are structure-validated in full, larger ones get a header check.
    /// Content that is all zeros or does not match its type is marked damaged before the user restores it.
    /// </summary>
    private void CheckContent(CancellationToken ct)
    {
        var todo = _db.UncheckedDeletedCandidates();
        if (todo.Count == 0) return;
        var prog = new EngineProgress { Phase = "Checking content of deleted files", Unit = "files", Total = todo.Count };
        var report = EngineProgressAdapter(0);
        int damaged = 0, done = 0;
        foreach (var f in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 64 == 0) { prog.Done = done; report.Report(prog); }
            if (!FormatValidator.ExpectsSignature(f.Extension) && f.Size > 4096) continue;
            using var cs = new ContentStream(_reader, f.Layout);
            Stream s = f.Size <= _opt.ValidateUpTo ? cs : new HeadStream(cs, 64 * 1024);
            FormatValidator.Validation v;
            var head = new byte[Math.Min(4096, f.Size)];
            cs.ReadExactly(head);
            cs.Position = 0;
            if (Bytes.IsAllZero(head)) v = new FormatValidator.Validation(StructureCheck.Damaged, "content is all zeros (never written, wiped or overwritten)", null);
            else if (f.Size <= _opt.ValidateUpTo) v = FormatValidator.Validate(new StreamCarveSource(cs), f.Extension);
            else
            {
                v = FormatValidator.Validate(new StreamCarveSource(s), f.Extension);
                if (v.Check is StructureCheck.Damaged && v.Note?.Contains("header") != true) v = new FormatValidator.Validation(StructureCheck.Plausible, "header valid (file too large for a full check during the scan)", null);
            }
            if (v.Check == StructureCheck.NotChecked) continue;
            bool bad = v.Check == StructureCheck.Damaged;
            if (bad) damaged++;
            _db.UpdateState(f.Id, bad ? RecoveryState.PartialOrDamaged : f.State, v.Check, bad ? "content check: " + v.Note : null);
            if (bad) _db.UpdateConfidence(f.Id, Confidence.Low);
        }
        _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.None, $"Content check of {todo.Count:N0} deleted files: {damaged:N0} are damaged or empty."));
    }

    /// <summary>Exposes only the first bytes of a stream (header checks of large files).</summary>
    private sealed class HeadStream : Stream
    {
        private readonly Stream _s; private readonly long _n;
        public HeadStream(Stream s, long n) { _s = s; _n = Math.Min(n, s.Length); }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => _n;
        public override long Position { get => _s.Position; set => _s.Position = Math.Min(value, _n); }
        public override int Read(byte[] b, int o, int c) => _s.Position >= _n ? 0 : _s.Read(b, o, (int)Math.Min(c, _n - _s.Position));
        public override long Seek(long o, SeekOrigin so) => Position = so == SeekOrigin.Begin ? o : so == SeekOrigin.Current ? Position + o : _n + o;
        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private List<string> PlannedPhases()
    {
        var l = new List<string> { "Partitions" };
        if (_opt.Methods.HasFlag(ScanMethods.FileSystemMetadata)) l.Add("Metadata");
        if ((_opt.Methods & (ScanMethods.SignatureCarving | ScanMethods.LostPartitionSearch | ScanMethods.OrphanMftRecords)) != 0) l.Add("Raw");
        if (_opt.Methods.HasFlag(ScanMethods.LostPartitionSearch)) l.Add("Lost");
        if (_opt.Methods.HasFlag(ScanMethods.OrphanMftRecords)) l.Add("Orphans");
        l.Add("Correlate");
        return l;
    }

    private void Enter(int idx, string phase)
    {
        _p.PhaseIndex = idx;
        _p.Phase = phase;
        _p.Detail = "";
        _p.Done = 0;
        _p.Total = -1;
        Report();
        _log.Info($"Phase: {phase}");
    }

    private long _lastBytes;
    private TimeSpan _lastTime;

    private void Report()
    {
        _p.Elapsed = _clock.Elapsed;
        _p.BadSectors = _reader?.TotalBadSectors ?? 0;
        _progress?.Report(new ScanProgress
        {
            PhaseIndex = _p.PhaseIndex, PhaseCount = _p.PhaseCount, Phase = _p.Phase, Detail = _p.Detail, Done = _p.Done, Total = _p.Total, Unit = _p.Unit,
            FilesFound = _p.FilesFound, BytesPerSecond = _p.BytesPerSecond, Elapsed = _p.Elapsed, BadSectors = _p.BadSectors,
            DriveHealthWarning = _p.DriveHealthWarning, LostPartitionsFound = _p.LostPartitionsFound, OrphanRecordsFound = _p.OrphanRecordsFound,
        });
    }

    private IProgress<EngineProgress> EngineProgressAdapter(long filesBase = 0) => new SyncProgress<EngineProgress>(e =>
    {
        _p.Detail = e.Phase;
        _p.Done = e.Done;
        _p.Total = e.Total;
        _p.Unit = e.Unit;
        _p.FilesFound = filesBase + e.FilesFound;
        if (e.Unit == "bytes")
        {
            var now = _clock.Elapsed;
            if ((now - _lastTime).TotalSeconds >= 1)
            {
                _p.BytesPerSecond = (e.Done - _lastBytes) / (now - _lastTime).TotalSeconds;
                _lastBytes = e.Done;
                _lastTime = now;
            }
        }
        Report();
    });

    private void AnalyseVolume(VolumeState v, bool includeExisting, bool prepareOnly, CancellationToken ct)
    {
        var bs = v.Part.Boot!;
        var prog = EngineProgressAdapter(_db.CountView(ResultView.All));
        try
        {
            switch (bs.Kind)
            {
                case FsKind.Ntfs:
                {
                    var s = new NtfsScanner(_src, _reader, _db, v.Part.Offset, bs, v.Unit, v.Label, new NtfsScanOptions { IncludeExistingFiles = includeExisting });
                    v.Ntfs = prepareOnly ? s.Prepare() : s.Run(prog, ct);
                    if (v.Ntfs.VolumeName is { Length: > 0 } name) v.Label = v.Label.TrimEnd(')') + $", {name})";
                    if (!prepareOnly)
                        _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.NtfsMft, $"{v.Label}: {v.Ntfs.RecordsRead:N0} MFT records read, {v.Ntfs.DeletedFiles:N0} deleted files, {v.Ntfs.DeletedDirectories:N0} deleted folders.", v.Part.Offset));
                    break;
                }
                case FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32:
                {
                    var s = new FatScanner(_src, _reader, _db, v.Part.Offset, bs, v.Label, new FatScanOptions { IncludeExistingFiles = includeExisting, ValidateUpTo = _opt.ValidateUpTo });
                    v.Fat = prepareOnly ? s.Prepare() : s.Run(prog, ct);
                    if (!prepareOnly)
                        _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.FatDirectory, $"{v.Label}: {v.Fat.DirectoriesScanned:N0} folders, {v.Fat.DeletedFiles:N0} deleted files.", v.Part.Offset));
                    break;
                }
                case FsKind.ExFat:
                {
                    if (prepareOnly) break;
                    var s = new ExFatScanner(_reader, _db, v.Part.Offset, bs, v.Label, includeExisting);
                    v.ExFat = s.Run(prog, ct);
                    _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.ExFatDirectory, $"{v.Label}: {v.ExFat.DeletedFiles:N0} deleted files.", v.Part.Offset));
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // An engine failure must never be silent, and must not stop the other engines.
            _db.AddIssue(new ScanIssue(IssueSeverity.Error, EngineId.None, $"{v.Label}: analysis failed ({ex.GetType().Name}: {ex.Message}). Other methods continue.", v.Part.Offset));
            _log.Error($"Volume analysis failed for {v.Label}", ex);
        }
        _db.Commit();
    }

    /// <summary>Regions to read in the raw pass according to the scope, plus bytes excluded.</summary>
    private (List<Extent> plan, long excluded) BuildPlan(List<VolumeState> volumes)
    {
        if (_opt.Scope == CarveScope.EntireDisk) return (new List<Extent> { new(0, _src.Length) }, 0);
        // Collect allocated runs from every volume whose allocation map we know.
        var allocated = new List<(long s, long e)>();
        foreach (var v in volumes)
        {
            if (v.Ntfs?.Bitmap is { } bm)
                AddRuns(allocated, bm, v.Part.Offset, v.Ntfs.ClusterSize, v.Ntfs.TotalClusters, 0);
            else if (v.Fat?.Fat is { } fat)
            {
                long start = -1;
                for (long c = 2; c <= fat.Length; c++)
                {
                    bool a = c < fat.Length && fat[c] != 0;
                    if (a && start < 0) start = c;
                    if (!a && start >= 0) { allocated.Add((v.Fat.ClusterOffset(start), v.Fat.ClusterOffset(c))); start = -1; }
                }
            }
            else if (v.ExFat?.Bitmap is { } xb)
                AddRuns(allocated, xb, v.ExFat.HeapOffset, v.ExFat.ClusterSize, v.ExFat.ClusterCount, 0);
        }
        allocated.Sort();
        var plan = new List<Extent>();
        long pos = 0, excluded = 0;
        foreach (var (s, e) in allocated)
        {
            if (e <= pos) continue;
            if (s > pos) plan.Add(new Extent(pos, s - pos));
            excluded += e - Math.Max(s, pos);
            pos = Math.Max(pos, e);
        }
        if (pos < _src.Length) plan.Add(new Extent(pos, _src.Length - pos));
        return (plan, excluded);
    }

    private static void AddRuns(List<(long, long)> list, byte[] bitmap, long baseOffset, int cs, long clusters, long firstCluster)
    {
        long start = -1;
        long n = Math.Min(clusters, (long)bitmap.Length * 8);
        for (long c = 0; c <= n; c++)
        {
            bool a = c < n && (bitmap[c >> 3] & (1 << (int)(c & 7))) != 0;
            if (a && start < 0) start = c;
            else if (!a && start >= 0)
            {
                list.Add((baseOffset + (start + firstCluster) * cs, baseOffset + (c + firstCluster) * cs));
                start = -1;
            }
        }
    }

    private void RunRawPass(List<VolumeState> volumes, PartitionTableResult table, SignatureRegistry registry, List<PartitionInfo> lost, ScanSummary summary, CancellationToken ct)
    {
        var phase = _db.GetPhase(PhaseRaw);
        List<Extent> plan;
        long excluded;
        if (phase != null && _db.GetMetaJson<List<Extent>>("raw_plan") is { } saved)
        {
            plan = saved;
            excluded = long.TryParse(_db.GetMeta("raw_excluded"), out var x) ? x : 0;
        }
        else
        {
            (plan, excluded) = BuildPlan(volumes);
            _db.SetMetaJson("raw_plan", plan);
            _db.SetMeta("raw_excluded", excluded.ToString());
            if (excluded > 0)
                _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.SignatureCarving,
                    $"Scope: {Bytes.FormatSize(excluded)} occupied by existing files was not carved (choose 'Entire disk' scope to include it)."));
        }
        long planTotal = plan.Sum(e => e.Length);
        summary.BytesPlannedRaw = planTotal;
        summary.BytesExcludedByScope = excluded;

        var observers = new List<ISectorObserver>();
        var lpd = new LostPartitionDetector(_src, table.Partitions);
        if (_opt.Methods.HasFlag(ScanMethods.LostPartitionSearch)) observers.Add(lpd);
        OrphanMftCollector? omc = null;
        if (_opt.Methods.HasFlag(ScanMethods.OrphanMftRecords))
        {
            var mftExtents = volumes.Where(v => v.Ntfs != null).SelectMany(v => v.Ntfs!.MftExtents);
            omc = new OrphanMftCollector(_reader, _db, mftExtents);
            observers.Add(omc);
        }
        // Restore candidates found before an interruption.
        foreach (var off in _db.GetMetaJson<List<long>>("lost_candidates") ?? new())
        {
            var buf = new byte[512];
            _reader.Read(off, buf);
            lpd.OnSector(off, buf);
        }

        if (phase?.Status != "done")
        {
            long resumeAt = phase?.Status is "running" or "cancelled" ? phase.Position : 0;
            _db.RollbackPhase(PhaseRaw);
            _db.CurrentPhase = PhaseRaw;
            if (phase != null) _db.DiscardFindsFrom(PhaseRaw, CarvingEngine.DiskOffsetAt(plan, resumeAt, new CarvingOptions().Step));
            // Baseline: what exists now is kept if the pass is stopped before its first checkpoint.
            _db.Checkpoint(PhaseRaw, "running", resumeAt);
            long filesBefore = _db.CountView(ResultView.All), orphansBefore = _db.Count;
            if (resumeAt > 0)
            {
                _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.SignatureCarving, $"Resuming the deep scan at {Bytes.FormatSize(resumeAt)} of {Bytes.FormatSize(planTotal)}."));
                _log.Info($"Resuming raw pass at plan position {resumeAt}");
            }
            var enabled = _opt.Methods.HasFlag(ScanMethods.SignatureCarving) ? (_opt.EnabledFormats ?? registry.DefaultEnabled()) : new HashSet<string>();
            var engine = new CarvingEngine(_src, _reader, _db, registry,
                new CarvingOptions { EnabledFormats = enabled, CheckpointInterval = _opt.CheckpointInterval, ChunkSize = _opt.ReadChunkSize }, observers, _bad);
            long reached = resumeAt;
            try
            {
                engine.Run(plan, resumeAt, pos =>
                {
                    reached = pos;
                    _db.SetMetaJson("lost_candidates", lpd.Candidates.Select(c => c.Offset).ToList());
                    _db.Checkpoint(PhaseRaw, "running", pos);
                    _p.LostPartitionsFound = lpd.Candidates.Count;
                    _p.OrphanRecordsFound = orphansBefore + (omc?.Found ?? 0);
                }, new SyncProgress<EngineProgress>(e =>
                {
                    _p.LostPartitionsFound = lpd.Candidates.Count;
                    _p.OrphanRecordsFound = orphansBefore + (omc?.Found ?? 0);
                    EngineProgressAdapter(filesBefore).Report(e);
                }), ct);
            }
            catch (OperationCanceledException)
            {
                // Keep the last checkpoint: finds made after it are saved for browsing but are found
                // again (not duplicated) when the scan resumes there. Record the unscanned remainder honestly.
                _db.MarkPhaseStopped(PhaseRaw, "cancelled");
                _db.Report(new DiskRegion(reached, planTotal - reached, RegionKind.NotReachedCancelled, $"{Bytes.FormatSize(planTotal - reached)} of the planned deep scan was not reached (offset is a position in the scan plan, not on disk)"));
                _db.Commit();
                throw;
            }
            _db.SetMetaJson("lost_candidates", lpd.Candidates.Select(c => c.Offset).ToList());
            _db.Checkpoint(PhaseRaw, "done", planTotal);
        }
        summary.BytesScannedRaw = planTotal;
        if (_opt.Methods.HasFlag(ScanMethods.SignatureCarving)) summary.MethodsRun.Add("Deep signature scan (carving)");
        lost.AddRange(lpd.Candidates.Where(c => !table.Partitions.Any(p => p.Offset == c.Offset && p.Boot != null)));
        foreach (var c in lost)
            _db.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.PartitionScan, $"Lost {FileSystemProbe.Describe(c.FileSystem)} volume found at byte {c.Offset} ({Bytes.FormatSize(c.Length)}), evidence: {c.Evidence}.", c.Offset));
        _db.Commit();
    }
}

/// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts to a sync context, which reorders updates).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T value) => _a(value);
}
