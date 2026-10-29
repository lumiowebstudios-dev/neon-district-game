using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;
using DeepVault.Core.Partitions;
using DeepVault.Core.Util;

namespace DeepVault.Core.FileSystems.Ntfs;

public sealed class NtfsScanOptions
{
    /// <summary>Also catalogue files that still exist (allocated) in the file system.</summary>
    public bool IncludeExistingFiles { get; init; }
    /// <summary>Upper bound on the $Bitmap kept in memory for overwrite analysis.</summary>
    public long MaxBitmapBytes { get; init; } = 512L * 1024 * 1024;
}

public sealed class NtfsVolumeResult
{
    public long VolumeOffset { get; init; }
    public int ClusterSize { get; init; }
    public long TotalClusters { get; init; }
    public int RecordSize { get; set; }
    /// <summary>Cluster allocation bitmap ($Bitmap), or null if unavailable.</summary>
    public byte[]? Bitmap { get; set; }
    public List<Extent> MftExtents { get; } = new();
    public long RecordsTotal { get; set; }
    public long RecordsRead { get; set; }
    public long RecordsUnreadable { get; set; }
    public long DeletedFiles { get; set; }
    public long ExistingFiles { get; set; }
    public long DeletedDirectories { get; set; }
    public bool Completed { get; set; }

    public bool IsClusterAllocated(long lcn)
    {
        if (Bitmap == null || lcn < 0) return false;
        long i = lcn >> 3;
        return i < Bitmap.Length && (Bitmap[i] & (1 << (int)(lcn & 7))) != 0;
    }
}

/// <summary>
/// Engine A/D for NTFS: walks every MFT record (read-only), recovering deleted (and optionally
/// existing) files with original names, directory paths, timestamps, sizes and data runs, and
/// estimates how much of each deleted file has since been reallocated to other data.
/// </summary>
public sealed class NtfsScanner
{
    private readonly IBlockSource _disk;
    private readonly ResilientReader _reader;
    private readonly IScanSink _sink;
    private readonly NtfsScanOptions _opt;
    private readonly long _vol;
    private readonly BootSectorInfo _boot;
    private readonly int _scanUnit;
    private readonly string _volLabel;

    private int _recordSize;
    private int _cs;

    private sealed record DirInfo(long ParentRecord, ushort ParentSeq, string Name, ushort Seq, bool InUse);
    private readonly Dictionary<long, DirInfo> _dirs = new();
    private readonly Dictionary<long, List<(MftRecord rec, long offset)>> _extensions = new();
    private readonly List<(MftRecord rec, long offset)> _pendingAttrList = new();

    public NtfsScanner(IBlockSource disk, ResilientReader reader, IScanSink sink, long volumeOffset, BootSectorInfo boot,
        int scanUnit, string volumeLabel, NtfsScanOptions? options = null)
    {
        _disk = disk;
        _reader = reader;
        _sink = sink;
        _vol = volumeOffset;
        _boot = boot;
        _scanUnit = scanUnit;
        _volLabel = volumeLabel;
        _opt = options ?? new NtfsScanOptions();
    }

    public static int RecordSizeFromBoot(ReadOnlySpan<byte> boot, int clusterSize)
    {
        sbyte v = (sbyte)boot[0x40];
        return v > 0 ? v * clusterSize : 1 << -v;
    }

