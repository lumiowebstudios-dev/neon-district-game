using System.Text.Json;
using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;
using DeepVault.Core.Util;

namespace DeepVault.Core.FileSystems.Ntfs;

/// <summary>Compact, serialisable summary of an MFT record found outside any current MFT.</summary>
public sealed class OrphanRecord
{
    public long Phys { get; set; }
    public int RecordSize { get; set; }
    public long RecordNumber { get; set; } = -1;
    public ushort Seq { get; set; }
    public bool InUse { get; set; }
    public bool IsDir { get; set; }
    public long BaseRecord { get; set; }
    public ushort BaseSeq { get; set; }
    public string? Name { get; set; }
    public long ParentRecord { get; set; } = -1;
    public ushort ParentSeq { get; set; }
    public long Created { get; set; }
    public long Modified { get; set; }
    public long Accessed { get; set; }
    public long FnSize { get; set; }
    public bool FixupOk { get; set; }
    public bool HasAttrList { get; set; }
    // Unnamed $DATA
    public bool HasData { get; set; }
    public bool Resident { get; set; }
    public int ResidentLength { get; set; }
    public ushort DataInstance { get; set; }
    public long StartVcn { get; set; }
    public long RealSize { get; set; }
    public long InitSize { get; set; }
    public bool Compressed { get; set; }
    public int CompressionLog2 { get; set; }
    public bool Encrypted { get; set; }
    public List<long[]> Runs { get; set; } = new();

    public static OrphanRecord From(MftRecord r, long phys, int recordSize)
    {
        var fn = r.BestFileName();
        var o = new OrphanRecord
        {
            Phys = phys, RecordSize = recordSize, RecordNumber = r.HasRecordNumber ? r.RecordNumber : -1, Seq = r.Sequence,
            InUse = r.InUse, IsDir = r.IsDirectory, BaseRecord = r.BaseRecordNumber, BaseSeq = (ushort)(r.BaseRef >> 48),
            Name = fn?.Name, ParentRecord = fn?.ParentRecord ?? -1, ParentSeq = fn?.ParentSeq ?? 0,
            Created = r.SiCreated != 0 ? r.SiCreated : fn?.Created ?? 0,
            Modified = r.SiModified != 0 ? r.SiModified : fn?.Modified ?? 0,
            Accessed = r.SiAccessed != 0 ? r.SiAccessed : fn?.Accessed ?? 0,
            FnSize = fn?.RealSize ?? 0, FixupOk = r.FixupOk, HasAttrList = r.AttributeListAttr != null,
        };
        var d = r.UnnamedData().OrderBy(a => a.StartVcn).FirstOrDefault();
        if (d != null)
        {
            o.HasData = true;
            o.Resident = !d.NonResident;
            o.DataInstance = d.Instance;
            if (o.Resident) o.ResidentLength = d.ValueLength;
            else
            {
                o.StartVcn = d.StartVcn; o.RealSize = d.RealSize; o.InitSize = d.InitializedSize;
                o.Compressed = d.IsCompressed; o.CompressionLog2 = d.CompressionUnitLog2; o.Encrypted = d.IsEncrypted;
                o.Runs = d.Runs.Select(x => new[] { x.Lcn, x.Clusters }).ToList();
            }
        }
        return o;
    }

    public string ToJson() => JsonSerializer.Serialize(this);
    public static OrphanRecord FromJson(string s) => JsonSerializer.Deserialize<OrphanRecord>(s)!;
}

public interface IOrphanRecordStore
{
    void Add(OrphanRecord r);
    IEnumerable<OrphanRecord> All();
    long Count { get; }
}

public sealed class MemoryOrphanStore : IOrphanRecordStore
{
    private readonly List<OrphanRecord> _list = new();
    public void Add(OrphanRecord r) => _list.Add(r);
    public IEnumerable<OrphanRecord> All() => _list;
    public long Count => _list.Count;
}

/// <summary>Sector observer that picks up NTFS FILE records lying outside every current MFT.</summary>
public sealed class OrphanMftCollector : ISectorObserver
{
    private readonly IOrphanRecordStore _store;
    private readonly List<Extent> _exclude;
    private readonly ResilientReader _reader;
    public long Found { get; private set; }

    public OrphanMftCollector(ResilientReader reader, IOrphanRecordStore store, IEnumerable<Extent> currentMftExtents)
    {
        _reader = reader;
        _store = store;
        _exclude = currentMftExtents.ToList();
    }

