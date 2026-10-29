using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepVault.Core.Content;

public enum LayoutKind
{
    /// <summary>Content is the concatenation of the extents (sparse extents read as zeros).</summary>
    Extents = 0,
    /// <summary>NTFS resident data stored inside an MFT record.</summary>
    NtfsResident = 1,
    /// <summary>NTFS LZNT1-compressed non-resident data.</summary>
    NtfsCompressed = 2,
}

/// <summary>A run of bytes on the source. Offset is absolute on the scanned source; -1 means sparse (zeros).</summary>
public readonly record struct Extent(long Offset, long Length)
{
    [JsonIgnore] public bool IsSparse => Offset < 0;
}

/// <summary>
/// Describes exactly where a candidate file's bytes live on the source so they can be read for
/// preview and restoration without ever copying them anywhere else first.
/// </summary>
public sealed class DataLayout
{
    public LayoutKind Kind { get; set; }
    /// <summary>Logical file size.</summary>
    public long Length { get; set; }
    /// <summary>For NTFS: bytes beyond this are zero (valid data length).</summary>
    public long InitializedLength { get; set; } = -1;
    public List<Extent> Extents { get; set; } = new();

    // NtfsResident
    public long RecordOffset { get; set; }
    public int RecordSize { get; set; }
    public int SectorSize { get; set; } = 512;
    public ushort AttributeInstance { get; set; }
    public string? StreamName { get; set; }

    // NtfsCompressed
    public int ClusterSize { get; set; }
    public int CompressionUnitClusters { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static DataLayout FromJson(string json) => JsonSerializer.Deserialize<DataLayout>(json, Opts)!;

    public static DataLayout Contiguous(long offset, long length) => new()
    {
        Kind = LayoutKind.Extents, Length = length, Extents = { new Extent(offset, length) },
    };

    /// <summary>First physical byte offset of real (non-sparse) data, or -1.</summary>
    public long FirstOffset => Kind == LayoutKind.NtfsResident
        ? RecordOffset
        : Extents.FirstOrDefault(e => !e.IsSparse) is { Length: > 0 } e ? e.Offset : -1;

    public int FragmentCount => Extents.Count(e => !e.IsSparse);
}
