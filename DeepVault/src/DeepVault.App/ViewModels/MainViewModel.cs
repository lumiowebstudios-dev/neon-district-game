using System.Collections.ObjectModel;
using System.Windows.Input;
using DeepVault.App.Services;
using DeepVault.Platform.Windows;

namespace DeepVault.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(MainViewModel main) => Main = main;
    public MainViewModel Main { get; }
    protected AppState S => AppState.Current;
    public abstract string Title { get; }
    /// <summary>Called each time the page becomes visible.</summary>
    public virtual void OnShown() { }
}

public sealed class NavItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public bool StartsGroup { get; init; }
    private bool _enabled = true;
    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, PageViewModel> _pages = new();
    private readonly Dictionary<string, Func<PageViewModel>> _factories;

    public ObservableCollection<NavItem> Nav { get; } = new()
    {
        new() { Key = "welcome", Number = "1", Title = "Welcome & safety" },
        new() { Key = "source", Number = "2", Title = "Select drive" },
        new() { Key = "details", Number = "3", Title = "Drive details" },
        new() { Key = "method", Number = "4", Title = "Scan methods" },
        new() { Key = "progress", Number = "5", Title = "Scan progress" },
        new() { Key = "issues", Number = "6", Title = "Scan issues" },
        new() { Key = "results", Number = "7", Title = "Recovered files", StartsGroup = true },
        new() { Key = "preview", Number = "8", Title = "Preview & details" },
        new() { Key = "restore", Number = "9", Title = "Restore" },
        new() { Key = "restoreprogress", Number = "10", Title = "Recovery progress" },
        new() { Key = "report", Number = "11", Title = "Recovery report" },
        new() { Key = "projects", Number = "12", Title = "Saved projects", StartsGroup = true },
        new() { Key = "settings", Number = "13", Title = "Settings & formats" },
        new() { Key = "diagnostics", Number = "14", Title = "Diagnostics & logs" },
    };

    private NavItem? _selectedNav;
    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set { if (Set(ref _selectedNav, value) && value != null) ShowPage(value.Key); }
    }

    private PageViewModel? _current;
    public PageViewModel? CurrentPage { get => _current; private set => Set(ref _current, value); }

    private string? _error;
    public string? ErrorMessage { get => _error; set => Set(ref _error, value); }
    public ICommand DismissError { get; }
    public ICommand RestartElevated { get; }
    public bool IsElevated { get; } = AppState.Current.Disks.IsElevated;

    private string _status = "";
    public string StatusText { get => _status; set => Set(ref _status, value); }

    public MainViewModel()
    {
        _factories = new()
        {
            ["welcome"] = () => new WelcomeViewModel(this),
            ["source"] = () => new SourceViewModel(this),
            ["details"] = () => new DetailsViewModel(this),
            ["method"] = () => new MethodViewModel(this),
            ["progress"] = () => new ProgressViewModel(this),
            ["issues"] = () => new IssuesViewModel(this),
            ["results"] = () => new ResultsViewModel(this),
            ["preview"] = () => new PreviewViewModel(this),
            ["restore"] = () => new RestoreViewModel(this),
            ["restoreprogress"] = () => new RestoreProgressViewModel(this),
            ["report"] = () => new ReportViewModel(this),
            ["projects"] = () => new ProjectsViewModel(this),
            ["settings"] = () => new SettingsViewModel(this),
            ["diagnostics"] = () => new DiagnosticsViewModel(this),
        };
        DismissError = new RelayCommand(() => ErrorMessage = null);
        RestartElevated = new RelayCommand(() =>
        {
            if (WindowsDiskEnumerator.RestartElevated()) System.Windows.Application.Current.Shutdown();
        });
        AppState.Current.ErrorRaised += (t, m) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ErrorMessage = $"{t}: {m}");
        AppState.Current.PropertyChanged += (_, _) => UpdateStatus();
        Navigate("welcome");
    }

    public T Page<T>(string key) where T : PageViewModel
    {
        if (!_pages.TryGetValue(key, out var p)) _pages[key] = p = _factories[key]();
        return (T)p;
    }

    public void Navigate(string key)
    {
        var item = Nav.First(n => n.Key == key);
        if (_selectedNav != item) { _selectedNav = item; Raise(nameof(SelectedNav)); }
        ShowPage(key);
    }

    private void ShowPage(string key)
    {
        var page = Page<PageViewModel>(key);
        CurrentPage = page;
        page.OnShown();
        UpdateStatus();
    }

    public void UpdateStatus()
    {
        var s = AppState.Current;
        var parts = new List<string>();
        parts.Add(s.Identity != null ? $"Source: {s.Identity.Describe()}" : "No source selected");
        if (s.ProjectPath != null) parts.Add($"Project: {System.IO.Path.GetFileName(s.ProjectPath)}");
        parts.Add(IsElevated ? "Administrator" : "Standard user");
        StatusText = string.Join("   •   ", parts);
    }
}
