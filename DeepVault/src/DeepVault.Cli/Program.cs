using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Engines;
using DeepVault.Core.Imaging;
using DeepVault.Core.IO;
using DeepVault.Core.Restore;
using DeepVault.Core.Scanning;
using DeepVault.Core.Util;
using DeepVault.Platform.Windows;

namespace DeepVault.Cli;

/// <summary>DeepVault command-line interface. Every operation on the source is read-only.</summary>
public static class Program
{
    private const string Usage = @"DeepVault command line (read-only data recovery)

  deepvault-cli disks
  deepvault-cli scan <image-file | disk:N> --project <file.dvproj> [--quick] [--scope all] [--include-existing]
                    [--no-carving] [--formats jpeg,png,...] [--resume]
  deepvault-cli list <file.dvproj> [--view all|recoverable|partial|uncertain|failed|duplicates] [--search text]
                    [--category Image] [--limit 50]
  deepvault-cli restore <file.dvproj> --dest <folder> [--source <image|disk:N>] [--ids 1,2,3 | --search text | --view recoverable]
                    [--flat] [--skip-existing]
  deepvault-cli image <image-file | disk:N> --out <file.img> [--start BYTES --length BYTES]
  deepvault-cli summary <file.dvproj>
  deepvault-cli scan-report <file.dvproj> --out <report.html>
  deepvault-cli formats | engines

Sources are opened read-only. Restores and images are never written to the source disk.";

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        try
        {
            return args[0] switch
            {
                "disks" => Disks(),
                "scan" => Scan(args),
                "list" => List(args),
                "restore" => Restore(args),
                "image" => Image(args),
                "summary" => Summary(args),
                "scan-report" => ScanReportCmd(args),
                "formats" => Formats(),
                "engines" => Engines(),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            return Fail(e.Message);
        }
    }

    private static int Fail(string msg) { Console.Error.WriteLine("error: " + msg); return 1; }

    private static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static bool Flag(string[] a, string name) => a.Contains(name);

    private static IDiskEnumerator Enumerator() => OperatingSystem.IsWindows() ? new WindowsDiskEnumerator() : new LinuxDiskEnumerator();
    private static IDestinationInspector Inspector() => OperatingSystem.IsWindows() ? new WindowsDestinationInspector() : new BasicDestinationInspector();

    private static int Disks()
    {
        var e = Enumerator();
        Console.WriteLine(e.IsElevated ? "Running with administrator rights." : "Not elevated: disks can be listed but reading them requires administrator/root.");
        foreach (var d in e.Enumerate())
        {
            Console.WriteLine($"\n{d.DisplayName}  [{d.DevicePath}]");
            Console.WriteLine($"   bus {d.BusType ?? "?"}, serial {d.SerialNumber ?? "?"}, sector {d.SectorSize}/{d.PhysicalSectorSize}{(d.Removable ? ", removable" : "")}{(d.IsSystemDisk ? ", SYSTEM DISK (Windows runs from it)" : "")}{(d.FailurePredicted == true ? ", SMART PREDICTS FAILURE" : "")}");
            foreach (var v in d.Volumes) Console.WriteLine($"   - {v.Describe()} @ {Bytes.FormatSize(v.Offset)} size {Bytes.FormatSize(v.Length)} free {Bytes.FormatSize(v.FreeBytes)}");
        }
        return 0;
    }

    /// <summary>Opens "disk:N" (physical disk, read-only) or an image file.</summary>
    private static (IBlockSource src, SourceIdentity id, string[] diskIds) OpenSource(string spec)
    {
        if (spec.StartsWith("disk:", StringComparison.OrdinalIgnoreCase))
        {
            int n = int.Parse(spec[5..]);
            var e = Enumerator();
            var d = e.Enumerate().FirstOrDefault(x => x.Number == n) ?? throw new ArgumentException($"Disk {n} not found.");
            var src = e.OpenReadOnly(d);
            return (src, new SourceIdentity
            {
                Kind = "PhysicalDisk", Path = d.DevicePath, Model = d.Model, SerialNumber = d.SerialNumber, BusType = d.BusType,
                Size = src.Length, SectorSize = src.SectorSize, IsSystemDisk = d.IsSystemDisk,
            }, new[] { d.Id });
        }
        var img = ImageOpener.Open(spec);
        return (img, new SourceIdentity { Kind = "Image", Path = Path.GetFullPath(spec), Size = img.Length, SectorSize = img.SectorSize }, Array.Empty<string>());
    }

