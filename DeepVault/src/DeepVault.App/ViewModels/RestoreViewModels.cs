using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using DeepVault.App.Services;
using DeepVault.Core.Catalog;
using DeepVault.Core.Restore;
using DeepVault.Core.Scanning;
using DeepVault.Core.Util;
using Microsoft.Win32;

namespace DeepVault.App.ViewModels;

public sealed class DestRow
{
    public string Root { get; init; } = "";
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public bool IsSource { get; init; }
    public bool IsExternal { get; init; }
    public string Title => $"{Root.TrimEnd('\\')}  {Label}";
    public string Detail => $"{FileSystem} • {Bytes.FormatSize(Free)} free of {Bytes.FormatSize(Total)}";
    public bool Selectable => !IsSource;
}

public sealed class RestoreViewModel : PageViewModel
{
    public override string Title => "Restore selected files";
    public ObservableCollection<DestRow> Destinations { get; } = new();
    private DestRow? _dest;
    public DestRow? SelectedDestination
    {
        get => _dest;
        set
        {
            if (!Set(ref _dest, value) || value == null) return;
            Folder = value.IsSource ? "" : value.Root;
        }
    }
    private string _folder = "";
    public string Folder { get => _folder; set { if (Set(ref _folder, value)) Check(); } }
    private bool _session = true;
    public bool CreateSessionFolder { get => _session; set { if (Set(ref _session, value)) Check(); } }
    private bool _preserve = true;
    public bool PreserveFolders { get => _preserve; set => Set(ref _preserve, value); }
    private CollisionPolicy _collision = CollisionPolicy.RenameNew;
    public CollisionPolicy Collision { get => _collision; set => Set(ref _collision, value); }

    private string _selectionText = "";
    public string SelectionText { get => _selectionText; set => Set(ref _selectionText, value); }
    private string _spaceText = "";
    public string SpaceText { get => _spaceText; set => Set(ref _spaceText, value); }
    private bool _spaceOk;
    public bool SpaceOk { get => _spaceOk; set => Set(ref _spaceOk, value); }
    public ObservableCollection<string> Blockers { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> NotRestorable { get; } = new();
    private RestorePreflight? _pf;
    private List<FoundFile> _files = new();

    public ICommand Browse { get; }
    public ICommand Refresh { get; }
    public ICommand Start { get; }
    public ICommand BackToResults { get; }

    public RestoreViewModel(MainViewModel m) : base(m)
    {
        Browse = new RelayCommand(() =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose where to save recovered files (not the drive being recovered)" };
            if (dlg.ShowDialog() == true) Folder = dlg.FolderName;
        });
        Refresh = new RelayCommand(OnShown);
        Start = new RelayCommand(() => _ = StartAsync(), () => _pf?.CanProceed == true && S.HasSource && !Main.Page<RestoreProgressViewModel>("restoreprogress").IsRunning);
        BackToResults = new RelayCommand(() => Main.Navigate("results"));
    }

