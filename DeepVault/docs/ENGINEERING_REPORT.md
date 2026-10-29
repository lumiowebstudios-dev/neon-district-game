# DeepVault 1.0 — engineering report

## What has genuinely been implemented
* A Windows desktop application (WPF, .NET 10, dark theme) with all 14 requested pages, and a
  command-line tool with the same engines. Self-contained win-x64 build (no installation).
* Read-only disk access: physical disks via `\\.\PhysicalDriveN` (`GENERIC_READ`, query-only
  IOCTLs, SMART failure prediction), raw images, VHD (fixed/dynamic); Linux block devices for the CLI.
* Six recovery engines combined in **Deep Scan — All Applicable Methods** (see `ENGINES.md`), a
  SQLite project catalogue (paged queries, checkpoints, resume, export), previews (memory only),
  restore with space/collision/source-disk checks and SHA-256 read-back verification, HTML/CSV
  recovery reports, HTML scan reports, resumable disk imaging with bad-sector maps.

## Operational engines
NTFS MFT (deleted files with names/paths/timestamps; fragmented, resident, compressed, sparse,
attribute-list files) · FAT12/16/32 · exFAT · signature carving (40 formats, CRC-checked where the
format allows; user-defined JSON signatures) · partition-table analysis and lost-volume search ·
previous-file-system MFT records (names after a quick format / reinstall) · cross-engine
correlation and content checks.

## Tests actually executed (see `TEST_RESULTS.md`)
* 27 automated tests, **all passing on Windows (GitHub windows-latest) and Linux**, against six
  disposable disk images built with real file-system drivers: byte-exact recovery of deleted
  NTFS/FAT32/exFAT files, carving of 29 real formats, lost partitions with wiped GPT/MBR/boot sector,
  a quick-reformatted ("reinstalled") volume, unreadable sectors, drive-health stop, cancel/resume
  equivalence, destination safety, imaging, previews, static source-safety checks.
* **Real Windows end-to-end:** a VHD formatted NTFS by Windows, files written and deleted by Windows,
  recovered through `\\.\PhysicalDrive2` — 10/10 byte-exact (incl. a fragmented file); restore to the
  source disk refused.
* **GUI self-test on real Windows** (and under Wine): every page, scan, previews, restore, report;
  0 binding errors.

## What remains incomplete / not verified
* Not tested on your PC or HDD, on Windows 10 hardware, on multi-TB disks or on physically failing drives.
* Carved formats not validated with real samples: RAR, PST, ASF, FLV, MPEG-TS/PS, MIDI, AIFF, PSD,
  RAF, CAB, PE, TIFF-based RAW variants and RAW preview extraction.
* No fragment reassembly for carved files; no `$UsnJrnl`/`$LogFile`/shadow-copy parsing; no ReFS,
  ext4, APFS, BitLocker, Storage Spaces, VHDX/E01. PDF preview is text-only. Imaging is CLI-only.
  Build not code-signed. Full list: `KNOWN_LIMITATIONS.md`.

## What is required to build and launch
* Launch: unzip `DeepVault-1.0.0-win-x64` on Windows 10/11 x64, run `DeepVault.exe` (restart as
  administrator to read physical disks).
* Build: .NET 10 SDK, then `build.ps1` (Windows) or `./build.sh` (Linux/macOS) — see `BUILD.md`.

## How to test safely before scanning the original HDD
Follow `TEST_PLAN.md` → "Manual test plan": practise on `test-images/reformatted_ntfs.img.gz`, then
on your own disposable VHD (create, copy files, delete, quick-format, recover), test restore into a
folder with existing files and with too little space, test stop/resume — and only then scan the
original HDD, ideally connected to another PC (Mode 2) with the project on the USB drive.
