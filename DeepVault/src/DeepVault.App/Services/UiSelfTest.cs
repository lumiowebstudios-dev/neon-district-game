using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeepVault.App.ViewModels;
using DeepVault.Core.Catalog;

namespace DeepVault.App.Services;

/// <summary>
/// Automated walk through every page using a disk image: open image → confirm → scan → results →
/// previews → restore → report → projects/settings/diagnostics. Saves a screenshot per page and a
/// JSON result including any WPF binding errors. Start with: DeepVault.exe --selftest &lt;image&gt; &lt;outdir&gt;
/// </summary>
public sealed class UiSelfTest
{
    private readonly MainWindow _w;
    private readonly string _image;
    private readonly string _out;
    private readonly List<object> _steps = new();
    private readonly BindingErrorListener _bindings = new();

    public UiSelfTest(MainWindow w, string image, string outDir)
    {
        _w = w; _image = image; _out = outDir;
        Directory.CreateDirectory(outDir);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Errors) Errors.Add(message); }
    }

    private async Task Settle(int ms = 600)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private async Task Shot(string name, string? note = null)
    {
        await Settle();
        var el = (FrameworkElement)_w.Content;
        int wpx = (int)Math.Ceiling(el.ActualWidth), hpx = (int)Math.Ceiling(el.ActualHeight);
        var rtb = new RenderTargetBitmap(Math.Max(1, wpx), Math.Max(1, hpx), 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("Bg"), null, new Rect(0, 0, wpx, hpx));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, wpx, hpx));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var file = Path.Combine(_out, $"{_steps.Count + 1:00}-{name}.png");
        using (var fs = File.Create(file)) enc.Save(fs);
        _steps.Add(new { step = name, screenshot = Path.GetFileName(file), note, bindingErrorsSoFar = _bindings.Errors.Count });
    }

    public async Task RunAsync()
    {
        int exit = 0;
        string? failure = null;
        var vm = _w.VM;
        try
        {
            _w.Width = 1320; _w.Height = 860;
            await Shot("welcome");
            vm.Navigate("source");
            await Settle(2500);
            await Shot("select-drive", $"{vm.Page<SourceViewModel>("source").Disks.Count} disks listed");

            vm.Page<SourceViewModel>("source").OpenImagePath(_image);
            var details = vm.Page<DetailsViewModel>("details");
            await Shot("drive-details", $"{details.Partitions.Count} partitions");
            details.Confirmed = true;
            if (details.IsSystemDisk) details.AcceptLiveRisk = true;

            vm.Navigate("method");
            var method = vm.Page<MethodViewModel>("method");
            method.ProjectFolder = Path.Combine(_out, "project");
            method.ProjectName = "selftest-" + DateTime.Now.ToString("HHmmss");
            await Shot("scan-methods");
            var scanTask = method.StartAsync();
            await Task.Delay(300);
            await Shot("scan-progress-running");
            await scanTask;
            var prog = vm.Page<ProgressViewModel>("progress");
            await Shot("scan-progress-finished", prog.ResultText);
            if (AppState.Current.LastSummary == null) throw new InvalidOperationException("scan produced no summary");

            vm.Navigate("issues");
            await Shot("scan-issues");

            vm.Navigate("results");
            var results = vm.Page<ResultsViewModel>("results");
            await Settle();
            results.SelectedRow = results.Items.FirstOrDefault(r => r.Ext == "jpg") ?? results.Items.FirstOrDefault();
            await Settle(1500);
            await Shot("results-list", $"{results.Total} in view");
            results.Category = results.Categories.First(c => c.Value is FileCategory.Image);
            results.GalleryMode = true;
            await Settle(4000);
            await Shot("results-gallery", $"{results.Items.Count(r => r.Thumbnail != null)} thumbnails decoded");
            results.GalleryMode = false;
            results.Category = results.Categories[0];

            var preview = vm.Page<PreviewViewModel>("preview");
            var list = results.Items.Select(i => i.File).ToList();
            foreach (var ext in new[] { "jpg", "pdf", "txt", "mp3" })
            {
                var f = AppState.Current.Project!.QueryFiles(new FileQuery { Extension = ext, Limit = 1 }).FirstOrDefault();
                if (f == null) continue;
                preview.Show(f, list);
                vm.Navigate("preview");
                await Settle(2500);
                await Shot("preview-" + ext, preview.Message);
            }

            vm.Navigate("results");
            results.SelectAllMatching.Execute(null);
            vm.Navigate("restore");
            var restore = vm.Page<RestoreViewModel>("restore");
            var dest = Path.Combine(_out, "restored");
            Directory.CreateDirectory(dest);
            restore.Folder = dest;
            await Shot("restore-destination", string.Join(" | ", restore.Blockers.Concat(restore.Warnings)));
            if (restore.Start.CanExecute(null))
            {
                restore.Start.Execute(null);
                for (int i = 0; i < 600 && (vm.CurrentPage is RestoreProgressViewModel || vm.Page<RestoreProgressViewModel>("restoreprogress").IsRunning); i++) await Task.Delay(200);
                await Shot("recovery-report", vm.Page<ReportViewModel>("report").Headline);
                vm.Navigate("restoreprogress");
                await Shot("recovery-progress-idle");
            }
            else failure = "restore could not start: " + string.Join("; ", restore.Blockers);

            vm.Navigate("projects");
            await Shot("saved-projects");
            vm.Navigate("settings");
            await Shot("settings-formats");
            vm.Navigate("diagnostics");
            await Shot("diagnostics");
            vm.Navigate("report");
            await Shot("recovery-report-final");
        }
        catch (Exception e)
        {
            failure = e.ToString();
            exit = 1;
            try { await Shot("failure"); } catch (Exception) { }
        }
        var res = AppState.Current.LastRestore;
        File.WriteAllText(Path.Combine(_out, "selftest-result.json"), JsonSerializer.Serialize(new
        {
            ok = failure == null,
            failure,
            steps = _steps,
            scan = AppState.Current.LastSummary == null ? null : new { outcome = AppState.Current.LastSummary.Outcome.ToString(), files = AppState.Current.LastSummary.FilesFound },
            recoverable = AppState.Current.Project?.CountView(ResultView.Recoverable),
            restore = res == null ? null : new { verified = res.Count(Core.Restore.RestoreOutcome.Verified), warnings = res.Count(Core.Restore.RestoreOutcome.RestoredWithWarnings), failed = res.Count(Core.Restore.RestoreOutcome.Failed), folder = res.SessionFolder },
            bindingErrors = _bindings.Errors.Distinct().ToList(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure != null) exit = 1;
        Application.Current.Shutdown(exit);
    }
}
