namespace DeepVault.Core.Catalog;

/// <summary>Maps file extensions to categories and descriptions (used for display and filtering only).</summary>
public static class FileTypes
{
    private static readonly Dictionary<string, (FileCategory cat, string desc)> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileTypes()
    {
        void Add(FileCategory c, string desc, params string[] exts) { foreach (var e in exts) Map[e] = (c, desc); }
        Add(FileCategory.Image, "JPEG image", "jpg", "jpeg", "jpe", "jfif");
        Add(FileCategory.Image, "PNG image", "png");
        Add(FileCategory.Image, "GIF image", "gif");
        Add(FileCategory.Image, "Bitmap image", "bmp", "dib");
        Add(FileCategory.Image, "TIFF image", "tif", "tiff");
        Add(FileCategory.Image, "WebP image", "webp");
        Add(FileCategory.Image, "HEIF image", "heic", "heif", "avif");
        Add(FileCategory.Image, "Icon", "ico", "cur");
        Add(FileCategory.Image, "Photoshop document", "psd");
        Add(FileCategory.Image, "SVG image", "svg");
        Add(FileCategory.RawPhoto, "Camera RAW", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "rw2", "raf", "pef", "srw", "3fr", "erf", "kdc", "mrw", "x3f");
        Add(FileCategory.Video, "MPEG-4 video", "mp4", "m4v");
        Add(FileCategory.Video, "QuickTime video", "mov", "qt");
        Add(FileCategory.Video, "3GP video", "3gp", "3g2");
        Add(FileCategory.Video, "AVI video", "avi");
        Add(FileCategory.Video, "Matroska video", "mkv", "webm");
        Add(FileCategory.Video, "Windows Media video", "wmv", "asf");
        Add(FileCategory.Video, "Flash video", "flv");
        Add(FileCategory.Video, "MPEG transport stream", "ts", "mts", "m2ts");
        Add(FileCategory.Video, "MPEG video", "mpg", "mpeg", "vob");
        Add(FileCategory.Audio, "MP3 audio", "mp3");
        Add(FileCategory.Audio, "WAV audio", "wav");
        Add(FileCategory.Audio, "FLAC audio", "flac");
        Add(FileCategory.Audio, "Ogg audio", "ogg", "oga", "opus");
        Add(FileCategory.Audio, "AAC/M4A audio", "m4a", "aac", "m4b");
        Add(FileCategory.Audio, "Windows Media audio", "wma");
        Add(FileCategory.Audio, "AIFF audio", "aif", "aiff");
        Add(FileCategory.Audio, "MIDI", "mid", "midi");
        Add(FileCategory.Pdf, "PDF document", "pdf");
        Add(FileCategory.Document, "Word document", "doc", "docx", "docm", "dot", "dotx", "odt", "rtf", "wpd", "pages");
        Add(FileCategory.Document, "E-book", "epub", "mobi");
        Add(FileCategory.Spreadsheet, "Spreadsheet", "xls", "xlsx", "xlsm", "xlsb", "ods", "csv", "numbers");
        Add(FileCategory.Presentation, "Presentation", "ppt", "pptx", "pptm", "pps", "ppsx", "odp", "key");
        Add(FileCategory.Text, "Text", "txt", "log", "md", "ini", "cfg", "nfo", "xml", "json", "yaml", "yml", "html", "htm", "srt", "vcf", "ics");
        Add(FileCategory.SourceCode, "Source code", "c", "h", "cpp", "hpp", "cs", "java", "py", "js", "ts", "tsx", "jsx", "php", "rb", "go", "rs", "swift", "kt", "sh", "ps1", "bat", "cmd", "sql", "css", "scss", "vb", "pl", "lua");
        Add(FileCategory.Archive, "Archive", "zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "tar", "cab", "iso", "jar", "apk", "zst", "lz", "lzma");
        Add(FileCategory.Database, "Database", "sqlite", "db", "sqlite3", "mdb", "accdb", "dbf", "kdbx");
        Add(FileCategory.Email, "E-mail", "pst", "ost", "eml", "msg", "mbox", "dbx");
        Add(FileCategory.Executable, "Program", "exe", "dll", "sys", "msi", "ocx", "scr", "com");
    }

    public static FileCategory CategoryOf(string ext) => Map.TryGetValue(ext.TrimStart('.'), out var v) ? v.cat : FileCategory.Other;
    public static string DescriptionOf(string ext) => Map.TryGetValue(ext.TrimStart('.'), out var v) ? v.desc : (ext.Length > 0 ? ext.ToUpperInvariant() + " file" : "File");

    public static string ExtensionOf(string name)
    {
        int i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 && name.Length - i <= 12 ? name[(i + 1)..].ToLowerInvariant() : "";
    }

    public static string CategoryName(FileCategory c) => c switch
    {
        FileCategory.RawPhoto => "Camera RAW",
        FileCategory.SourceCode => "Source code",
        FileCategory.Pdf => "PDF",
        FileCategory.Email => "E-mail",
        _ => c.ToString(),
    };
}