    public void OnSector(long abs, ReadOnlySpan<byte> data)
    {
        if (!MftRecord.HasSignature(data) || data.Length < 8) return;
        int usaCount = Bytes.U16(data, 6);
        int size = (usaCount - 1) * 512;
        if (size is not (1024 or 2048 or 4096)) return;
        foreach (var e in _exclude) if (abs >= e.Offset && abs < e.Offset + e.Length) return;
        MftRecord? rec;
        if (data.Length >= size) rec = MftRecord.Parse(data, size);
        else
        {
            var b = new byte[size];
            _reader.Read(abs, b);
            rec = MftRecord.Parse(b, size);
        }
        if (rec == null || (rec.FileNames.Count == 0 && !rec.IsExtension)) return;
        _store.Add(OrphanRecord.From(rec, abs, size));
        Found++;
    }
}

/// <summary>Known geometry of a volume that orphan records may belong to.</summary>
public sealed record VolumeGeometry(long Offset, long Length, int ClusterSize, string Label, Func<long, bool>? IsClusterAllocated);

/// <summary>
/// Engine D/E: reconstructs files from MFT records of a previous or damaged NTFS file system found
/// anywhere on the disk (e.g. after a quick format / Windows reinstall). The old volume's geometry
/// is inferred from a surviving $MFT record (validated against $MFTMirr) or taken from the volume
/// that contains the records; every reconstructed file is then format-validated.
/// </summary>
public sealed class OrphanMftEngine
{
    private readonly ResilientReader _reader;
    private readonly IBlockSource _disk;
    private readonly IScanSink _sink;
    private readonly IOrphanRecordStore _store;
    private readonly IReadOnlyList<VolumeGeometry> _volumes;
    private readonly long _validateUpTo;

    public long FilesEmitted { get; private set; }
    public List<string> Findings { get; } = new();

    public OrphanMftEngine(IBlockSource disk, ResilientReader reader, IScanSink sink, IOrphanRecordStore store, IReadOnlyList<VolumeGeometry> volumes, long validateUpTo = 64L * 1024 * 1024)
    {
        _disk = disk; _reader = reader; _sink = sink; _store = store; _volumes = volumes; _validateUpTo = validateUpTo;
    }

    private sealed class Group
    {
        public long VolStart = -1;
        public int ClusterSize;
        public string Label = "";
        public Func<long, bool>? Allocated;
        public string Basis = "";
        public readonly Dictionary<long, (long parent, ushort pseq, string name, ushort seq, bool inUse)> Dirs = new();
        public readonly Dictionary<long, List<OrphanRecord>> Extensions = new();
    }

    public void Run(IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        if (_store.Count == 0) return;
        var hyps = InferFromMftRecords();
        var groups = new Dictionary<string, Group>();
        var prog = new EngineProgress { Phase = "Previous-file-system MFT records", Unit = "records", Total = _store.Count * 2 };

        Group GroupFor(OrphanRecord r)
        {
            foreach (var h in hyps)
                if (r.Phys >= h.VolStart && r.Phys < h.VolStart + h.Length)
                    return Get($"mft@{h.VolStart}", h.VolStart, h.ClusterSize, h.Label, null, h.Basis);
            foreach (var v in _volumes)
                if (r.Phys >= v.Offset && r.Phys < v.Offset + v.Length)
                    return Get($"vol@{v.Offset}", v.Offset, v.ClusterSize, v.Label, v.IsClusterAllocated, "geometry of the volume containing the records");
            // No geometry: group by implied MFT start so paths can still be resolved.
            long key = r.RecordNumber >= 0 ? r.Phys - r.RecordNumber * r.RecordSize : r.Phys;
            return Get($"nogeo@{key}", -1, 0, "unknown volume", null, "volume geometry unknown");
        }

        Group Get(string k, long vs, int cs, string label, Func<long, bool>? alloc, string basis)
        {
            if (!groups.TryGetValue(k, out var g))
                groups[k] = g = new Group { VolStart = vs, ClusterSize = cs, Label = label, Allocated = alloc, Basis = basis };
            return g;
        }

        // Pass 1: directories and extension records.
        foreach (var r in _store.All())
        {
            ct.ThrowIfCancellationRequested();
            prog.Done++;
            if (prog.Done % 4096 == 0) progress?.Report(prog);
            var g = GroupFor(r);
            if (r.BaseRecord != 0)
            {
                if (!g.Extensions.TryGetValue(r.BaseRecord, out var l)) g.Extensions[r.BaseRecord] = l = new();
                l.Add(r);
                continue;
            }
            if (r.IsDir && r.Name != null && r.RecordNumber >= 0)
            {
                if (!g.Dirs.TryGetValue(r.RecordNumber, out var existing) || (!existing.inUse && r.InUse))
                    g.Dirs[r.RecordNumber] = (r.ParentRecord, r.ParentSeq, r.Name, r.Seq, r.InUse);
            }
        }
        // Pass 2: files.
        foreach (var r in _store.All())
        {
            ct.ThrowIfCancellationRequested();
            prog.Done++;
            if (prog.Done % 1024 == 0) { prog.FilesFound = FilesEmitted; progress?.Report(prog); }
            if (r.IsDir || r.BaseRecord != 0 || r.Name == null) continue;
            if (r.RecordNumber is >= 0 and < 24) continue;
            if (r.Name.StartsWith('$') && (r.ParentRecord == 5 || r.ParentRecord == 11)) continue;
            Emit(r, GroupFor(r));
        }
        foreach (var g in groups.Values.Where(g => g.VolStart >= 0))
            Findings.Add($"Previous NTFS volume at offset {g.VolStart} (cluster {g.ClusterSize} B): {g.Basis}");
        _sink.Commit();
    }

