# Troubleshooting

| Problem | Cause / solution |
|---|---|
| "Reading a whole disk requires administrator rights" | Click *Restart as administrator* (bottom right) and accept the UAC prompt. Disk images do not need it. |
| Windows SmartScreen blocks DeepVault.exe | The build is not code-signed. *More info → Run anyway*. |
| My USB drive is not listed as a destination | Reconnect it, click *Refresh drives*. BitLocker-locked drives must be unlocked first. Network shares can be typed into the folder box. |
| The destination is marked "Drive being recovered — not allowed" | It is on the same physical disk as the source. Use another drive. |
| "Not enough free space" | DeepVault never deletes anything to make room. Select fewer files (filter by type, sort by size) or use a bigger drive. |
| Scan stopped: "the drive may be failing" | The drive returned more than the configured number of unreadable sectors. Stop using it and consider professional recovery or imaging with ddrescue. You can raise the threshold in *Settings* and resume — at your own risk. |
| Scan is very slow | Raw scanning is limited by the drive (HDDs: ~80–200 MB/s; failing drives much slower). Close other programs; USB 2.0 adapters limit to ~35 MB/s. The *Quick scan* finds recently deleted files in minutes. |
| No files found after a Windows reinstall | Check *Scan issues*: was the whole disk scanned? Make sure the Deep Scan (not Quick) ran. If old data was overwritten (e.g. "full" format, TRIM on SSDs, many GB written since), nothing remains. SSDs with TRIM usually erase deleted data within minutes. |
| Files listed as "Partial / damaged" | Their clusters were reused or are unreadable. They can still be restored as-is; they may open partially. |
| Restored file does not open although "Verified" | Verified means the copy equals what is on the disk now. The disk contents themselves were already damaged (e.g. fragmented carved file, partly overwritten). Try other candidates of the same file (Duplicates view) or the carved version. |
| Carved files have names like `DV_0001234567.jpg` | Carving finds content without file-system records; the original name is unknown. Use previews/thumbnails to identify them. |
| PDF preview shows little text | The preview extracts text only; scanned PDFs contain images. Restore the file to view it. |
| HEIC/WebP thumbnails missing | Install the "HEIF Image Extensions"/"WebP Image Extensions" from Microsoft for Windows' image codecs. |
| A project cannot be reopened | The project file is on a disconnected drive, or the drive letter changed: use *Open a project file…*. |
| Where are the logs? | `<project folder>\logs\*.log` (JSON lines). *Diagnostics → Copy diagnostics* collects version info and the recent log for support. Logs never contain file contents. |
