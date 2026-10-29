# Test results

Everything below was produced by actually running the tests; nothing is hand-entered. Regenerate with `tools/make_test_results.py`.

## Automated tests — Windows (GitHub windows-latest, run 5, commit e645886)

Run 2026-10-29T14:31:13 — total 27, passed 27, failed 0.

| Test | Outcome | Duration |
|---|---|---|
| CarvingTests.Carves_intact_files_byte_exactly_without_metadata | Passed | 00:00:00.08 |
| CarvingTests.Damaged_candidates_are_never_reported_as_verified | Passed | 00:00:00.14 |
| CustomSignatureTests.Sample_custom_signature_file_loads_and_carves_header_footer_files | Passed | 00:00:00.01 |
| DeepScanTests.Cancelled_scan_resumes_and_produces_the_same_results_as_an_uninterrupted_scan | Passed | 00:00:00.65 |
| DeepScanTests.DeepScan_after_quick_reformat_recovers_old_files_with_original_names | Passed | 00:00:00.51 |
| DeepScanTests.DeepScan_carving_only_image_report | Passed | 00:00:00.32 |
| DeepScanTests.DeepScan_fat32_and_exfat | Passed | 00:00:00.31 |
| DeepScanTests.DeepScan_finds_lost_partitions_and_their_files_without_modifying_the_image | Passed | 00:00:00.73 |
| DeepScanTests.DeepScan_ntfs_deleted_files_are_recovered_and_correlated_with_carving | Passed | 00:00:00.37 |
| DeepScanTests.Deleted_file_whose_data_never_reached_the_disk_is_flagged_before_restore | Passed | 00:00:00.63 |
| DeepScanTests.Drive_health_threshold_stops_the_scan_resumably | Passed | 00:00:00.08 |
| DeepScanTests.Unreadable_sectors_are_recorded_skipped_and_flagged_not_hidden | Passed | 00:00:00.13 |
| FatEngineTests.Recovers_deleted_exfat_files | Passed | 00:00:00.13 |
| FatEngineTests.Recovers_deleted_fat32_files_including_lfn_names_and_deleted_directory | Passed | 00:00:00.36 |
| ImagingTests.Images_a_disk_with_bad_sectors_mapped_and_refuses_when_space_is_insufficient | Passed | 00:00:01.42 |
| NtfsEngineTests.Recovers_deleted_ntfs_files_with_names_paths_and_exact_content | Passed | 00:00:00.12 |
| PreviewTests.Previews_are_produced_in_memory_for_images_text_pdf_and_raw_bytes | Passed | 00:00:00.71 |
| RestoreTests.Default_restore_uses_a_new_session_folder_and_preserves_original_folders | Passed | 00:00:00.20 |
| RestoreTests.Destination_full_during_restore_stops_cleanly_and_deletes_only_its_partial_file | Passed | 00:00:00.26 |
| RestoreTests.Destination_on_the_source_disk_is_refused | Passed | 00:00:00.17 |
| RestoreTests.Fat32_destination_rejects_files_over_4GB | Passed | 00:00:00.30 |
| RestoreTests.Insufficient_space_blocks_before_anything_is_written | Passed | 00:00:00.16 |
| RestoreTests.Restores_verified_copies_without_touching_existing_destination_files | Passed | 00:00:00.89 |
| RestoreTests.Skip_policy_skips_collisions | Passed | 00:00:00.20 |
| SourceSafetyTests.Block_source_interfaces_expose_no_write_operations | Passed | 00:00:00.21 |
| SourceSafetyTests.Preview_and_scan_engines_contain_no_file_writing_code | Passed | 00:00:00.10 |
| SourceSafetyTests.Raw_disk_access_code_opens_devices_read_only_and_never_issues_write_or_repair_requests | Passed | 00:00:00.02 |

## Automated tests — Linux (development container)

Run 2026-10-29T14:28:53 — total 27, passed 27, failed 0.

