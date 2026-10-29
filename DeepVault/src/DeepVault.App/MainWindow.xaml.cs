using System.IO;
using System.Windows;
using DeepVault.App.ViewModels;

namespace DeepVault.App;

public partial class MainWindow : Window
{
    public MainViewModel VM { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = VM;
        Closing += (_, e) =>
        {
            bool scanning = VM.Page<ProgressViewModel>("progress").IsRunning;
            bool restoring = VM.Page<RestoreProgressViewModel>("restoreprogress").IsRunning;
            if ((scanning || restoring) && MessageBox.Show(this,
                    scanning ? "A scan is running. Close anyway? Progress up to the last checkpoint is saved and the scan can be resumed."
                             : "Files are being restored. Close anyway? The file being written will be incomplete and is removed on the next restore.",
                    "DeepVault", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
    }

    /// <summary>Opens a project (.dvproj) or a disk image passed on the command line.</summary>
    public void OpenPath(string path)
    {
        if (path.EndsWith(Core.Catalog.ProjectDatabase.FileExtension, StringComparison.OrdinalIgnoreCase))
            VM.Page<ProjectsViewModel>("projects").OpenProject(path);
        else if (File.Exists(path))
        {
            VM.Navigate("source");
            VM.Page<SourceViewModel>("source").OpenImagePath(path);
        }
    }
}
