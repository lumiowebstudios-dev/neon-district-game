# Recovery engines

The engine registry (`DeepVault.Core.Engines.EngineRegistry`, shown in *Settings*) documents each
engine's capabilities, limits, permissions and progress reporting. All engines are read-only and
deliver results to one catalogue (`IScanSink` → the project database).

## Deep Scan order

1. **Partition analysis (C)** — MBR (incl. extended/logical chains), GPT primary and backup headers
   with CRC checks; boot sector of every partition validated (NTFS → MFT signature, FAT → FAT[0]
   media byte, exFAT → FAT signature). A damaged NTFS boot sector falls back to its backup copy.
2. **File-system metadata (A/D)** per recognised volume: NTFS, FAT12/16/32, exFAT.
3. **One sequential raw pass** over the planned regions. Per sector it runs: the signature
   carver (B), the lost-volume detector (C) and the orphan-MFT-record collector (D/E). Default
   scope = free clusters of recognised volumes + everything outside them; regions in use by
   existing files are excluded and the excluded size reported. Checkpoints every 256 MB.
4. **Lost volumes (C)** found by boot sectors are analysed with the metadata engines (all their
   files are listed, since the volume itself is inaccessible).
5. **Previous file system (D/E)** — orphan MFT records are grouped and rebuilt into files.
6. **Correlation** — a carved file starting exactly at a named file's first data byte is the same
   file: it is kept as a duplicate (never deleted), and the named result gains the carving engine
   and, if sizes match, its *Verified* structure verdict.

Unreadable sectors are zero-filled, recorded as regions, never re-read, and after 32 consecutive
bad sectors 1 MiB is skipped (recorded). After 256 bad sectors the scan stops itself (resumable)
unless the user disabled that. Any engine exception is recorded as an issue and the other engines
continue; a scan is only reported *Completed* when every phase finished with no unreadable areas.

## A — NTFS MFT analysis (`FileSystems/Ntfs/NtfsScanner`)
* Locates `$MFT` from the boot sector (falls back to `$MFTMirr`), follows its run list (and
  `$ATTRIBUTE_LIST`), reads every record with update-sequence (fixup) verification.
* For files: best name (Win32 > POSIX > DOS), `$STANDARD_INFORMATION` times, unnamed `$DATA`:
  resident data, non-resident run lists (fragments, sparse runs), LZNT1 compression units,
  initialised size, extension records; EFS flagged.
* Paths from directory records (incl. deleted directories) with sequence-number checks; broken
  chains become `\$OrphanFiles\Folder #n\…`.
* Overwrite estimate: share of the file's clusters now marked used in `$Bitmap`.
* Limits: reused records are gone; a cluster freed again after reuse looks free (the content
  may still be wrong — structure checks catch many such cases); named streams are skipped.

## A — FAT12/16/32 (`FileSystems/Fat/FatScanner`)
* Walks directories from the root, including entries marked deleted (0xE5) and deleted
  sub-directories (recognised by their `.`/`..` entries).
* Long names reassembled from LFN entries; for deleted entries the lost first character of the
  8.3 name is recovered from the LFN checksum when an LFN exists, otherwise shown as `_`.
* Deleted content: FAT clears the chain, so two layouts are built — strictly contiguous and
  "skip clusters now in use" — and validated with the format analyzers; the better one is kept.

## A — exFAT (`FileSystems/ExFat/ExFatScanner`)
* Entry sets (File + Stream extension + File name) incl. deleted ones; allocation bitmap loaded.
* NoFatChain files are exact; chained files follow the FAT if intact, otherwise contiguity is assumed
  and validated. UTC offsets in timestamps honoured.

## B — Signature carving (`Carving/*`)
See `FORMATS.md`. Each candidate is delimited by a parser and classified Verified / Plausible /
Header only / Damaged. A Verified/Plausible file is skipped over (embedded thumbnails are not
reported separately). Custom header/footer signatures via JSON (`SIGNATURES.md`).

## C — Lost partitions (`Partitions/LostPartitionDetector`)
Every scanned sector ending in 55AA is parsed as NTFS/FAT/exFAT boot sector; both the primary
hypothesis and the backup-copy hypothesis (NTFS: last sector, FAT32: sector 6, exFAT: sector 12)
are validated against the volume's own structures before a candidate is reported. Candidates are
listed separately (status *Candidate*) and never written anywhere.

## D/E — Previous file-system records (`FileSystems/Ntfs/OrphanMft`)
* FILE records found outside the current MFT (e.g. beyond the new, smaller MFT after a quick
  format, or from an earlier partition layout) are collected during the raw pass.
* Old volume geometry: from a surviving `$MFT` record (volume start = position − LCN × cluster
  size, validated by finding `$MFTMirr` or the boot sector), else from the volume that contains
  the records, else unknown (non-resident files then keep metadata only and are marked
  *Not recoverable*).
* Files are rebuilt from their run lists, validated by format, and checked against the current
  `$Bitmap` for overwriting.
* This is the engine that returns original **names** after a Windows reinstall; carving alone
  cannot. In the reformat test, 6/6 documents and photos came back named and byte-exact.

## Methods evaluated and not implemented
* **Repeated scanning / "deep recovery" of overwritten data** — not possible on modern drives;
  not implemented and never claimed.
* **Partition table repair / file-system repair** — would write to the source; out of scope.
* **Fragment reassembly for carved files (e.g. JPEG bifragment gap carving)** — feasible future
  work; today fragmented carved files are flagged rather than reassembled.
* **NTFS `$LogFile`/`$UsnJrnl` analysis** — can reveal names of deleted files but rarely content;
  future work.
* **Volume Shadow Copies** — readable through Windows for existing files; contents of old
  shadow-copy diff areas are not parsed. Future work.
* **External engines (PhotoRec, The Sleuth Kit)** — evaluated in `THIRD_PARTY.md`; not bundled.