    private static int Scan(string[] a)
    {
        if (a.Length < 2) return Fail("scan: missing source");
        var project = Opt(a, "--project") ?? Path.ChangeExtension(Path.GetFileName(a[1].Replace(':', '_')), ProjectDatabase.FileExtension);
        var (src, id, _) = OpenSource(a[1]);
        using var _src = src;
        if (id.IsSystemDisk)
            Console.WriteLine("WARNING: this is the disk Windows is running from. Windows and other programs keep writing to it during the scan, which can overwrite recoverable data. Prefer scanning from another computer or an image.");
        bool resume = File.Exists(project);
        if (resume && !Flag(a, "--resume")) return Fail($"{project} exists. Use --resume to continue that scan, or choose another --project.");
        using var db = resume ? ProjectDatabase.Open(project) : ProjectDatabase.Create(project);
        if (resume)
        {
            var saved = db.GetMetaJson<SourceIdentity>("source");
            if (saved != null && !saved.Matches(id)) return Fail("This project belongs to a different source (size/model/serial differ).");
        }
        else db.SetMetaJson("source", id);
        var opt = resume && db.GetMetaJson<ScanOptions>("scan_options") is { } so ? so : new ScanOptions
        {
            Methods = Flag(a, "--quick") ? ScanMethods.Quick : ScanMethods.DeepScanAll,
            Scope = Opt(a, "--scope") == "all" ? CarveScope.EntireDisk : CarveScope.UnallocatedAndUnpartitioned,
            IncludeExistingFiles = Flag(a, "--include-existing"),
            EnabledFormats = Opt(a, "--formats")?.Split(',').ToHashSet(),
        };
        if (Flag(a, "--no-carving")) opt.Methods &= ~ScanMethods.SignatureCarving;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping at the next safe point (the scan can be resumed)..."); };
        var last = DateTime.MinValue;
        var sum = new ScanOrchestrator(src, db, opt, new Core.Logging.FileLog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "logs"))).Run(new SyncProgress<ScanProgress>(p =>
        {
            if ((DateTime.Now - last).TotalMilliseconds < 500) return;
            last = DateTime.Now;
            string pct = p.Total > 0 ? $"{100.0 * p.Done / p.Total:0.0}%" : "(total unknown)";
            string rate = p.Unit == "bytes" && p.BytesPerSecond > 0 ? $" {Bytes.FormatSize((long)p.BytesPerSecond)}/s" : "";
            Console.Write($"\r[{p.PhaseIndex + 1}/{p.PhaseCount}] {p.Phase}: {pct}{rate}  found {p.FilesFound}  bad sectors {p.BadSectors}      ");
        }), cts.Token);
        Console.WriteLine($"\n\nResult: {sum.Outcome} in {sum.Duration:hh\\:mm\\:ss}. {sum.FilesFound} candidates. Unreadable sectors: {sum.BadSectors}.");
        if (!sum.IsExhaustive) Console.WriteLine("NOTE: the scan is NOT exhaustive (cancelled, stopped, or areas were unreadable). See 'summary'.");
        return sum.Outcome is ScanOutcome.Failed ? 2 : 0;
    }

    private static FileQuery QueryFrom(string[] a)
    {
        var q = new FileQuery { Search = Opt(a, "--search"), Limit = int.TryParse(Opt(a, "--limit"), out var l) ? l : 50 };
        if (Opt(a, "--view") is { } v) q.View = Enum.Parse<ResultView>(v, true);
        if (Opt(a, "--category") is { } c) q.Category = Enum.Parse<FileCategory>(c, true);
        return q;
    }

    private static int List(string[] a)
    {
        using var db = ProjectDatabase.Open(a[1]);
        var q = QueryFrom(a);
        Console.WriteLine($"{db.CountFiles(q)} matching candidates (showing up to {q.Limit}):");
        foreach (var f in db.QueryFiles(q))
            Console.WriteLine($"#{f.Id,-7} {f.Confidence,-7} {f.Structure,-10} {Bytes.FormatSize(f.Size),10}  {f.DisplayPath}  [{f.Engines}]");
        return 0;
    }

    private static int Restore(string[] a)
    {
        using var db = ProjectDatabase.Open(a[1]);
        var dest = Opt(a, "--dest") ?? throw new ArgumentException("restore: --dest is required");
        var saved = db.GetMetaJson<SourceIdentity>("source") ?? throw new InvalidDataException("Project has no source identity.");
        string spec = Opt(a, "--source") ?? (saved.Kind == "Image" ? saved.Path : throw new ArgumentException("restore: specify --source disk:N for a physical disk."));
        var (src, id, diskIds) = OpenSource(spec);
        using var _ = src;
        if (!saved.Matches(id)) return Fail("The given source does not match the scanned one (size/model/serial). Refusing to restore from a different disk.");
        List<FoundFile> files;
        if (Opt(a, "--ids") is { } ids) files = db.GetFiles(ids.Split(',').Select(long.Parse));
        else { var q = QueryFrom(a); q.Limit = int.MaxValue; files = db.QueryFiles(q); }
        var svc = new RestoreService(src, id, Inspector(), db) { SourceDisks = diskIds };
        var req = new RestoreRequest { Files = files, DestinationFolder = dest, PreserveFolders = !Flag(a, "--flat"), Collisions = Flag(a, "--skip-existing") ? CollisionPolicy.Skip : CollisionPolicy.RenameNew };
        var pf = svc.Preflight(req);
        Console.WriteLine($"{pf.FileCount} files, {Bytes.FormatSize(pf.DataBytes)}; destination '{pf.Destination.Label}' {pf.Destination.FileSystem} has {Bytes.FormatSize(pf.Destination.FreeBytes)} free.");
        foreach (var w in pf.Warnings) Console.WriteLine("warning: " + w);
        foreach (var (f, why) in pf.NotRestorable) Console.WriteLine($"cannot restore #{f.Id} {f.Name}: {why}");
        if (!pf.CanProceed) { foreach (var b in pf.Blockers) Console.Error.WriteLine("blocked: " + b); return 3; }
        var res = svc.Run(req, null, CancellationToken.None);
        Console.WriteLine($"Done: {res.Count(RestoreOutcome.Verified)} verified, {res.Count(RestoreOutcome.RestoredWithWarnings)} with warnings, {res.Count(RestoreOutcome.Failed)} failed, {res.Count(RestoreOutcome.Skipped)} skipped, {res.Count(RestoreOutcome.NotAttempted)} not attempted.");
        Console.WriteLine($"Files and report: {res.SessionFolder}");
        return res.Count(RestoreOutcome.Failed) > 0 ? 4 : 0;
    }

    private static int Image(string[] a)
    {
        var (src, id, diskIds) = OpenSource(a[1]);
        using var _ = src;
        var req = new ImageRequest
        {
            ImagePath = Opt(a, "--out") ?? throw new ArgumentException("image: --out is required"),
            Start = long.TryParse(Opt(a, "--start"), out var s) ? s : 0,
            Length = long.TryParse(Opt(a, "--length"), out var l) ? l : -1,
        };
        var imager = new DiskImager(src, id, Inspector()) { SourceDisks = diskIds };
        var pf = imager.Preflight(req);
        foreach (var w in pf.Warnings) Console.WriteLine("warning: " + w);
        if (pf.Blockers.Count > 0) { foreach (var b in pf.Blockers) Console.Error.WriteLine("blocked: " + b); return 3; }
        if (pf.ResumeAvailable) Console.WriteLine($"Resuming interrupted image at {Bytes.FormatSize(pf.ResumeFrom)}.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var m = imager.Run(req, new SyncProgress<(long done, long total, long bad)>(p => Console.Write($"\r{100.0 * p.done / p.total:0.0}%  bad sectors {p.bad}   ")), cts.Token);
        Console.WriteLine($"\n{(m.Complete ? "Complete" : "INCOMPLETE")}: {m.Notes}\nSHA-256: {m.Sha256 ?? "(not computed: incomplete)"}");
        return m.Complete ? 0 : 5;
    }

    private static int Summary(string[] a)
    {
        using var db = ProjectDatabase.Open(a[1]);
        var src = db.GetMetaJson<SourceIdentity>("source");
        Console.WriteLine($"Source: {src?.Describe()}\nStatus: {db.GetMeta("scan_status")}");
        foreach (var p in db.LoadPartitions()) Console.WriteLine($"  partition: {p.Status} {p.FileSystem} @{p.Offset} {Bytes.FormatSize(p.Length)} {p.Label} {p.Evidence}");
        foreach (ResultView v in Enum.GetValues<ResultView>()) Console.WriteLine($"  {v}: {db.CountView(v)}");
        foreach (var r in db.GetRegions()) Console.WriteLine($"  region: {r.Kind} @{r.Offset} {Bytes.FormatSize(r.Length)} {r.Note}");
        foreach (var (t, i) in db.GetIssues().Where(x => x.issue.Severity >= IssueSeverity.Warning)) Console.WriteLine($"  {i.Severity}: {i.Message}");
        return 0;
    }

    private static int ScanReportCmd(string[] a)
    {
        using var db = ProjectDatabase.Open(a[1]);
        var o = Opt(a, "--out") ?? throw new ArgumentException("scan-report: --out is required");
        Core.Scanning.ScanReport.Write(db, o);
        Console.WriteLine($"Written {o}");
        return 0;
    }

    private static int Formats()
    {
        foreach (var f in SignatureRegistry.Create().Formats)
            Console.WriteLine($"{f.Id,-10} {f.Name,-48} .{f.DefaultExtension,-6} {(f.HasChecksums ? "checksums" : ""),-10} {(f.EnabledByDefault ? "" : "(opt-in)")}");
        return 0;
    }

    private static int Engines()
    {
        foreach (var e in EngineRegistry.All) Console.WriteLine($"{e.Name}\n  {e.Summary}\n  Can: {e.Capabilities}\n  Cannot: {e.Limitations}\n");
        return 0;
    }
}
