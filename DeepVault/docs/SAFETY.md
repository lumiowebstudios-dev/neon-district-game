# Safety instructions — scanning your original HDD

Treat the drive that held your old files as evidence. Every write to it can destroy old data
for good, and a failing drive can get worse each time it is read.

## 1. Before anything else

* **Stop writing to the drive.** Do not install programs, download, save documents, run disk
  "optimisers", defragmenters, `chkdsk /f`, Windows "repair" or partitioning tools on it.
* **Do not install DeepVault onto it.** DeepVault needs no installation: unzip it onto the USB
  drive (or another drive) and run it from there.
* **Never restore files to the same drive.** DeepVault refuses by default.
* **Listen to the drive.** Clicking, grinding, repeated spin-up, disconnections or very slow
  reads mean physical trouble: stop and consider a professional data-recovery lab. DeepVault
  stops a scan automatically after many read errors (configurable) and never re-reads a sector
  it already found unreadable.

## 2. What DeepVault guarantees — and what it cannot

DeepVault opens the source with read-only access (`GENERIC_READ` on `\\.\PhysicalDriveN`, or
`FileAccess.Read` for images). There is no write code path to the source; automated tests check
the source code for any write, lock, dismount or partition-layout call, and check that scanned
images are bit-identical afterwards.

DeepVault **cannot** stop Windows itself, Windows Update, antivirus, indexing, the page file,
hibernation or other programs from writing to the drive Windows is running from. That is why
there are two modes.

## 3. Mode 1 — Live Windows scan (convenient, some risk)

Use only if no other option is available, or for a first look.

1. Close every other program. Disconnect from the internet to stop updates (optional but helpful).
2. Run DeepVault from the USB drive. *Restart as administrator* when asked.
3. Put the **scan project on the USB drive** (DeepVault suggests it automatically). The project
   holds the results database and logs; on a big disk it can reach hundreds of MB.
4. The drive page shows **"Windows runs from this drive"**; you must tick an extra acknowledgement.
5. Run the Deep Scan, then restore to the USB drive. Keep the session as short as possible.

## 4. Mode 2 — Offline / image-based recovery (safest)

Any of these avoids background writes to the old drive. None requires formatting your USB drive.

**2a. Connect the HDD to another Windows PC (recommended).** Use a SATA-to-USB adapter or dock
(inexpensive) or an internal port of another PC. On that PC the old drive is just a data disk:
Windows does not run from it. If Windows offers to "scan and fix" the drive, choose **Cancel**.
If it asks to "format" a drive, **never accept**. Then scan it with DeepVault as a normal disk.

**2b. Scan a disk image.** Make a sector-by-sector image onto a large, separate drive and scan the
image instead of the disk. DeepVault can create the image (`deepvault-cli image disk:N --out
E:\old-disk.img`); it checks free space first and refuses to start if the image will not fit —
a 500 GB USB drive with data on it cannot hold an image of a 500 GB+ disk. An interrupted
image resumes; unreadable sectors are recorded in `*.dvimage.json`. Images of a *range*
(e.g. one partition) are possible but are labelled PARTIAL. For a drive that is already failing,
dedicated tools such as GNU ddrescue on a separate computer are preferable.

**2c. Boot another system.** Booting a Windows PE / Linux live system from *a separate* USB stick
also avoids writes (DeepVault's command-line tool runs on Linux too). This needs a second USB
stick, because creating boot media erases it — never use your 500 GB data drive for this.

## 5. Choosing the correct drive

The drive page shows model, serial number, capacity, connection type, the mounted volumes and
whether Windows runs from it. Your **USB destination is also listed — do not select it as the
source.** Confirm with the checkbox only after checking these details. For a physical disk the
project stores the model, serial and size; restoring later from a different disk is refused.

## 6. Restoring to the 500 GB USB drive

* DeepVault creates a new folder `DeepVault Recovery <date>` and writes only inside it.
* It never formats, initialises, repartitions or deletes anything on the USB drive, and never
  overwrites a file: name clashes become `name (2).ext` (or are skipped, if you choose).
* It checks free space before starting (the advertised 500 GB is not assumed to be free). If
  space runs out mid-way it stops cleanly, removes only its own unfinished temporary file and
  reports what was not attempted.
* Each file is written under a temporary name, read back and compared by SHA-256 with what was
  read from the source, then renamed. A report (HTML + CSV) is written into the recovery folder.

## 7. Privacy

No telemetry, no network, no accounts. Logs never contain file contents. Project files and reports
list file *names and folders* found on your drive — keep them private.
