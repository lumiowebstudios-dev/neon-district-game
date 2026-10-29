using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace DeepVault.App.Services;

/// <summary>
/// Decodes images entirely in memory (no temp files). All decoding runs serially on one dedicated
/// STA thread: imaging codecs are COM components and are most reliable there. Malformed data yields
/// null, never an exception.
/// </summary>
public static class ImageDecoder
{
    private static readonly BlockingCollection<Action> Queue = new();

    static ImageDecoder()
    {
        var t = new Thread(() => { foreach (var a in Queue.GetConsumingEnumerable()) a(); }) { IsBackground = true, Name = "DeepVault imaging" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static Task<BitmapSource?> DecodeAsync(byte[] data, int decodeWidth = 0)
    {
        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() => tcs.SetResult(DecodeCore(data, decodeWidth)));
        return tcs.Task;
    }

    /// <summary>Synchronous convenience (blocks the caller; never call from the imaging thread).</summary>
    public static BitmapSource? Decode(byte[] data, int decodeWidth = 0) => DecodeAsync(data, decodeWidth).GetAwaiter().GetResult();

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetModuleHandleA(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);

    /// <summary>True when running under Wine (used only to avoid a crash in Wine's GIF codec).</summary>
    public static bool IsWine { get; } = DetectWine();
    private static bool DetectWine()
    {
        try { var h = GetModuleHandleA("ntdll.dll"); return h != 0 && GetProcAddress(h, "wine_get_version") != 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static BitmapSource? DecodeCore(byte[] data, int decodeWidth)
    {
        if (data.Length < 8) return null;
        // Wine's GIF decoder crashes natively (an access violation .NET cannot catch); Windows' codec is unaffected.
        if (IsWine && data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(data, writable: false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
