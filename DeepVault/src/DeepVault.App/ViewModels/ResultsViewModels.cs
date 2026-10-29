using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DeepVault.App.Services;
using DeepVault.Core.Catalog;
using DeepVault.Core.Content;
using DeepVault.Core.IO;
using DeepVault.Core.Preview;
using DeepVault.Core.Util;
using Microsoft.Win32;

namespace DeepVault.App.ViewModels;

public sealed class FileRow : ObservableObject
{
    public FoundFile File { get; }
    public FileRow(FoundFile f) { File = f; }
    public long Id => File.Id;
    public string Name => File.Name;
    public string Folder => File.Path ?? "";
    public string Type => FileTypes.DescriptionOf(File.Extension);
    public string Ext => File.Extension;
    public long Size => File.Size;
    public string SizeText => Bytes.FormatSize(File.Size);
    public DateTime? Modified => File.Modified;
    public DateTime? Created => File.Created;
    public string Method => Text.Engines(File.Engines);
    public string Status => Text.State(File.State);
    public string Structure => Text.Structure(File.Structure);
    public Confidence Confidence => File.Confidence;
    public string ConfidenceText => File.Confidence switch { Confidence.High => "High", Confidence.Medium => "Medium", Confidence.Low => "Low", _ => "Unknown" };
    public string Deleted => File.IsDeleted switch { true => "Deleted", false => "Exists", _ => "Lost" };
    public bool Previewable => PreviewService.KindFor(File) != PreviewKind.Hex && File.State != RecoveryState.Failed && File.Size > 0;
    public bool IsImage => PreviewService.KindFor(File) == PreviewKind.Image;
    public string Overwritten => File.OverwrittenFraction is double d ? $"{d:P0}" : "unknown";

    public bool IsChecked
    {
        get => AppState.Current.Selected.Contains(File.Id);
        set
        {
            if (value) AppState.Current.Selected.Add(File.Id); else AppState.Current.Selected.Remove(File.Id);
            Raise();
            AppState.Current.NotifySelection();
        }
    }
    public void RefreshChecked() => Raise(nameof(IsChecked));

    private ImageSource? _thumb;
    public ImageSource? Thumbnail { get => _thumb; set => Set(ref _thumb, value); }
    private bool _thumbTried;
    public bool ThumbTried { get => _thumbTried; set => Set(ref _thumbTried, value); }
}

public sealed record ChoiceItem(string Label, object? Value)
{
    public override string ToString() => Label;
}

public sealed class ResultsViewModel : PageViewModel
{
    public override string Title => "Recovered files";
    public const int PageSize = 200;
    public ObservableCollection<FileRow> Items { get; } = new();
    public ObservableCollection<ChoiceItem> ViewTabs { get; } = new();
    public List<ChoiceItem> Categories { get; } = new[] { new ChoiceItem("All types", null) }
        .Concat(Enum.GetValues<FileCategory>().Select(c => new ChoiceItem(FileTypes.CategoryName(c), c))).ToList();
    public List<ChoiceItem> EngineChoices { get; } = new()
    {
        new("All methods", null), new("NTFS MFT (deleted files)", EngineId.NtfsMft), new("FAT directories", EngineId.FatDirectory),
        new("exFAT directories", EngineId.ExFatDirectory), new("Previous file system records", EngineId.OrphanMftRecords), new("Signature scan (carving)", EngineId.SignatureCarving),
    };
    public List<ChoiceItem> SortChoices { get; } = new()
    {
        new("Name", SortField.Name), new("Folder", SortField.Path), new("Size", SortField.Size), new("Type", SortField.Type),
        new("Modified", SortField.Modified), new("Created", SortField.Created), new("Confidence", SortField.Confidence), new("Disk position", SortField.Offset),
    };
    public List<ChoiceItem> ConfidenceChoices { get; } = new() { new("Any integrity", null), new("Medium or better", Confidence.Medium), new("High only", Confidence.High) };