    public override void OnShown()
    {
        Destinations.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
                var info = S.Inspector.Inspect(d.RootDirectory.FullName);
                bool isSrc = info.PhysicalDisks.Any(x => S.SourceDiskIds.Contains(x));
                Destinations.Add(new DestRow
                {
                    Root = d.RootDirectory.FullName, Label = string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel, FileSystem = d.DriveFormat,
                    Total = d.TotalSize, Free = d.AvailableFreeSpace, IsSource = isSrc, IsExternal = d.DriveType == DriveType.Removable || info.IsRemovable,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        _files = S.Project?.GetFiles(S.Selected.ToList()) ?? new();
        SelectionText = $"{_files.Count:N0} files selected, {Bytes.FormatSize(_files.Sum(f => f.Size))} of data";
        if (string.IsNullOrEmpty(Folder))
        {
            var ext = Destinations.FirstOrDefault(d => d.IsExternal && !d.IsSource) ?? Destinations.FirstOrDefault(d => !d.IsSource);
            if (ext != null) SelectedDestination = ext;
        }
        Check();
    }

    private RestoreService Service() => new(S.Source!, S.Identity!, S.Inspector, S.Project, null, S.Log) { SourceDisks = S.SourceDiskIds };

    private void Check()
    {
        Blockers.Clear(); Warnings.Clear(); NotRestorable.Clear();
        _pf = null;
        if (!S.HasProject) { Blockers.Add("Open a project first."); return; }
        if (!S.HasSource) { Blockers.Add("The scanned drive or image must be connected to restore files."); return; }
        if (_files.Count == 0) { Blockers.Add("No files selected. Tick files in Recovered files."); return; }
        if (string.IsNullOrWhiteSpace(Folder)) { Blockers.Add("Choose a destination drive or folder."); return; }
        _pf = Service().Preflight(Request());
        foreach (var b in _pf.Blockers) Blockers.Add(b);
        foreach (var w in _pf.Warnings) Warnings.Add(w);
        foreach (var (f, why) in _pf.NotRestorable.Take(200)) NotRestorable.Add($"{f.Name}: {why}");
        SpaceOk = _pf.EnoughSpace;
        SpaceText = $"Needed: about {Bytes.FormatSize(_pf.RequiredBytes)} • Free on destination: {Bytes.FormatSize(_pf.Destination.FreeBytes)} ({_pf.Destination.Label} {_pf.Destination.FileSystem})";
    }

    private RestoreRequest Request() => new()
    {
        Files = _files, DestinationFolder = Folder, CreateSessionFolder = CreateSessionFolder, PreserveFolders = PreserveFolders, Collisions = Collision, Verify = true,
    };

    private async Task StartAsync()
    {
        Check();
        if (_pf?.CanProceed != true) return;
        Main.Navigate("restoreprogress");
        await Main.Page<RestoreProgressViewModel>("restoreprogress").RunAsync(Service(), Request());
    }
}

public sealed class RestoreProgressViewModel : PageViewModel
{
    public override string Title => "Recovering files";
    private CancellationTokenSource? _cts;
    private bool _running;
    public bool IsRunning { get => _running; set => Set(ref _running, value); }
    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    private string _text = "Not started";
    public string ProgressText { get => _text; set => Set(ref _text, value); }
    private string _current = "";
    public string Current { get => _current; set => Set(ref _current, value); }
    public ICommand Cancel { get; }

    public RestoreProgressViewModel(MainViewModel m) : base(m)
    {
        Cancel = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
    }

    public async Task RunAsync(RestoreService svc, RestoreRequest req)
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        RestoreProgress? latest = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            var p = latest;
            if (p == null) return;
            Percent = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
            ProgressText = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files • {Bytes.FormatSize(p.BytesDone)} of {Bytes.FormatSize(p.BytesTotal)}";
            Current = p.CurrentName;
        };
        timer.Start();
        try
        {
            var res = await Task.Run(() => svc.Run(req, new SyncProgress<RestoreProgress>(p => latest = p), _cts.Token));
            S.LastRestore = res;
            Main.Navigate("report");
        }
        finally
        {
            timer.Stop();
            IsRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public sealed class ReportRow
{
    public string Outcome { get; init; } = "";
    public string Original { get; init; } = "";
    public string Target { get; init; } = "";
    public string Size { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class ReportViewModel : PageViewModel
{
    public override string Title => "Recovery report";
    public ObservableCollection<ReportRow> Rows { get; } = new();
    public ObservableCollection<KV> Counts { get; } = new();
    private string _headline = "";
    public string Headline { get => _headline; set => Set(ref _headline, value); }
    private string? _folder;
    public string? SessionFolder { get => _folder; set => Set(ref _folder, value); }
    private string? _html;
    public ICommand OpenReport { get; }
    public ICommand OpenFolder { get; }
    public ICommand BackToResults { get; }
    public string Disclaimer => "“Verified” means the restored file was read back from the destination and its SHA-256 matches the bytes read from the source, and its structure was checked where the format allows. It proves the copy is faithful to the source as it is now — it cannot prove the source data was not already partly overwritten. Open important files to confirm them.";

    public ReportViewModel(MainViewModel m) : base(m)
    {
        OpenReport = new RelayCommand(() => Shell(_html!), () => _html != null && File.Exists(_html));
        OpenFolder = new RelayCommand(() => Shell(SessionFolder!), () => SessionFolder != null && Directory.Exists(SessionFolder));
        BackToResults = new RelayCommand(() => Main.Navigate("results"));
    }

    private static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { AppState.Current.ReportError("Could not open", e); }
    }

    public override void OnShown()
    {
        Rows.Clear(); Counts.Clear();
        var r = S.LastRestore;
        if (r == null)
        {
            Headline = "No restore has been run in this session.";
            var runs = S.Project?.GetRestoreRuns();
            if (runs is { Count: > 0 })
            {
                var last = runs[0];
                Headline = $"Last restore of this project: {last.started.ToLocalTime():g} to {last.destination}.";
                SessionFolder = last.destination;
                foreach (var i in S.Project!.GetRestoreItems(last.id))
                    Rows.Add(new ReportRow { Outcome = i.Outcome, Target = i.TargetPath ?? "", Size = Bytes.FormatSize(i.Bytes), Message = i.Message ?? "" });
            }
            return;
        }
        SessionFolder = r.SessionFolder;
        _html = r.ReportHtml;
        foreach (RestoreOutcome o in Enum.GetValues<RestoreOutcome>()) Counts.Add(new KV(o switch
        {
            RestoreOutcome.Verified => "Restored and verified",
            RestoreOutcome.RestoredWithWarnings => "Restored with warnings",
            RestoreOutcome.Skipped => "Skipped",
            RestoreOutcome.Failed => "Failed",
            _ => "Not attempted",
        }, r.Count(o).ToString("N0")));
        Headline = r.StoppedDestinationFull ? "The destination became full. Remaining files were not attempted; nothing on the destination was deleted."
            : r.Cancelled ? "The restore was cancelled before all files were processed."
            : r.Count(RestoreOutcome.Failed) > 0 ? "Restore finished with failures. See details below."
            : "Restore finished.";
        foreach (var i in r.Items)
            Rows.Add(new ReportRow { Outcome = i.Outcome.ToString(), Original = i.OriginalPath, Target = i.TargetPath ?? "", Size = Bytes.FormatSize(i.WrittenBytes > 0 ? i.WrittenBytes : i.ExpectedSize), Message = i.Message });
    }
}

public sealed class ProjectRow
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Source { get; init; } = "";
    public string Status { get; init; } = "";
    public string Files { get; init; } = "";
    public string Created { get; init; } = "";
    public bool Exists { get; init; }
}

public sealed class ProjectsViewModel : PageViewModel
{
    public override string Title => "Saved scan projects";
    public ObservableCollection<ProjectRow> Projects { get; } = new();
    private ProjectRow? _sel;
    public ProjectRow? Selected { get => _sel; set => Set(ref _sel, value); }
    private string? _message;
    public string? Message { get => _message; set => Set(ref _message, value); }
    public ICommand Open { get; }
    public ICommand Browse { get; }
    public ICommand Forget { get; }

    public ProjectsViewModel(MainViewModel m) : base(m)
    {
        Open = new RelayCommand(() => OpenProject(Selected!.Path), () => Selected?.Exists == true);
        Browse = new RelayCommand(() =>
        {
            var dlg = new OpenFileDialog { Title = "Open a DeepVault project", Filter = "DeepVault project (*.dvproj)|*.dvproj" };
            if (dlg.ShowDialog() == true) OpenProject(dlg.FileName);
        });
        Forget = new RelayCommand(() =>
        {
            S.Settings.RecentProjects.Remove(Selected!.Path);
            try { S.Settings.Save(); } catch (IOException) { }
            OnShown();
        }, () => Selected != null);
    }

    public override void OnShown()
    {
        Projects.Clear();
        foreach (var p in S.Settings.RecentProjects)
        {
            if (!File.Exists(p)) { Projects.Add(new ProjectRow { Path = p, Status = "File not found (drive disconnected?)" }); continue; }
            try
            {
                using var db = ProjectDatabase.Open(p);
                var id = db.GetMetaJson<SourceIdentity>("source");
                Projects.Add(new ProjectRow
                {
                    Path = p, Exists = true, Source = id?.Describe() ?? "unknown", Status = db.GetMeta("scan_status") ?? "not started",
                    Files = db.CountView(ResultView.All).ToString("N0"), Created = DateTime.TryParse(db.GetMeta("created"), out var c) ? c.ToLocalTime().ToString("g") : "",
                });
            }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or JsonException) { Projects.Add(new ProjectRow { Path = p, Status = "Cannot be opened: " + e.Message }); }
        }
    }

    public void OpenProject(string path)
    {
        try
        {
            var db = ProjectDatabase.Open(path);
            var id = db.GetMetaJson<SourceIdentity>("source");
            S.SetProject(db, path);
            S.LastSummary = db.GetMetaJson<ScanSummary>("scan_summary");
            S.ScanOptions = db.GetMetaJson<ScanOptions>("scan_options") ?? new ScanOptions();
            Message = id == null ? "This project has no source information." : S.TryReopenSourceFor(id);
            Main.Navigate("results");
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { Message = "Could not open the project: " + e.Message; }
    }
}

public sealed class EngineRow
{
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Can { get; init; } = "";
    public string Cannot { get; init; } = "";
}

public sealed class SettingsViewModel : PageViewModel
{
    public override string Title => "Settings & supported formats";
    public AppSettings Settings => S.Settings;
    public ObservableCollection<FormatRow> Formats { get; } = new();
    public ObservableCollection<EngineRow> Engines { get; } = new();
    private string? _message;
    public string? Message { get => _message; set => Set(ref _message, value); }
    public ICommand Save { get; }
    public ICommand BrowseSignatures { get; }

    public SettingsViewModel(MainViewModel m) : base(m)
    {
        var reg = Core.Carving.SignatureRegistry.Create(S.Settings.CustomSignaturesPath);
        foreach (var f in reg.Formats)
            Formats.Add(new FormatRow { Id = f.Id, Name = f.Name, Extension = f.DefaultExtension, Category = FileTypes.CategoryName(f.Category), Description = f.Description, Checksums = f.HasChecksums, Enabled = f.EnabledByDefault });
        foreach (var e in Core.Engines.EngineRegistry.All) Engines.Add(new EngineRow { Name = e.Name, Summary = e.Summary, Can = e.Capabilities, Cannot = e.Limitations });
        Save = new RelayCommand(() =>
        {
            try { S.Settings.Save(); Message = "Settings saved."; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = "Could not save: " + e.Message; }
        });
        BrowseSignatures = new RelayCommand(() =>
        {
            var dlg = new OpenFileDialog { Title = "Custom signature definitions (JSON)", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog() == true) { S.Settings.CustomSignaturesPath = dlg.FileName; Raise(nameof(Settings)); }
        });
    }
}

public sealed class DiagnosticsViewModel : PageViewModel
{
    public override string Title => "Diagnostics & logs";
    public ObservableCollection<LogLine> Lines => S.Log.Lines;
    public ObservableCollection<KV> SystemInfo { get; } = new();
    public ICommand Copy { get; }
    public ICommand OpenLogFolder { get; }

    public DiagnosticsViewModel(MainViewModel m) : base(m)
    {
        Copy = new RelayCommand(() => System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
            SystemInfo.Select(k => $"{k.Label}: {k.Value}").Concat(Lines.Select(l => $"{l.Time:O} {l.Level} {l.Message}")))));
        OpenLogFolder = new RelayCommand(() => Process.Start(new ProcessStartInfo(S.Log.FileDirectory!) { UseShellExecute = true }), () => S.Log.FileDirectory != null);
    }

    public override void OnShown()
    {
        SystemInfo.Clear();
        SystemInfo.Add(new("DeepVault", typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString() ?? "?"));
        SystemInfo.Add(new("Windows", Environment.OSVersion.VersionString));
        SystemInfo.Add(new(".NET", Environment.Version.ToString()));
        SystemInfo.Add(new("Process", Environment.Is64BitProcess ? "64-bit" : "32-bit"));
        SystemInfo.Add(new("Administrator", Main.IsElevated ? "yes" : "no"));
        SystemInfo.Add(new("Log folder", S.Log.FileDirectory ?? "not yet written to disk (logs are kept in memory until a project folder is chosen)"));
        SystemInfo.Add(new("Network / telemetry", "none — DeepVault never connects to the internet"));
    }
}
