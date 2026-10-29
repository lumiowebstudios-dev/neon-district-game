using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepVault.Tests.Infrastructure;

public sealed class FixtureFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("deleted")] public bool Deleted { get; set; } = true;
    [JsonPropertyName("expect")] public string Expect { get; set; } = "exact";
    [JsonPropertyName("offset")] public long? Offset { get; set; }
    [JsonPropertyName("fragmented")] public bool Fragmented { get; set; }
    [JsonPropertyName("resident")] public bool Resident { get; set; }
    [JsonPropertyName("ntfs_compressed")] public bool NtfsCompressed { get; set; }
    [JsonPropertyName("partition")] public int? Partition { get; set; }
}

public sealed class FixturePartition
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("length")] public long Length { get; set; }
    [JsonPropertyName("fs")] public string Fs { get; set; } = "";
}

public sealed class FixtureImage
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
    [JsonPropertyName("files")] public List<FixtureFile> Files { get; set; } = new();
    [JsonPropertyName("partition_offset")] public long? PartitionOffset { get; set; }
    [JsonPropertyName("partitions")] public List<FixturePartition>? Partitions { get; set; }
    [JsonPropertyName("compressed_applied")] public bool? CompressedApplied { get; set; }

    /// <summary>Path of the decompressed, read-only working copy of this image.</summary>
    public string ImagePath { get; set; } = "";
}

/// <summary>
/// Loads the disposable test disk images from tests/fixtures/images (built by tools/fixtures).
/// Each image is decompressed once into a temp folder and verified against its recorded SHA-256.
/// </summary>
public static class Fixtures
{
    private static readonly object Lock = new();
    private static Dictionary<string, FixtureImage>? _images;

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "images");
    public static string WorkDir { get; } = Path.Combine(Path.GetTempPath(), "deepvault-test-fixtures");

    public static FixtureImage Get(string name)
    {
        lock (Lock)
        {
            _images ??= JsonSerializer.Deserialize<ManifestRoot>(File.ReadAllText(Path.Combine(Dir, "manifest.json")))!
                .Images.ToDictionary(i => i.Name);
            var img = _images[name];
            if (img.ImagePath.Length == 0)
            {
                Directory.CreateDirectory(WorkDir);
                var target = Path.Combine(WorkDir, $"{img.Name}-{img.Sha256[..12]}.img");
                if (!File.Exists(target) || Sha256File(target) != img.Sha256)
                {
                    var tmp = target + ".tmp";
                    using (var gz = new GZipStream(File.OpenRead(Path.Combine(Dir, img.File)), CompressionMode.Decompress))
                    using (var o = File.Create(tmp))
                        gz.CopyTo(o);
                    File.Move(tmp, target, true);
                    if (Sha256File(target) != img.Sha256) throw new InvalidDataException($"Fixture {name} hash mismatch");
                }
                img.ImagePath = target;
            }
            return img;
        }
    }

    public static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static string Sha256(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    private sealed class ManifestRoot
    {
        [JsonPropertyName("images")] public List<FixtureImage> Images { get; set; } = new();
    }

    public static string NewTempDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), "deepvault-tests", prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }
}
