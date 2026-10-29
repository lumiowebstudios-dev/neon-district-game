using System.Text;
using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;
using DeepVault.Core.Partitions;
using DeepVault.Core.Util;

namespace DeepVault.Core.FileSystems.ExFat;

public sealed class ExFatVolumeResult
{
    public long VolumeOffset { get; init; }
    public int ClusterSize { get; init; }
    public long HeapOffset { get; init; }
    public long ClusterCount { get; init; }
    public byte[]? Bitmap { get; set; }
    public long DeletedFiles { get; set; }
    public long ExistingFiles { get; set; }
    public bool Completed { get; set; }
    public bool IsAllocated(long cluster)
    {
        long i = cluster - 2;
        return Bitmap != null && i >= 0 && (i >> 3) < Bitmap.Length && (Bitmap[i >> 3] & (1 << (int)(i & 7))) != 0;
    }
    public long ClusterOffset(long c) => HeapOffset + (c - 2) * ClusterSize;
}

/// <summary>
/// Engine A for exFAT: walks directory entry sets, including deleted ones (in-use bit cleared).
/// Files marked "NoFatChain" are contiguous and recoverable exactly; others follow the FAT chain
/// when it still looks intact, otherwise contiguity is assumed and the result validated.
/// </summary>
public sealed class ExFatScanner
{
    private readonly ResilientReader _reader;
    private readonly IScanSink _sink;
    private readonly long _vol;
    private readonly BootSectorInfo _boot;
    private readonly bool _includeExisting;
    private readonly string _label;
    private ExFatVolumeResult _r = null!;
    private long _fatOff;
    private readonly HashSet<long> _visited = new();

    public ExFatScanner(ResilientReader reader, IScanSink sink, long volumeOffset, BootSectorInfo boot, string label, bool includeExisting = false)
    {
        _reader = reader; _sink = sink; _vol = volumeOffset; _boot = boot; _label = label; _includeExisting = includeExisting;
    }

    public ExFatVolumeResult Run(IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var b = _boot.Raw;
        int bps = 1 << b[0x6C];
        int cs = bps << b[0x6D];
        _fatOff = _vol + (long)Bytes.U32(b, 0x50) * bps;
        long heap = _vol + (long)Bytes.U32(b, 0x58) * bps;
        long count = Bytes.U32(b, 0x5C);
        long root = Bytes.U32(b, 0x60);
        _r = new ExFatVolumeResult { VolumeOffset = _vol, ClusterSize = cs, HeapOffset = heap, ClusterCount = count };
        var prog = new EngineProgress { Phase = $"exFAT directory analysis ({_label})", Unit = "directories" };
        WalkDir(FollowChain(root, 256L * 1024 * 1024), "\\", false, 0, prog, progress, ct);
        _sink.Commit();
        _r.Completed = true;
        return _r;
    }

    private uint FatEntry(long c)
    {
        var b = new byte[4];
        _reader.Read(_fatOff + c * 4, b);
        return Bytes.U32(b, 0);
    }

    private List<Extent> FollowChain(long first, long maxBytes)
    {
        var res = new List<Extent>();
        long c = first;
        var seen = new HashSet<long>();
        long total = 0;
        while (c >= 2 && c < _r.ClusterCount + 2 && seen.Add(c) && total < maxBytes)
        {
            Add(res, _r.ClusterOffset(c), _r.ClusterSize);
            total += _r.ClusterSize;
            uint n = FatEntry(c);
            if (n >= 0xFFFFFFF7 || n < 2) break;
            c = n;
        }
        return res;
    }

    private static void Add(List<Extent> l, long off, long len)
    {
        if (l.Count > 0 && l[^1].Offset + l[^1].Length == off) l[^1] = new Extent(l[^1].Offset, l[^1].Length + len);
        else l.Add(new Extent(off, len));
    }

