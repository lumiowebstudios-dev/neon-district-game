using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeepVault.App.Services;
using DeepVault.Core.Carving;
using DeepVault.Core.Catalog;
using DeepVault.Core.Engines;
using DeepVault.Core.IO;
using DeepVault.Core.Partitions;
using DeepVault.Core.Scanning;
using DeepVault.Core.Util;
using Microsoft.Win32;

namespace DeepVault.App.ViewModels;

public sealed record KV(string Label, string Value);

public sealed class WelcomeViewModel : PageViewModel
{
    public override string Title => "Welcome";
    public ICommand Start { get; }
    public ICommand OpenProjects { get; }
    public WelcomeViewModel(MainViewModel m) : base(m)
    {
        Start = new RelayCommand(() => Main.Navigate("source"));
        OpenProjects = new RelayCommand(() => Main.Navigate("projects"));
    }
}

public sealed class DiskRow
{
    public PhysicalDiskInfo Disk { get; init; } = null!;
    public string Name => Disk.DisplayName;
    public string Identity => $"{Disk.BusType ?? "Unknown bus"} • serial {Disk.SerialNumber ?? "not reported"} • {Disk.SectorSize}-byte sectors";
    public string Volumes => Disk.Volumes.Count == 0 ? "No mounted volumes (may contain lost or unrecognised partitions)"
        : string.Join("    ", Disk.Volumes.Select(v => $"{v.DriveLetter} {v.Label} ({v.FileSystem}, {Bytes.FormatSize(v.Length)})"));
    public bool IsSystem => Disk.IsSystemDisk;
    public bool IsExternal => Disk.LooksExternal;
    public bool FailurePredicted => Disk.FailurePredicted == true;
    public bool HostsDeepVault => Disk.HostsDeepVault && !Disk.IsSystemDisk;
}

public sealed class SourceViewModel : PageViewModel
{
    public override string Title => "Select the drive to recover from";
    public ObservableCollection<DiskRow> Disks { get; } = new();
    private DiskRow? _sel;
    public DiskRow? SelectedDisk { get => _sel; set => Set(ref _sel, value); }
    private bool _loading;
    public bool Loading { get => _loading; set => Set(ref _loading, value); }
    private string? _message;
    public string? Message { get => _message; set => Set(ref _message, value); }
    public bool IsElevated => Main.IsElevated;
    public ICommand Refresh { get; }
    public ICommand OpenImage { get; }
    public ICommand UseDisk { get; }

    public SourceViewModel(MainViewModel m) : base(m)
    {
        Refresh = new AsyncCommand(LoadAsync);
        OpenImage = new RelayCommand(DoOpenImage);
        UseDisk = new RelayCommand(DoUseDisk, () => SelectedDisk != null);
    }

    public override void OnShown()
    {
        if (Disks.Count == 0 && !Loading) Refresh.Execute(null);
    }

    public async Task LoadAsync()
    {
        Loading = true;
        Message = null;
        try
        {
            S.Disks.WorkFolder = S.ProjectPath;
            var list = await Task.Run(() => S.Disks.Enumerate());
            Disks.Clear();
            foreach (var d in list) Disks.Add(new DiskRow { Disk = d });
            if (list.Count == 0) Message = "No disks were found. On Windows this usually means the disk list could not be queried.";
        }
        catch (Exception e) { Message = "Could not list disks: " + e.Message; }
        finally { Loading = false; }
    }

    private void DoUseDisk()
    {
        var d = SelectedDisk!.Disk;
        if (!S.Disks.IsElevated)
        {
            Message = "Reading a whole disk needs administrator rights (Windows only allows administrators to read raw sectors). Use “Restart as administrator” above, then select the disk again.";
            return;
        }
        try
        {
            var src = S.Disks.OpenReadOnly(d);
            S.SelectedDisk = d;
            S.SelectedImagePath = null;
            S.SetSource(src, new SourceIdentity
            {
                Kind = "PhysicalDisk", Path = d.DevicePath, Model = d.Model, SerialNumber = d.SerialNumber, BusType = d.BusType,
                Size = src.Length, SectorSize = src.SectorSize, IsSystemDisk = d.IsSystemDisk,
            }, new[] { d.Id });
            Main.Navigate("details");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = e.Message; }
    }