| Test | Outcome | Duration |
|---|---|---|
| CarvingTests.Carves_intact_files_byte_exactly_without_metadata | Passed | 00:00:00.23 |
| CarvingTests.Damaged_candidates_are_never_reported_as_verified | Passed | 00:00:00.49 |
| CustomSignatureTests.Sample_custom_signature_file_loads_and_carves_header_footer_files | Passed | 00:00:00.03 |
| DeepScanTests.Cancelled_scan_resumes_and_produces_the_same_results_as_an_uninterrupted_scan | Passed | 00:00:00.22 |
| DeepScanTests.DeepScan_after_quick_reformat_recovers_old_files_with_original_names | Passed | 00:00:00.44 |
| DeepScanTests.DeepScan_carving_only_image_report | Passed | 00:00:00.21 |
| DeepScanTests.DeepScan_fat32_and_exfat | Passed | 00:00:00.49 |
| DeepScanTests.DeepScan_finds_lost_partitions_and_their_files_without_modifying_the_image | Passed | 00:00:01.81 |
| DeepScanTests.DeepScan_ntfs_deleted_files_are_recovered_and_correlated_with_carving | Passed | 00:00:00.34 |
| DeepScanTests.Deleted_file_whose_data_never_reached_the_disk_is_flagged_before_restore | Passed | 00:00:00.79 |
| DeepScanTests.Drive_health_threshold_stops_the_scan_resumably | Passed | 00:00:00.08 |
| DeepScanTests.Unreadable_sectors_are_recorded_skipped_and_flagged_not_hidden | Passed | 00:00:00.11 |
| FatEngineTests.Recovers_deleted_exfat_files | Passed | 00:00:00.22 |
| FatEngineTests.Recovers_deleted_fat32_files_including_lfn_names_and_deleted_directory | Passed | 00:00:00.28 |
| ImagingTests.Images_a_disk_with_bad_sectors_mapped_and_refuses_when_space_is_insufficient | Passed | 00:00:02.75 |
| NtfsEngineTests.Recovers_deleted_ntfs_files_with_names_paths_and_exact_content | Passed | 00:00:00.04 |
| PreviewTests.Previews_are_produced_in_memory_for_images_text_pdf_and_raw_bytes | Passed | 00:00:00.65 |
| RestoreTests.Default_restore_uses_a_new_session_folder_and_preserves_original_folders | Passed | 00:00:01.04 |
| RestoreTests.Destination_full_during_restore_stops_cleanly_and_deletes_only_its_partial_file | Passed | 00:00:00.37 |
| RestoreTests.Destination_on_the_source_disk_is_refused | Passed | 00:00:00.52 |
| RestoreTests.Fat32_destination_rejects_files_over_4GB | Passed | 00:00:00.14 |
| RestoreTests.Insufficient_space_blocks_before_anything_is_written | Passed | 00:00:00.12 |
| RestoreTests.Restores_verified_copies_without_touching_existing_destination_files | Passed | 00:00:00.38 |
| RestoreTests.Skip_policy_skips_collisions | Passed | 00:00:00.11 |
| SourceSafetyTests.Block_source_interfaces_expose_no_write_operations | Passed | 00:00:00.04 |
| SourceSafetyTests.Preview_and_scan_engines_contain_no_file_writing_code | Passed | 00:00:00.09 |
| SourceSafetyTests.Raw_disk_access_code_opens_devices_read_only_and_never_issues_write_or_repair_requests | Passed | 00:00:00.02 |

## Recovery scenarios (Test G records)

### A1 NTFS deleted files (deep scan)

*Setup:* NTFS volume; files deleted through ntfs-3g (one fragmented, one LZNT1-compressed, one resident, one deleted directory)  
*Methods:* Partition table analysis, File-system metadata (NTFS / FAT / exFAT), Deep signature scan (carving), Lost partition search (0 found), Previous file-system MFT records  
*Outcome:* Completed

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 14 | 14 | 14 | 14 | 0 | 0 | 935 |

