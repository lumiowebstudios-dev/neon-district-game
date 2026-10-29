using System.Security.Cryptography;
using System.Text.Json;
using DeepVault.Core.IO;
using DeepVault.Core.Restore;
using DeepVault.Core.Scanning;
using DeepVault.Core.Util;

namespace DeepVault.Core.Imaging;

public sealed class ImageRequest
{
    public string ImagePath { get; set; } = "";
    /// <summary>Byte range of the source to image (whole source by default). A partial image is clearly labelled as such.</summary>
    public long Start { get; set; }
    public long Length { get; set; } = -1;
    public int ChunkSize { get; set; } = 4 * 1024 * 1024;
    public bool AllowDestinationOnSource { get; set; }
}

/// <summary>Sidecar describing an image: what it covers, its hash and every unreadable range (ddrescue-style map).</summary>
public sealed class ImageManifest
{
    public string Format { get; set; } = "raw";
    public SourceIdentity Source { get; set; } = new();
    public long Start { get; set; }
    public long Length { get; set; }
    public long BytesWritten { get; set; }
    public bool Complete { get; set; }
    public bool PartialRange { get; set; }
    public string? Sha256 { get; set; }
    public List<long[]> UnreadableRanges { get; set; } = new();
    public DateTime Started { get; set; }
    public DateTime? Finished { get; set; }
    public string Notes { get; set; } = "";

    public static string PathFor(string image) => image + ".dvimage.json";
}

public sealed class ImagePreflight
{
    public long Required { get; set; }
    public DestinationInfo Destination { get; set; } = new();
    public List<string> Blockers { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool ResumeAvailable { get; set; }
    public long ResumeFrom { get; set; }
}

/// <summary>
/// Creates a sector-by-sector raw image of a source (read-only) on a separate destination, with a
/// map of unreadable sectors. Refuses to start without enough free space instead of silently
/// creating an incomplete image; interrupted images resume from the sidecar.
/// </summary>
public sealed class DiskImager
{
    private readonly IBlockSource _src;
    private readonly SourceIdentity _id;
    private readonly IDestinationInspector _inspector;
    public IReadOnlyCollection<string> SourceDisks { get; init; } = Array.Empty<string>();

    public DiskImager(IBlockSource src, SourceIdentity id, IDestinationInspector inspector)
    {
        _src = src; _id = id; _inspector = inspector;
    }

    public ImagePreflight Preflight(ImageRequest req)
    {
        var pf = new ImagePreflight();
        long len = req.Length < 0 ? _src.Length - req.Start : req.Length;
        var dir = Path.GetDirectoryName(Path.GetFullPath(req.ImagePath))!;
        pf.Destination = _inspector.Inspect(dir);
        var manifestPath = ImageManifest.PathFor(req.ImagePath);
        if (File.Exists(req.ImagePath))
        {
            var m = File.Exists(manifestPath) ? JsonSerializer.Deserialize<ImageManifest>(File.ReadAllText(manifestPath)) : null;
            if (m != null && !m.Complete && m.Source.Matches(_id) && m.Start == req.Start && m.Length == len && new FileInfo(req.ImagePath).Length >= m.BytesWritten)
            {
                pf.ResumeAvailable = true;
                pf.ResumeFrom = m.BytesWritten;
            }
            else pf.Blockers.Add("A file with this name already exists. DeepVault never overwrites files; choose a new name.");
        }
        pf.Required = len - pf.ResumeFrom + 1024 * 1024;
        if (pf.Destination.PhysicalDisks.Any(d => SourceDisks.Contains(d, StringComparer.OrdinalIgnoreCase)) && !req.AllowDestinationOnSource)
            pf.Blockers.Add("The image cannot be written to the disk being imaged.");
        if (pf.Destination.FreeBytes < pf.Required)
            pf.Blockers.Add($"Not enough space for the image: {Bytes.FormatSize(pf.Required)} needed, {Bytes.FormatSize(pf.Destination.FreeBytes)} free. " +
                            "An incomplete image would be misleading, so DeepVault will not start. Use a larger drive, or image only a partition/range (clearly labelled as partial).");
        if (len > pf.Destination.MaxFileSize)
            pf.Blockers.Add($"The destination file system ({pf.Destination.FileSystem}) cannot hold a single file of {Bytes.FormatSize(len)}. Use an NTFS or exFAT drive.");
        if (req.Start != 0 || len != _src.Length) pf.Warnings.Add("This is a PARTIAL image (a range of the source), not a full backup of the disk.");
        return pf;
    }