    private void DoOpenImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open a disk image (opened read-only)",
            Filter = "Disk images (*.img;*.dd;*.raw;*.bin;*.001;*.vhd;*.iso)|*.img;*.dd;*.raw;*.bin;*.001;*.vhd;*.iso|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        OpenImagePath(dlg.FileName);
    }

    public void OpenImagePath(string path)
    {
        try
        {
            var src = ImageOpener.Open(path);
            S.SelectedDisk = null;
            S.SelectedImagePath = path;
            S.SetSource(src, new SourceIdentity { Kind = "Image", Path = Path.GetFullPath(path), Size = src.Length, SectorSize = src.SectorSize }, Array.Empty<string>());
            Main.Navigate("details");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException) { Message = e.Message; }
    }
}

public sealed class PartitionRow
{
    public string Index { get; init; } = "";
    public string Scheme { get; init; } = "";
    public string Status { get; init; } = "";
    public string Start { get; init; } = "";
    public string Size { get; init; } = "";
    public string Type { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class DetailsViewModel : PageViewModel
{
    public override string Title => "Confirm the drive";
    public ObservableCollection<PartitionRow> Partitions { get; } = new();
    public ObservableCollection<string> TableIssues { get; } = new();
    public ObservableCollection<KV> Identity { get; } = new();
    private string _confirmText = "";
    public string ConfirmText { get => _confirmText; set => Set(ref _confirmText, value); }
    private bool _confirmed;
    public bool Confirmed { get => _confirmed; set { Set(ref _confirmed, value); S.SourceConfirmed = value && (!IsSystemDisk || AcceptLiveRisk); } }
    private bool _acceptLive;
    public bool AcceptLiveRisk { get => _acceptLive; set { Set(ref _acceptLive, value); Confirmed = _confirmed; } }
    public bool IsSystemDisk { get; private set; }
    public bool IsImage { get; private set; }
    public bool FailurePredicted { get; private set; }
    public string Scheme { get; private set; } = "";
    public ICommand Next { get; }
    public ICommand Back { get; }

    public DetailsViewModel(MainViewModel m) : base(m)
    {
        Next = new RelayCommand(() => Main.Navigate("method"), () => S.HasSource && S.SourceConfirmed);
        Back = new RelayCommand(() => Main.Navigate("source"));
    }

    public override void OnShown()
    {
        Identity.Clear(); Partitions.Clear(); TableIssues.Clear();
        var id = S.Identity;
        if (id == null || S.Source == null) { ConfirmText = "No drive selected yet."; return; }
        IsImage = id.Kind == "Image";
        IsSystemDisk = id.IsSystemDisk;
        FailurePredicted = S.SelectedDisk?.FailurePredicted == true;
        if (IsImage)
        {
            Identity.Add(new KV("Disk image", id.Path));
            Identity.Add(new KV("Size", $"{Bytes.FormatSize(id.Size)} ({id.Size:N0} bytes)"));
        }
        else
        {
            var d = S.SelectedDisk!;
            Identity.Add(new KV("Model", d.Model ?? "not reported"));
            Identity.Add(new KV("Serial number", d.SerialNumber ?? "not reported"));
            Identity.Add(new KV("Capacity", $"{Bytes.FormatSize(d.Size)} ({d.Size:N0} bytes)"));
            Identity.Add(new KV("Connection", d.BusType ?? "unknown"));
            Identity.Add(new KV("Sector size", $"{d.SectorSize} logical / {d.PhysicalSectorSize} physical"));
            Identity.Add(new KV("Windows device", $"Disk {d.Number} ({d.DevicePath})"));
            Identity.Add(new KV("Mounted volumes", d.Volumes.Count == 0 ? "none" : string.Join(", ", d.Volumes.Select(v => v.Describe()))));
            Identity.Add(new KV("SMART status", d.FailurePredicted switch { true => "FAILURE PREDICTED by the drive", false => "No failure predicted", _ => "not reported" }));
        }
        try
        {
            var pt = PartitionTableParser.Parse(S.Source);
            S.Partitions = pt;
            Scheme = pt.Scheme switch { PartitionScheme.Gpt => "GPT", PartitionScheme.Mbr => "MBR", _ => "No partition table" };
            foreach (var p in pt.Partitions)
                Partitions.Add(new PartitionRow
                {
                    Index = p.Index.ToString(), Scheme = p.Scheme.ToString(), Status = p.Status switch
                    {
                        PartitionStatus.Confirmed => "Readable", PartitionStatus.InTableNoFileSystem => "No recognised file system",
                        PartitionStatus.WholeDevice => "Whole device", _ => p.Status.ToString(),
                    },
                    Start = Bytes.FormatSize(p.Offset), Size = Bytes.FormatSize(p.Length), Type = p.TypeDescription + (p.Name != null ? $" “{p.Name}”" : ""),
                    FileSystem = FileSystemProbe.Describe(p.FileSystem), Detail = p.Boot != null ? $"cluster {Bytes.FormatSize(p.Boot.ClusterSize)}" : "",
                });
            foreach (var i in pt.Issues) TableIssues.Add(i);
            if (pt.Partitions.Count == 0) TableIssues.Add("No readable partitions were found. The Deep Scan searches the whole disk for lost volumes and files.");
        }
        catch (IOException e) { TableIssues.Add("The partition table could not be read: " + e.Message); }
        ConfirmText = IsImage
            ? $"I want to scan this image: {Path.GetFileName(id.Path)} ({Bytes.FormatSize(id.Size)})."
            : $"I have checked that this is the drive I want to recover from: {S.SelectedDisk!.Model}, {Bytes.FormatSize(id.Size)}, serial {S.SelectedDisk.SerialNumber ?? "n/a"}.";
        Confirmed = false;
        AcceptLiveRisk = false;
        Raise(nameof(IsSystemDisk)); Raise(nameof(IsImage)); Raise(nameof(FailurePredicted)); Raise(nameof(Scheme));
    }
}

public sealed class FormatRow : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Extension { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Checksums { get; init; }
    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
}

public enum ScanMode { Deep, Quick, Custom }

public sealed class MethodViewModel : PageViewModel
{
    public override string Title => "Choose how to scan";
    private ScanMode _mode = ScanMode.Deep;
    public ScanMode Mode { get => _mode; set { Set(ref _mode, value); ApplyMode(); } }
    private bool _meta = true, _lost = true, _carve = true, _orphan = true;
    public bool UseMetadata { get => _meta; set { Set(ref _meta, value); Validate(); } }
    public bool UseLostPartitions { get => _lost; set { Set(ref _lost, value); Validate(); } }
    public bool UseCarving { get => _carve; set { Set(ref _carve, value); Validate(); } }
    public bool UseOrphanRecords { get => _orphan; set { Set(ref _orphan, value); Validate(); } }
    public bool CustomEnabled => Mode == ScanMode.Custom;
    private CarveScope _scope = CarveScope.UnallocatedAndUnpartitioned;
    public CarveScope Scope { get => _scope; set => Set(ref _scope, value); }
    private bool _existing;
    public bool IncludeExisting { get => _existing; set => Set(ref _existing, value); }
    public ObservableCollection<FormatRow> Formats { get; } = new();

    private string _projectFolder = "";
    public string ProjectFolder { get => _projectFolder; set { Set(ref _projectFolder, value); Validate(); } }
    private string _projectName = "";
    public string ProjectName { get => _projectName; set { Set(ref _projectName, value); Validate(); } }
    private string? _projectWarning;
    public string? ProjectWarning { get => _projectWarning; set => Set(ref _projectWarning, value); }
    private bool _acceptProjectOnSource;
    public bool AcceptProjectOnSource { get => _acceptProjectOnSource; set => Set(ref _acceptProjectOnSource, value); }
    private string? _error;
    public string? Error { get => _error; set => Set(ref _error, value); }
    private bool _resume;
    public bool ResumeExisting { get => _resume; set => Set(ref _resume, value); }

    public ICommand BrowseFolder { get; }
    public ICommand Start { get; }
    public ICommand SelectAllFormats { get; }
    public ICommand SelectNoFormats { get; }

    public MethodViewModel(MainViewModel m) : base(m)
    {
        foreach (var f in SignatureRegistry.Create(S.Settings.CustomSignaturesPath).Formats)
            Formats.Add(new FormatRow { Id = f.Id, Name = f.Name, Extension = f.DefaultExtension, Category = FileTypes.CategoryName(f.Category), Description = f.Description, Checksums = f.HasChecksums, Enabled = f.EnabledByDefault });
        BrowseFolder = new RelayCommand(() =>
        {
            var dlg = new OpenFolderDialog { Title = "Folder for the scan project (results database and logs)" };
            if (dlg.ShowDialog() == true) ProjectFolder = dlg.FolderName;
        });
        Start = new RelayCommand(() => _ = StartAsync(), CanStart);
        SelectAllFormats = new RelayCommand(() => { foreach (var f in Formats) f.Enabled = true; });
        SelectNoFormats = new RelayCommand(() => { foreach (var f in Formats) f.Enabled = false; });
    }

    public override void OnShown()
    {
        if (string.IsNullOrEmpty(ProjectFolder)) ProjectFolder = SuggestProjectFolder();
        if (string.IsNullOrEmpty(ProjectName) && S.Identity != null)
            ProjectName = $"DeepVault {(S.Identity.Kind == "Image" ? Path.GetFileNameWithoutExtension(S.Identity.Path) : S.SelectedDisk?.Model ?? "disk")} {DateTime.Now:yyyy-MM-dd HHmm}";
        Validate();
    }

    private void ApplyMode()
    {
        if (Mode == ScanMode.Deep) { UseMetadata = UseLostPartitions = UseCarving = UseOrphanRecords = true; }
        else if (Mode == ScanMode.Quick) { UseMetadata = true; UseLostPartitions = UseCarving = UseOrphanRecords = false; }
        Raise(nameof(CustomEnabled));
    }

    /// <summary>Prefers a lettered volume that is NOT on the source disk (e.g. the USB drive).</summary>
    private string SuggestProjectFolder()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable).OrderByDescending(d => d.AvailableFreeSpace))
            {
                var info = S.Inspector.Inspect(d.RootDirectory.FullName);
                if (info.PhysicalDisks.Count > 0 && !info.PhysicalDisks.Any(x => S.SourceDiskIds.Contains(x)) && d.AvailableFreeSpace > 2L << 30)
                    return Path.Combine(d.RootDirectory.FullName, "DeepVault Projects");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepVault", "Projects");
    }