**Details:**
- holiday_beach.jpg: recovered byte-exact by NtfsMft, SignatureCarving as '\Photos\2019\holiday_beach.jpg' (Verified, High)
- animation.gif: recovered byte-exact by NtfsMft, SignatureCarving as '\Photos\2019\animation.gif' (Verified, High)
- diagram.png: recovered byte-exact by NtfsMft, SignatureCarving as '\Photos\diagram.png' (Verified, High)
- invoice_2019.pdf: recovered byte-exact by NtfsMft, SignatureCarving as '\Documents\invoice_2019.pdf' (Verified, High)
- thesis.docx: recovered byte-exact by NtfsMft, SignatureCarving as '\Documents\thesis.docx' (Verified, High)
- old_report.doc: recovered byte-exact by NtfsMft, SignatureCarving as '\Documents\old_report.doc' (Verified, High)
- tiny.txt: recovered byte-exact by NtfsMft as '\Documents\tiny.txt' (NotChecked, High)
- notes.txt: recovered byte-exact by NtfsMft as '\Documents\notes.txt' (NotChecked, High)
- letter.rtf: recovered byte-exact by NtfsMft, SignatureCarving as '\Documents\letter.rtf' (NotChecked, High)
- song.mp3: recovered byte-exact by NtfsMft, SignatureCarving as '\Music\song.mp3' (NotChecked, High)
- memo.wav: recovered byte-exact by NtfsMft, SignatureCarving as '\Music\memo.wav' (Verified, High)
- lossless.flac: recovered byte-exact by NtfsMft, SignatureCarving as '\Music\lossless.flac' (Verified, High)
- family_large.jpg: recovered byte-exact by NtfsMft as '\Photos\family_large.jpg' (Verified, High)
- compressible.txt: recovered byte-exact by NtfsMft as '\Compressed\compressible.txt' (NotChecked, High)

**Limitations:**
- Deleted files whose clusters are reused would be flagged as overwritten; none were reused in this image.

**Failures:** none

### A2 FAT32 deleted files (deep scan)

*Setup:* FAT32 volume; files and a directory deleted with mtools (mdel/mdeltree)  
*Methods:* Partition table analysis, File-system metadata (NTFS / FAT / exFAT), Deep signature scan (carving), Lost partition search (0 found), Previous file-system MFT records  
*Outcome:* Completed

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 7 | 7 | 7 | 7 | 0 | 0 | 7 |

**Details:**
- holiday_beach.jpg: recovered byte-exact by FatDirectory, SignatureCarving as '\Photos\holiday_beach.jpg' (Verified, High)
- phone_photo.webp: recovered byte-exact by FatDirectory, SignatureCarving as '\Photos\phone_photo.webp' (Verified, High)
- invoice_2019.pdf: recovered byte-exact by FatDirectory, SignatureCarving as '\Docs\invoice_2019.pdf' (Verified, High)
- contacts.csv: recovered byte-exact by FatDirectory as '\Docs\_ontacts.csv' (NotChecked, Medium)
- old_budget.xls: recovered byte-exact by FatDirectory, SignatureCarving as '\Docs\Old Projects\old_budget.xls' (Verified, High)
- backup.zip: recovered byte-exact by FatDirectory, SignatureCarving as '\Docs\Old Projects\_ackup.zip' (Verified, High)
- A long file name for testing LFN recovery.txt: recovered byte-exact by FatDirectory as '\Docs\A long file name for testing LFN recovery.txt' (NotChecked, Medium)

**Limitations:**
- FAT erases the first character of deleted 8.3-only names; they are shown as '_'.

**Failures:** none

### A3 exFAT deleted files (deep scan)

*Setup:* exFAT volume; files deleted through exfat-fuse  
*Methods:* Partition table analysis, File-system metadata (NTFS / FAT / exFAT), Deep signature scan (carving), Lost partition search (0 found), Previous file-system MFT records  
*Outcome:* Completed

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 4 | 4 | 4 | 4 | 0 | 0 | 4 |

**Details:**
- clip.mp4: recovered byte-exact by ExFatDirectory, SignatureCarving as '\Camera\clip.mp4' (Plausible, High)
- holiday_beach.jpg: recovered byte-exact by ExFatDirectory, SignatureCarving as '\Camera\holiday_beach.jpg' (Verified, High)
- song.mp3: recovered byte-exact by ExFatDirectory, SignatureCarving as '\Camera\song.mp3' (Plausible, High)
- invoice_2019.pdf: recovered byte-exact by ExFatDirectory, SignatureCarving as '\Camera\invoice_2019.pdf' (Verified, High)

