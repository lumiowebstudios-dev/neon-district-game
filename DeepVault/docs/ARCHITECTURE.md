# Architecture and engineering plan

## Choice of technology
* **C# / .NET 10 (LTS, supported to Nov 2028)**. .NET 8 was rejected: its support ends on
  10 Nov 2026. Self-contained publishing means the user installs nothing.
* **WPF** for the desktop UI (mature, native Windows, low GPU needs, virtualised DataGrid).
* **SQLite** (Microsoft.Data.Sqlite) for the project/catalogue: transactional, crash-safe (WAL),
  handles millions of rows with indexed, paged queries, single portable file.
* No WinRT/UWP APIs: an earlier build used Windows.Data.Pdf, which pulled in the Windows SDK
  projection and made Microsoft.Data.Sqlite probe UWP app-data APIs at start-up; it was replaced by
  a pure C# PDF text extractor.

## Projects
```
DeepVault.sln
 src/DeepVault.Core              net10.0  – everything platform-independent (unit-testable on any OS)
   IO/          IBlockSource (read-only by construction), FileBlockSource, VhdBlockSource,
                SubRangeSource, ResilientReader (bad-sector policy), CachedReader, disk models
   Partitions/  MBR/GPT parser, boot-sector probe/validation, LostPartitionDetector
   FileSystems/ Ntfs (MftRecord, Lznt1, NtfsScanner, OrphanMft), Fat (FatScanner), ExFat
   Carving/     CarveFormat contract, 40 format analyzers, SignatureRegistry (+JSON custom),
                CarvingEngine (sequential pass + sector observers), FormatValidator
   Content/     DataLayout (extents/resident/compressed), ContentStream (reconstructs bytes)
   Catalog/     FoundFile model, FileTypes, ProjectDatabase (SQLite, checkpoints, queries)
   Scanning/    ScanOptions, ScanOrchestrator (Deep Scan), ScanReport
   Restore/     RestoreService (preflight, never-overwrite, verify), destination inspection, reports
   Imaging/     DiskImager (resumable raw imaging with bad-sector map)
   Preview/     PreviewService (memory only), PdfTextExtractor
   Engines/     contracts + EngineRegistry;  Logging/ JSON-lines log
 src/DeepVault.Platform.Windows  net10.0 – the only raw-disk code: CreateFile(GENERIC_READ),
                ReadFile with sector-aligned bounce buffer, query-only IOCTLs (geometry, storage
                properties, volume extents, SMART predict-failure), elevation, destination mapping
 src/DeepVault.App               net10.0-windows WPF – 14 pages, MVVM, UI self-test
 src/DeepVault.Cli               net10.0 – command-line front end (Windows and Linux)
 tests/DeepVault.Tests           xunit – engine, scenario, safety, restore, imaging tests
 tools/fixtures                  generators for the disposable test disks
 tools/ci                        Windows end-to-end test script (used by GitHub Actions)
```

## Key design decisions
* **Read-only by construction.** `IBlockSource` has only `Read`. The Windows source opens
  `\\.\PhysicalDriveN` with `GENERIC_READ`; a test fails the build if write/lock/dismount/layout
  APIs appear in the platform code.
* **Locate, don't copy.** Every candidate stores a `DataLayout` (extents, resident record,
  compression units). Previews and restores read straight from the source through
  `ContentStream`; nothing is staged in temp files.
* **One pass over the disk.** Carving, lost-volume and old-MFT detection share a single
  sequential read; analyzers use a bounded LRU page cache for look-ahead.
* **Honest states.** Candidate → metadata recovered → preview generated → content readable →
  restored → restore verified, plus partial/damaged and failed; structure verdicts and
  confidence are separate fields. "Completed" requires every phase and no unreadable areas.
* **Crash-safe resume.** Results are buffered and committed with the phase position in one
  transaction; on resume rows written after the last checkpoint are rolled back (no duplicates).
* **Bounded memory.** Streaming reads (4 MiB), paged UI queries (200 rows), directory maps only
  for directories; NTFS `$Bitmap` (≤512 MB) kept for overwrite analysis. Measured: ~190 MB peak RSS
  for a 2 GiB deep scan.
* **Commercial readiness.** Engines are behind small interfaces (`IScanSink`, `ISectorObserver`,
  `CarveFormat`, `IDiskEnumerator`); no ads/accounts/licensing code in 1.0, but the separation of
  Core/Platform/UI allows a licensing layer later without touching the engines.

## Data model (project database)
`files` (one row per candidate: names, path, type, size, times, engines bitmask, state,
confidence, structure, overwrite fraction, layout JSON, duplicate link, hash), `regions`
(unreadable/skipped/excluded/not-reached), `issues`, `partitions`, `phases` (checkpoints),
`orphan_records`, `restore_runs`, `restore_items`, `meta` (source identity, options, summary).

## Implementation sequence followed
1. I/O + partitions → 2. NTFS engine + real ntfs-3g fixtures → 3. carving framework + formats →
4. FAT/exFAT → 5. lost partitions + orphan MFT + orchestrator + SQLite catalogue + resume →
6. restore, imaging, previews, Windows raw-disk layer → 7. CLI → 8. WPF UI + self-test under Wine →
9. Windows CI (unit tests, real-disk E2E, GUI self-test) → 10. documentation and packaging.
