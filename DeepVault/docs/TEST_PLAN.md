# Test plan

Never use the original HDD as a test fixture. All automated tests use disposable images.

## Automated tests (`dotnet test tests/DeepVault.Tests`)

Fixtures (`tests/fixtures/images`, built by `tools/fixtures/make_images.py` with real file-system
drivers; manifest with SHA-256 of every tracked file):

| Image | Built with | Scenario |
|---|---|---|
| ntfs_deleted | ntfs-3g | 12 files + 1 fragmented (4 runs) + 1 LZNT1-compressed + 1 resident, deleted; deleted folder; 1 kept |
| fat32_deleted | mtools | files incl. LFN name deleted with `mdel`, folder deleted with `mdeltree` |
| exfat_deleted | exfat-fuse | 4 files deleted |
| carve_raw | python | 29 formats at sector offsets with random/zero gaps; truncated PNG, fragmented PNG, corrupt JPEG header, truncated ZIP |
| lost_partitions | sgdisk, mkfs.ntfs, mkfs.vfat | GPT disk with NTFS + FAT32; MBR, both GPTs and NTFS primary boot sector wiped |
| reformatted_ntfs | ntfs-3g, mkfs.ntfs | 400+ files written, volume quick-formatted again and new files written ("Windows reinstall") |

| Requirement | Tests |
|---|---|
| **A** Deleted files | `NtfsEngineTests`, `FatEngineTests` (FAT32, exFAT), `DeepScan_ntfs_*`, `DeepScan_fat32_and_exfat` — names, folders, sizes and SHA-256 of content |
| **B** Carving | `CarvingTests` (byte-exact for every intact format; damaged ones never *Verified*), `CustomSignatureTests` |
| **C** Lost partitions | `DeepScan_finds_lost_partitions_and_their_files_without_modifying_the_image` |
| **D** Damaged/incomplete | `Damaged_candidates_are_never_reported_as_verified`, `Unreadable_sectors_are_recorded_skipped_and_flagged_not_hidden`, `Deleted_file_whose_data_never_reached_the_disk_is_flagged_before_restore` |
| **E** Destination safety | `RestoreTests` (existing files untouched, collisions renamed/skipped, insufficient space blocks before writing, source-disk refusal, FAT32 4 GB limit, disk-full mid-restore) |
| **F** Source safety, cancel/resume, failures | `SourceSafetyTests` (no write APIs, read-only handles, query-only IOCTLs, no temp-file code in engines/preview), image hash unchanged after scans, `Cancelled_scan_resumes_*`, `Drive_health_threshold_stops_the_scan_resumably` |
| **G** Reports | every scenario test writes `scenario-reports/*.json` (detected / reconstructed / verified / partial / failures / skipped regions) |
| Previews, imaging | `PreviewTests`, `ImagingTests` (bad-sector map, refusal without space, resume, never overwrite) |

Windows-only (CI, `tools/ci/windows-e2e.ps1`): VHD formatted and files deleted by Windows,
recovered through `\\.\PhysicalDriveN`, SHA-256 compared; restore to the source refused.
GUI: `DeepVault.exe --selftest <image> <outdir>`.

## Manual test plan (repeat before using DeepVault on the real disk)

1. **Practice image.** Unzip `test-images/reformatted_ntfs.img.gz`, open it, Deep Scan, verify
   that `holiday_beach.jpg`, `invoice_2019.pdf`, `thesis.docx` appear with names and previews;
   restore them to a test folder; open them.
2. **Your own disposable test on Windows.** In Disk Management create a VHD (Action → Create VHD,
   256 MB, fixed), initialise, format NTFS. Copy photos/documents onto it, wait a minute, delete
   them, empty the Recycle Bin. Restart DeepVault as administrator, select the VHD disk (bus
   "FileBackedVirtual"), Deep Scan, restore to another folder, compare with the originals.
   Then quick-format the VHD and repeat: files should come back via *Previous file system*.
3. **Destination safety.** Restore into a folder that already contains files with the same names:
   nothing is replaced; new copies are named `(2)`. Try selecting the VHD itself as destination:
   refused.
4. **Space check.** Select more data than a small USB stick holds: the restore is blocked before
   anything is written.
5. **Stop/resume.** Start a Deep Scan of a larger disk/image, stop it after a minute, reopen the
   project in *Saved projects*, *Resume scan*: it continues ("Resuming the deep scan at …").
6. **Only then** scan the original HDD — preferably connected to another PC (Mode 2), project on
   the USB drive.
