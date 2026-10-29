using DeepVault.Core.Content;

namespace DeepVault.Core.Catalog;

[Flags]
public enum EngineId
{
    None = 0,
    PartitionScan = 1,
    NtfsMft = 2,
    FatDirectory = 4,
    ExFatDirectory = 8,
    SignatureCarving = 16,
    OrphanMftRecords = 32,
}

/// <summary>Lifecycle of a candidate. A detected file is NOT a recovered file.</summary>
public enum RecoveryState
{
    CandidateDetected = 0,
    MetadataRecovered = 1,
    PreviewGenerated = 2,
    ContentReadable = 3,
    Restored = 4,
    RestoreVerified = 5,
    PartialOrDamaged = 6,
    Failed = 7,
}

public enum Confidence { Unknown = 0, Low = 1, Medium = 2, High = 3 }

/// <summary>Result of format-specific structural validation of the content.</summary>
public enum StructureCheck
{
    NotChecked = 0,
    /// <summary>The format's internal structure (lengths, checksums, end markers) is consistent.</summary>
    Verified = 1,
    /// <summary>Header and basic fields are consistent but the format offers no stronger check.</summary>
    Plausible = 2,
    /// <summary>Only a header was recognised; the end of the file had to be estimated.</summary>
    HeaderOnly = 3,
    /// <summary>Structural errors found: truncated, fragmented or overwritten.</summary>
    Damaged = 4,
    Unsupported = 5,
}

public enum FileCategory
{
    Other = 0, Image, RawPhoto, Video, Audio, Document, Spreadsheet, Presentation, Pdf, Text, Archive, Database, Email, Executable, SourceCode,
}

/// <summary>A candidate file produced by any recovery engine.</summary>
public sealed class FoundFile
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Original directory path (null when unknown, e.g. carved files).</summary>
    public string? Path { get; set; }
    public string Extension { get; set; } = "";
    public FileCategory Category { get; set; }
    public long Size { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? Accessed { get; set; }
    public EngineId Engines { get; set; }
    /// <summary>Technical description of where the candidate came from (no file contents).</summary>
    public string SourceDetail { get; set; } = "";
    public string? VolumeLabel { get; set; }
    public long VolumeOffset { get; set; } = -1;
    public bool? IsDeleted { get; set; }
    public RecoveryState State { get; set; }
    public Confidence Confidence { get; set; }
    public StructureCheck Structure { get; set; }
    /// <summary>Fraction (0..1) of the file's clusters now allocated to other data. Null when unknown.</summary>
    public double? OverwrittenFraction { get; set; }
    public DataLayout Layout { get; set; } = new();
    public string? FormatId { get; set; }
    public string Notes { get; set; } = "";
    public long? DuplicateOf { get; set; }
    public string? ContentHash { get; set; }

    // Deferred path resolution (NTFS/orphan records): key of the parent directory.
    public long ParentKey { get; set; } = -1;
    public int ScanUnit { get; set; }

    public long FirstOffset => Layout.FirstOffset;
    public string DisplayPath => string.IsNullOrEmpty(Path) ? Name : (Path!.EndsWith('\\') ? Path + Name : Path + "\\" + Name);
}
