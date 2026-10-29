# Supported file formats (signature scan / carving)

Generated from the built-in registry (`deepvault-cli formats --markdown`). Every format below has a
dedicated parser that delimits the file and checks its structure; nothing is accepted on a header
match alone except where the table says "end estimated".

**Validation levels used in the results:** *Verified* — internal structure consistent, including
checksums where the format has them; *Plausible* — structure consistent, format has no checksums;
*Header only* — the end of the file had to be estimated; *Damaged* — inconsistent (truncated,
overwritten or fragmented).

**Fragmentation:** carving assumes a file is stored contiguously. Fragmented files are reported as
*Damaged* when the format has checksums (PNG, ZIP/Office, FLAC, Ogg, gzip, 7z, xz, RAR headers) or
other strong structure (JPEG restart/marker checks, OLE2 chains, PDF xref); for formats without
such checks (e.g. MP4 sample data, MP3 audio, BMP pixels, TIFF strips) a fragmented file can look
*Plausible* but contain wrong data. Files found by the metadata engines (NTFS/FAT/exFAT/orphan MFT)
do not have this limitation for NTFS, because the run list gives every fragment.

Formats identified **inside** ZIP containers: DOCX/DOCM, XLSX/XLSM/XLSB, PPTX/PPTM, VSDX, ODT/ODS/ODP,
EPUB, JAR, APK, XPS. Inside OLE2: DOC, XLS, PPT, MSG, VSD, MSI. Inside TIFF: CR2, NEF, ARW, DNG, PEF,
SRW, ORF, RW2. Inside ISO-BMFF: MP4, MOV, M4A, M4V, 3GP, HEIC, AVIF, CR3. RIFF: WAV, AVI, WebP, ANI.

