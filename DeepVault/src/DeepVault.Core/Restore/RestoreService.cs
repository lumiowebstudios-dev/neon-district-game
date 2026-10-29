using System.Security.Cryptography;
using System.Text.Json;
using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.IO;
using DeepVault.Core.Logging;
using DeepVault.Core.Scanning;
using DeepVault.Core.Util;

namespace DeepVault.Core.Restore;

public enum CollisionPolicy { RenameNew = 0, Skip = 1 }

public enum RestoreOutcome { Verified, RestoredWithWarnings, Skipped, Failed, NotAttempted }

public sealed class RestoreRequest
{
    public List<FoundFile> Files { get; set; } = new();
    public string DestinationFolder { get; set; } = "";
    /// <summary>Write into a new "DeepVault Recovery &lt;date&gt;" folder (recommended: nothing existing can collide).</summary>
    public bool CreateSessionFolder { get; set; } = true;
    public bool PreserveFolders { get; set; } = true;
    public CollisionPolicy Collisions { get; set; } = CollisionPolicy.RenameNew;
    public bool Verify { get; set; } = true;
    public bool SetTimestamps { get; set; } = true;
    /// <summary>Advanced override: only an explicit user choice may allow writing to the scanned disk.</summary>
    public bool AllowDestinationOnSource { get; set; }
}

