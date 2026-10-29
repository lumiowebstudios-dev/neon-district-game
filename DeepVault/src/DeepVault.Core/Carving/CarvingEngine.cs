using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;

namespace DeepVault.Core.Carving;

/// <summary>Sees every scanned sector (used for lost-partition and orphan-MFT-record detection in the same pass).</summary>
public interface ISectorObserver
{
    /// <param name="abs">Absolute offset of the sector.</param>
    /// <param name="data">Bytes from the sector start to the end of the current read chunk (at least one sector).</param>
    void OnSector(long abs, ReadOnlySpan<byte> data);
}

public sealed class CarvingOptions
{
    public ISet<string>? EnabledFormats { get; init; }
    /// <summary>Granularity at which file starts are tested. 512 covers both 512e and 4Kn layouts.</summary>
    public int Step { get; init; } = 512;
    public int ChunkSize { get; init; } = 4 * 1024 * 1024;
    /// <summary>Results and position are committed at least this often (bytes of plan).</summary>
    public long CheckpointInterval { get; init; } = 256L * 1024 * 1024;
    public long MinFileSize { get; init; } = 64;
}

/// <summary>Tracks unreadable regions so carved results overlapping them can be flagged.</summary>
public sealed class BadRegionTracker : IRegionSink
{
    private readonly IRegionSink? _inner;
    private readonly List<(long s, long e)> _bad = new();
    public BadRegionTracker(IRegionSink? inner) => _inner = inner;
    public void Report(DiskRegion r)
    {
        if (r.Kind is RegionKind.Unreadable or RegionKind.SkippedAfterErrors) lock (_bad) _bad.Add((r.Offset, r.Offset + r.Length));
        _inner?.Report(r);
    }
    public long OverlapBytes(long s, long e)
    {
        long t = 0;
        lock (_bad) foreach (var (a, b) in _bad) { long o = Math.Min(b, e) - Math.Max(a, s); if (o > 0) t += o; }
        return t;
    }
}

/// <summary>
/// Engine B: deep signature scan. Reads the planned regions sequentially, tests every sector start
/// against the signature registry, and delimits candidates with format-specific analyzers.
/// Carved files are assumed contiguous; analyzers flag inconsistencies caused by fragmentation.
/// </summary>
public sealed class CarvingEngine
{
    private readonly IBlockSource _disk;
    private readonly ResilientReader _reader;
    private readonly IScanSink _sink;
    private readonly SignatureRegistry _registry;
    private readonly CarvingOptions _opt;
    private readonly IReadOnlyList<ISectorObserver> _observers;
    private readonly BadRegionTracker? _bad;

    public long FilesFound { get; private set; }

    public CarvingEngine(IBlockSource disk, ResilientReader reader, IScanSink sink, SignatureRegistry registry,
        CarvingOptions? options = null, IReadOnlyList<ISectorObserver>? observers = null, BadRegionTracker? badTracker = null)
    {
        _disk = disk;
        _reader = reader;
        _sink = sink;
        _registry = registry;
        _opt = options ?? new CarvingOptions();
        _observers = observers ?? Array.Empty<ISectorObserver>();
        _bad = badTracker;
    }

    /// <summary>Disk offset where a scan resumed at plan position <paramref name="planPos"/> starts reading.</summary>
    public static long DiskOffsetAt(IReadOnlyList<Extent> regions, long planPos, int step)
    {
        long pos = 0;
        foreach (var r in regions)
        {
            if (pos + r.Length > planPos)
            {
                long abs = r.Offset + Math.Max(0, planPos - pos);
                return abs - (abs - r.Offset) % step;
            }
            pos += r.Length;
        }
        return long.MaxValue;
    }