    private sealed record Hypothesis(long VolStart, long Length, int ClusterSize, string Label, string Basis);

    private List<Hypothesis> InferFromMftRecords()
    {
        var res = new List<Hypothesis>();
        foreach (var r in _store.All().Where(x => x.RecordNumber == 0 && x.Name == "$MFT" && !x.Resident && x.Runs.Count > 0))
        {
            long l0 = r.Runs[0][0];
            var mirror = _store.All().FirstOrDefault(x => x.RecordNumber == 1 && x.Name == "$MFTMirr" && Math.Abs(x.Phys - r.Phys) <= 4 * r.RecordSize && x.Runs.Count > 0);
            for (int cs = 512; cs <= 65536; cs <<= 1)
            {
                long vs = r.Phys - l0 * cs;
                if (vs < 0 || vs % 512 != 0) continue;
                bool ok = false;
                if (mirror != null)
                {
                    long mirrPhys = vs + mirror.Runs[0][0] * cs;
                    if (mirrPhys + 4 <= _disk.Length && mirrPhys != r.Phys)
                    {
                        var b = new byte[4];
                        _reader.Read(mirrPhys, b);
                        ok = b.AsSpan().SequenceEqual("FILE"u8);
                    }
                }
                var boot = Partitions.FileSystemProbe.Probe(_disk, vs);
                if (boot is { Kind: Partitions.FsKind.Ntfs } && boot.ClusterSize == cs) ok = true;
                if (!ok) continue;
                long len = boot?.TotalBytes ?? _disk.Length - vs;
                if (res.Any(h => h.VolStart == vs)) continue;
                res.Add(new Hypothesis(vs, len, cs, $"previous NTFS @{vs}", "inferred from a surviving $MFT record (validated by $MFTMirr / boot sector)"));
                break;
            }
        }
        return res;
    }

    private string ResolvePath(Group g, long parent, ushort pseq)
    {
        var parts = new List<string>();
        long cur = parent;
        ushort seq = pseq;
        var seen = new HashSet<long>();
        bool complete = false;
        while (seen.Add(cur) && parts.Count < 256)
        {
            if (cur == 5) { complete = true; break; }
            if (!g.Dirs.TryGetValue(cur, out var d)) break;
            if (seq != 0 && d.seq != seq && d.seq != (ushort)(seq + 1)) break;
            parts.Add(d.name);
            cur = d.parent;
            seq = d.pseq;
        }
        parts.Reverse();
        var tail = string.Join("\\", parts);
        if (complete) return "\\" + tail;
        // Unknown ancestors: group by the original parent folder's record number so files that shared a folder stay together.
        return "\\$PreviousFileSystem\\Folder #" + (parts.Count > 0 ? cur : parent) + (tail.Length > 0 ? "\\" + tail : "");
    }