    private void WalkDir(List<Extent> extents, string path, bool parentDeleted, int depth, EngineProgress prog, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        if (depth > 64) return;
        ct.ThrowIfCancellationRequested();
        prog.Done++;
        prog.FilesFound = _r.DeletedFiles + _r.ExistingFiles;
        progress?.Report(prog);
        var all = new MemoryStream();
        foreach (var e in extents)
        {
            if (!_visited.Add(e.Offset)) return;
            var d = new byte[e.Length];
            _reader.Read(e.Offset, d);
            all.Write(d);
        }
        var data = all.ToArray();
        var subdirs = new List<(List<Extent>, string, bool)>();
        for (int i = 0; i + 32 <= data.Length; i += 32)
        {
            byte type = data[i];
            if (type == 0x00) break;
            if (type == 0x81 && depth == 0 && _r.Bitmap == null)
            {
                long bc = Bytes.U32(data, i + 20);
                long blen = (long)Bytes.U64(data, i + 24);
                if (blen > 0 && blen < 512L * 1024 * 1024)
                {
                    var bm = new byte[blen];
                    long p = 0;
                    foreach (var x in FollowChain(bc, blen)) { int n = (int)Math.Min(x.Length, blen - p); _reader.Read(x.Offset, bm.AsSpan((int)p, n)); p += n; }
                    _r.Bitmap = bm;
                }
                continue;
            }
            if ((type & 0x7F) != 0x05) continue; // not a File entry (in-use 0x85 or deleted 0x05)
            bool deleted = (type & 0x80) == 0 || parentDeleted;
            int secondary = data[i + 1];
            if (secondary < 2 || secondary > 18 || i + 32 * (secondary + 1) > data.Length) continue;
            int s = i + 32;
            if ((data[s] & 0x7F) != 0x40) continue; // stream extension (0xC0 / 0x40)
            ushort attrs = Bytes.U16(data, i + 4);
            int flags = data[s + 1];
            int nameLen = data[s + 3];
            long validLen = (long)Bytes.U64(data, s + 8);
            long firstCluster = Bytes.U32(data, s + 20);
            long dataLen = (long)Bytes.U64(data, s + 24);
            var sb = new StringBuilder();
            for (int k = 2; k <= secondary && sb.Length < nameLen; k++)
            {
                int fe = i + 32 * k;
                if ((data[fe] & 0x7F) != 0x41) break;
                for (int c = 0; c < 15 && sb.Length < nameLen; c++) sb.Append((char)Bytes.U16(data, fe + 2 + c * 2));
            }
            string name = sb.ToString();
            i += 32 * secondary;
            if (name.Length == 0) continue;
            bool noFatChain = (flags & 2) != 0;
            if ((attrs & 0x10) != 0)
            {
                if (firstCluster < 2) continue;
                var ext = noFatChain ? new List<Extent> { new(_r.ClusterOffset(firstCluster), Math.Max(dataLen, _r.ClusterSize)) } : FollowChain(firstCluster, Math.Max(dataLen, _r.ClusterSize));
                subdirs.Add((ext, path.TrimEnd('\\') + "\\" + name, deleted));
                continue;
            }
            if (!deleted && !_includeExisting) continue;
            Emit(name, path, data.AsSpan(i - 32 * secondary, 32), deleted, noFatChain, firstCluster, dataLen, validLen);
        }
        foreach (var (e, p, d) in subdirs) WalkDir(e, p, d, depth + 1, prog, progress, ct);
    }