public sealed class RestorePreflight
{
    public DestinationInfo Destination { get; set; } = new();
    public long RequiredBytes { get; set; }
    public long DataBytes { get; set; }
    public int FileCount { get; set; }
    public bool EnoughSpace => Destination.FreeBytes >= RequiredBytes;
    public bool DestinationOnSourceDisk { get; set; }
    public List<string> Blockers { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<(FoundFile file, string reason)> NotRestorable { get; } = new();
    public bool CanProceed => Blockers.Count == 0;
}

public sealed class RestoreItemResult
{
    public long FileId { get; set; }
    public string OriginalPath { get; set; } = "";
    public string? TargetPath { get; set; }
    public long ExpectedSize { get; set; }
    public long WrittenBytes { get; set; }
    public string? SourceSha256 { get; set; }
    public string? DestinationSha256 { get; set; }
    public RestoreOutcome Outcome { get; set; }
    public string? Structure { get; set; }
    public string Message { get; set; } = "";
}

public sealed class RestoreProgress
{
    public int FilesDone { get; set; }
    public int FilesTotal { get; set; }
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public string CurrentName { get; set; } = "";
}

public sealed class RestoreResult
{
    public long RunId { get; set; }
    public string SessionFolder { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public List<RestoreItemResult> Items { get; } = new();
    public bool StoppedDestinationFull { get; set; }
    public bool Cancelled { get; set; }
    public string? ReportHtml { get; set; }
    public string? ReportCsv { get; set; }
    public int Count(RestoreOutcome o) => Items.Count(i => i.Outcome == o);
}

/// <summary>Abstraction over destination writes so tests can simulate a full disk.</summary>
public interface IRestoreTarget
{
    Stream CreateNew(string path);
    void MoveNoOverwrite(string from, string to);
    bool Exists(string path);
    void CreateDirectory(string path);
    void Delete(string path);
    Stream OpenRead(string path);
    void SetTimes(string path, DateTime? created, DateTime? modified, DateTime? accessed);
}

public sealed class FileSystemRestoreTarget : IRestoreTarget
{
    public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
    public void MoveNoOverwrite(string from, string to) => File.Move(from, to, overwrite: false);
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void Delete(string path) { if (File.Exists(path)) File.Delete(path); }
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
    public void SetTimes(string path, DateTime? c, DateTime? m, DateTime? a)
    {
        try
        {
            if (c != null) File.SetCreationTimeUtc(path, c.Value);
            if (m != null) File.SetLastWriteTimeUtc(path, m.Value);
            if (a != null) File.SetLastAccessTimeUtc(path, a.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
    }
}

/// <summary>
/// Restores selected candidates to a separate destination. It never overwrites, deletes, formats or
/// repartitions anything: every file is created new (FileMode.CreateNew) inside a fresh session folder,
/// written to a temporary name first, re-read and SHA-256-compared with what was read from the source.
/// </summary>
public sealed class RestoreService
{
    private readonly IBlockSource _source;
    private readonly SourceIdentity _sourceId;
    private readonly IDestinationInspector _inspector;
    private readonly ProjectDatabase? _db;
    private readonly IRestoreTarget _target;
    private readonly ILog _log;
    /// <summary>Physical disk ids that make up the scanned source (e.g. "PhysicalDrive0").</summary>
    public IReadOnlyCollection<string> SourceDisks { get; init; } = Array.Empty<string>();

    public RestoreService(IBlockSource source, SourceIdentity sourceId, IDestinationInspector inspector, ProjectDatabase? db, IRestoreTarget? target = null, ILog? log = null)
    {
        _source = source; _sourceId = sourceId; _inspector = inspector; _db = db; _target = target ?? new FileSystemRestoreTarget(); _log = log ?? NullLog.Instance;
    }

    public RestorePreflight Preflight(RestoreRequest req)
    {
        var pf = new RestorePreflight();
        if (string.IsNullOrWhiteSpace(req.DestinationFolder)) { pf.Blockers.Add("Choose a destination folder."); return pf; }
        var dest = _inspector.Inspect(req.DestinationFolder);
        pf.Destination = dest;
        if (dest.TotalBytes == 0) pf.Blockers.Add("The destination drive could not be read. Check that it is connected.");

        // Same physical disk as the source?
        bool onSource = dest.PhysicalDisks.Any(d => SourceDisks.Contains(d, StringComparer.OrdinalIgnoreCase));
        if (_sourceId.Kind == "Image" && !string.IsNullOrEmpty(_sourceId.Path))
        {
            var imgInfo = _inspector.Inspect(Path.GetDirectoryName(Path.GetFullPath(_sourceId.Path))!);
            if (imgInfo.VolumeRoot.Length > 0 && string.Equals(imgInfo.VolumeRoot, dest.VolumeRoot, StringComparison.OrdinalIgnoreCase))
                pf.Warnings.Add("The destination is on the same drive as the disk image. This does not change the image file, but keep enough free space.");
        }
        pf.DestinationOnSourceDisk = onSource;
        if (onSource && !req.AllowDestinationOnSource)
            pf.Blockers.Add("The destination is on the disk being recovered. Writing there can overwrite the very data you are trying to recover. Choose a folder on a different drive (for example your USB drive).");
        else if (onSource)
            pf.Warnings.Add("DANGER: restoring to the source disk was explicitly allowed. This can permanently overwrite recoverable data.");

        int cs = Math.Max(512, dest.ClusterSize);
        foreach (var f in req.Files)
        {
            if (f.State == RecoveryState.Failed || (f.Size > 0 && f.Layout.Kind == LayoutKind.Extents && f.Layout.Extents.Count == 0))
            {
                pf.NotRestorable.Add((f, "content location unknown"));
                continue;
            }
            if (f.Size > dest.MaxFileSize)
            {
                pf.NotRestorable.Add((f, $"larger than the {dest.FileSystem} file-size limit (4 GB) of the destination"));
                continue;
            }
            if (f.Size > _source.Length)
            {
                pf.NotRestorable.Add((f, "declared size exceeds the source size (corrupt metadata)"));
                continue;
            }
            pf.FileCount++;
            pf.DataBytes += f.Size;
            pf.RequiredBytes += (f.Size + cs - 1) / cs * cs + cs; // data rounded to clusters + directory overhead
        }
        pf.RequiredBytes += 4L * 1024 * 1024; // reports
        if (pf.FileCount == 0) pf.Blockers.Add("None of the selected files can be restored.");
        if (dest.TotalBytes > 0 && !pf.EnoughSpace)
            pf.Blockers.Add($"Not enough free space: {Bytes.FormatSize(pf.RequiredBytes)} needed, {Bytes.FormatSize(dest.FreeBytes)} available. Select fewer files or choose another destination. Existing files on the destination are never deleted to make room.");
        if (req.Files.Any(f => f.Structure == StructureCheck.Damaged || f.State == RecoveryState.PartialOrDamaged))
            pf.Warnings.Add("Some selected files are damaged or partially overwritten; they will be restored as-is and marked in the report.");
        if (req.Files.Any(f => f.Notes.Contains("EFS-encrypted")))
            pf.Warnings.Add("Some files are EFS-encrypted; restored copies remain encrypted and need the original Windows account's key.");
        return pf;
    }

    public RestoreResult Run(RestoreRequest req, IProgress<RestoreProgress>? progress, CancellationToken ct)
    {
        var pf = Preflight(req);
        if (!pf.CanProceed) throw new InvalidOperationException(string.Join(" ", pf.Blockers));
        var result = new RestoreResult { Started = DateTime.UtcNow };
        string root = req.DestinationFolder;
        if (req.CreateSessionFolder)
        {
            string baseName = $"DeepVault Recovery {DateTime.Now:yyyy-MM-dd HHmm}";
            root = Path.Combine(req.DestinationFolder, baseName);
            for (int n = 2; _target.Exists(root); n++) root = Path.Combine(req.DestinationFolder, $"{baseName} ({n})");
        }
        _target.CreateDirectory(root);
        result.SessionFolder = root;
        result.RunId = _db?.BeginRestoreRun(root) ?? 0;
        _log.Info($"Restore started: {pf.FileCount} files, {pf.DataBytes} bytes");

        var reader = new ResilientReader(_source, null);
        var restorable = req.Files.Where(f => !pf.NotRestorable.Any(n => n.file.Id == f.Id && ReferenceEquals(n.file, f))).ToList();
        foreach (var (f, why) in pf.NotRestorable)
            Record(result, new RestoreItemResult { FileId = f.Id, OriginalPath = f.DisplayPath, ExpectedSize = f.Size, Outcome = RestoreOutcome.Skipped, Message = why });

        var prog = new RestoreProgress { FilesTotal = restorable.Count, BytesTotal = restorable.Sum(f => f.Size) };
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < restorable.Count; i++)
        {
            var f = restorable[i];
            if (ct.IsCancellationRequested || result.StoppedDestinationFull)
            {
                result.Cancelled |= ct.IsCancellationRequested;
                Record(result, new RestoreItemResult { FileId = f.Id, OriginalPath = f.DisplayPath, ExpectedSize = f.Size, Outcome = RestoreOutcome.NotAttempted,
                    Message = result.StoppedDestinationFull ? "destination became full" : "restore cancelled" });
                continue;
            }
            prog.CurrentName = f.Name;
            progress?.Report(prog);
            var item = RestoreOne(f, root, req, reader, usedNames, prog, progress, ct);
            Record(result, item);
            if (item.Message.StartsWith("Destination is full")) result.StoppedDestinationFull = true;
            prog.FilesDone++;
            progress?.Report(prog);
        }
        result.Finished = DateTime.UtcNow;
        try
        {
            (result.ReportHtml, result.ReportCsv) = RecoveryReport.Write(root, result, _sourceId, pf.Destination, _target);
        }
        catch (IOException e) { _log.Warn($"Could not write report: {e.Message}"); }
        _db?.FinishRestoreRun(result.RunId, JsonSerializer.Serialize(new
        {
            verified = result.Count(RestoreOutcome.Verified), warnings = result.Count(RestoreOutcome.RestoredWithWarnings),
            skipped = result.Count(RestoreOutcome.Skipped), failed = result.Count(RestoreOutcome.Failed), notAttempted = result.Count(RestoreOutcome.NotAttempted),
            full = result.StoppedDestinationFull, cancelled = result.Cancelled, report = result.ReportHtml,
        }));
        _log.Info($"Restore finished: verified={result.Count(RestoreOutcome.Verified)} warnings={result.Count(RestoreOutcome.RestoredWithWarnings)} failed={result.Count(RestoreOutcome.Failed)}");
        return result;
    }

    private void Record(RestoreResult r, RestoreItemResult item)
    {
        r.Items.Add(item);
        _db?.AddRestoreItem(new RestoreItemRecord(r.RunId, item.FileId, item.Outcome.ToString(), item.TargetPath, item.WrittenBytes, item.DestinationSha256, item.Message));
        if (_db != null && item.FileId > 0)
        {
            switch (item.Outcome)
            {
                case RestoreOutcome.Verified: _db.UpdateState(item.FileId, RecoveryState.RestoreVerified); break;
                case RestoreOutcome.RestoredWithWarnings: _db.UpdateState(item.FileId, RecoveryState.Restored, null, item.Message); break;
            }
        }
    }

    private static bool IsDiskFull(IOException e) =>
        (e.HResult & 0xFFFF) is 0x70 or 0x27 || e.HResult == 28 || e.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase);

