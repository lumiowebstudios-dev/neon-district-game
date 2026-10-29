using System.Buffers.Binary;

namespace DeepVault.Core.IO;

/// <summary>
/// Read-only reader for Microsoft VHD images (fixed and dynamic). Differencing disks are rejected
/// because they need their parent chain. VHDX is not supported.
/// </summary>
public sealed class VhdBlockSource : IBlockSource
{
    private readonly FileBlockSource _file;
    private readonly bool _dynamic;
    private readonly uint[] _bat = Array.Empty<uint>();
    private readonly int _blockSize;
    private readonly int _bitmapBytes;

    public long Length { get; }
    public int SectorSize => 512;
    public string Description { get; }

    private VhdBlockSource(FileBlockSource file, long length, bool dynamic, uint[] bat, int blockSize)
    {
        _file = file;
        Length = length;
        _dynamic = dynamic;
        _bat = bat;
        _blockSize = blockSize;
        int sectorsPerBlock = blockSize / 512;
        _bitmapBytes = ((sectorsPerBlock / 8 + 511) / 512) * 512;
        Description = $"VHD image ({Path.GetFileName(file.FilePath)}, {(dynamic ? "dynamic" : "fixed")})";
    }

    public static bool LooksLikeVhd(string path)
    {
        try
        {
            using var f = new FileBlockSource(path);
            if (f.Length < 512) return false;
            var footer = f.ReadBytes(f.Length - 512, 512);
            return footer.AsSpan(0, 8).SequenceEqual("conectix"u8);
        }
        catch { return false; }
    }

    public static VhdBlockSource Open(string path)
    {
        var f = new FileBlockSource(path);
        try
        {
            var footer = f.ReadBytes(f.Length - 512, 512);
            if (!footer.AsSpan(0, 8).SequenceEqual("conectix"u8))
                throw new InvalidDataException("Not a VHD file (footer cookie missing).");
            long currentSize = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(0x30));
            uint diskType = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(0x3C));
            if (diskType == 2)
                return new VhdBlockSource(f, Math.Min(currentSize, f.Length - 512), false, Array.Empty<uint>(), 512);
            if (diskType == 4)
                throw new NotSupportedException("Differencing VHDs are not supported; merge it with its parent first.");
            if (diskType != 3)
                throw new InvalidDataException($"Unknown VHD disk type {diskType}.");
            long dynOff = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(0x10));
            var dyn = f.ReadBytes(dynOff, 1024);
            if (!dyn.AsSpan(0, 8).SequenceEqual("cxsparse"u8))
                throw new InvalidDataException("VHD dynamic header missing.");
            long batOff = (long)BinaryPrimitives.ReadUInt64BigEndian(dyn.AsSpan(0x10));
            int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(dyn.AsSpan(0x1C));
            int blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(dyn.AsSpan(0x20));
            if (entries <= 0 || entries > 64 * 1024 * 1024 || blockSize < 512 || blockSize % 512 != 0)
                throw new InvalidDataException("VHD dynamic header has implausible values.");
            var batBytes = f.ReadBytes(batOff, entries * 4);
            var bat = new uint[entries];
            for (int i = 0; i < entries; i++) bat[i] = BinaryPrimitives.ReadUInt32BigEndian(batBytes.AsSpan(i * 4));
            return new VhdBlockSource(f, currentSize, true, bat, blockSize);
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        if (buffer.Length > Length - offset) buffer = buffer.Slice(0, (int)(Length - offset));
        if (!_dynamic) return _file.Read(offset, buffer);
        int done = 0;
        while (done < buffer.Length)
        {
            long abs = offset + done;
            long block = abs / _blockSize;
            int inBlock = (int)(abs % _blockSize);
            int n = Math.Min(_blockSize - inBlock, buffer.Length - done);
            uint entry = block < _bat.Length ? _bat[block] : 0xFFFFFFFF;
            if (entry == 0xFFFFFFFF) buffer.Slice(done, n).Clear();
            else _file.ReadAt((long)entry * 512 + _bitmapBytes + inBlock, buffer.Slice(done, n));
            done += n;
        }
        return done;
    }

    public void Dispose() => _file.Dispose();
}

public static class ImageOpener
{
    /// <summary>Opens a disk image file read-only, detecting VHD; everything else is treated as a raw image.</summary>
    public static IBlockSource Open(string path)
    {
        if (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("VHDX images are not supported yet. Convert to VHD or raw (e.g. with Hyper-V 'Convert-VHD' or qemu-img) first.");
        if (VhdBlockSource.LooksLikeVhd(path)) return VhdBlockSource.Open(path);
        return new FileBlockSource(path);
    }
}