    private static DateTime? ExTime(uint ts, byte ms10, byte utcOff)
    {
        if (ts == 0) return null;
        int date = (int)(ts >> 16), time = (int)(ts & 0xFFFF);
        try
        {
            var dt = new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Unspecified).AddMilliseconds(ms10 * 10);
            if ((utcOff & 0x80) != 0)
            {
                int q = (sbyte)(utcOff << 1) >> 1; // 7-bit signed, 15-minute units
                return DateTime.SpecifyKind(dt.AddMinutes(-15 * q), DateTimeKind.Utc);
            }
            return DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private void Emit(string name, string path, ReadOnlySpan<byte> fileEntry, bool deleted, bool noFatChain, long cluster, long size, long validLen)
    {
        var f = new FoundFile
        {
            Name = name, Path = path, Extension = FileTypes.ExtensionOf(name), Size = size,
            Created = ExTime(Bytes.U32(fileEntry, 8), fileEntry[20], fileEntry[22]),
            Modified = ExTime(Bytes.U32(fileEntry, 12), fileEntry[21], fileEntry[23]),
            Accessed = ExTime(Bytes.U32(fileEntry, 16), 0, fileEntry[24]),
            Engines = EngineId.ExFatDirectory, IsDeleted = deleted, State = RecoveryState.MetadataRecovered,
            VolumeLabel = _label, VolumeOffset = _vol,
            SourceDetail = $"exFAT directory entry set, first cluster {cluster}" + (deleted ? " (deleted)" : " (exists)"),
        };
        f.Category = FileTypes.CategoryOf(f.Extension);
        var notes = new List<string>();
        long nClusters = (size + _r.ClusterSize - 1) / _r.ClusterSize;
        if (size == 0) { f.Layout = new DataLayout(); f.Confidence = Confidence.High; }
        else if (cluster < 2 || cluster + (noFatChain ? nClusters : 1) > _r.ClusterCount + 2)
        {
            f.Layout = new DataLayout { Length = size };
            f.State = RecoveryState.Failed;
            f.Confidence = Confidence.Low;
            notes.Add("Cluster reference invalid");
        }
        else
        {
            List<Extent> ext;
            if (noFatChain)
            {
                ext = new List<Extent> { new(_r.ClusterOffset(cluster), nClusters * _r.ClusterSize) };
                notes.Add("Stored contiguously (NoFatChain): layout is exact");
            }
            else
            {
                ext = FollowChain(cluster, nClusters * _r.ClusterSize);
                if (ext.Sum(e => e.Length) < size)
                {
                    ext = new List<Extent> { new(_r.ClusterOffset(cluster), nClusters * _r.ClusterSize) };
                    notes.Add("FAT chain not intact: content assumed contiguous");
                }
                else if (deleted) notes.Add("Reassembled from the surviving FAT chain");
            }
            var l = new DataLayout { Length = size, InitializedLength = validLen < size ? validLen : -1 };
            long remain = size;
            foreach (var e in ext) { if (remain <= 0) break; long n = Math.Min(e.Length, remain); l.Extents.Add(new Extent(e.Offset, n)); remain -= n; }
            f.Layout = l;
            if (deleted)
            {
                long alloc = 0, tot = 0;
                foreach (var e in l.Extents)
                    for (long o = 0; o < e.Length; o += _r.ClusterSize, tot++)
                        if (_r.IsAllocated(2 + (e.Offset + o - _r.HeapOffset) / _r.ClusterSize)) alloc++;
                f.OverwrittenFraction = tot == 0 ? 0 : (double)alloc / tot;
                if (size <= 64L * 1024 * 1024)
                {
                    using var cs = new ContentStream(_reader, l);
                    var v = FormatValidator.Validate(new StreamCarveSource(cs), f.Extension);
                    f.Structure = v.Check;
                    if (v.Note != null && v.Check != StructureCheck.Verified) notes.Add(v.Note);
                }
                f.Confidence = f.Structure == StructureCheck.Damaged ? Confidence.Low
                    : f.OverwrittenFraction > 0 ? Confidence.Low
                    : noFatChain || f.Structure == StructureCheck.Verified ? Confidence.High : Confidence.Medium;
                if (f.Structure == StructureCheck.Damaged || f.OverwrittenFraction > 0)
                {
                    f.State = RecoveryState.PartialOrDamaged;
                    if (f.OverwrittenFraction > 0) notes.Add($"{f.OverwrittenFraction:P0} of clusters are now in use by other files");
                }
            }
            else f.Confidence = Confidence.High;
        }
        f.Notes = string.Join("; ", notes);
        if (deleted) _r.DeletedFiles++; else _r.ExistingFiles++;
        _sink.AddFile(f);
    }
}