    public NtfsVolumeResult Run(IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        _cs = _boot.ClusterSize;
        var bootRaw = _boot.Raw;
        _recordSize = RecordSizeFromBoot(bootRaw, _cs);
        long mftLcn = Bytes.I64(bootRaw, 0x30);
        long mirrLcn = Bytes.I64(bootRaw, 0x38);
        var result = new NtfsVolumeResult { VolumeOffset = _vol, ClusterSize = _cs, TotalClusters = _boot.TotalBytes / _cs, RecordSize = _recordSize };
        if (_recordSize < 512 || _recordSize > 65536)
        {
            _sink.AddIssue(new ScanIssue(IssueSeverity.Error, EngineId.NtfsMft, $"Implausible MFT record size {_recordSize}", _vol));
            return result;
        }

        var mft = ReadRecordAt(_vol + mftLcn * _cs);
        string mftSource = "$MFT";
        if (mft == null || !mft.UnnamedData().Any())
        {
            _sink.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.NtfsMft, "$MFT record 0 unreadable or damaged; trying $MFTMirr.", _vol + mftLcn * _cs));
            mft = ReadRecordAt(_vol + mirrLcn * _cs);
            mftSource = "$MFTMirr";
        }
        if (mft == null || !mft.UnnamedData().Any())
        {
            _sink.AddIssue(new ScanIssue(IssueSeverity.Error, EngineId.NtfsMft, "Could not locate the MFT (record 0 and mirror unusable). Use the orphan-record and carving engines.", _vol));
            return result;
        }

        var mftRuns = CollectRuns(mft, _vol + mftLcn * _cs, null);
        long mftSize = mft.UnnamedData().FirstOrDefault(a => a.StartVcn == 0)?.RealSize ?? 0;
        var mftExtents = RunsToExtents(mftRuns, mftSize);
        result.MftExtents.AddRange(mftExtents);
        result.RecordsTotal = mftSize / _recordSize;
        if (mftSource != "$MFT")
            _sink.AddIssue(new ScanIssue(IssueSeverity.Info, EngineId.NtfsMft, "MFT located via $MFTMirr.", _vol));

        result.Bitmap = LoadBitmap(mftExtents, result);

        // Walk every record.
        var prog = new EngineProgress { Phase = $"NTFS metadata ({_volLabel})", Unit = "MFT records", Total = result.RecordsTotal };
        long recNo = 0;
        int chunkBytes = Math.Max(_recordSize, (1024 * 1024 / _recordSize) * _recordSize);
        var buf = new byte[chunkBytes];
        foreach (var ext in mftExtents)
        {
            long pos = 0;
            while (pos < ext.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(chunkBytes, ext.Length - pos);
                n -= n % _recordSize;
                if (n <= 0) break;
                _reader.Read(ext.Offset + pos, buf.AsSpan(0, n), out bool errs);
                for (int o = 0; o < n; o += _recordSize, recNo++)
                {
                    long abs = ext.Offset + pos + o;
                    var span = buf.AsSpan(o, _recordSize);
                    if (!MftRecord.HasSignature(span))
                    {
                        if (errs && Bytes.IsAllZero(span)) result.RecordsUnreadable++;
                        continue;
                    }
                    var rec = MftRecord.Parse(span, _recordSize);
                    if (rec == null) continue;
                    result.RecordsRead++;
                    ProcessRecord(rec, recNo, abs, result);
                }
                pos += n;
                prog.Done = recNo;
                prog.FilesFound = result.DeletedFiles + result.ExistingFiles;
                progress?.Report(prog);
            }
        }

        // Files whose data runs live in extension records.
        foreach (var (rec, off) in _pendingAttrList)
        {
            ct.ThrowIfCancellationRequested();
            EmitFile(rec, rec.HasRecordNumber ? rec.RecordNumber : -1, off, result, true);
        }

        _sink.ResolvePaths(_scanUnit, ResolveParentKey);
        _sink.Commit();
        result.Completed = true;
        if (result.RecordsUnreadable > 0)
            _sink.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.NtfsMft, $"{result.RecordsUnreadable} MFT records could not be read.", _vol));
        return result;
    }

    private void ProcessRecord(MftRecord rec, long recNo, long abs, NtfsVolumeResult result)
    {
        if (rec.HasRecordNumber) recNo = rec.RecordNumber;
        if (rec.IsExtension)
        {
            if (!_extensions.TryGetValue(rec.BaseRecordNumber, out var list))
                _extensions[rec.BaseRecordNumber] = list = new();
            list.Add((rec, abs));
            return;
        }
        var fn = rec.BestFileName();
        if (rec.IsDirectory)
        {
            if (fn != null)
                _dirs[recNo] = new DirInfo(fn.ParentRecord, fn.ParentSeq, fn.Name, rec.Sequence, rec.InUse);
            else if (recNo == 5)
                _dirs[5] = new DirInfo(5, 5, "", rec.Sequence, true);
            if (!rec.InUse) result.DeletedDirectories++;
            return;
        }
        if (fn == null) return;
        if (recNo < 24) return; // NTFS metafiles
        if (fn.Name.StartsWith('$') && (fn.ParentRecord == 5 || fn.ParentRecord == 11)) return;
        if (rec.InUse && !_opt.IncludeExistingFiles) return;

        if (rec.AttributeListAttr != null && !rec.UnnamedData().Any(a => a.StartVcn == 0 || !a.NonResident))
        {
            _pendingAttrList.Add((rec, abs));
            return;
        }
        if (rec.AttributeListAttr != null && rec.UnnamedData().Any(a => a.NonResident))
        {
            // Runs may continue in extension records that we have not seen yet.
            _pendingAttrList.Add((rec, abs));
            return;
        }
        EmitFile(rec, recNo, abs, result, false);
    }

    private void EmitFile(MftRecord rec, long recNo, long abs, NtfsVolumeResult result, bool useExtensions)
    {
        var fn = rec.BestFileName()!;
        var dataAttrs = rec.UnnamedData().ToList();
        if (useExtensions && _extensions.TryGetValue(recNo, out var exts))
        {
            foreach (var (er, _) in exts)
            {
                ushort baseSeq = (ushort)(er.BaseRef >> 48);
                if (baseSeq != rec.Sequence && baseSeq != (ushort)(rec.Sequence - 1)) continue;
                dataAttrs.AddRange(er.UnnamedData());
            }
        }

        bool deleted = !rec.InUse;
        var f = new FoundFile
        {
            Name = fn.Name,
            Extension = FileTypes.ExtensionOf(fn.Name),
            Created = Bytes.FromFileTime(rec.SiCreated != 0 ? rec.SiCreated : fn.Created),
            Modified = Bytes.FromFileTime(rec.SiModified != 0 ? rec.SiModified : fn.Modified),
            Accessed = Bytes.FromFileTime(rec.SiAccessed != 0 ? rec.SiAccessed : fn.Accessed),
            Engines = EngineId.NtfsMft,
            IsDeleted = deleted,
            State = RecoveryState.MetadataRecovered,
            ParentKey = (long)fn.ParentRef & long.MaxValue,
            ScanUnit = _scanUnit,
            VolumeLabel = _volLabel,
            VolumeOffset = _vol,
            SourceDetail = $"NTFS MFT record #{recNo} @ {abs}" + (deleted ? " (deleted)" : " (exists)"),
        };
        f.Category = FileTypes.CategoryOf(f.Extension);
        var notes = new List<string>();
        if (!rec.FixupOk) notes.Add("MFT record failed its update-sequence check (torn or partially overwritten record)");

        var data0 = dataAttrs.FirstOrDefault(a => !a.NonResident) ?? dataAttrs.Where(a => a.NonResident).OrderBy(a => a.StartVcn).FirstOrDefault();
        if (data0 == null)
        {
            f.Size = fn.RealSize;
            f.Layout = new DataLayout { Kind = LayoutKind.Extents, Length = 0 };
            f.Confidence = Confidence.Low;
            f.Structure = StructureCheck.NotChecked;
            notes.Add("No $DATA attribute found (content location unknown)");
            if (fn.RealSize == 0) { f.Confidence = Confidence.High; notes.Clear(); notes.Add("Empty file"); }
            else f.State = RecoveryState.Failed;
        }
        else if (!data0.NonResident)
        {
            f.Size = data0.ValueLength;
            f.Layout = new DataLayout
            {
                Kind = LayoutKind.NtfsResident, Length = data0.ValueLength, RecordOffset = abs, RecordSize = _recordSize,
                AttributeInstance = data0.Instance,
            };
            f.Confidence = rec.FixupOk ? Confidence.High : Confidence.Low;
            f.OverwrittenFraction = 0;
            notes.Add("Content stored inside the MFT record (resident)");
        }
        else
        {
            var nonRes = dataAttrs.Where(a => a.NonResident).OrderBy(a => a.StartVcn).ToList();
            var first = nonRes.First();
            long realSize = first.StartVcn == 0 ? first.RealSize : fn.RealSize;
            long initSize = first.StartVcn == 0 ? first.InitializedSize : realSize;
            var runs = new List<DataRun>();
            long expectVcn = 0;
            bool runsOk = true;
            foreach (var a in nonRes)
            {
                if (a.StartVcn != expectVcn) { runsOk = false; notes.Add("Gap in data runs (missing extension record)"); break; }
                if (!a.RunlistValid) runsOk = false;
                runs.AddRange(a.Runs);
                expectVcn = a.StartVcn + a.Runs.Sum(r => r.Clusters);
            }
            f.Size = realSize;
            bool compressed = first.IsCompressed && first.CompressionUnitLog2 > 0;
            var extents = new List<Extent>();
            foreach (var r in runs)
            {
                if (r.IsSparse) extents.Add(new Extent(-1, r.Clusters * _cs));
                else extents.Add(new Extent(_vol + r.Lcn * _cs, r.Clusters * _cs));
            }
            bool beyond = runs.Any(r => !r.IsSparse && (r.Lcn + r.Clusters) * _cs > _boot.TotalBytes + _cs);
            if (beyond) { runsOk = false; notes.Add("Data runs point outside the volume"); }
            f.Layout = new DataLayout
            {
                Kind = compressed ? LayoutKind.NtfsCompressed : LayoutKind.Extents,
                Length = realSize,
                InitializedLength = initSize < realSize ? initSize : -1,
                Extents = compressed ? extents : TrimExtents(extents, realSize),
                ClusterSize = _cs,
                CompressionUnitClusters = compressed ? 1 << first.CompressionUnitLog2 : 0,
            };
            if (first.IsEncrypted)
            {
                f.Structure = StructureCheck.Unsupported;
                notes.Add("EFS-encrypted: restored bytes stay encrypted and need the original Windows user's key");
            }
            if (compressed) notes.Add("NTFS-compressed (LZNT1); decompressed on read");
            int frags = f.Layout.FragmentCount;
            if (frags > 1) notes.Add($"Fragmented into {frags} pieces (reassembled from the MFT run list)");

            // Overwrite estimate: how many of the file's clusters are now allocated to something else.
            if (deleted && result.Bitmap != null && runsOk)
            {
                long total = 0, alloc = 0;
                long neededClusters = (realSize + _cs - 1) / _cs;
                foreach (var r in runs)
                {
                    if (r.IsSparse) continue;
                    for (long c = 0; c < r.Clusters && total < neededClusters; c++, total++)
                        if (result.IsClusterAllocated(r.Lcn + c)) alloc++;
                }
                f.OverwrittenFraction = total == 0 ? 0 : (double)alloc / total;
            }
            if (!runsOk) { f.Confidence = Confidence.Low; f.State = RecoveryState.PartialOrDamaged; }
            else if (!deleted) f.Confidence = Confidence.High;
            else if (f.OverwrittenFraction is null) f.Confidence = Confidence.Medium;
            else if (f.OverwrittenFraction == 0) f.Confidence = rec.FixupOk ? Confidence.High : Confidence.Medium;
            else
            {
                f.Confidence = Confidence.Low;
                f.State = RecoveryState.PartialOrDamaged;
                notes.Add(f.OverwrittenFraction >= 0.999
                    ? "All clusters have been reused by other data: content is almost certainly overwritten"
                    : $"{f.OverwrittenFraction:P0} of clusters have been reused by other data: content is partially overwritten");
            }
        }
        if (deleted) result.DeletedFiles++; else result.ExistingFiles++;
        f.Notes = string.Join("; ", notes);
        _sink.AddFile(f);
    }

    private static List<Extent> TrimExtents(List<Extent> extents, long length)
    {
        var res = new List<Extent>();
        long remain = length;
        foreach (var e in extents)
        {
            if (remain <= 0) break;
            long n = Math.Min(e.Length, remain);
            res.Add(new Extent(e.Offset, n));
            remain -= n;
        }
        return res;
    }

    private string ResolveParentKey(long parentRef)
    {
        long rec = parentRef & 0xFFFFFFFFFFFF;
        ushort seq = (ushort)((ulong)parentRef >> 48);
        var parts = new List<string>();
        bool orphan = false;
        bool deletedAncestor = false;
        var seen = new HashSet<long>();
        long cur = rec;
        ushort curSeq = seq;
        while (cur != 5)
        {
            if (!seen.Add(cur) || parts.Count > 255) { orphan = true; break; }
            if (!_dirs.TryGetValue(cur, out var d)) { orphan = true; break; }
            bool seqOk = curSeq == 0 || d.Seq == curSeq || (!d.InUse && d.Seq == (ushort)(curSeq + 1));
            if (!seqOk) { orphan = true; break; }
            if (!d.InUse) deletedAncestor = true;
            parts.Add(d.Name);
            cur = d.ParentRecord;
            curSeq = d.ParentSeq;
        }
        parts.Reverse();
        string path = "\\" + string.Join("\\", parts);
        if (orphan) path = "\\$OrphanFiles" + (parts.Count > 0 ? "\\…\\" + string.Join("\\", parts) : "");
        _ = deletedAncestor;
        return path;
    }

    private MftRecord? ReadRecordAt(long abs)
    {
        if (abs < 0 || abs + _recordSize > _disk.Length) return null;
        var b = new byte[_recordSize];
        _reader.Read(abs, b);
        return MftRecord.Parse(b, _recordSize);
    }

    /// <summary>Collects all unnamed $DATA runs of a record, following its attribute list if present.</summary>
    private List<DataRun> CollectRuns(MftRecord rec, long recOffset, List<Extent>? mftExtentsForLookup)
    {
        var attrs = rec.UnnamedData().Where(a => a.NonResident).ToList();
        if (rec.AttributeListAttr != null)
        {
            if (rec.AttributeListNonResident)
            {
                var al = rec.AttributeListAttr;
                var bytes = ReadRuns(al.Runs, al.RealSize);
                rec.ParseAttributeList(bytes);
            }
            var known = RunsToExtents(attrs.OrderBy(a => a.StartVcn).SelectMany(a => a.Runs).ToList(), long.MaxValue);
            foreach (var e in rec.AttributeList.Where(e => e.Type == MftRecord.AttrData && e.Name.Length == 0 && e.RecordNumber != (rec.HasRecordNumber ? rec.RecordNumber : 0)))
            {
                long off = MapRecordOffset(known, e.RecordNumber);
                if (off < 0) continue;
                var ext = ReadRecordAt(off);
                if (ext != null) attrs.AddRange(ext.UnnamedData().Where(a => a.NonResident));
            }
        }
        return attrs.GroupBy(a => a.StartVcn).Select(g => g.First()).OrderBy(a => a.StartVcn).SelectMany(a => a.Runs).ToList();
    }

    private long MapRecordOffset(List<Extent> mftExtents, long recordNumber)
    {
        long byteOff = recordNumber * _recordSize;
        foreach (var e in mftExtents)
        {
            if (byteOff < e.Length) return e.IsSparse ? -1 : e.Offset + byteOff;
            byteOff -= e.Length;
        }
        return -1;
    }

    private List<Extent> RunsToExtents(List<DataRun> runs, long size)
    {
        var res = new List<Extent>();
        long remain = size;
        foreach (var r in runs)
        {
            if (remain <= 0) break;
            long len = Math.Min(r.Clusters * _cs, remain);
            res.Add(r.IsSparse ? new Extent(-1, len) : new Extent(_vol + r.Lcn * _cs, len));
            remain -= len;
        }
        return res;
    }

    private byte[] ReadRuns(List<DataRun> runs, long size)
    {
        var ext = RunsToExtents(runs, size);
        var res = new byte[size];
        long pos = 0;
        foreach (var e in ext)
        {
            if (!e.IsSparse) _reader.Read(e.Offset, res.AsSpan((int)pos, (int)e.Length));
            pos += e.Length;
        }
        return res;
    }

    private byte[]? LoadBitmap(List<Extent> mftExtents, NtfsVolumeResult result)
    {
        long off = MapRecordOffset(mftExtents, 6);
        var rec = off >= 0 ? ReadRecordAt(off) : null;
        var data = rec?.UnnamedData().FirstOrDefault();
        if (rec == null || data == null)
        {
            _sink.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.NtfsMft, "$Bitmap unreadable: overwrite analysis unavailable; unallocated-space detection disabled for this volume.", _vol));
            return null;
        }
        if (!data.NonResident) return rec.Raw.AsSpan(data.ValueOffsetInRecord, data.ValueLength).ToArray();
        if (data.RealSize > _opt.MaxBitmapBytes || data.RealSize <= 0)
        {
            _sink.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.NtfsMft, "$Bitmap too large to analyse in memory; overwrite analysis disabled.", _vol));
            return null;
        }
        return ReadRuns(data.Runs, data.RealSize);
    }
}
