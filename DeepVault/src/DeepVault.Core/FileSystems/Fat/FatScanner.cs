using System.Text;
using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;
using DeepVault.Core.Partitions;
using DeepVault.Core.Util;

namespace DeepVault.Core.FileSystems.Fat;

public sealed class FatScanOptions
{
    public bool IncludeExistingFiles { get; init; }
    /// <summary>Deleted files up to this size are read in full and structure-checked to choose the best layout.</summary>
    public long ValidateUpTo { get; init; } = 64L * 1024 * 1024;
    public long MaxFatBytes { get; init; } = 512L * 1024 * 1024;
}

public sealed class FatVolumeResult
{
    public long VolumeOffset { get; init; }
    public int ClusterSize { get; init; }
    public long DataStart { get; init; }
    public long ClusterCount { get; init; }
    public uint[]? Fat { get; set; }
    public long DeletedFiles { get; set; }
    public long ExistingFiles { get; set; }
    public long DirectoriesScanned { get; set; }
    public bool Completed { get; set; }
    public bool IsAllocated(long cluster) => Fat != null && cluster >= 2 && cluster < Fat.Length && Fat[cluster] != 0;
    public long ClusterOffset(long c) => DataStart + (c - 2) * ClusterSize;
}

/// <summary>
/// Engine A for FAT12/16/32: walks the directory tree (including deleted entries and deleted
/// directories). FAT zeroes the cluster chain on deletion, so deleted content is reconstructed by
/// assuming contiguity (and, as an alternative, by skipping clusters now in use); each candidate
/// layout is validated against the file's format where possible.
/// </summary>
public sealed class FatScanner
{
    private readonly IBlockSource _disk;
    private readonly ResilientReader _reader;
    private readonly IScanSink _sink;
    private readonly long _vol;
    private readonly BootSectorInfo _boot;
    private readonly FatScanOptions _opt;
    private readonly string _label;
    private FatVolumeResult _r = null!;
    private long _fatEntries;
    private readonly HashSet<long> _visitedDirClusters = new();

    public FatScanner(IBlockSource disk, ResilientReader reader, IScanSink sink, long volumeOffset, BootSectorInfo boot, string label, FatScanOptions? opt = null)
    {
        _disk = disk; _reader = reader; _sink = sink; _vol = volumeOffset; _boot = boot; _label = label;
        _opt = opt ?? new FatScanOptions();
    }

    private long _rootOff, _rootDirBytes;

