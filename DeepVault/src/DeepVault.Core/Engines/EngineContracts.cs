using DeepVault.Core.Catalog;
using DeepVault.Core.IO;

namespace DeepVault.Core.Engines;

public enum IssueSeverity { Info = 0, Warning = 1, Error = 2 }

public sealed record ScanIssue(IssueSeverity Severity, EngineId Engine, string Message, long? Offset = null);

/// <summary>Where engines deliver their findings. Implemented by the project database and by in-memory sinks for tests.</summary>
public interface IScanSink : IRegionSink
{
    void AddFile(FoundFile file);
    void AddIssue(ScanIssue issue);
    /// <summary>Fills in Path for files of a scan unit that were added with a ParentKey and no Path.</summary>
    void ResolvePaths(int scanUnit, Func<long, string> resolveParentKey);
    /// <summary>Makes everything added so far durable (a checkpoint boundary).</summary>
    void Commit();
}

public sealed class EngineProgress
{
    public string Phase { get; set; } = "";
    public string Detail { get; set; } = "";
    public long Done { get; set; }
    /// <summary>-1 when the total cannot be estimated honestly.</summary>
    public long Total { get; set; } = -1;
    public string Unit { get; set; } = "bytes";
    public long FilesFound { get; set; }
}

/// <summary>Static description of an engine for the registry/UI.</summary>
public sealed record EngineDescriptor(
    EngineId Id,
    string Name,
    string Summary,
    string Capabilities,
    string Limitations,
    string RequiredPermissions,
    string ProgressReporting,
    bool SupportsResume);

/// <summary>Simple in-memory sink (tests, small CLI runs).</summary>
public sealed class ListSink : IScanSink
{
    public List<FoundFile> Files { get; } = new();
    public List<ScanIssue> Issues { get; } = new();
    public List<DiskRegion> Regions { get; } = new();

    public void AddFile(FoundFile file) { lock (Files) { file.Id = Files.Count + 1; Files.Add(file); } }
    public void AddIssue(ScanIssue issue) { lock (Issues) Issues.Add(issue); }
    public void Report(DiskRegion region) { lock (Regions) Regions.Add(region); }
    public void Commit() { }

    public void ResolvePaths(int scanUnit, Func<long, string> resolve)
    {
        foreach (var f in Files)
            if (f.ScanUnit == scanUnit && f.Path == null && f.ParentKey >= 0)
                f.Path = resolve(f.ParentKey);
    }
}