**Failures:** none

### B1 Signature carving without any file system

*Setup:* No file system: files at sector-aligned offsets separated by random/zero gaps, plus truncated/fragmented/corrupt files  
*Methods:* Partition table analysis, File-system metadata (NTFS / FAT / exFAT), Deep signature scan (carving), Lost partition search (0 found), Previous file-system MFT records  
*Outcome:* Completed

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 29 | 29 | 29 | 29 | 0 | 0 | 32 |

**Details:**
- holiday_beach.jpg: recovered byte-exact by SignatureCarving as '\Carved\Image\DV_0000000128.jpg' (Verified, High)
- diagram.png: recovered byte-exact by SignatureCarving as '\Carved\Image\DV_0000000227.png' (Verified, High)
- animation.gif: recovered byte-exact by SignatureCarving as '\Carved\Image\DV_0000000400.gif' (Verified, High)
- scan.bmp: recovered byte-exact by SignatureCarving as '\Carved\Image\DV_0000000422.bmp' (Plausible, Medium)
- document_scan.tif: recovered byte-exact by SignatureCarving as '\Carved\Image\DV_0000000509.tif' (Plausible, Medium)
- phone_photo.webp: recovered byte-exact by SignatureCarving as '\Carved\Image\DV_0000000623.webp' (Verified, High)
- invoice_2019.pdf: recovered byte-exact by SignatureCarving as '\Carved\PDF\DV_0000000676.pdf' (Verified, High)
- thesis.docx: recovered byte-exact by SignatureCarving as '\Carved\Document\DV_0000000702.docx' (Verified, High)
- budget.xlsx: recovered byte-exact by SignatureCarving as '\Carved\Spreadsheet\DV_0000000705.xlsx' (Verified, High)
- slides.pptx: recovered byte-exact by SignatureCarving as '\Carved\Presentation\DV_0000000715.pptx' (Verified, High)
- old_report.doc: recovered byte-exact by SignatureCarving as '\Carved\Document\DV_0000000757.doc' (Verified, High)
- old_budget.xls: recovered byte-exact by SignatureCarving as '\Carved\Spreadsheet\DV_0000000828.xls' (Verified, High)
- backup.zip: recovered byte-exact by SignatureCarving as '\Carved\Archive\DV_0000000883.zip' (Verified, High)
- archive.7z: recovered byte-exact by SignatureCarving as '\Carved\Archive\DV_0000000898.7z' (Verified, High)
- logs.gz: recovered byte-exact by SignatureCarving as '\Carved\Archive\DV_0000000927.gz' (Verified, High)
- dump.xz: recovered byte-exact by SignatureCarving as '\Carved\Archive\DV_0000000946.xz' (Verified, High)
- app_data.sqlite: recovered byte-exact by SignatureCarving as '\Carved\Database\DV_0000000965.sqlite' (Plausible, Medium)
- letter.rtf: recovered byte-exact by SignatureCarving as '\Carved\Document\DV_0000001047.rtf' (Plausible, Medium)
- song.mp3: recovered byte-exact by SignatureCarving as '\Carved\Audio\DV_0000001053.mp3' (Plausible, Medium)
- memo.wav: recovered byte-exact by SignatureCarving as '\Carved\Audio\DV_0000001104.wav' (Verified, High)
- lossless.flac: recovered byte-exact by SignatureCarving as '\Carved\Audio\DV_0000001137.flac' (Verified, High)
- voice.ogg: recovered byte-exact by SignatureCarving as '\Carved\Audio\DV_0000001179.ogg' (Verified, High)
- clip.mp4: recovered byte-exact by SignatureCarving as '\Carved\Video\DV_0000001230.mp4' (Plausible, Medium)
- camera.mov: recovered byte-exact by SignatureCarving as '\Carved\Video\DV_0000001241.mov' (Plausible, Medium)
- old_video.avi: recovered byte-exact by SignatureCarving as '\Carved\Video\DV_0000001292.avi' (Verified, High)
- movie.mkv: recovered byte-exact by SignatureCarving as '\Carved\Video\DV_0000001367.mkv' (Plausible, Medium)
- family_large.jpg: recovered byte-exact by SignatureCarving as '\Carved\Image\DV_0000001432.jpg' (Verified, High)
- settings.xml: recovered byte-exact by SignatureCarving as '\Carved\Text\DV_0000002146.xml' (HeaderOnly, Low)
- page.html: recovered byte-exact by SignatureCarving as '\Carved\Text\DV_0000002196.html' (HeaderOnly, Low)
- truncated.png (deliberately damaged): Damaged/PartialOrDamaged: 200×150; 1 chunk(s) failed CRC (content corrupted, overwritten or fragmented); Carved from raw sectors: assumes the file was stored contiguously
- fragmented.png (deliberately damaged): Damaged/PartialOrDamaged: 640×480; 1 chunk(s) failed CRC (content corrupted, overwritten or fragmented); Carved from raw sectors: assumes the file was stored contiguously
- corrupt_header.jpg (deliberately damaged): not reported
- truncated.zip (deliberately damaged): Damaged/PartialOrDamaged: central directory not found (truncated, fragmented or overwritten); 2 local entries precede the damage; Carved from raw sectors: assumes the file was stored contiguously

