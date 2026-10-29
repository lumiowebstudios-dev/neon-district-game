using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DeepVault.Core.Catalog;

namespace DeepVault.App.Services;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _run(p);
}

/// <summary>Async command that disables itself while running and reports failures instead of crashing.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly Func<bool>? _can;
    private bool _busy;
    public AsyncCommand(Func<Task> run, Func<bool>? can = null) { _run = run; _can = can; }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => !_busy && (_can?.Invoke() ?? true);
    public async void Execute(object? p)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _run(); }
        catch (Exception e) { AppState.Current.ReportError("Operation failed", e); }
        finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
    }
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch { bool x => x, int i => i != 0, long l => l != 0, string s => s.Length > 0, null => false, _ => true };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

/// <summary>Binds a RadioButton to an enum value: IsChecked="{Binding Mode, Converter={StaticResource EnumBool}, ConverterParameter=Value}".</summary>
public sealed class EnumToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is true ? Enum.Parse(t, p!.ToString()!) : Binding.DoNothing;
}

public sealed class SizeText : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is long l ? Core.Util.Bytes.FormatSize(l) : value is int i ? Core.Util.Bytes.FormatSize(i) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class LocalTime : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is DateTime d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ConfidenceBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        Confidence.High => Application.Current.FindResource("Success"),
        Confidence.Medium => Application.Current.FindResource("Accent"),
        Confidence.Low => Application.Current.FindResource("Warning"),
        RecoveryState.Failed => Application.Current.FindResource("Danger"),
        RecoveryState.PartialOrDamaged => Application.Current.FindResource("Warning"),
        RecoveryState.RestoreVerified or RecoveryState.Restored => Application.Current.FindResource("Success"),
        _ => Application.Current.FindResource("Muted"),
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public static class Text
{
    public static string State(RecoveryState s) => s switch
    {
        RecoveryState.CandidateDetected => "Candidate detected",
        RecoveryState.MetadataRecovered => "Metadata recovered",
        RecoveryState.PreviewGenerated => "Preview generated",
        RecoveryState.ContentReadable => "Content appears readable",
        RecoveryState.Restored => "Restored (with warnings)",
        RecoveryState.RestoreVerified => "Restored and verified",
        RecoveryState.PartialOrDamaged => "Partial or damaged",
        RecoveryState.Failed => "Recovery not possible",
        _ => s.ToString(),
    };

    public static string Structure(StructureCheck s) => s switch
    {
        StructureCheck.Verified => "Structure verified",
        StructureCheck.Plausible => "Structure plausible",
        StructureCheck.HeaderOnly => "Header only (end estimated)",
        StructureCheck.Damaged => "Damaged / inconsistent",
        StructureCheck.Unsupported => "Cannot be checked",
        _ => "Not checked",
    };

    public static string Engines(EngineId e)
    {
        var parts = new List<string>();
        if (e.HasFlag(EngineId.NtfsMft)) parts.Add("NTFS MFT");
        if (e.HasFlag(EngineId.FatDirectory)) parts.Add("FAT");
        if (e.HasFlag(EngineId.ExFatDirectory)) parts.Add("exFAT");
        if (e.HasFlag(EngineId.OrphanMftRecords)) parts.Add("Previous FS records");
        if (e.HasFlag(EngineId.SignatureCarving)) parts.Add("Signature scan");
        return string.Join(" + ", parts);
    }
}

public sealed class StateText : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        RecoveryState s => Text.State(s),
        StructureCheck s => Text.Structure(s),
        EngineId e => Text.Engines(e),
        _ => value?.ToString() ?? "",
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
