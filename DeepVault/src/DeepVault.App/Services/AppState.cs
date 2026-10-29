using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using DeepVault.Core.Catalog;
using DeepVault.Core.IO;
using DeepVault.Core.Logging;
using DeepVault.Core.Partitions;
using DeepVault.Core.Restore;
using DeepVault.Core.Scanning;
using DeepVault.Platform.Windows;

namespace DeepVault.App.Services;

public sealed class AppSettings
{
    public List<string> RecentProjects { get; set; } = new();
    public bool StopWhenDriveFailing { get; set; } = true;
    public int BadSectorStopThreshold { get; set; } = 256;
    public string? CustomSignaturesPath { get; set; }
    public bool ShowThumbnails { get; set; } = true;
    public long ValidateUpToMb { get; set; } = 64;

    /// <summary>Settings live in %APPDATA%\DeepVault. They are only written when the user changes something.</summary>
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepVault", "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record LogLine(DateTime Time, string Level, string Message);

/// <summary>
/// Logs to memory until a project folder exists, then also to &lt;project&gt;\logs — so DeepVault
/// does not write logs to the disk being recovered unless the user put the project there.
/// </summary>
public sealed class AppLog : ILog
{
    private readonly object _lock = new();
    private FileLog? _file;
    public ObservableCollection<LogLine> Lines { get; } = new();
    public string? FileDirectory => _file?.Directory;

    public void AttachFile(string dir)
    {
        lock (_lock)
        {
            if (_file?.Directory == dir) return;
            _file?.Dispose();
            _file = new FileLog(dir);
            foreach (var l in Lines.ToList()) _file.Info($"[{l.Time:O} {l.Level}] {l.Message}");
        }
    }

    private void Add(string level, string msg)
    {
        var line = new LogLine(DateTime.Now, level, msg);
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Append(line); else d.BeginInvoke(() => Append(line));
    }

    private void Append(LogLine l)
    {
        Lines.Add(l);
        while (Lines.Count > 5000) Lines.RemoveAt(0);
    }

    public void Info(string message) { Add("info", message); lock (_lock) _file?.Info(message); }
    public void Warn(string message) { Add("warn", message); lock (_lock) _file?.Warn(message); }
    public void Error(string message, Exception? ex = null) { Add("error", message + (ex != null ? $" ({ex.GetType().Name}: {ex.Message})" : "")); lock (_lock) _file?.Error(message, ex); }
}

/// <summary>Everything the pages share: the selected source, the open project, selections and results.</summary>
public sealed class AppState : ObservableObject
{
    public static AppState Current { get; } = new();

    public AppLog Log { get; } = new();
    public AppSettings Settings { get; set; } = AppSettings.Load();
    public WindowsDiskEnumerator Disks { get; } = new();
    public IDestinationInspector Inspector { get; } = new WindowsDestinationInspector();

    private PhysicalDiskInfo? _disk;
    public PhysicalDiskInfo? SelectedDisk { get => _disk; set => Set(ref _disk, value); }
    public string? SelectedImagePath { get; set; }

    private IBlockSource? _source;
    public IBlockSource? Source { get => _source; private set { Set(ref _source, value); Raise(nameof(HasSource)); } }
    public bool HasSource => _source != null;
    public SourceIdentity? Identity { get; private set; }
    public string[] SourceDiskIds { get; private set; } = Array.Empty<string>();
    public PartitionTableResult? Partitions { get; set; }
    public bool SourceConfirmed { get; set; }

    private ProjectDatabase? _project;
    public ProjectDatabase? Project { get => _project; private set { Set(ref _project, value); Raise(nameof(HasProject)); } }
    public bool HasProject => _project != null;
    public string? ProjectPath { get; private set; }

    public ScanOptions ScanOptions { get; set; } = new();
    public ScanSummary? LastSummary { get; set; }
    public RestoreResult? LastRestore { get; set; }

    public HashSet<long> Selected { get; } = new();
    public event Action? SelectionChanged;
    public void NotifySelection() { SelectionChanged?.Invoke(); Raise(nameof(SelectedCount)); }
    public int SelectedCount => Selected.Count;

    public event Action<string, string>? ErrorRaised;

    public void ReportError(string title, Exception e)
    {
        Log.Error(title, e);
        ErrorRaised?.Invoke(title, e.Message);
    }

    public void SetSource(IBlockSource src, SourceIdentity id, string[] diskIds)
    {
        CloseSource();
        Source = src;
        Identity = id;
        SourceDiskIds = diskIds;
        SourceConfirmed = false;
        Partitions = null;
        Log.Info($"Source opened: {id.Kind}, {Core.Util.Bytes.FormatSize(id.Size)}, sector {id.SectorSize}");
    }

    public void CloseSource()
    {
        Source?.Dispose();
        Source = null;
        Identity = null;
        SourceDiskIds = Array.Empty<string>();
    }

    public void SetProject(ProjectDatabase db, string path)
    {
        Project?.Dispose();
        Project = db;
        ProjectPath = path;
        Selected.Clear();
        NotifySelection();
        Log.AttachFile(Path.Combine(Path.GetDirectoryName(path)!, "logs"));
        Settings.RecentProjects.Remove(path);
        Settings.RecentProjects.Insert(0, path);
        if (Settings.RecentProjects.Count > 20) Settings.RecentProjects.RemoveRange(20, Settings.RecentProjects.Count - 20);
        try { Settings.Save(); } catch (IOException) { }
        Log.Info("Project opened");
    }

    /// <summary>Reopens the scanned source for a project (image path, or the physical disk with matching identity).</summary>
    public string? TryReopenSourceFor(SourceIdentity id)
    {
        try
        {
            if (id.Kind == "Image")
            {
                if (!File.Exists(id.Path)) return $"The disk image '{id.Path}' is not available. Previews and restore need it.";
                var img = ImageOpener.Open(id.Path);
                if (img.Length != id.Size) { img.Dispose(); return "The image file has a different size than when it was scanned."; }
                SetSource(img, id, Array.Empty<string>());
                SourceConfirmed = true;
                return null;
            }
            var disk = Disks.Enumerate().FirstOrDefault(d => d.Size == id.Size && string.Equals(d.SerialNumber?.Trim(), id.SerialNumber?.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(d.Model?.Trim(), id.Model?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (disk == null) return "The scanned disk is not connected (no disk with the same model, serial number and size). Results can be browsed, but previews and restore need the disk.";
            if (!Disks.IsElevated) return "Reading the scanned disk requires administrator rights. Use 'Restart as administrator'.";
            SetSource(Disks.OpenReadOnly(disk), id, new[] { disk.Id });
            SelectedDisk = disk;
            SourceConfirmed = true;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return e.Message;
        }
    }
}
