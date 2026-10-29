using DeepVault.Core.IO;

namespace DeepVault.Core.Scanning;

[Flags]
public enum ScanMethods
{
    None = 0,
    PartitionAnalysis = 1,
    FileSystemMetadata = 2,
    LostPartitionSearch = 4,
    SignatureCarving = 8,
    OrphanMftRecords = 16,
    DeepScanAll = PartitionAnalysis | FileSystemMetadata | LostPartitionSearch | SignatureCarving | OrphanMftRecords,
    Quick = PartitionAnalysis | FileSystemMetadata,
}

public enum CarveScope
{
    /// <summary>Free space of recognised volumes plus every area outside them (where old/lost files can be).</summary>
    UnallocatedAndUnpartitioned = 0,
    /// <summary>Every sector, including space that belongs to existing files (slower, mostly finds existing files).</summary>
    EntireDisk = 1,
}

public sealed class ScanOptions
{
    public ScanMethods Methods { get; set; } = ScanMethods.DeepScanAll;
    public CarveScope Scope { get; set; } = CarveScope.UnallocatedAndUnpartitioned;
    /// <summary>Also list files that still exist on recognised volumes (normally they are reachable through Windows).</summary>
    public bool IncludeExistingFiles { get; set; }
    /// <summary>Carving format ids; null = registry defaults.</summary>
    public HashSet<string>? EnabledFormats { get; set; }
    public string? CustomSignaturesPath { get; set; }
    /// <summary>Metadata candidates up to this size are content-validated during the scan.</summary>
    public long ValidateUpTo { get; set; } = 64L * 1024 * 1024;
    /// <summary>Stop (resumably) when the drive returns many read errors, so a failing disk is not stressed.</summary>
    public bool StopWhenDriveFailing { get; set; } = true;
    public int BadSectorStopThreshold { get; set; } = 256;
    public int ConsecutiveBadSectorsBeforeSkip { get; set; } = 32;
    public long SkipBytesAfterErrors { get; set; } = 1024 * 1024;
    public long CheckpointInterval { get; set; } = 256L * 1024 * 1024;
    /// <summary>Sequential read size for the raw pass.</summary>
    public int ReadChunkSize { get; set; } = 4 * 1024 * 1024;

    public ReadPolicy ToReadPolicy() => new()
    {
        HealthWarningThreshold = BadSectorStopThreshold,
        ConsecutiveBadSectorsBeforeSkip = ConsecutiveBadSectorsBeforeSkip,
        SkipBytesAfterErrors = SkipBytesAfterErrors,
    };
}

/// <summary>Identification of the scanned source, stored with the project to prevent mixing up drives.</summary>
public sealed class SourceIdentity
{
    public string Kind { get; set; } = "Image"; // PhysicalDisk | Image | BlockDevice
    public string Path { get; set; } = "";
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? BusType { get; set; }
    public long Size { get; set; }
    public int SectorSize { get; set; } = 512;
    public string? PartitionStyle { get; set; }
    public string? DiskId { get; set; }
    public bool IsSystemDisk { get; set; }
    public string? ImageSha256 { get; set; }

    public string Describe() => Kind == "PhysicalDisk"
        ? $"{Model ?? "Disk"} ({Util.Bytes.FormatSize(Size)}){(string.IsNullOrEmpty(SerialNumber) ? "" : $", serial {SerialNumber}")}"
        : $"{System.IO.Path.GetFileName(Path)} ({Util.Bytes.FormatSize(Size)})";

    /// <summary>True when another identity plausibly refers to the same physical device/image.</summary>
    public bool Matches(SourceIdentity other) =>
        Size == other.Size && string.Equals(Kind, other.Kind, StringComparison.OrdinalIgnoreCase) &&
        (Kind != "PhysicalDisk" || (string.Equals(SerialNumber?.Trim(), other.SerialNumber?.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(Model?.Trim(), other.Model?.Trim(), StringComparison.OrdinalIgnoreCase)));
}

public sealed class ScanProgress
{
    public int PhaseIndex { get; set; }
    public int PhaseCount { get; set; }
    public string Phase { get; set; } = "";
    public string Detail { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; } = -1;
    public string Unit { get; set; } = "";
    public long FilesFound { get; set; }
    public double BytesPerSecond { get; set; }
    public TimeSpan Elapsed { get; set; }
    public long BadSectors { get; set; }
    public bool DriveHealthWarning { get; set; }
    public int LostPartitionsFound { get; set; }
    public long OrphanRecordsFound { get; set; }
}

public enum ScanOutcome { Completed, CompletedWithGaps, Cancelled, StoppedDriveHealth, Failed }

public sealed class ScanSummary
{
    public ScanOutcome Outcome { get; set; }
    public TimeSpan Duration { get; set; }
    public List<string> MethodsRun { get; } = new();
    public List<string> MethodsSkipped { get; } = new();
    public long FilesFound { get; set; }
    public long BadSectors { get; set; }
    public long BytesScannedRaw { get; set; }
    public long BytesPlannedRaw { get; set; }
    public long BytesExcludedByScope { get; set; }
    public int LostPartitions { get; set; }
    public int Correlated { get; set; }
    public string? Error { get; set; }
    public List<string> Findings { get; } = new();

    /// <summary>A scan is only exhaustive for its scope when every planned phase ran to the end without unreadable areas.</summary>
    public bool IsExhaustive => Outcome == ScanOutcome.Completed;
}