    private void Validate()
    {
        Error = null;
        ProjectWarning = null;
        if (!UseMetadata && !UseLostPartitions && !UseCarving && !UseOrphanRecords) Error = "Select at least one scan method.";
        if (string.IsNullOrWhiteSpace(ProjectFolder) || string.IsNullOrWhiteSpace(ProjectName)) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(ProjectFolder));
            var info = S.Inspector.Inspect(root ?? ProjectFolder);
            if (info.PhysicalDisks.Any(x => S.SourceDiskIds.Contains(x)))
                ProjectWarning = "The project folder is on the disk you are recovering from. The results database (often tens to hundreds of MB) and logs would be written there and could overwrite recoverable files. Choose a folder on another drive — for example your USB drive — unless no other drive is available.";
            else if (S.Identity?.Kind == "Image" && info.FreeBytes < 512L << 20)
                ProjectWarning = "Less than 512 MB is free in the project location; large scans need more.";
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { Error = "Invalid project folder: " + e.Message; }
        var path = ProjectFile;
        ResumeExisting = File.Exists(path);
    }

    public string ProjectFile => Path.Combine(ProjectFolder, SafeFile(ProjectName) + ProjectDatabase.FileExtension);
    private static string SafeFile(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private bool CanStart() => S.HasSource && S.SourceConfirmed && Error == null && !string.IsNullOrWhiteSpace(ProjectFolder) && !string.IsNullOrWhiteSpace(ProjectName)
                               && (ProjectWarning == null || !ProjectWarning.Contains("disk you are recovering") || AcceptProjectOnSource)
                               && !Main.Page<ProgressViewModel>("progress").IsRunning;

    public ScanOptions BuildOptions() => new()
    {
        Methods = (UseMetadata ? ScanMethods.FileSystemMetadata : 0) | ScanMethods.PartitionAnalysis
                  | (UseLostPartitions ? ScanMethods.LostPartitionSearch : 0) | (UseCarving ? ScanMethods.SignatureCarving : 0) | (UseOrphanRecords ? ScanMethods.OrphanMftRecords : 0),
        Scope = Scope,
        IncludeExistingFiles = IncludeExisting,
        EnabledFormats = Formats.Where(f => f.Enabled).Select(f => f.Id).ToHashSet(),
        CustomSignaturesPath = S.Settings.CustomSignaturesPath,
        StopWhenDriveFailing = S.Settings.StopWhenDriveFailing,
        BadSectorStopThreshold = S.Settings.BadSectorStopThreshold,
        ValidateUpTo = S.Settings.ValidateUpToMb * 1024 * 1024,
    };

    public async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(ProjectFolder);
            var path = ProjectFile;
            ProjectDatabase db;
            ScanOptions opts;
            if (File.Exists(path))
            {
                db = ProjectDatabase.Open(path);
                var saved = db.GetMetaJson<SourceIdentity>("source");
                if (saved != null && !saved.Matches(S.Identity!)) { db.Dispose(); Error = "That project belongs to a different drive. Choose another project name."; return; }
                opts = db.GetMetaJson<ScanOptions>("scan_options") ?? BuildOptions();
            }
            else
            {
                db = ProjectDatabase.Create(path);
                db.SetMetaJson("source", S.Identity!);
                db.SetMeta("mode", S.Identity!.Kind == "Image" ? "image" : S.Identity.IsSystemDisk ? "live-system-disk" : "live-disk");
                opts = BuildOptions();
            }
            S.SetProject(db, path);
            S.ScanOptions = opts;
            Main.Navigate("progress");
            await Main.Page<ProgressViewModel>("progress").RunAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = e.Message; }
    }
}

