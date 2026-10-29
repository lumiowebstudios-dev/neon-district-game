# Known limitations

## Fundamental (no software can overcome these)
* Overwritten data cannot be recovered. After a Windows reinstall, the installation itself
  (~20–40 GB) and everything written since overwrote part of the old data.
* SSDs with TRIM (and many USB flash drives) erase freed blocks internally; deleted data on them is
  usually gone. The target here is a mechanical HDD, where this does not apply.
* Files deleted while their data was still in the write cache may never have reached the disk
  (DeepVault detects such empty content and marks the candidate damaged).
* A scan of the running Windows drive cannot prevent Windows from writing to it (see SAFETY.md).

## Engine limitations
* **Carving assumes contiguity.** Fragmented carved files are flagged when the format has
  checksums/strong structure, but are not reassembled. Formats without checksums (MP4/MOV sample
  data, MP3 audio, BMP, TIFF/RAW strips, plain text) can look *plausible* while containing wrong data.
* **NTFS:** EFS-encrypted files stay encrypted; named alternate data streams are not listed;
  reused MFT records are lost; `$LogFile`/`$UsnJrnl`/Volume Shadow Copy contents are not parsed;
  a cluster that was reused and freed again looks free (content checks catch many but not all
  such cases).
* **FAT:** deleted fragmented files cannot be reassembled exactly (the chain is erased); the first
  character of deleted 8.3-only names is lost.
* **Previous-file-system records:** need the old volume's geometry. If neither a surviving `$MFT`
  record nor the containing volume gives it, non-resident files keep metadata only.
* **Not supported file systems/containers:** ReFS, ext2/3/4, HFS+/APFS, Btrfs, BitLocker (unless
  unlocked and imaged), Windows Storage Spaces / dynamic (LDM) spanned volumes, RAID, VHDX and E01
  images (raw `.img/.dd`, VHD fixed/dynamic are supported).
* **Carved formats not tested against real samples:** RAR, PST/OST, ASF/WMV/WMA, FLV, MPEG-TS/PS,
  MIDI, AIFF, PSD, RAF, CAB, PE, the TIFF-based camera RAW variants and RAW embedded-preview
  extraction are implemented from their specifications but were not validated with real files.

## Application
* **PDF preview shows extracted text only** (no page rendering). Fonts with custom encodings may
  show garbled text; the file itself is unaffected.
* HEIC/WebP/RAW thumbnails depend on the image codecs installed in Windows.
* The UI is English only. The build is not code-signed.
* "Find exact duplicates" reads every equal-size candidate; on huge result sets it can take long.
* Resume restarts at the last checkpoint (default every 256 MB of the raw pass); the file-system
  metadata phase is restarted as a whole if interrupted.
* Disk imaging is available in the command-line tool only (not yet in the GUI).
* Windows PE: not tested. The WPF GUI does not run in standard WinPE.

## What was verified where
| Verified | Environment |
|---|---|
| All engine, scenario, restore, imaging and safety tests | Linux (development) **and** Windows Server 2025 (GitHub `windows-latest`) |
| Raw physical-disk access, Windows NTFS deletions, restore to a separate volume, refusal to restore to the source | Windows (`windows-latest`), on a VHD attached as a physical disk |
| GUI: every page, scan, previews, restore, report, zero binding errors | Windows (`windows-latest`) and Wine 9 (Linux) |
| Not verified | Your specific PC and HDD; Windows 10 on physical hardware; very large disks (only up to 2 GiB images and a 150 GB disk enumeration were exercised); real failing drives |