    public ImageManifest Run(ImageRequest req, IProgress<(long done, long total, long bad)>? progress, CancellationToken ct)
    {
        var pf = Preflight(req);
        if (pf.Blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", pf.Blockers));
        long len = req.Length < 0 ? _src.Length - req.Start : req.Length;
        var manifestPath = ImageManifest.PathFor(req.ImagePath);
        var bad = new List<long[]>();
        var sink = new ListRegionSink(bad, req.Start);
        var reader = new ResilientReader(_src, sink);
        ImageManifest m = pf.ResumeAvailable
            ? JsonSerializer.Deserialize<ImageManifest>(File.ReadAllText(manifestPath))!
            : new ImageManifest { Source = _id, Start = req.Start, Length = len, Started = DateTime.UtcNow, PartialRange = req.Start != 0 || len != _src.Length };
        bad.AddRange(m.UnreadableRanges);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FileStream outS;
        if (pf.ResumeAvailable)
        {
            outS = new FileStream(req.ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            // Re-hash what is already there so the final hash covers the whole image.
            var b = new byte[req.ChunkSize];
            long p = 0;
            while (p < m.BytesWritten)
            {
                int n = outS.Read(b, 0, (int)Math.Min(b.Length, m.BytesWritten - p));
                if (n <= 0) break;
                sha.AppendData(b, 0, n);
                p += n;
            }
            outS.SetLength(m.BytesWritten);
            outS.Position = m.BytesWritten;
        }
        else outS = new FileStream(req.ImagePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using (outS)
        {
            var buf = new byte[req.ChunkSize];
            long pos = m.BytesWritten;
            long lastSave = pos;
            try
            {
                while (pos < len)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buf.Length, len - pos);
                    int got = reader.Read(req.Start + pos, buf.AsSpan(0, want));
                    if (got < want) buf.AsSpan(got, want - got).Clear();
                    outS.Write(buf, 0, want);
                    sha.AppendData(buf, 0, want);
                    pos += want;
                    m.BytesWritten = pos;
                    progress?.Report((pos, len, reader.TotalBadSectors));
                    if (pos - lastSave >= 256L * 1024 * 1024)
                    {
                        outS.Flush(true);
                        m.UnreadableRanges = bad.ToList();
                        Save(manifestPath, m);
                        lastSave = pos;
                    }
                }
                outS.Flush(true);
                m.Complete = true;
                m.Finished = DateTime.UtcNow;
                m.Sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                m.Notes = bad.Count == 0 ? "All sectors were read successfully."
                    : $"{bad.Sum(x => x[1]) / 512} sectors were unreadable and are zero-filled in the image (see UnreadableRanges).";
            }
            catch (OperationCanceledException)
            {
                outS.Flush(true);
                m.Notes = "Imaging was interrupted; it can be resumed. The image is INCOMPLETE.";
            }
            catch (IOException e)
            {
                m.Notes = $"Imaging stopped: {e.Message}. The image is INCOMPLETE.";
            }
            m.UnreadableRanges = bad.ToList();
            Save(manifestPath, m);
        }
        return m;
    }

    private static void Save(string path, ImageManifest m)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }

    private sealed class ListRegionSink : IRegionSink
    {
        private readonly List<long[]> _l;
        private readonly long _base;
        public ListRegionSink(List<long[]> l, long b) { _l = l; _base = b; }
        public void Report(DiskRegion r)
        {
            if (r.Kind is not (RegionKind.Unreadable or RegionKind.SkippedAfterErrors)) return;
            lock (_l)
            {
                if (_l.Count > 0 && _l[^1][0] + _l[^1][1] == r.Offset - _base) _l[^1][1] += r.Length;
                else _l.Add(new[] { r.Offset - _base, r.Length });
            }
        }
    }
}
