# Custom file signatures

DeepVault's carver can be extended without changing code. Create a JSON file (see
`custom-signatures.sample.json`) and select it in *Settings → Custom file signatures* (or pass it via
`ScanOptions.CustomSignaturesPath`). Each entry:

| Field | Meaning |
|---|---|
| `id` | unique id (shown as `custom:<id>`) |
| `name` | display name |
| `extension` | extension given to carved files |
| `category` | one of Image, RawPhoto, Video, Audio, Document, Spreadsheet, Presentation, Pdf, Text, Archive, Database, Email, Executable, SourceCode, Other |
| `header` | hex bytes (spaces allowed), at least 2 bytes |
| `headerOffset` | offset of the header from the start of the file (default 0) |
| `footer` | optional hex bytes that end the file; the file ends after the first footer found |
| `maxSize` | largest file to consider (bytes) |
| `enabled` | default on/off in the format list |

Without a footer the end of the file is estimated at the first all-zero 4 KiB block, and results
are marked *Header only*. Custom signatures never get the *Verified* level. Invalid entries are
skipped and reported in *Scan issues*.
