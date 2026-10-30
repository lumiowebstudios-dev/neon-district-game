# Changelog

## Unreleased

* A scan interrupted without a normal stop (project drive unplugged, crash, power loss) can be
  resumed with **Resume scan** after reopening the project; previously the button stayed disabled
  and the scan had to be restarted under the same project name from Scan methods.
* If the project cannot be saved during a scan, the progress page now says so and explains how to
  resume, instead of staying on "Working…".
* The time-left estimate after a resume no longer counts the jump to the resume position as
  scanning speed (it could show "15 seconds left" with 50 GB to go).

## 1.0.1

* **Much faster deep scans on real Windows disks.** The ZIP check (used for ZIP archives and
  Office/app packages) could search up to 4 GB ahead for the end of every damaged archive, and of
  every entry inside one. On a used Windows disk this made the drive read about 20× more data than
  the scan covered (measured: 22 GB read to scan 1 GB of real system files; 177 GB read to scan
  512 MB of damaged archives). The search now stops at the next archive's end record, and
  searches for the same marker are not repeated over data already searched. Results are
  byte-for-byte identical on the test images; read volume is now about 1.3–3× the scanned size.
* The progress window no longer shows "Not responding" while the scan saves results to a slow
  project drive (the "recently found" list is now loaded in the background).
* Time-left estimates use the average speed of the current phase and include whole days (the
  previous display dropped days, so "22:03:16" could mean several days).
* **Resuming no longer lists files twice.** Stopping a deep scan recorded its checkpoint after the
  files found since the last checkpoint, so resuming found them again (they appeared under
  Duplicates and inflated the candidate count). Resuming now removes anything found at or past the
  resume point before scanning it again; this also repairs projects stopped with 1.0.0.
* Counters (candidates, old file records) continue from the previous totals after a resume instead
  of restarting from zero.
* A scan started with 1.0.0 can be resumed with 1.0.1 (same project format).
