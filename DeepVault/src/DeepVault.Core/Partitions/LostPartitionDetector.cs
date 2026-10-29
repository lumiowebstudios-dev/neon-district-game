using DeepVault.Core.Carving;
using DeepVault.Core.IO;

namespace DeepVault.Core.Partitions;

/// <summary>
/// Engine C (lost partitions): watches every scanned sector for volume boot sectors (primary or
/// backup copies), validates the implied volume, and reports volumes that the partition table does
/// not list. It never writes or repairs anything; candidates are only analysed read-only.
/// </summary>
public sealed class LostPartitionDetector : ISectorObserver
{
    private readonly IBlockSource _disk;
    private readonly IReadOnlyList<PartitionInfo> _known;
    private readonly Dictionary<long, PartitionInfo> _candidates = new();

    public IReadOnlyCollection<PartitionInfo> Candidates => _candidates.Values;
    public long BootSectorsSeen { get; private set; }

    public LostPartitionDetector(IBlockSource disk, IReadOnlyList<PartitionInfo> known)
    {
        _disk = disk;
        _known = known;
    }

    public void OnSector(long abs, ReadOnlySpan<byte> data)
    {
        if (data.Length < 512 || data[510] != 0x55 || data[511] != 0xAA) return;
        var bs = FileSystemProbe.Parse(data.Slice(0, 512));
        if (bs == null) return;
        BootSectorsSeen++;
        Consider(abs, bs, "primary boot sector");
        long back = bs.BackupBootOffset;
        if (back > 0 && abs - back >= 0) Consider(abs - back, bs, "backup boot sector");
    }

    private void Consider(long start, BootSectorInfo bs, string how)
    {
        if (start % 512 != 0) return;
        if (_known.Any(p => p.Offset == start && p.FileSystem == bs.Kind)) return;
        if (_candidates.TryGetValue(start, out var existing))
        {
            if (existing.Evidence != null && !existing.Evidence.Contains(how)) existing.Evidence += $"; {how} at +{Math.Abs(start)}";
            return;
        }
        if (!FileSystemProbe.ValidateVolume(_disk, start, bs)) return;
        long len = bs.TotalBytes + (bs.Kind == FsKind.Ntfs ? bs.BytesPerSector : 0);
        _candidates[start] = new PartitionInfo
        {
            Index = 100 + _candidates.Count,
            Scheme = PartitionScheme.None,
            Status = PartitionStatus.Candidate,
            Offset = start,
            Length = len,
            FileSystem = bs.Kind,
            Boot = bs,
            TypeDescription = "Lost/unlisted volume",
            Evidence = $"{how} found",
        };
    }
}
