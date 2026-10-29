namespace DeepVault.Core.IO;

/// <summary>A read-only window into a parent source (e.g. one partition of a disk).</summary>
public sealed class SubRangeSource : IBlockSource
{
    private readonly IBlockSource _parent;
    public long Start { get; }
    public long Length { get; }
    public int SectorSize => _parent.SectorSize;
    public string Description => $"{_parent.Description} @ {Start}";

    public SubRangeSource(IBlockSource parent, long start, long length)
    {
        _parent = parent;
        Start = start;
        Length = Math.Max(0, Math.Min(length, parent.Length - start));
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        long remain = Length - offset;
        if (buffer.Length > remain) buffer = buffer.Slice(0, (int)remain);
        return _parent.Read(Start + offset, buffer);
    }

    /// <summary>Does not dispose the parent.</summary>
    public void Dispose() { }
}
