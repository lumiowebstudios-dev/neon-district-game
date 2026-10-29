# Dependency and licence inventory

All recovery engines, parsers, the catalogue, restore and imaging code are original DeepVault code.
Runtime dependencies (all redistributable in a commercial product, with attribution):

| Component | Version | Licence | Used for | Obligations |
|---|---|---|---|---|
| .NET runtime + WPF (self-contained in the package) | 10.0 | MIT | runtime, UI | include licence notice |
| Microsoft.Data.Sqlite.Core | 10.0.14 | MIT | project database | include licence notice |
| SQLitePCLRaw.bundle_e_sqlite3 / core / provider | 2.1.14 | Apache-2.0 | SQLite bindings | include licence + NOTICE |
| SQLite (e_sqlite3 native library) | 3.x via SQLitePCLRaw.lib.e_sqlite3 | Public domain | database engine | none |

Version 2.1.14 of SQLitePCLRaw was chosen because the default transitive 2.1.11 has a published
high-severity advisory (GHSA-2m69-gcr7-jv3q); `dotnet build` reports no vulnerable packages now.

Test-only (not redistributed in the application): xunit 2.9.3 (Apache-2.0),
xunit.runner.visualstudio (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

Fixture generation tools (used only on the build machine to create test images; not distributed):
ntfs-3g (GPL-2.0), dosfstools/mtools (GPL-3.0), exfatprogs/exfat-fuse (GPL-2.0), Pillow
(MIT-CMU), FFmpeg (LGPL/GPL), 7-Zip (LGPL). The generated images contain only synthetic data
produced by `tools/fixtures/make_samples.py` and may be redistributed; they contain no code from
those tools beyond the on-disk file-system structures they write.

Fonts: none bundled (Windows' own fonts are used).

## External recovery engines — evaluation

| Engine | Licence | Capability | Decision |
|---|---|---|---|
| PhotoRec / TestDisk (CGSecurity) | GPL-2.0-or-later | ~480 carving signatures, partition recovery | **Not bundled.** Linking or shipping it inside a proprietary product imposes GPL obligations on the combined work; running it as a separate program the user installs is possible but it writes carved files itself (to its own output folder), which conflicts with DeepVault's preview-first, verify-everything design. Recommended as a second opinion for formats DeepVault does not carve. |
| The Sleuth Kit (TSK) | mixed: IPL-1.0 / CPL-1.0 / GPL for some tools | NTFS/FAT/ext/HFS analysis, `fls`/`icat` | Not bundled: licence mix needs legal review for commercial redistribution; NTFS/FAT functionality was implemented natively instead. |
| ntfs-3g / ntfsundelete | GPL-2.0 | NTFS undelete | Used only as an independent oracle during development (it reported the same deleted files as DeepVault; it cannot recover the LZNT1-compressed test file, which DeepVault does). Not bundled. |
| libewf (E01 images) | LGPL-3.0 | forensic image format | Not included; E01 support is future work (dynamic linking would be acceptable under LGPL). |
| ddrescue | GPL-3.0 | imaging failing drives | Recommended in the safety guide for failing drives; not bundled. |

A commercial release should repeat this review with legal counsel and add a NOTICE file listing the
MIT/Apache licences above (`THIRD_PARTY_NOTICES.txt` is included in the package for this purpose).