    private ChoiceItem? _view;
    public ChoiceItem? SelectedView { get => _view; set { if (Set(ref _view, value)) Reload(true); } }
    private string _search = "";
    public string Search { get => _search; set => Set(ref _search, value); }
    private ChoiceItem? _cat;
    public ChoiceItem? Category { get => _cat; set { if (Set(ref _cat, value)) Reload(true); } }
    private ChoiceItem? _engine;
    public ChoiceItem? Engine { get => _engine; set { if (Set(ref _engine, value)) Reload(true); } }
    private ChoiceItem? _sort;
    public ChoiceItem? Sort { get => _sort; set { if (Set(ref _sort, value)) Reload(true); } }
    private ChoiceItem? _conf;
    public ChoiceItem? MinConfidence { get => _conf; set { if (Set(ref _conf, value)) Reload(true); } }
    private bool _desc;
    public bool Descending { get => _desc; set { if (Set(ref _desc, value)) Reload(true); } }
    private string _ext = "";
    public string ExtensionFilter { get => _ext; set => Set(ref _ext, value); }
    private bool _existing = true;
    public bool IncludeExisting { get => _existing; set { if (Set(ref _existing, value)) Reload(true); } }
    private bool _gallery;
    public bool GalleryMode { get => _gallery; set { if (Set(ref _gallery, value)) LoadThumbnails(); } }

    private int _page;
    public int Page { get => _page; set => Set(ref _page, value); }
    private long _total;
    public long Total { get => _total; set => Set(ref _total, value); }
    public string PageText => Total == 0 ? "No matching candidates" : $"{Page * PageSize + 1:N0}–{Math.Min(Total, (Page + 1) * (long)PageSize):N0} of {Total:N0}";
    public string SelectionText => $"{S.SelectedCount:N0} selected";
    private FileRow? _selected;
    public FileRow? SelectedRow { get => _selected; set { if (Set(ref _selected, value)) LoadDetails(); } }
    public ObservableCollection<KV> Details { get; } = new();
    private ImageSource? _detailImage;
    public ImageSource? DetailImage { get => _detailImage; set => Set(ref _detailImage, value); }
    private string? _message;
    public string? Message { get => _message; set => Set(ref _message, value); }
    private string? _busy;
    public string? Busy { get => _busy; set => Set(ref _busy, value); }
    public bool HasProject => S.HasProject;

    public ICommand ApplySearch { get; }
    public ICommand NextPage { get; }
    public ICommand PrevPage { get; }
    public ICommand SelectAllMatching { get; }
    public ICommand ClearSelection { get; }
    public ICommand CheckPage { get; }
    public ICommand OpenPreview { get; }
    public ICommand RestoreSelected { get; }
    public ICommand ExportCsv { get; }
    public ICommand FindDuplicates { get; }
    public ICommand CheckIntegrity { get; }

    public ResultsViewModel(MainViewModel m) : base(m)
    {
        _cat = Categories[0]; _engine = EngineChoices[0]; _sort = SortChoices[0]; _conf = ConfidenceChoices[0];
        ApplySearch = new RelayCommand(() => Reload(true));
        NextPage = new RelayCommand(() => { Page++; Reload(false); }, () => (Page + 1) * (long)PageSize < Total);
        PrevPage = new RelayCommand(() => { Page--; Reload(false); }, () => Page > 0);
        SelectAllMatching = new RelayCommand(() =>
        {
            if (S.Project == null) return;
            foreach (var id in S.Project.QueryIds(Query())) S.Selected.Add(id);
            foreach (var r in Items) r.RefreshChecked();
            S.NotifySelection();
        }, () => S.HasProject);
        ClearSelection = new RelayCommand(() => { S.Selected.Clear(); foreach (var r in Items) r.RefreshChecked(); S.NotifySelection(); });
        CheckPage = new RelayCommand(() => { foreach (var r in Items) S.Selected.Add(r.Id); foreach (var r in Items) r.RefreshChecked(); S.NotifySelection(); });
        OpenPreview = new RelayCommand(p =>
        {
            var row = p as FileRow ?? SelectedRow;
            if (row == null) return;
            Main.Page<PreviewViewModel>("preview").Show(row.File, Items.Select(i => i.File).ToList());
            Main.Navigate("preview");
        });
        RestoreSelected = new RelayCommand(() => Main.Navigate("restore"), () => S.SelectedCount > 0);
        ExportCsv = new AsyncCommand(DoExportCsv, () => S.HasProject);
        FindDuplicates = new AsyncCommand(DoFindDuplicates, () => S.HasProject && S.HasSource);
        CheckIntegrity = new AsyncCommand(DoCheckIntegrity, () => S.HasProject && S.HasSource && S.SelectedCount > 0);
        S.SelectionChanged += () => Raise(nameof(SelectionText));
    }

    public override void OnShown()
    {
        Raise(nameof(HasProject));
        if (S.Project == null) { Message = "No results yet: run a scan or open a saved project."; Items.Clear(); Total = 0; ViewTabs.Clear(); Raise(nameof(PageText)); return; }
        Message = S.HasSource ? null : "The scanned drive or image is not connected: you can browse and select results, but previews and restore need the source.";
        RefreshTabs();
        Reload(false);
    }

