namespace DeepVault.Core.Util;

/// <summary>Standard CRC-32 (IEEE 802.3, reflected, as used by PNG, ZIP, GPT, gzip, xz, 7z).</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);
}

public static class Bytes
{
    public static ushort U16(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o));
    public static uint U32(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o));
    public static ulong U64(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o));
    public static long I64(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o));
    public static ushort U16BE(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b.Slice(o));
    public static uint U32BE(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o));
    public static ulong U64BE(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.Slice(o));

    public static bool IsAllZero(ReadOnlySpan<byte> b) => b.IndexOfAnyExcept((byte)0) < 0;

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }

    /// <summary>Windows FILETIME (100ns since 1601) to UTC DateTime; null if out of range.</summary>
    public static DateTime? FromFileTime(long ft)
    {
        if (ft <= 0) return null;
        try
        {
            var d = DateTime.FromFileTimeUtc(ft);
            if (d.Year < 1980 || d.Year > 2100) return null;
            return d;
        }
        catch { return null; }
    }
}