public sealed class PhaseRow : ObservableObject
{
    public string Name { get; init; } = "";
    private string _state = "Pending";
    public string State { get => _state; set => Set(ref _state, value); }
}

public sealed class ProgressViewModel : PageViewModel
{
    public override string Title => "Scanning";
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;
    private ScanProgress? _latest;

    public ObservableCollection<PhaseRow> Phases { get; } = new();
    public ObservableCollection<FoundFile> RecentFinds { get; } = new();
    private bool _running;
    public bool IsRunning { get => _running; set { Set(ref _running, value); Raise(nameof(NotRunning)); } }
    public bool NotRunning => !IsRunning;
    private string _phase = "Not started";
    public string PhaseText { get => _phase; set => Set(ref _phase, value); }
    private string _detail = "";
    public string DetailText { get => _detail; set => Set(ref _detail, value); }
    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    private bool _indeterminate;
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    private string _amount = "";
    public string AmountText { get => _amount; set => Set(ref _amount, value); }
    private string _speed = "";
    public string SpeedText { get => _speed; set => Set(ref _speed, value); }
    private string _elapsed = "";
    public string ElapsedText { get => _elapsed; set => Set(ref _elapsed, value); }
    private long _found;
    public long FilesFound { get => _found; set => Set(ref _found, value); }
    private long _bad;
    public long BadSectors { get => _bad; set => Set(ref _bad, value); }
    private int _lost;
    public int LostPartitions { get => _lost; set => Set(ref _lost, value); }
    private long _orphans;
    public long OrphanRecords { get => _orphans; set => Set(ref _orphans, value); }
    private bool _health;
    public bool HealthWarning { get => _health; set => Set(ref _health, value); }
    private string? _resultText;
    public string? ResultText { get => _resultText; set => Set(ref _resultText, value); }
    private bool _resultIsWarning;
    public bool ResultIsWarning { get => _resultIsWarning; set => Set(ref _resultIsWarning, value); }
    public bool IsLiveSystemDisk => S.Identity?.IsSystemDisk == true;