| Format | Default ext. | Category | Checksums | On by default | How it is delimited and validated |
|---|---|---|---|---|---|
| JPEG image | .jpg | Image | no | yes | Marker/segment walk plus entropy-coded data scan to the EOI marker; restart-marker sequence checked. |
| PNG image | .png | Image | yes | yes | Chunk walk to IEND with every chunk CRC-32 verified. |
| GIF image | .gif | Image | no | yes | Block walk (extensions, image descriptors, LZW sub-blocks) to the trailer byte. |
| Windows bitmap | .bmp | Image | no | yes | Header fields cross-checked (declared size vs. dimensions and bit depth). No checksums. |
| TIFF image / TIFF-based camera RAW | .tif | Image | no | yes | IFD chain walk; file length is the furthest byte referenced by strips, tiles, sub-IFDs, EXIF and embedded previews. No checksums. |
| Fujifilm RAW | .raf | Camera RAW | no | yes | Size from the offsets/lengths of the embedded JPEG, CFA header and CFA data. No checksums. |
| Photoshop document | .psd | Image | no | yes | Section lengths walked (colour mode, resources, layers); raw/RLE image data size computed. No checksums. |
| RIFF (WAV / AVI / WebP) | .wav | Audio | no | yes | Declared RIFF size checked against a walk of its top-level chunks. |
| MP4 / MOV / HEIC (ISO base media) | .mp4 | Video | no | yes | Top-level box walk (ftyp/moov/mdat...). Box sizes are validated but media samples are not checksummed. |
| Matroska / WebM | .mkv | Video | no | yes | EBML header + Segment size; segment children walked when the size is unknown. |
| MP3 audio | .mp3 | Audio | no | yes | ID3v2 tag + MPEG audio frame chain walk (frame lengths computed from each header) + optional ID3v1 tag. |
| FLAC audio | .flac | Audio | yes | yes | Metadata blocks, then every frame located by header CRC-8 and verified with its CRC-16 until STREAMINFO's sample count is reached. |
| Ogg (Vorbis / Opus / Theora) | .ogg | Audio | yes | yes | Page walk with every page CRC verified until all logical streams reach end-of-stream. |
| Windows Media (WMV/WMA/ASF) | .wmv | Video | no | yes | File size taken from the File Properties object inside the header. No checksums. |
| Flash video | .flv | Video | no | yes | Tag chain walk; every PreviousTagSize must match the tag before it. |
| MPEG transport stream (.ts) | .ts | Video | no | yes | Requires 64 consecutive packet sync bytes, then follows the packet chain. Payload is not checksummed. |
| MPEG transport stream, 192-byte (camcorder .MTS/.M2TS) | .mts | Video | no | yes | Requires 64 consecutive packet sync bytes, then follows the packet chain. Payload is not checksummed. |
| MPEG program stream (.mpg/.vob) | .mpg | Video | no | yes | Pack/PES chain walk to the program end code. |
| MIDI | .mid | Audio | no | yes | Header + MTrk chunk lengths walked. |
| AIFF audio | .aiff | Audio | no | yes | FORM chunk size. No checksums. |
| PDF document | .pdf | PDF | no | yes | Searches %%EOF markers (incl. incremental updates); each is accepted only if its startxref points to a valid xref table/stream whose entries point to real objects. |
| ZIP / Office Open XML / OpenDocument | .zip | Archive | yes | yes | Locates the end-of-central-directory record whose offsets are consistent with the start, walks the central directory, and CRC-32-checks every entry (archives up to 256 MB). |
| OLE2 compound file (DOC / XLS / PPT / MSG) | .doc | Document | no | yes | Size computed from the sector allocation table (FAT, via DIFAT); directory and stream chains validated. No content checksums. |
| Rich Text Format | .rtf | Document | no | yes | Brace-depth tracking (escapes and \bin data honoured) until the document's closing brace. |
| Outlook data file (PST/OST) | .pst | E-mail | no | yes | End-of-file offset from the PST header (ANSI and Unicode). No checksums. |
| RAR archive (v4 and v5) | .rar | Archive | yes | yes | Block walk with header CRCs verified, to the end-of-archive block. (Validator not tested against real RAR files: no redistributable RAR creator was available.) |
| 7-Zip archive | .7z | Archive | yes | yes | Start-header CRC and next-header CRC verified; size = 32 + next-header offset + size. Packed data is not decompressed. |
| gzip | .gz | Archive | yes | yes | Fully decompressed; the trailer CRC-32 and size must match the decompressed data. Concatenated members are followed. |
| xz | .xz | Archive | yes | yes | Stream header/footer CRCs verified and the index located via the footer's backward size. |
| Microsoft Cabinet | .cab | Archive | no | yes | Cabinet size from the CFHEADER. No checksums. |
| SQLite database | .sqlite | Database | no | yes | Page size × page count from the header (only trusted when the header's version-valid-for counter matches). |
| Windows executable (EXE/DLL) | .exe | Executable | no | no (opt-in) | Size from the section table and certificate directory. Recovered programs are never executed by DeepVault. |
| XML document | .xml | Text | no | yes | Text run from the signature until the first byte that cannot be text (usually the zero slack after the file). No checksums. |
| HTML page | .html | Text | no | yes | Text run from the signature until the first byte that cannot be text (usually the zero slack after the file). No checksums. |
| E-mail message | .eml | E-mail | no | yes | Text run from the signature until the first byte that cannot be text (usually the zero slack after the file). No checksums. |
| vCard contact | .vcf | Text | no | yes | Text run from the signature until the first byte that cannot be text (usually the zero slack after the file). No checksums. |
| iCalendar | .ics | Text | no | yes | Text run from the signature until the first byte that cannot be text (usually the zero slack after the file). No checksums. |
| Plain text (no signature, experimental) | .txt | Text | no | no (opt-in) | Any sector that begins with at least 256 bytes of printable text. Produces many fragments; enable only when looking for lost text documents. |

## Not supported by the carver (yet)

bzip2, Zstandard, Matroska files with unknown-size clusters (end estimated), MPEG-4 files whose
`mdat` box size is 0, Access MDB/ACCDB, KeePass, ISO 9660, HEVC/H.264 raw streams, AAC ADTS,
encrypted containers. Plain text without a signature is opt-in (very noisy on system disks).
These can be added as custom signatures (see `SIGNATURES.md`) with header/footer patterns.

## Tested against real files

The automated test `CarvingTests` carves 29 real sample files (JPEG, PNG, GIF, BMP, TIFF, WebP, PDF,
DOCX, XLSX, PPTX, DOC, XLS, ZIP, 7z, gzip, xz, SQLite, RTF, MP3, WAV, FLAC, Ogg, MP4, MOV, AVI, MKV,
XML, HTML) byte-exactly from a raw image. RAR, PST, ASF/WMV, FLV, MPEG-TS/PS, MIDI, AIFF, PSD, RAF,
CAB, PE and the camera-RAW variants are implemented from their specifications but were **not**
tested against real sample files (none could be generated legally in the build environment).
