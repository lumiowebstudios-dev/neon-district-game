namespace DeepVault.Core.IO;

/// <summary>
/// A strictly read-only, random-access view of a disk, partition or disk image.
/// There is deliberately no write method anywhere in this interface hierarchy: source media
/// can only ever be read through DeepVault.
/// </summary>
public interface IBlockSource : IDisposable
{
    /// <summary>Total length in bytes.</summary>
    long Length { get; }

    /// <summary>Logical sector size in bytes (usually 512 or 4096).</summary>
    int SectorSize { get; }

    /// <summary>Human-readable description (no personal data).</summary>
    string Description { get; }

    /// <summary>
    /// Reads up to <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>.
    /// Returns the number of bytes read (less than requested only at end of media).
    /// Throws <see cref="SourceReadException"/> when the media reports an error.
    /// </summary>
    int Read(long offset, Span<byte> buffer);
}

/// <summary>Raised when the underlying media fails to return data for a region.</summary>
public sealed class SourceReadException : IOException
{
    public long Offset { get; }
    public int Count { get; }

    public SourceReadException(long offset, int count, string message, Exception? inner = null)
        : base(message, inner)
    {
        Offset = offset;
        Count = count;
    }
}

public static class BlockSourceExtensions
{
    /// <summary>Reads exactly buffer.Length bytes or throws. Bytes beyond the end of media are zero-filled and reported.</summary>
    public static int ReadAt(this IBlockSource src, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            if (offset + total >= src.Length) break;
            int n = src.Read(offset + total, buffer.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        if (total < buffer.Length) buffer.Slice(total).Clear();
        return total;
    }

    public static byte[] ReadBytes(this IBlockSource src, long offset, int count)
    {
        var b = new byte[count];
        src.ReadAt(offset, b);
        return b;
    }
}