    public ICommand Stop { get; }
    public ICommand ViewResults { get; }
    public ICommand ViewIssues { get; }
    public ICommand Resume { get; }

    public ProgressViewModel(MainViewModel m) : base(m)
    {
        Stop = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        ViewResults = new RelayCommand(() => Main.Navigate("results"), () => S.HasProject);
        ViewIssues = new RelayCommand(() => Main.Navigate("issues"), () => S.HasProject);
        Resume = new AsyncCommand(RunAsync, () => !IsRunning && S.HasProject && S.HasSource && S.LastSummary is { IsExhaustive: false });
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Refresh();
    }

    public async Task RunAsync()
    {
        if (IsRunning || S.Project == null || S.Source == null) return;
        var opts = S.ScanOptions;
        Phases.Clear();
        Phases.Add(new PhaseRow { Name = "Partition table" });
        if (opts.Methods.HasFlag(ScanMethods.FileSystemMetadata)) Phases.Add(new PhaseRow { Name = "File-system metadata (deleted files with names)" });
        if ((opts.Methods & (ScanMethods.SignatureCarving | ScanMethods.LostPartitionSearch | ScanMethods.OrphanMftRecords)) != 0) Phases.Add(new PhaseRow { Name = "Deep sector scan (signatures, lost volumes, old MFT records)" });
        if (opts.Methods.HasFlag(ScanMethods.LostPartitionSearch)) Phases.Add(new PhaseRow { Name = "Analyse lost volumes" });
        if (opts.Methods.HasFlag(ScanMethods.OrphanMftRecords)) Phases.Add(new PhaseRow { Name = "Rebuild files from previous file system" });
        Phases.Add(new PhaseRow { Name = "Combine and de-duplicate results" });
        IsRunning = true;
        ResultText = null;
        HealthWarning = false;
        Raise(nameof(IsLiveSystemDisk));
        _cts = new CancellationTokenSource();
        _timer.Start();
        var db = S.Project; var src = S.Source;
        var progress = new SyncProgress<ScanProgress>(p => _latest = p);
        S.Log.Info($"Scan starting: {opts.Methods}, scope {opts.Scope}");
        try
        {
            var sum = await Task.Run(() => new ScanOrchestrator(src, db, opts, S.Log).Run(progress, _cts.Token));
            S.LastSummary = sum;
            Refresh();
            Indeterminate = false;
            bool finished = sum.Outcome is ScanOutcome.Completed or ScanOutcome.CompletedWithGaps;
            if (finished) { Percent = 100; PhaseText = "Scan finished"; DetailText = ""; AmountText = ""; SpeedText = ""; }
            else PhaseText = "Scan stopped";
            FilesFound = sum.FilesFound;
            RecentFinds.Clear();
            foreach (var f in db.QueryFiles(new FileQuery { Sort = SortField.Id, Descending = true, Limit = 10 })) RecentFinds.Add(f);
            foreach (var ph in Phases) if (ph.State == "Running") ph.State = sum.Outcome is ScanOutcome.Completed or ScanOutcome.CompletedWithGaps ? "Done" : "Stopped";
            if (sum.Outcome is ScanOutcome.Completed or ScanOutcome.CompletedWithGaps) foreach (var ph in Phases) ph.State = "Done";
            ResultIsWarning = sum.Outcome != ScanOutcome.Completed;
            ResultText = sum.Outcome switch
            {
                ScanOutcome.Completed => $"Scan complete for the selected methods and scope. {sum.FilesFound:N0} candidates found in {sum.Duration:hh\\:mm\\:ss}.",
                ScanOutcome.CompletedWithGaps => $"Scan finished, but {sum.BadSectors:N0} sectors could not be read: it is NOT complete for those areas (see Scan issues). {sum.FilesFound:N0} candidates found.",
                ScanOutcome.Cancelled => $"Scan stopped by you. It is NOT complete. {sum.FilesFound:N0} candidates found so far; you can resume later.",
                ScanOutcome.StoppedDriveHealth => "Scan stopped automatically: the drive returned many read errors and may be failing. Continuing can damage it further. Consider professional data recovery, or image the drive with a dedicated tool before scanning again.",
                _ => $"Scan failed: {sum.Error}",
            };
        }
        finally
        {
            _timer.Stop();
            IsRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private int _tick;

    private void Refresh()
    {
        var p = _latest;
        if (p == null) return;
        PhaseText = p.Phase;
        DetailText = p.Detail;
        for (int i = 0; i < Phases.Count; i++)
            Phases[i].State = i < p.PhaseIndex ? "Done" : i == p.PhaseIndex ? "Running" : Phases[i].State == "Done" ? "Done" : "Pending";
        Indeterminate = p.Total <= 0;
        Percent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        AmountText = p.Total > 0
            ? (p.Unit == "bytes" ? $"{Bytes.FormatSize(p.Done)} of {Bytes.FormatSize(p.Total)} ({Percent:0.0}%)" : $"{p.Done:N0} of {p.Total:N0} {p.Unit} ({Percent:0.0}%)")
            : (p.Done > 0 ? $"{p.Done:N0} {p.Unit} processed — total not known in advance" : "Working…");
        SpeedText = p.Unit == "bytes" && p.BytesPerSecond > 0
            ? $"{Bytes.FormatSize((long)p.BytesPerSecond)}/s" + (p.Total > 0 ? $" • about {TimeSpan.FromSeconds((p.Total - p.Done) / Math.Max(1, p.BytesPerSecond)):hh\\:mm\\:ss} left in this phase (estimate)" : "")
            : "";
        ElapsedText = $"{p.Elapsed:hh\\:mm\\:ss}";
        FilesFound = p.FilesFound;
        BadSectors = p.BadSectors;
        LostPartitions = p.LostPartitionsFound;
        OrphanRecords = p.OrphanRecordsFound;
        HealthWarning = p.DriveHealthWarning;
        if (++_tick % 5 == 0 && S.Project != null)
        {
            RecentFinds.Clear();
            foreach (var f in S.Project.QueryFiles(new FileQuery { Sort = SortField.Id, Descending = true, Limit = 10 })) RecentFinds.Add(f);
        }
    }
}

public sealed class IssueRow
{
    public string Severity { get; init; } = "";
    public string Time { get; init; } = "";
    public string Message { get; init; } = "";
    public string Where { get; init; } = "";
}

public sealed class IssuesViewModel : PageViewModel
{
    public override string Title => "Scan issues and unreadable regions";
    public ObservableCollection<IssueRow> Issues { get; } = new();
    public ObservableCollection<IssueRow> Regions { get; } = new();
    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    private bool _complete;
    public bool IsComplete { get => _complete; set => Set(ref _complete, value); }
    public ICommand CopyAll { get; }

    public IssuesViewModel(MainViewModel m) : base(m)
    {
        CopyAll = new RelayCommand(() => Clipboard.SetText(string.Join(Environment.NewLine,
            Regions.Select(r => $"{r.Severity}\t{r.Where}\t{r.Message}").Concat(Issues.Select(i => $"{i.Time}\t{i.Severity}\t{i.Message}")))));
    }

    public override void OnShown()
    {
        Issues.Clear(); Regions.Clear();
        var db = S.Project;
        if (db == null) { Summary = "Open or run a scan first."; return; }
        var status = db.GetMeta("scan_status") ?? "not started";
        var sum = db.GetMetaJson<ScanSummary>("scan_summary");
        IsComplete = status == nameof(ScanOutcome.Completed);
        Summary = status switch
        {
            nameof(ScanOutcome.Completed) => "The scan completed every selected method over its whole scope with no unreadable areas.",
            nameof(ScanOutcome.CompletedWithGaps) => "The scan finished but some areas could not be read. Files in those areas may be incomplete.",
            nameof(ScanOutcome.Cancelled) => "The scan was stopped before it finished. Results are NOT exhaustive.",
            nameof(ScanOutcome.StoppedDriveHealth) => "The scan was stopped because the drive appears to be failing. Results are NOT exhaustive.",
            _ => $"Scan status: {status}.",
        };
        if (sum != null && sum.BytesExcludedByScope > 0)
            Summary += $" {Bytes.FormatSize(sum.BytesExcludedByScope)} used by existing files was intentionally not carved (scope setting).";
        foreach (var r in db.GetRegions())
            Regions.Add(new IssueRow
            {
                Severity = r.Kind switch { RegionKind.Unreadable => "Unreadable", RegionKind.SkippedAfterErrors => "Skipped (errors)", RegionKind.NotReachedCancelled => "Not scanned (stopped)", _ => "Excluded" },
                Where = r.Kind == RegionKind.NotReachedCancelled ? $"{Bytes.FormatSize(r.Length)} of the scan plan" : $"byte {r.Offset:N0} – {r.Offset + r.Length:N0} ({Bytes.FormatSize(r.Length)})",
                Message = r.Note,
            });
        foreach (var (t, i) in db.GetIssues())
            Issues.Add(new IssueRow { Severity = i.Severity.ToString(), Time = t.ToLocalTime().ToString("HH:mm:ss"), Message = i.Message, Where = i.Offset is long o ? $"byte {o:N0}" : "" });
    }
}