**Limitations:**
- Carved files have no original names or folders (named DV_<sector>.<ext>).
- Fragmented files are detected as damaged only for formats with checksums (PNG/ZIP/FLAC/Ogg/gzip...).

**Failures:** none

### C1 Lost partitions (MBR, GPT and NTFS boot sector wiped)

*Setup:* GPT disk whose MBR, primary GPT, backup GPT and NTFS primary boot sector were wiped  
*Methods:* Partition table analysis, File-system metadata (NTFS / FAT / exFAT), Deep signature scan (carving), Lost partition search (2 found), Previous file-system MFT records  
*Outcome:* Completed

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 5 | 5 | 5 | 5 | 0 | 0 | 5 |

**Details:**
- Lost volumes found: NTFS@1048576 (backup boot sector found), FAT32@42991616 (primary boot sector found; backup boot sector at +42991616)
- thesis.docx: recovered byte-exact by NtfsMft, SignatureCarving as '\Work\thesis.docx' (Verified, High)
- invoice_2019.pdf: recovered byte-exact by NtfsMft, SignatureCarving as '\Work\invoice_2019.pdf' (Verified, High)
- notes.txt: recovered byte-exact by NtfsMft as '\Work\notes.txt' (NotChecked, High)
- holiday_beach.jpg: recovered byte-exact by FatDirectory, SignatureCarving as '\Pictures\holiday_beach.jpg' (Verified, High)
- diagram.png: recovered byte-exact by FatDirectory, SignatureCarving as '\Pictures\diagram.png' (Verified, High)

**Failures:** none

### D1 Unreadable sectors

*Setup:* carve_raw image behind a fault injector: 16 bad sectors inside a JPEG, 64 bad sectors elsewhere  
*Methods:* Partition table analysis, File-system metadata (NTFS / FAT / exFAT), Deep signature scan (carving), Lost partition search (0 found), Previous file-system MFT records  
*Outcome:* CompletedWithGaps

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 1 | 1 | 0 | 0 | 1 | 0 | 0 |

**Details:**
- JPEG overlapping bad sectors: PartialOrDamaged, Low: 1024×768; Carved from raw sectors: assumes the file was stored contiguously; 8192 bytes lie in unreadable sectors and were zero-filled

**Skipped/unreadable regions:**
- Unreadable @798720 len 8192
- Unreadable @20971520 len 16384
- SkippedAfterErrors @20987904 len 1048576

**Failures:** none

### E1 Quick-reformatted NTFS (Windows reinstall simulation)

*Setup:* NTFS volume populated, then quick-formatted again and partially reused (simulates a Windows reinstall)  
*Methods:* Partition table analysis, File-system metadata (NTFS / FAT / exFAT), Deep signature scan (carving), Lost partition search (0 found), Previous file-system MFT records  
*Outcome:* Completed

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 9 | 8 | 8 | 8 | 0 | 1 | 384 |