    private RestoreItemResult RestoreOne(FoundFile f, string root, RestoreRequest req, ResilientReader reader, HashSet<string> used,
        RestoreProgress prog, IProgress<RestoreProgress>? progress, CancellationToken ct)
    {
        var item = new RestoreItemResult { FileId = f.Id, OriginalPath = f.DisplayPath, ExpectedSize = f.Size };
        // Target folder: <root>\<volume>\<original path>  or <root>\<category>
        var parts = new List<string>();
        if (req.PreserveFolders)
        {
            if (!string.IsNullOrEmpty(f.VolumeLabel) && f.Engines != Catalog.EngineId.SignatureCarving) parts.Add(SafeNames.Component(f.VolumeLabel!, 60));
            parts.AddRange(SafeNames.PathComponents(f.Path));
        }
        else parts.Add(FileTypes.CategoryName(f.Category));
        string dir = parts.Aggregate(root, Path.Combine);
        string name = SafeNames.Component(f.Name);
        string target = Path.Combine(dir, name);
        for (int n = 2; used.Contains(target) || _target.Exists(target); n++)
        {
            if (req.Collisions == CollisionPolicy.Skip && _target.Exists(target))
            {
                item.Outcome = RestoreOutcome.Skipped;
                item.Message = "a file with this name already exists at the destination (skip policy)";
                return item;
            }
            target = Path.Combine(dir, SafeNames.WithSuffix(name, n));
        }
        used.Add(target);
        string temp = target + ".dvpart";
        for (int n = 2; _target.Exists(temp); n++) temp = target + $".{n}.dvpart";
        try
        {
            _target.CreateDirectory(dir);
            string srcHash;
            long written = 0;
            bool readErrors;
            string? layoutError;
            using (var cs = new ContentStream(reader, f.Layout))
            using (var outS = _target.CreateNew(temp))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buf = new byte[1 << 20];
                int nRead;
                while ((nRead = cs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.AppendData(buf, 0, nRead);
                    outS.Write(buf, 0, nRead);
                    written += nRead;
                    prog.BytesDone += nRead;
                    progress?.Report(prog);
                }
                outS.Flush();
                if (outS is FileStream fs) fs.Flush(true);
                srcHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                readErrors = cs.HadReadErrors;
                layoutError = cs.LayoutError;
            }
            _target.MoveNoOverwrite(temp, target);
            item.TargetPath = target;
            item.WrittenBytes = written;
            item.SourceSha256 = srcHash;
            if (req.SetTimestamps) _target.SetTimes(target, f.Created, f.Modified, f.Accessed);

            var warnings = new List<string>();
            if (written != f.Size) warnings.Add($"only {written} of {f.Size} bytes could be reconstructed");
            if (readErrors) warnings.Add("some source sectors were unreadable and are zero-filled in the restored file");
            if (layoutError != null) warnings.Add(layoutError);
            if (req.Verify)
            {
                using var back = _target.OpenRead(target);
                item.DestinationSha256 = Convert.ToHexString(SHA256.HashData(back)).ToLowerInvariant();
                if (item.DestinationSha256 != srcHash)
                {
                    item.Outcome = RestoreOutcome.Failed;
                    item.Message = "verification failed: the file read back from the destination differs from what was written (destination problem)";
                    return item;
                }
                back.Position = 0;
                var v = FormatValidator.Validate(new StreamCarveSource(back), f.Extension);
                item.Structure = v.Check.ToString();
                if (v.Check is StructureCheck.Damaged or StructureCheck.HeaderOnly && f.Category != FileCategory.Text)
                    warnings.Add($"structure check: {v.Check}{(v.Note != null ? " – " + v.Note : "")}");
            }
            if (f.State == RecoveryState.PartialOrDamaged || f.Confidence == Confidence.Low)
                warnings.Add("the scan had already marked this candidate as damaged or uncertain");
            item.Outcome = warnings.Count == 0 && req.Verify ? RestoreOutcome.Verified : RestoreOutcome.RestoredWithWarnings;
            item.Message = warnings.Count == 0
                ? (req.Verify ? "written and read back: SHA-256 matches the data read from the source" : "written (not verified)")
                : string.Join("; ", warnings);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(temp);
            item.Outcome = RestoreOutcome.NotAttempted;
            item.Message = "restore cancelled";
        }
        catch (IOException e) when (IsDiskFull(e))
        {
            SafeDelete(temp);
            item.Outcome = RestoreOutcome.Failed;
            item.Message = "Destination is full: partial file removed; remaining files were not attempted";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SafeDelete(temp);
            item.Outcome = RestoreOutcome.Failed;
            item.Message = $"write failed: {e.Message}";
        }
        return item;
    }

    private void SafeDelete(string tempPath)
    {
        // Only ever deletes DeepVault's own temporary file, never anything else on the destination.
        if (!tempPath.EndsWith(".dvpart", StringComparison.Ordinal)) return;
        try { _target.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