    private void Emit(OrphanRecord r, Group g)
    {
        var notes = new List<string> { "Found in an MFT record outside the current file system (previous format / damaged MFT)" };
        var f = new FoundFile
        {
            Name = r.Name!, Extension = FileTypes.ExtensionOf(r.Name!),
            Created = Bytes.FromFileTime(r.Created), Modified = Bytes.FromFileTime(r.Modified), Accessed = Bytes.FromFileTime(r.Accessed),
            Engines = EngineId.OrphanMftRecords, IsDeleted = r.InUse ? null : true,
            State = RecoveryState.MetadataRecovered, VolumeLabel = g.Label, VolumeOffset = g.VolStart,
            SourceDetail = $"Orphan MFT record #{r.RecordNumber} at byte offset {r.Phys}",
            Path = ResolvePath(g, r.ParentRecord, r.ParentSeq),
        };
        f.Category = FileTypes.CategoryOf(f.Extension);
        if (!r.FixupOk) notes.Add("record failed its update-sequence check");

        var runs = r.Runs.Select(x => new DataRun(x[0], x[1])).ToList();
        long realSize = r.RealSize;
        if (r.HasAttrList && (!r.HasData || r.StartVcn != 0 || runs.Count == 0) && r.RecordNumber >= 0 && g.Extensions.TryGetValue(r.RecordNumber, out var exts))
        {
            var parts = exts.Where(e => e.HasData && !e.Resident).OrderBy(e => e.StartVcn).ToList();
            if (parts.Count > 0)
            {
                runs = parts.SelectMany(p => p.Runs.Select(x => new DataRun(x[0], x[1]))).ToList();
                if (parts[0].StartVcn == 0) { realSize = parts[0].RealSize; r.InitSize = parts[0].InitSize; }
                r.HasData = true; r.Resident = false;
                notes.Add("data runs reassembled from extension records");
            }
        }

        if (!r.HasData)
        {
            f.Size = r.FnSize;
            f.Layout = new DataLayout { Length = r.FnSize };
            f.Confidence = Confidence.Low;
            f.State = r.FnSize == 0 ? RecoveryState.MetadataRecovered : RecoveryState.Failed;
            notes.Add("content location not recorded in this record");
        }
        else if (r.Resident)
        {
            f.Size = r.ResidentLength;
            f.Layout = new DataLayout { Kind = LayoutKind.NtfsResident, Length = r.ResidentLength, RecordOffset = r.Phys, RecordSize = r.RecordSize, AttributeInstance = r.DataInstance };
            f.Confidence = r.FixupOk ? Confidence.High : Confidence.Low;
            notes.Add("content stored inside the record (resident)");
        }
        else if (g.VolStart < 0)
        {
            f.Size = realSize > 0 ? realSize : r.FnSize;
            f.Layout = new DataLayout { Length = f.Size };
            f.Confidence = Confidence.Low;
            f.State = RecoveryState.Failed;
            notes.Add("the old volume's position could not be determined, so the data runs cannot be located");
        }
        else
        {
            f.Size = realSize;
            int cs = g.ClusterSize;
            var extents = new List<Extent>();
            bool outside = false;
            foreach (var run in runs)
            {
                if (run.IsSparse) { extents.Add(new Extent(-1, run.Clusters * cs)); continue; }
                long off = g.VolStart + run.Lcn * cs;
                if (off + run.Clusters * cs > _disk.Length) outside = true;
                extents.Add(new Extent(off, run.Clusters * cs));
            }
            bool compressed = r.Compressed && r.CompressionLog2 > 0;
            var layout = new DataLayout
            {
                Kind = compressed ? LayoutKind.NtfsCompressed : LayoutKind.Extents, Length = realSize,
                InitializedLength = r.InitSize < realSize ? r.InitSize : -1, ClusterSize = cs,
                CompressionUnitClusters = compressed ? 1 << r.CompressionLog2 : 0,
            };
            if (compressed) layout.Extents = extents;
            else
            {
                long remain = realSize;
                foreach (var e in extents) { if (remain <= 0) break; long n = Math.Min(e.Length, remain); layout.Extents.Add(new Extent(e.Offset, n)); remain -= n; }
            }
            f.Layout = layout;
            if (layout.FragmentCount > 1) notes.Add($"{layout.FragmentCount} fragments (from the record's run list)");
            if (r.Encrypted) { f.Structure = StructureCheck.Unsupported; notes.Add("EFS-encrypted"); }
            if (outside) { f.State = RecoveryState.Failed; f.Confidence = Confidence.Low; notes.Add("data runs point beyond the end of the disk (geometry mismatch)"); }
            else
            {
                if (g.Allocated != null)
                {
                    long tot = 0, alloc = 0;
                    long need = (realSize + cs - 1) / cs;
                    foreach (var run in runs.Where(x => !x.IsSparse))
                        for (long c = 0; c < run.Clusters && tot < need; c++, tot++)
                            if (g.Allocated(run.Lcn + c)) alloc++;
                    f.OverwrittenFraction = tot == 0 ? 0 : (double)alloc / tot;
                }
                if (realSize <= _validateUpTo && f.Structure != StructureCheck.Unsupported)
                {
                    using var cstream = new ContentStream(_reader, layout);
                    var v = FormatValidator.Validate(new StreamCarveSource(cstream), f.Extension);
                    f.Structure = v.Check;
                    if (v.Note != null && v.Check != StructureCheck.Verified) notes.Add(v.Note);
                }
                f.Confidence = f.Structure switch
                {
                    StructureCheck.Verified => Confidence.High,
                    StructureCheck.Damaged => Confidence.Low,
                    _ => f.OverwrittenFraction > 0 ? Confidence.Low : Confidence.Medium,
                };
                if (f.Structure == StructureCheck.Damaged || f.OverwrittenFraction > 0)
                {
                    f.State = RecoveryState.PartialOrDamaged;
                    if (f.OverwrittenFraction > 0) notes.Add($"{f.OverwrittenFraction:P0} of its clusters are now used by the current file system");
                }
                notes.Add($"volume geometry: {g.Basis}");
            }
        }
        f.Notes = string.Join("; ", notes);
        _sink.AddFile(f);
        FilesEmitted++;
    }
}