**Details:**
- holiday_beach.jpg: recovered byte-exact by SignatureCarving, OrphanMftRecords as '\$PreviousFileSystem\Folder #67\holiday_beach.jpg' (Verified, High)
- diagram.png: recovered byte-exact by SignatureCarving, OrphanMftRecords as '\$PreviousFileSystem\Folder #67\diagram.png' (Verified, High)
- invoice_2019.pdf: recovered byte-exact by SignatureCarving, OrphanMftRecords as '\$PreviousFileSystem\Folder #66\invoice_2019.pdf' (Verified, High)
- thesis.docx: recovered byte-exact by SignatureCarving, OrphanMftRecords as '\$PreviousFileSystem\Folder #66\thesis.docx' (Verified, High)
- song.mp3: recovered byte-exact by SignatureCarving, OrphanMftRecords as '\$PreviousFileSystem\Folder #68\song.mp3' (Plausible, Medium)
- old_report.doc: recovered byte-exact by SignatureCarving, OrphanMftRecords as '\$PreviousFileSystem\Folder #66\old_report.doc' (Verified, High)
- letter_007.txt: not recovered
- letter_150.txt: recovered byte-exact by OrphanMftRecords as '\$PreviousFileSystem\Folder #69\letter_150.txt' (NotChecked, Medium)
- letter_399.txt: recovered byte-exact by OrphanMftRecords as '\$PreviousFileSystem\Folder #66\letter_399.txt' (NotChecked, Medium)

**Limitations:**
- Files whose MFT records and clusters were reused by the new file system cannot be recovered.
- Old folder records were overwritten by the new MFT, so recovered files are grouped as $PreviousFileSystem\Folder #N.

**Failures:** none

### E1 Restore to a destination with existing files

*Setup:* ntfs_deleted scanned; destination pre-populated with a same-named file  
*Methods:* n/a  
*Outcome:* existing files unchanged; collision renamed to 'holiday_beach (2).jpg'; every restored file SHA-256 verified

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 14 | 0 | 14 | 14 | 0 | 0 | 0 |

**Failures:** none

### E2 Destination fills up during restore

*Setup:* simulated destination with a 60 kB budget  
*Methods:* n/a  
*Outcome:* stopped cleanly: 7 verified, 1 failed (partial removed), 6 not attempted; existing files intact

| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |
|---|---|---|---|---|---|---|
| 14 | 0 | 0 | 7 | 0 | 0 | 0 |

**Failures:** none

### F2 Cancel and resume

*Setup:* carve_raw: deep scan cancelled after 1.5 MiB, project reopened and resumed  
*Methods:* n/a  
*Outcome:* Cancelled then Completed

**Details:**
- 31 candidates before cancellation; 32 after resume; uninterrupted scan: 32; identical sets: True

**Failures:** none

## Windows end-to-end: files deleted by Windows NTFS, recovered from \\.\PhysicalDriveN

Result: **PASSED**

| File | Original folder | Byte-exact (SHA-256) |
|---|---|---|
| holiday_beach.jpg | \Photos\2020 | yes |
| diagram.png | \Photos\2020 | yes |
| invoice_2019.pdf | \Documents | yes |
| thesis.docx | \Documents | yes |
| old_report.doc | \Documents | yes |
| notes.txt | \Documents | yes |
| song.mp3 | \Music | yes |
| backup.zip | \Archive | yes |
| family_large.jpg | \Photos | yes |

Steps: Create and attach disposable VHD; Write sample files with Windows NTFS; Delete the files with Windows; VHD is physical disk 2; deepvault-cli disks; Deep scan of the physical disk (read-only); Restore onto the source disk must be refused; Restore recoverable files to a separate folder; Compare SHA-256 with the originals

## GUI self-test — real Windows (GitHub windows-latest)

Result: **PASSED**; scan {'outcome': 'Completed', 'files': 935}; restore {'verified': 934, 'warnings': 1, 'failed': 0, 'folder': 'D:\\a\\neon-district-game\\neon-district-game\\ci-out\\selftest\\out\\restored\\DeepVault Recovery 2026-10-29 1432'}; WPF binding errors: 0