    private void RefreshTabs()
    {
        var db = S.Project!;
        var current = _view?.Value as ResultView? ?? ResultView.Recoverable;
        ViewTabs.Clear();
        foreach (var (v, label) in new[] { (ResultView.Recoverable, "Recoverable"), (ResultView.Partial, "Partial / damaged"), (ResultView.Uncertain, "Uncertain"), (ResultView.Failed, "Not recoverable"), (ResultView.All, "All"), (ResultView.Duplicates, "Duplicates") })
            ViewTabs.Add(new ChoiceItem($"{label} ({db.CountView(v):N0})", v));
        _view = ViewTabs.First(t => (ResultView)t.Value! == current);
        Raise(nameof(SelectedView));
    }

    public FileQuery Query() => new()
    {
        View = _view?.Value as ResultView? ?? ResultView.Recoverable,
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search,
        Category = Category?.Value as FileCategory?,
        Engine = Engine?.Value as EngineId?,
        Extension = string.IsNullOrWhiteSpace(ExtensionFilter) ? null : ExtensionFilter,
        MinConfidence = MinConfidence?.Value as Confidence?,
        IncludeExisting = IncludeExisting,
        Sort = Sort?.Value as SortField? ?? SortField.Name,
        Descending = Descending,
        Offset = Page * PageSize,
        Limit = PageSize,
    };

    public void Reload(bool resetPage)
    {
        if (S.Project == null) return;
        if (resetPage) Page = 0;
        var q = Query();
        Total = S.Project.CountFiles(q);
        if (Page * (long)PageSize >= Total && Page > 0) { Page = (int)((Total - 1) / PageSize); q.Offset = Page * PageSize; }
        Items.Clear();
        foreach (var f in S.Project.QueryFiles(q)) Items.Add(new FileRow(f));
        Raise(nameof(PageText));
        if (GalleryMode) LoadThumbnails();
    }

    private CancellationTokenSource? _thumbCts;

    private void LoadThumbnails()
    {
        if (!GalleryMode || !S.HasSource || !S.Settings.ShowThumbnails) return;
        _thumbCts?.Cancel();
        var cts = _thumbCts = new CancellationTokenSource();
        var rows = Items.Where(r => r.IsImage && r.Thumbnail == null && r.File.Size <= 32L * 1024 * 1024).ToList();
        var src = S.Source!;
        _ = Task.Run(() =>
        {
            var svc = new PreviewService(new ResilientReader(src, null));
            foreach (var r in rows)
            {
                if (cts.IsCancellationRequested) return;
                BitmapSourceResult(r, svc);
            }
        });
    }