    /// <summary>
    /// Scans the regions. <paramref name="resumeAt"/> is a position in "plan bytes" (sum of region
    /// lengths) previously passed to <paramref name="checkpoint"/>. Returns the plan position reached.
    /// </summary>
    public long Run(IReadOnlyList<Extent> regions, long resumeAt, Action<long>? checkpoint, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var enabled = _opt.EnabledFormats ?? _registry.DefaultEnabled();
        var cache = new CachedReader(_reader);
        var csrc = new CachedCarveSource(cache);
        long planTotal = regions.Sum(r => r.Length);
        var prog = new EngineProgress { Phase = "Deep signature scan", Unit = "bytes", Total = planTotal };
        var matches = new List<CarveFormat>();
        var buf = new byte[_opt.ChunkSize];
        long planPos = 0;
        long lastCheckpoint = resumeAt;
        long skipUntil = -1;
        int step = _opt.Step;

        foreach (var region in regions)
        {
            long regionStart = region.Offset, regionEnd = region.Offset + region.Length;
            if (planPos + region.Length <= resumeAt) { planPos += region.Length; continue; }
            long abs = regionStart + Math.Max(0, resumeAt - planPos);
            abs -= (abs - regionStart) % step;
            while (abs < regionEnd)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buf.Length, regionEnd - abs);
                int got = _reader.Read(abs, buf.AsSpan(0, want));
                if (got <= 0) break;
                for (int o = 0; o + step <= got || (o < got && o == 0); o += step)
                {
                    long sec = abs + o;
                    if (sec < skipUntil) continue;
                    var span = buf.AsSpan(o, got - o);
                    foreach (var obs in _observers) obs.OnSector(sec, span);
                    _registry.Match(span, enabled, matches);
                    foreach (var f in matches)
                    {
                        var r = f.SafeAnalyze(csrc, sec, _disk.Length - sec);
                        if (r == null || r.Length < _opt.MinFileSize) continue;
                        Emit(f, r, sec);
                        if (r.Check is StructureCheck.Verified or StructureCheck.Plausible)
                            skipUntil = sec + CarveIO.AlignUp(r.Length, step);
                        break;
                    }
                }
                abs += got;
                long newPlan = planPos + (abs - regionStart);
                prog.Done = Math.Min(newPlan, planTotal);
                prog.FilesFound = FilesFound;
                progress?.Report(prog);
                if (newPlan - lastCheckpoint >= _opt.CheckpointInterval)
                {
                    _sink.Commit();
                    checkpoint?.Invoke(newPlan);
                    lastCheckpoint = newPlan;
                }
            }
            planPos += region.Length;
        }
        _sink.Commit();
        checkpoint?.Invoke(planTotal);
        return planTotal;
    }

    private void Emit(CarveFormat f, CarveResult r, long abs)
    {
        string ext = string.IsNullOrEmpty(r.Extension) ? f.DefaultExtension : r.Extension;
        var cat = FileTypes.CategoryOf(ext);
        if (cat == FileCategory.Other) cat = f.Category;
        long len = Math.Min(r.Length, _disk.Length - abs);
        var notes = new List<string>();
        if (r.Detail != null) notes.Add(r.Detail);
        if (r.Note != null) notes.Add(r.Note);
        notes.Add("Carved from raw sectors: assumes the file was stored contiguously");
        var state = RecoveryState.CandidateDetected;
        var conf = r.Check switch
        {
            StructureCheck.Verified => Confidence.High,
            StructureCheck.Plausible => Confidence.Medium,
            _ => Confidence.Low,
        };
        if (r.Check == StructureCheck.Damaged) state = RecoveryState.PartialOrDamaged;
        long bad = _bad?.OverlapBytes(abs, abs + len) ?? 0;
        if (bad > 0)
        {
            notes.Add($"{bad} bytes lie in unreadable sectors and were zero-filled");
            state = RecoveryState.PartialOrDamaged;
            conf = Confidence.Low;
        }
        _sink.AddFile(new FoundFile
        {
            Name = $"DV_{abs / 512:D10}.{ext}",
            Path = "\\Carved\\" + FileTypes.CategoryName(cat),
            Extension = ext,
            Category = cat,
            Size = len,
            Engines = EngineId.SignatureCarving,
            SourceDetail = $"Signature '{f.Id}' at byte offset {abs}",
            State = state,
            Confidence = conf,
            Structure = r.Check,
            Layout = DataLayout.Contiguous(abs, len),
            FormatId = f.Id,
            Notes = string.Join("; ", notes),
        });
        FilesFound++;
    }
}
