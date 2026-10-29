namespace DeepVault.Core.Restore;

/// <summary>What DeepVault knows about a restore destination.</summary>
public sealed class DestinationInfo
{
    public string Folder { get; set; } = "";
    public string VolumeRoot { get; set; } = "";
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    /// <summary>Physical disk identifiers backing the volume (e.g. "PhysicalDrive1" on Windows). Empty when unknown.</summary>
    public List<string> PhysicalDisks { get; set; } = new();
    public string? BusType { get; set; }
    public bool IsRemovable { get; set; }
    public int ClusterSize { get; set; } = 4096;

    public long MaxFileSize => FileSystem?.Equals("FAT32", StringComparison.OrdinalIgnoreCase) == true ? uint.MaxValue : long.MaxValue;
}

public interface IDestinationInspector
{
    DestinationInfo Inspect(string folder);
}

/// <summary>Portable inspector based on DriveInfo (no physical-disk mapping).</summary>
public sealed class BasicDestinationInspector : IDestinationInspector
{
    public DestinationInfo Inspect(string folder)
    {
        var full = Path.GetFullPath(folder);
        DriveInfo? best = null;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var root = d.RootDirectory.FullName;
                if (full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                    (best == null || root.Length > best.RootDirectory.FullName.Length))
                    best = d;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var info = new DestinationInfo { Folder = full };
        if (best != null)
        {
            info.VolumeRoot = best.RootDirectory.FullName;
            try { info.Label = best.VolumeLabel; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            info.FileSystem = best.DriveFormat;
            info.TotalBytes = best.TotalSize;
            info.FreeBytes = best.AvailableFreeSpace;
            info.IsRemovable = best.DriveType == DriveType.Removable;
        }
        return info;
    }
}

/// <summary>Turns recovered names/paths into safe names for Windows (and other) file systems.</summary>
public static class SafeNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Component(string name, int maxLen = 120)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_";
        var chars = name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        if (s.Length == 0 || s == "." || s == "..") s = "_";
        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(stem)) s = "_" + s;
        if (s.Length > maxLen)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 16) ext = "";
            s = s[..(maxLen - ext.Length)] + ext;
        }
        return s;
    }

    /// <summary>Splits a recovered "\a\b\c" path into safe components (drops "..", drive letters, empties).</summary>
    public static IEnumerable<string> PathComponents(string? path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        foreach (var part in path.Split('\\', '/'))
        {
            if (part.Length == 0 || part == "." || part == "..") continue;
            yield return Component(part.Replace("…", "_"), 80);
        }
    }

    /// <summary>"name.ext" → "name (2).ext".</summary>
    public static string WithSuffix(string fileName, int n)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem} ({n}){ext}";
    }
}