| Page | Note |
|---|---|
| welcome |  |
| select-drive | 2 disks listed |
| drive-details | 1 partitions |
| scan-methods |  |
| scan-progress-running |  |
| scan-progress-finished | Scan complete for the selected methods and scope. 935 candidates found in 00:00:00. |
| scan-issues |  |
| results-list | 935 in view |
| results-gallery | 4 thumbnails decoded |
| preview-jpg |  |
| preview-pdf | Text preview of a 1-page PDF (layout and images are not shown). |
| preview-txt |  |
| preview-mp3 | No visual preview for this type. Showing the first bytes so you can judge whether the content looks intact (all zeros usually means overwritten). |
| restore-destination | The destination is on the same drive as the disk image. This does not change the image file, but keep enough free space. |
| recovery-report | Restore finished. |
| recovery-progress-idle |  |
| saved-projects |  |
| settings-formats |  |
| diagnostics |  |
| recovery-report-final |  |

## GUI self-test — Wine 9 on Linux, packaged build, reformat image

Result: **PASSED**; scan {'outcome': 'Completed', 'files': 384}; restore {'verified': 363, 'warnings': 0, 'failed': 0, 'folder': 'Z:\\tmp\\st\\final\\restored\\DeepVault Recovery 2026-10-29 1431'}; WPF binding errors: 0

| Page | Note |
|---|---|
| welcome |  |
| select-drive | 1 disks listed |
| drive-details | 1 partitions |
| scan-methods |  |
| scan-progress-running |  |
| scan-progress-finished | Scan complete for the selected methods and scope. 384 candidates found in 00:00:00. |
| scan-issues |  |
| results-list | 363 in view |
| results-gallery | 2 thumbnails decoded |
| preview-jpg |  |
| preview-pdf | Text preview of a 1-page PDF (layout and images are not shown). |
| preview-txt | This candidate is damaged or partly overwritten: the preview may be incomplete or misleading. |
| preview-mp3 | No visual preview for this type. Showing the first bytes so you can judge whether the content looks intact (all zeros usually means overwritten). |
| restore-destination | The destination is on the same drive as the disk image. This does not change the image file, but keep enough free space. |
| recovery-report | Restore finished. |
| recovery-progress-idle |  |
| saved-projects |  |
| settings-formats |  |
| diagnostics |  |
| recovery-report-final |  |

## Additional measurements (copied from command output during development)

| Measurement | Result |
|---|---|
| Deep Scan throughput, 2 GiB random data, CLI Release build (file in page cache) | ~1.0 GB/s (2.1 s), 0 false candidates |
| Deep Scan throughput, 2 GiB zeros | ~1.3 GB/s (1.6 s) |
| Peak memory, 2 GiB deep scan | ~190 MB resident |
| Expected on a real HDD | limited by the drive (~80–200 MB/s): ≈1.5–4 h per TB |
| `ntfsundelete` (ntfs-3g) cross-check on `ntfs_deleted` | same deleted files listed; it reports the LZNT1-compressed file as 0 % recoverable, DeepVault recovers it byte-exact |

## Problems found by testing and fixed (history)

| Found by | Problem | Fix |
|---|---|---|
| Fixture test | test image's "fragmented" file was not fragmented (ntfs-3g allocation pattern) | fixture generator punches holes in both allocation regions |
| Carving test | HTML signature was case-sensitive; truncated ZIP gave no estimate | case variants; local-header walk |
| Deep-scan test | bad sectors were re-read by several readers (stresses failing drives) | reader remembers unreadable sectors and never re-reads them |
| Wine launch | duplicate XAML resource key; SQLite start-up probe of UWP APIs; GIF codec crash and missing fonts in Wine | key renamed; WinRT dependency removed; Wine-only GIF guard; font fallbacks |
| Windows E2E (run 2) | files deleted by Windows before the write cache flushed have all-zero content; they were listed as recoverable until the restore-time check | new content-check phase marks such candidates *Damaged* during the scan (regression test added) |
| Wine GUI test | a lost NTFS volume's own MFT was also reported as "previous file system" (duplicates) | records in analysed lost volumes are excluded (test asserts no duplicates) |