    /// <summary>Computes the layout and loads the FAT (allocation information) without walking directories.</summary>
    public FatVolumeResult Prepare()
    {
        var b = _boot.Raw;
        int bps = Bytes.U16(b, 0x0B);
        int spc = b[0x0D];
        int reserved = Bytes.U16(b, 0x0E);
        int nFats = b[0x10];
        int rootEntries = Bytes.U16(b, 0x11);
        long fatSz = Bytes.U16(b, 0x16) != 0 ? Bytes.U16(b, 0x16) : Bytes.U32(b, 0x24);
        long totalSectors = Bytes.U16(b, 0x13) != 0 ? Bytes.U16(b, 0x13) : Bytes.U32(b, 0x20);
        long rootDirSectors = (rootEntries * 32L + bps - 1) / bps;
        long fatOff = _vol + (long)reserved * bps;
        long rootOff = fatOff + nFats * fatSz * bps;
        long dataStart = rootOff + rootDirSectors * bps;
        long clusters = (totalSectors - (reserved + nFats * fatSz + rootDirSectors)) / spc;
        _r = new FatVolumeResult { VolumeOffset = _vol, ClusterSize = bps * spc, DataStart = dataStart, ClusterCount = clusters };
        _fatEntries = clusters + 2;

        // Load the first FAT copy (fall back to the second if the first is unreadable).
        long fatBytes = fatSz * bps;
        if (fatBytes <= _opt.MaxFatBytes)
        {
            var raw = new byte[fatBytes];
            _reader.Read(fatOff, raw, out bool err);
            if (err && nFats > 1)
            {
                _sink.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.FatDirectory, "Primary FAT has unreadable sectors; using the second FAT copy.", fatOff));
                _reader.Read(fatOff + fatBytes, raw);
            }
            var fat = new uint[_fatEntries];
            for (long i = 0; i < _fatEntries; i++)
            {
                fat[i] = _boot.Kind switch
                {
                    FsKind.Fat32 => i * 4 + 4 <= raw.Length ? Bytes.U32(raw, (int)(i * 4)) & 0x0FFFFFFF : 0,
                    FsKind.Fat16 => i * 2 + 2 <= raw.Length ? Bytes.U16(raw, (int)(i * 2)) : 0u,
                    _ => Fat12Entry(raw, i),
                };
            }
            _r.Fat = fat;
        }
        else _sink.AddIssue(new ScanIssue(IssueSeverity.Warning, EngineId.FatDirectory, "FAT too large to load; deleted-file overwrite analysis disabled.", fatOff));
        _rootOff = rootOff;
        _rootDirBytes = rootDirSectors * bps;
        return _r;
    }

    public FatVolumeResult Run(IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        Prepare();
        var b = _boot.Raw;
        var prog = new EngineProgress { Phase = $"FAT directory analysis ({_label})", Unit = "directories" };
        if (_boot.Kind == FsKind.Fat32)
        {
            long rootCluster = Bytes.U32(b, 0x2C);
            WalkDirectory(ChainExtents(rootCluster, long.MaxValue), "\\", false, prog, progress, ct, 0);
        }
        else
        {
            WalkDirectory(new List<Extent> { new(_rootOff, _rootDirBytes) }, "\\", false, prog, progress, ct, 0);
        }
        _sink.Commit();
        _r.Completed = true;
        return _r;
    }

    private static uint Fat12Entry(byte[] raw, long i)
    {
        long o = i * 3 / 2;
        if (o + 1 >= raw.Length) return 0;
        int v = raw[o] | (raw[o + 1] << 8);
        return (uint)((i & 1) != 0 ? v >> 4 : v & 0xFFF);
    }

    private bool IsEoc(uint v) => _boot.Kind switch { FsKind.Fat32 => v >= 0x0FFFFFF8, FsKind.Fat16 => v >= 0xFFF8, _ => v >= 0xFF8 };

    /// <summary>Follows a FAT chain (existing files/dirs).</summary>
    private List<Extent> ChainExtents(long first, long maxBytes)
    {
        var res = new List<Extent>();
        if (_r.Fat == null || first < 2) return res;
        long c = first;
        var seen = new HashSet<long>();
        long total = 0;
        while (c >= 2 && c < _fatEntries && seen.Add(c) && total < maxBytes)
        {
            long off = _r.ClusterOffset(c);
            if (res.Count > 0 && res[^1].Offset + res[^1].Length == off) res[^1] = new Extent(res[^1].Offset, res[^1].Length + _r.ClusterSize);
            else res.Add(new Extent(off, _r.ClusterSize));
            total += _r.ClusterSize;
            uint n = _r.Fat[c];
            if (IsEoc(n) || n < 2) break;
            c = n;
        }
        return res;
    }

    private sealed class LfnPart { public int Seq; public byte Checksum; public string Text = ""; public bool Deleted; }

    private void WalkDirectory(List<Extent> extents, string path, bool deletedDir, EngineProgress prog, IProgress<EngineProgress>? progress, CancellationToken ct, int depth)
    {
        if (depth > 64) return;
        ct.ThrowIfCancellationRequested();
        _r.DirectoriesScanned++;
        prog.Done = _r.DirectoriesScanned;
        prog.FilesFound = _r.DeletedFiles + _r.ExistingFiles;
        progress?.Report(prog);

        var lfn = new List<LfnPart>();
        var subdirs = new List<(List<Extent> ext, string path, bool deleted)>();
        foreach (var ext in extents)
        {
            if (!_visitedDirClusters.Add(ext.Offset)) return;
            var data = new byte[ext.Length];
            _reader.Read(ext.Offset, data);
            for (int i = 0; i + 32 <= data.Length; i += 32)
            {
                var e = data.AsSpan(i, 32);
                byte first = e[0];
                if (first == 0x00) { if (!deletedDir) goto done; continue; }
                byte attr = e[11];
                if (attr == 0x0F)
                {
                    var part = new LfnPart { Seq = first & 0x1F, Checksum = e[13], Deleted = first == 0xE5, Text = LfnText(e) };
                    if ((first & 0x40) != 0 && first != 0xE5) lfn.Clear();
                    lfn.Add(part);
                    continue;
                }
                bool deleted = first == 0xE5 || deletedDir;
                if ((attr & 0x08) != 0) { lfn.Clear(); continue; } // volume label
                var shortName = e.Slice(0, 11).ToArray();
                string name = AssembleName(shortName, lfn, first == 0xE5, e[12], out bool firstCharGuessed);
                lfn.Clear();
                if (name == "." || name == "..") continue;
                if (!IsPlausibleEntry(e)) continue;
                long cluster = Bytes.U16(e, 26) | (_boot.Kind == FsKind.Fat32 ? (long)Bytes.U16(e, 20) << 16 : 0);
                long size = Bytes.U32(e, 28);
                if ((attr & 0x10) != 0)
                {
                    if (cluster < 2 || cluster >= _fatEntries) continue;
                    string sub = path.TrimEnd('\\') + "\\" + name;
                    if (deleted)
                    {
                        // Deleted directory: its chain is gone; read contiguous clusters while they look like directory data.
                        var dext = DeletedDirExtents(cluster);
                        if (dext.Count > 0) subdirs.Add((dext, sub, true));
                    }
                    else subdirs.Add((ChainExtents(cluster, 64L * 1024 * 1024), sub, false));
                    continue;
                }
                if (!deleted && !_opt.IncludeExistingFiles) continue;
                EmitFile(name, path, e, cluster, size, deleted, firstCharGuessed);
            }
        }
        done:
        foreach (var (ext, p, d) in subdirs) WalkDirectory(ext, p, d, prog, progress, ct, depth + 1);
    }

    private static bool IsPlausibleEntry(ReadOnlySpan<byte> e)
    {
        for (int i = 1; i < 11; i++) if (e[i] < 0x20 && e[i] != 0x05) return false;
        if ((e[11] & 0xC0) != 0) return false;
        return true;
    }

    private List<Extent> DeletedDirExtents(long cluster)
    {
        var res = new List<Extent>();
        var buf = new byte[_r.ClusterSize];
        for (int k = 0; k < 256; k++)
        {
            long c = cluster + k;
            if (c >= _fatEntries) break;
            if (k > 0 && _r.IsAllocated(c)) break;
            long off = _r.ClusterOffset(c);
            _reader.Read(off, buf);
            if (k == 0 && !(buf[0] == '.' && buf[32] == '.' && buf[33] == '.')) break; // must start with "." and ".."
            if (k > 0 && !LooksLikeDirCluster(buf)) break;
            res.Add(new Extent(off, _r.ClusterSize));
        }
        return res;
    }

    private static bool LooksLikeDirCluster(byte[] b)
    {
        int valid = 0;
        for (int i = 0; i + 32 <= b.Length; i += 32)
        {
            if (b[i] == 0) return valid > 0 || i > 0;
            byte attr = b[i + 11];
            if (attr == 0x0F || (attr & 0xC0) == 0) valid++;
            else return false;
        }
        return valid > 0;
    }

    private static string LfnText(ReadOnlySpan<byte> e)
    {
        Span<char> chars = stackalloc char[13];
        int n = 0;
        foreach (var (o, c) in new[] { (1, 5), (14, 6), (28, 2) })
            for (int k = 0; k < c; k++)
            {
                char ch = (char)Bytes.U16(e, o + k * 2);
                if (ch == 0 || ch == 0xFFFF) return new string(chars[..n]);
                chars[n++] = ch;
            }
        return new string(chars[..n]);
    }

    private static byte ShortChecksum(ReadOnlySpan<byte> name11)
    {
        byte sum = 0;
        foreach (var c in name11) sum = (byte)((((sum & 1) << 7) | (sum >> 1)) + c);
        return sum;
    }

    private static string AssembleName(byte[] shortName, List<LfnPart> lfn, bool deleted, byte ntCase, out bool firstCharGuessed)
    {
        firstCharGuessed = false;
        if (deleted && lfn.Count > 0)
        {
            // Recover the lost first character from the LFN checksum (it covers the original short name).
            byte want = lfn[^1].Checksum;
            var candidate = (byte[])shortName.Clone();
            // Deleted LFN entries lose their sequence numbers; use the run of entries just before the
            // short entry that share its checksum (disk order is last part first).
            var parts = Enumerable.Reverse(lfn).TakeWhile(l => l.Checksum == want).ToList();
            string longName = string.Concat(parts.Select(l => l.Text));
            var tries = new List<byte>();
            if (longName.Length > 0) tries.Add((byte)char.ToUpperInvariant(longName[0]));
            for (byte c = 0x21; c < 0x7F; c++) tries.Add(c);
            foreach (var c in tries)
            {
                candidate[0] = c;
                if (ShortChecksum(candidate) == want)
                {
                    shortName[0] = c;
                    break;
                }
            }
            if (longName.Length > 0) return longName;
        }
        if (!deleted && lfn.Count > 0 && lfn.All(l => l.Checksum == ShortChecksum(shortName)))
            return string.Concat(Enumerable.Reverse(lfn).OrderBy(l => l.Seq).Select(l => l.Text));
        if (deleted && shortName[0] == 0xE5) { shortName[0] = (byte)'_'; firstCharGuessed = true; }
        if (shortName[0] == 0x05) shortName[0] = 0xE5;
        string bn = Encoding.Latin1.GetString(shortName, 0, 8).TrimEnd();
        string ex = Encoding.Latin1.GetString(shortName, 8, 3).TrimEnd();
        // Windows NT stores all-lowercase 8.3 names as upper case plus these flags.
        if ((ntCase & 0x08) != 0) bn = bn.ToLowerInvariant();
        if ((ntCase & 0x10) != 0) ex = ex.ToLowerInvariant();
        return ex.Length > 0 ? bn + "." + ex : bn;
    }

    private static DateTime? FatTime(ushort date, ushort time, byte tenths = 0)
    {
        if (date == 0) return null;
        int y = 1980 + (date >> 9), mo = (date >> 5) & 15, d = date & 31;
        int h = time >> 11, mi = (time >> 5) & 63, s = (time & 31) * 2;
        try { return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Local).AddMilliseconds(tenths * 10).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private void EmitFile(string name, string path, ReadOnlySpan<byte> e, long cluster, long size, bool deleted, bool firstCharGuessed)
    {
        var f = new FoundFile
        {
            Name = name,
            Path = path.Length == 0 ? "\\" : path,
            Extension = FileTypes.ExtensionOf(name),
            Size = size,
            Created = FatTime(Bytes.U16(e, 16), Bytes.U16(e, 14), e[13]),
            Modified = FatTime(Bytes.U16(e, 24), Bytes.U16(e, 22)),
            Accessed = FatTime(Bytes.U16(e, 18), 0),
            Engines = EngineId.FatDirectory,
            IsDeleted = deleted,
            State = RecoveryState.MetadataRecovered,
            VolumeLabel = _label,
            VolumeOffset = _vol,
            SourceDetail = $"{FileSystemProbe.Describe(_boot.Kind)} directory entry, start cluster {cluster}" + (deleted ? " (deleted)" : " (exists)"),
        };
        f.Category = FileTypes.CategoryOf(f.Extension);
        var notes = new List<string>();
        if (firstCharGuessed) notes.Add("First character of the name was erased by deletion (shown as '_')");

        if (size == 0) { f.Layout = new DataLayout { Length = 0 }; f.Confidence = Confidence.High; notes.Add("Empty file"); Finish(f, notes, deleted); return; }
        if (cluster < 2 || cluster >= _fatEntries)
        {
            f.Layout = new DataLayout { Length = size };
            f.Confidence = Confidence.Low;
            f.State = RecoveryState.Failed;
            notes.Add("Start cluster is invalid; content location unknown");
            Finish(f, notes, deleted);
            return;
        }
        long nClusters = (size + _r.ClusterSize - 1) / _r.ClusterSize;
        if (!deleted)
        {
            var ext = ChainExtents(cluster, nClusters * _r.ClusterSize);
            f.Layout = Trim(ext, size);
            f.Confidence = ext.Sum(x => x.Length) >= size ? Confidence.High : Confidence.Low;
            if (f.Layout.FragmentCount > 1) notes.Add($"Fragmented into {f.Layout.FragmentCount} pieces (from the FAT chain)");
            Finish(f, notes, deleted);
            return;
        }

        // Deleted: the chain is gone. Candidate A: strictly contiguous. Candidate B: contiguous skipping clusters now in use.
        var contiguous = new List<Extent> { new(_r.ClusterOffset(cluster), nClusters * _r.ClusterSize) };
        long allocated = 0;
        for (long c = cluster; c < cluster + nClusters; c++) if (_r.IsAllocated(c)) allocated++;
        if (cluster + nClusters > _fatEntries) { notes.Add("File would extend past the end of the volume"); f.State = RecoveryState.PartialOrDamaged; }
        var layoutA = Trim(contiguous, size);
        DataLayout? layoutB = null;
        if (allocated > 0 && _r.Fat != null)
        {
            var skip = new List<Extent>();
            long got = 0;
            for (long c = cluster; c < _fatEntries && got < nClusters; c++)
            {
                if (_r.IsAllocated(c)) continue;
                long off = _r.ClusterOffset(c);
                if (skip.Count > 0 && skip[^1].Offset + skip[^1].Length == off) skip[^1] = new Extent(skip[^1].Offset, skip[^1].Length + _r.ClusterSize);
                else skip.Add(new Extent(off, _r.ClusterSize));
                got++;
            }
            layoutB = Trim(skip, size);
        }
        f.OverwrittenFraction = (double)allocated / nClusters;

        var va = Validate(layoutA, f.Extension, size);
        f.Layout = layoutA;
        f.Structure = va.Check;
        if (layoutB != null)
        {
            var vb = Validate(layoutB, f.Extension, size);
            if (Rank(vb.Check) > Rank(va.Check))
            {
                f.Layout = layoutB;
                f.Structure = vb.Check;
                f.OverwrittenFraction = 0;
                notes.Add("Reassembled by skipping clusters that are now in use (validated against the file format)");
                va = vb;
            }
        }
        if (va.Note != null && f.Structure != StructureCheck.Verified) notes.Add(va.Note);
        notes.Add("FAT erases the cluster chain on deletion: content assumed contiguous");
        f.Confidence = f.Structure switch
        {
            StructureCheck.Verified => Confidence.High,
            StructureCheck.Damaged => Confidence.Low,
            _ => f.OverwrittenFraction == 0 ? Confidence.Medium : Confidence.Low,
        };
        if (f.Structure == StructureCheck.Damaged || f.OverwrittenFraction > 0 && f.Structure != StructureCheck.Verified)
        {
            f.State = RecoveryState.PartialOrDamaged;
            if (f.OverwrittenFraction > 0) notes.Add($"{f.OverwrittenFraction:P0} of the assumed clusters are now in use by other files");
        }
        Finish(f, notes, deleted);
    }

    private static int Rank(StructureCheck c) => c switch { StructureCheck.Verified => 4, StructureCheck.Plausible => 3, StructureCheck.NotChecked => 2, StructureCheck.HeaderOnly => 1, _ => 0 };

    private FormatValidator.Validation Validate(DataLayout layout, string ext, long size)
    {
        if (size > _opt.ValidateUpTo) return new FormatValidator.Validation(StructureCheck.NotChecked, "too large to validate during scan", null);
        using var cs = new ContentStream(_reader, layout);
        return FormatValidator.Validate(new StreamCarveSource(cs), ext);
    }

    private static DataLayout Trim(List<Extent> ext, long size)
    {
        var l = new DataLayout { Kind = LayoutKind.Extents, Length = size };
        long remain = size;
        foreach (var e in ext)
        {
            if (remain <= 0) break;
            long n = Math.Min(e.Length, remain);
            l.Extents.Add(new Extent(e.Offset, n));
            remain -= n;
        }
        return l;
    }

    private void Finish(FoundFile f, List<string> notes, bool deleted)
    {
        f.Notes = string.Join("; ", notes);
        if (deleted) _r.DeletedFiles++; else _r.ExistingFiles++;
        _sink.AddFile(f);
    }
}