    private static void BitmapSourceResult(FileRow r, PreviewService svc)
    {
        try
        {
            var data = svc.Load(r.File);
            var img = data.Kind == PreviewKind.Image ? ImageDecoder.Decode(data.Bytes, 160) : null;
            Application.Current.Dispatcher.BeginInvoke(() => { r.Thumbnail = img; r.ThumbTried = true; });
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
    }

    private void LoadDetails()
    {
        Details.Clear();
        DetailImage = null;
        var r = SelectedRow;
        if (r == null) return;
        foreach (var kv in PreviewViewModel.DescribeFile(r.File)) Details.Add(kv);
        if (r.IsImage && S.HasSource && r.File.Size <= 32L * 1024 * 1024)
        {
            var f = r.File; var src = S.Source!;
            Task.Run(() =>
            {
                var data = new PreviewService(new ResilientReader(src, null)).Load(f);
                return data.Kind == PreviewKind.Image ? ImageDecoder.Decode(data.Bytes, 360) : null;
            }).ContinueWith(t => { if (SelectedRow?.File == f && t.IsCompletedSuccessfully) DetailImage = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private async Task DoExportCsv()
    {
        var dlg = new SaveFileDialog { Title = "Export results catalogue (CSV)", Filter = "CSV (*.csv)|*.csv", FileName = "DeepVault results.csv" };
        if (dlg.ShowDialog() != true) return;
        var info = S.Inspector.Inspect(Path.GetDirectoryName(dlg.FileName)!);
        if (info.PhysicalDisks.Any(d => S.SourceDiskIds.Contains(d)) &&
            MessageBox.Show("This file would be written to the disk you are recovering from, which can overwrite recoverable data. Continue anyway?", "DeepVault", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var db = S.Project!;
        Busy = "Exporting…";
        await Task.Run(() =>
        {
            using var w = new StreamWriter(new FileStream(dlg.FileName, FileMode.CreateNew), new UTF8Encoding(true));
            w.WriteLine("id,name,folder,extension,category,size,modified,created,methods,deleted,status,structure,confidence,overwritten,first_offset,notes");
            static string C(string? v) => v == null ? "" : v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
            foreach (var f in db.AllFiles(true))
                w.WriteLine(string.Join(",", f.Id, C(f.Name), C(f.Path), f.Extension, f.Category, f.Size, f.Modified?.ToString("O"), f.Created?.ToString("O"),
                    C(Text.Engines(f.Engines)), f.IsDeleted, f.State, f.Structure, f.Confidence, f.OverwrittenFraction?.ToString("0.###"), f.FirstOffset, C(f.Notes)));
        });
        Busy = null;
        Message = $"Catalogue exported to {dlg.FileName}.";
    }

    /// <summary>Reads each selected candidate from the source and runs its format's structural check.</summary>
    private async Task DoCheckIntegrity()
    {
        var db = S.Project!; var src = S.Source!;
        var ids = S.Selected.ToList();
        int ok = 0, bad = 0, unknown = 0;
        await Task.Run(() =>
        {
            var reader = new ResilientReader(src, null);
            int n = 0;
            foreach (var f in db.GetFiles(ids))
            {
                n++;
                if (n % 20 == 0) Application.Current.Dispatcher.BeginInvoke(() => Busy = $"Checking {n:N0} of {ids.Count:N0}…");
                if (f.State == RecoveryState.Failed || f.Size == 0) { unknown++; continue; }
                using var cs = new ContentStream(reader, f.Layout);
                var v = Core.Carving.FormatValidator.Validate(new Core.Carving.StreamCarveSource(cs), f.Extension);
                if (v.Check == StructureCheck.NotChecked) { unknown++; continue; }
                bool damaged = v.Check == StructureCheck.Damaged || cs.HadReadErrors;
                if (damaged) bad++; else ok++;
                if (f.State is RecoveryState.Restored or RecoveryState.RestoreVerified) continue;
                db.UpdateState(f.Id, damaged ? RecoveryState.PartialOrDamaged : RecoveryState.ContentReadable, v.Check,
                    damaged ? "integrity check: " + (v.Note ?? "structure inconsistent") : null);
            }
        });
        Busy = null;
        Message = $"Integrity check: {ok:N0} look intact, {bad:N0} damaged, {unknown:N0} cannot be checked for their type (e.g. plain text).";
        RefreshTabs();
        Reload(false);
    }

    /// <summary>Computes SHA-256 for candidates that share a size and marks byte-identical copies as duplicates.</summary>
    private async Task DoFindDuplicates()
    {
        var db = S.Project!; var src = S.Source!;
        Busy = "Comparing candidates of equal size…";
        int marked = await Task.Run(() =>
        {
            var reader = new ResilientReader(src, null);
            var bySize = db.AllFiles().Where(f => f.Size > 0 && f.State != RecoveryState.Failed && f.ContentHash == null).GroupBy(f => f.Size).Where(g => g.Count() > 1);
            foreach (var g in bySize)
                foreach (var f in g)
                {
                    using var cs = new ContentStream(reader, f.Layout);
                    db.SetContentHash(f.Id, Convert.ToHexString(SHA256.HashData(cs)).ToLowerInvariant());
                }
            return db.MarkContentDuplicates();
        });
        Busy = null;
        Message = marked == 0 ? "No byte-identical duplicates found." : $"{marked:N0} byte-identical copies were moved to the Duplicates view (the best-rated copy of each stays in place).";
        RefreshTabs();
        Reload(false);
    }
}

public sealed class PreviewViewModel : PageViewModel
{
    public override string Title => "Preview";
    private List<FoundFile> _list = new();
    private FoundFile? _file;
    public FoundFile? File { get => _file; private set => Set(ref _file, value); }
    public ObservableCollection<KV> Details { get; } = new();
    private ImageSource? _image;
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }
    private string? _text;
    public string? TextContent { get => _text; set => Set(ref _text, value); }
    private string? _hex;
    public string? Hex { get => _hex; set => Set(ref _hex, value); }
    private string? _message;
    public string? Message { get => _message; set => Set(ref _message, value); }
    private bool _loading;
    public bool Loading { get => _loading; set => Set(ref _loading, value); }
    public string Heading => File == null ? "Nothing selected" : File.Name;
    public bool IsChecked
    {
        get => File != null && S.Selected.Contains(File.Id);
        set { if (File == null) return; if (value) S.Selected.Add(File.Id); else S.Selected.Remove(File.Id); S.NotifySelection(); Raise(); }
    }
    public ICommand Next { get; }
    public ICommand Prev { get; }
    public ICommand BackToResults { get; }
    public ICommand RestoreThis { get; }

    public PreviewViewModel(MainViewModel m) : base(m)
    {
        Next = new RelayCommand(() => Move(1), () => File != null && _list.IndexOf(File) < _list.Count - 1);
        Prev = new RelayCommand(() => Move(-1), () => File != null && _list.IndexOf(File) > 0);
        BackToResults = new RelayCommand(() => Main.Navigate("results"));
        RestoreThis = new RelayCommand(() => { if (File == null) return; S.Selected.Add(File.Id); S.NotifySelection(); Main.Navigate("restore"); }, () => File != null);
    }

    public override void OnShown()
    {
        if (File == null) Message = "Choose a file in Recovered files and click Preview.";
    }

    public void Show(FoundFile f, List<FoundFile> list)
    {
        _list = list;
        Load(f);
    }

    private void Move(int d)
    {
        int i = File == null ? -1 : _list.IndexOf(File);
        if (i + d >= 0 && i + d < _list.Count) Load(_list[i + d]);
    }

    public static IEnumerable<KV> DescribeFile(FoundFile f)
    {
        yield return new("Name", f.Name);
        yield return new("Original folder", string.IsNullOrEmpty(f.Path) ? "unknown (no file-system metadata)" : f.Path);
        yield return new("Type", $"{FileTypes.DescriptionOf(f.Extension)} (.{f.Extension})");
        yield return new("Size", $"{Bytes.FormatSize(f.Size)} ({f.Size:N0} bytes)");
        yield return new("Modified", f.Modified?.ToLocalTime().ToString("F") ?? "unknown");
        yield return new("Created", f.Created?.ToLocalTime().ToString("F") ?? "unknown");
        yield return new("Found by", Text.Engines(f.Engines));
        yield return new("Status", Text.State(f.State));
        yield return new("Integrity", $"{Text.Structure(f.Structure)}; confidence {f.Confidence}");
        yield return new("File-system state", f.IsDeleted switch { true => "Deleted", false => "Still exists in the file system", _ => "Lost (no current file-system entry)" });
        yield return new("Overwritten clusters", f.OverwrittenFraction is double d ? $"{d:P0} of its clusters are now used by other data" : "unknown");
        yield return new("Fragments", f.Layout.Kind == LayoutKind.NtfsResident ? "stored inside the MFT record" : $"{f.Layout.FragmentCount}");
        yield return new("Volume", f.VolumeLabel ?? "—");
        yield return new("Location", f.SourceDetail);
        if (!string.IsNullOrEmpty(f.Notes)) yield return new("Notes", f.Notes);
    }

    private async void Load(FoundFile f)
    {
        File = f;
        Raise(nameof(Heading)); Raise(nameof(IsChecked));
        Details.Clear();
        foreach (var kv in DescribeFile(f)) Details.Add(kv);
        Image = null; TextContent = null; Hex = null; Message = null;
        if (!S.HasSource) { Message = "Connect the scanned drive or image to preview its content."; return; }
        Loading = true;
        try
        {
            var src = S.Source!;
            var data = await Task.Run(() => new PreviewService(new ResilientReader(src, null)).Load(f));
            if (File != f) return;
            Message = data.Message;
            switch (data.Kind)
            {
                case PreviewKind.Image:
                    Image = await ImageDecoder.DecodeAsync(data.Bytes, 1600);
                    if (Image == null) { Hex = PreviewService.Hex(data.Bytes.AsSpan(0, Math.Min(data.Bytes.Length, 4096))); Message = ((Message ?? "") + " The image could not be decoded: it is probably damaged, overwritten or fragmented. First bytes shown.").Trim(); }
                    break;
                case PreviewKind.Pdf:
                    if (!string.IsNullOrEmpty(data.Text)) TextContent = data.Text;
                    else Hex = PreviewService.Hex(data.Bytes.AsSpan(0, Math.Min(4096, data.Bytes.Length)));
                    break;
                case PreviewKind.Text:
                    TextContent = data.Text;
                    if (data.Truncated) Message = ((Message ?? "") + " Only the beginning of the file is shown.").Trim();
                    break;
                case PreviewKind.Hex:
                    Hex = data.HexDump;
                    break;
                default:
                    Message ??= "No preview available.";
                    break;
            }
            bool shown = Image != null || TextContent != null;
            if (shown && f.State is RecoveryState.CandidateDetected or RecoveryState.MetadataRecovered)
                S.Project?.UpdateState(f.Id, RecoveryState.PreviewGenerated);
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { Message = "Preview failed: " + e.Message; }
        finally { Loading = false; }
    }
}
