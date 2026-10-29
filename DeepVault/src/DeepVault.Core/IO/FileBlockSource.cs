using Microsoft.Win32.SafeHandles;

namespace DeepVault.Core.IO;

/// <summary>
/// Read-only source backed by a file: a raw disk image (.img/.dd/.raw/.bin) or, on Linux,
/// a block device such as /dev/sdb. The file is opened with FileAccess.Read only.
/// </summary>
public sealed class FileBlockSource : IBlockSource
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;

    public long Length => _length;
    public int SectorSize { get; }
    public string Description { get; }
    public string FilePath { get; }

    public FileBlockSource(string path, int sectorSize = 512)
    {
        FilePath = Path.GetFullPath(path);
        // FileAccess.Read: the OS will refuse any write through this handle.
        _handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        _length = RandomAccess.GetLength(_handle);
        if (_length == 0 && OperatingSystem.IsLinux() && FilePath.StartsWith("/dev/", StringComparison.Ordinal))
            _length = LinuxBlockDeviceSize(FilePath);
        SectorSize = sectorSize;
        Description = $"Image file ({Path.GetFileName(FilePath)})";
    }

    private static long LinuxBlockDeviceSize(string devPath)
    {
        try
        {
            var name = Path.GetFileName(devPath);
            var sizeFile = Directory.Exists($"/sys/class/block/{name}") ? $"/sys/class/block/{name}/size" : null;
            if (sizeFile != null && long.TryParse(File.ReadAllText(sizeFile).Trim(), out var sectors))
                return sectors * 512;
        }
        catch { }
        return 0;
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= _length) return 0;
        long remain = _length - offset;
        if (buffer.Length > remain) buffer = buffer.Slice(0, (int)remain);
        try
        {
            return RandomAccess.Read(_handle, buffer, offset);
        }
        catch (IOException ex)
        {
            throw new SourceReadException(offset, buffer.Length, $"Read failed at offset {offset}: {ex.Message}", ex);
        }
    }

    public void Dispose() => _handle.Dispose();
}
