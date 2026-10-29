using DeepVault.Core.Catalog;
using DeepVault.Core.IO;

namespace DeepVault.Core.Carving;

/// <summary>Random-access byte source used by format analyzers (disk via cache, or a stream/file for validation).</summary>
public interface ICarveSource
{
    long Length { get; }
    /// <summary>Reads up to buffer.Length bytes; returns the count available (short only at end).</summary>
    int Read(long offset, Span<byte> buffer);
}

public sealed class CachedCarveSource : ICarveSource
{
    private readonly CachedReader _r;
    public CachedCarveSource(CachedReader r) => _r = r;
    public long Length => _r.Length;
    public int Read(long offset, Span<byte> buffer) => offset >= Length ? 0 : _r.Read(offset, buffer);
}

public sealed class StreamCarveSource : ICarveSource
{
    private readonly Stream _s;
    public StreamCarveSource(Stream s) => _s = s;
    public long Length => _s.Length;
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= _s.Length) return 0;
        _s.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _s.Read(buffer.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

public sealed class ByteArrayCarveSource : ICarveSource
{
    private readonly byte[] _b;
    public ByteArrayCarveSource(byte[] b) => _b = b;
    public long Length => _b.Length;
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= _b.Length) return 0;
        int n = (int)Math.Min(buffer.Length, _b.Length - offset);
        _b.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }
}

/// <summary>Outcome of analysing a candidate at a given offset.</summary>
public sealed class CarveResult
{
    /// <summary>Length of the file in bytes as determined from its structure (or estimated, see Check).</summary>
    public long Length { get; init; }
    public StructureCheck Check { get; init; }
    public string Extension { get; init; } = "";
    /// <summary>Short human-readable description (e.g. dimensions) — never file content.</summary>
    public string? Detail { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// A file format the carver knows how to recognise and delimit. Implementations must be defensive:
/// they read untrusted bytes and must never throw for malformed data (return null or Damaged instead).
/// </summary>
public abstract class CarveFormat
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract FileCategory Category { get; }
    public abstract string DefaultExtension { get; }
    /// <summary>Byte patterns that must appear at <see cref="SignatureOffset"/> from the start of the file.</summary>
    public abstract IReadOnlyList<byte[]> Signatures { get; }
    public virtual int SignatureOffset => 0;
    /// <summary>Largest file this analyzer will consider (bounds look-ahead reading).</summary>
    public virtual long MaxSize => 1L << 30;
    /// <summary>Whether the format has internal checksums that let the carver detect corrupted content.</summary>
    public virtual bool HasChecksums => false;
    /// <summary>Formats that produce excessive noise on system disks are opt-in.</summary>
    public virtual bool EnabledByDefault => true;
    public virtual string Description => "";

    /// <summary>Returns null if the bytes at <paramref name="start"/> are not really this format.</summary>
    public abstract CarveResult? Analyze(ICarveSource src, long start, long maxLength);

    public CarveResult? SafeAnalyze(ICarveSource src, long start, long maxLength)
    {
        try { return Analyze(src, start, maxLength); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidDataException or OverflowException or IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Bounds-checked reading helpers for analyzers.</summary>
public static class CarveIO
{
    public static bool Fill(this ICarveSource s, long off, Span<byte> buf) => off >= 0 && s.Read(off, buf) == buf.Length;

    public static byte[]? Get(this ICarveSource s, long off, int count)
    {
        var b = new byte[count];
        return s.Fill(off, b) ? b : null;
    }

    public static int U8(this ICarveSource s, long off)
    {
        Span<byte> b = stackalloc byte[1];
        return s.Fill(off, b) ? b[0] : -1;
    }

    public static long U16LE(this ICarveSource s, long off) { Span<byte> b = stackalloc byte[2]; return s.Fill(off, b) ? b[0] | (b[1] << 8) : -1; }
    public static long U16BE(this ICarveSource s, long off) { Span<byte> b = stackalloc byte[2]; return s.Fill(off, b) ? (b[0] << 8) | b[1] : -1; }
    public static long U24BE(this ICarveSource s, long off) { Span<byte> b = stackalloc byte[3]; return s.Fill(off, b) ? (b[0] << 16) | (b[1] << 8) | b[2] : -1; }
    public static long U32LE(this ICarveSource s, long off) { Span<byte> b = stackalloc byte[4]; return s.Fill(off, b) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b) : -1; }
    public static long U32BE(this ICarveSource s, long off) { Span<byte> b = stackalloc byte[4]; return s.Fill(off, b) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b) : -1; }
    public static ulong? U64LE(this ICarveSource s, long off) { Span<byte> b = stackalloc byte[8]; return s.Fill(off, b) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b) : null; }
    public static ulong? U64BE(this ICarveSource s, long off) { Span<byte> b = stackalloc byte[8]; return s.Fill(off, b) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b) : null; }

    public static bool Matches(this ICarveSource s, long off, ReadOnlySpan<byte> pattern)
    {
        Span<byte> b = pattern.Length <= 64 ? stackalloc byte[pattern.Length] : new byte[pattern.Length];
        return s.Fill(off, b) && b.SequenceEqual(pattern);
    }

    /// <summary>Finds the next occurrence of pattern in [from, limit). Returns -1 if not found.</summary>
    public static long Find(this ICarveSource s, ReadOnlySpan<byte> pattern, long from, long limit, int chunk = 256 * 1024)
    {
        var buf = new byte[chunk + pattern.Length];
        long pos = from;
        while (pos < limit)
        {
            int want = (int)Math.Min(buf.Length, limit - pos + pattern.Length - 1);
            int n = s.Read(pos, buf.AsSpan(0, want));
            if (n < pattern.Length) return -1;
            int idx = buf.AsSpan(0, n).IndexOf(pattern);
            if (idx >= 0 && pos + idx < limit) return pos + idx;
            if (n < want) return -1;
            pos += n - pattern.Length + 1;
        }
        return -1;
    }

    public static bool IsPrintableAscii(ReadOnlySpan<byte> b)
    {
        foreach (var c in b) if (c < 0x20 || c > 0x7E) return false;
        return true;
    }

    public static long AlignUp(long v, long a) => (v + a - 1) / a * a;
}
