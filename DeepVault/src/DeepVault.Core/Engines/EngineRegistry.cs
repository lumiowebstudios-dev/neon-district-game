using DeepVault.Core.Catalog;

namespace DeepVault.Core.Engines;

/// <summary>The documented set of recovery engines and what each can and cannot do.</summary>
public static class EngineRegistry
{
    public static IReadOnlyList<EngineDescriptor> All { get; } = new[]
    {
        new EngineDescriptor(EngineId.PartitionScan, "C — Partition & lost-volume discovery",
            "Reads MBR (incl. extended/logical) and GPT (primary and backup, CRC-checked); during the deep pass recognises NTFS, FAT12/16/32 and exFAT boot sectors (and their backup copies) to find volumes the partition table no longer lists.",
            "Confirmed vs. candidate partitions, volume geometry, backup-boot-sector recovery of damaged NTFS/FAT32/exFAT boot sectors.",
            "Never writes or repairs a partition table. Does not recognise Linux/macOS file systems, Storage Spaces, BitLocker contents or dynamic-disk (LDM) volumes spanning disks.",
            "Raw read access to the disk (administrator on Windows) or a disk image.",
            "Exact: partition table analysis is instant; boot-sector search runs inside the deep pass.", true),
        new EngineDescriptor(EngineId.NtfsMft, "A/D — NTFS MFT analysis",
            "Walks every MFT record of each NTFS volume, including records of deleted files and directories.",
            "Original names, full directory paths (incl. deleted folders), sizes, created/modified/accessed times, exact fragment lists, resident files, sparse and LZNT1-compressed files, files split over attribute-list extension records. Estimates overwriting from the current cluster bitmap.",
            "Cannot recover a file whose MFT record has been reused. EFS-encrypted files stay encrypted. Named alternate data streams are not listed. A cluster marked free may still have been overwritten earlier and freed again; structural checks flag many but not all such cases.",
            "Raw read access.", "Exact (MFT records processed / total).", false),
        new EngineDescriptor(EngineId.FatDirectory, "A — FAT12/16/32 directory analysis",
            "Walks directories including deleted entries and deleted subdirectories; reassembles long file names.",
            "Names (long names; for 8.3-only names the first character is lost), paths, sizes, timestamps; content of deleted files reconstructed contiguously or by skipping in-use clusters, and validated against the file format.",
            "FAT erases the cluster chain on deletion, so fragmented deleted files cannot be reassembled exactly; such files are flagged when their format allows detection.",
            "Raw read access.", "Directories processed (total unknown in advance).", false),
        new EngineDescriptor(EngineId.ExFatDirectory, "A — exFAT directory analysis",
            "Walks exFAT directory entry sets including deleted ones.",
            "Names, paths, sizes, UTC-aware timestamps; contiguous (NoFatChain) files are exact; chained files follow the surviving FAT chain.",
            "If the FAT chain of a deleted fragmented file was cleared, contiguity is assumed and the result validated.",
            "Raw read access.", "Directories processed (total unknown in advance).", false),
        new EngineDescriptor(EngineId.SignatureCarving, "B — Deep signature scan (carving)",
            "Reads the selected regions sector by sector and recognises file headers; each candidate is delimited by a format-specific parser.",
            "Works without any file-system metadata (formatted, overwritten MFT, lost partitions). 40+ formats with structural validation; CRC-verified for PNG, ZIP/Office, FLAC, Ogg, gzip, 7z, xz, RAR.",
            "Carved files have no original names or folders. Assumes each file is stored contiguously: fragmented files are reconstructed incorrectly (formats with checksums report this as Damaged; others may not). Text files have no end marker.",
            "Raw read access.", "Exact (bytes scanned / bytes planned).", true),
        new EngineDescriptor(EngineId.OrphanMftRecords, "D/E — Previous file-system MFT records",
            "Collects NTFS FILE records found outside the current MFT during the deep pass (e.g. from the file system that existed before a format / Windows reinstall), infers the old volume geometry and rebuilds files from them.",
            "Original names, partial or full paths, timestamps and fragment lists of files from a previous NTFS file system, which carving alone cannot provide. Results are format-validated.",
            "Only records that physically survived are found. If the old volume geometry cannot be inferred, non-resident files are listed with metadata only.",
            "Raw read access.", "Records processed / total.", true),
    };
}
