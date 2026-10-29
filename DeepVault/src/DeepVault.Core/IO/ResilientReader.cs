namespace DeepVault.Core.IO;

public enum RegionKind
{
    /// <summary>The media returned an error; bytes were replaced with zeros.</summary>
    Unreadable = 1,
    /// <summary>Not read because of the error policy (to avoid stressing a failing drive).</summary>
    SkippedAfterErrors = 2,
    /// <summary>Not scanned because the user chose to exclude it (e.g. space allocated to existing files).</summary>
    ExcludedByScope = 3,
    /// <summary>Not scanned because the scan was cancelled before it got there.</summary>
    NotReachedCancelled = 4,
}

public readonly record struct DiskRegion(long Offset, long Length, RegionKind Kind, string Note);

public interface IRegionSink
{
    void Report(DiskRegion region);
}

public sealed class ReadPolicy
{
    /// <summary>How many times a failed chunk read is retried before falling back to per-sector reads.</summary>
    public int ChunkRetries { get; init; } = 1;
    /// <summary>Consecutive unreadable sectors after which the reader stops probing and skips ahead.</summary>
    public int ConsecutiveBadSectorsBeforeSkip { get; init; } = 32;
    /// <summary>Bytes skipped (and recorded) once the consecutive-error threshold is hit.</summary>
    public long SkipBytesAfterErrors { get; init; } = 1024 * 1024;
    /// <summary>Total unreadable sectors at which the drive is reported as possibly failing.</summary>
    public int HealthWarningThreshold { get; init; } = 256;
}

/// <summary>
/// Wraps a source and never throws for media errors: unreadable sectors are zero-filled and
/// reported, so a scan can continue past bad areas while every gap is recorded.
/// </summary>
public sealed class ResilientReader
{
    private readonly IBlockSource _src;
    private readonly IRegionSink? _sink;
    private readonly ReadPolicy _policy;
    private int _consecutiveBad;
    private long _skipUntil = -1;
    // Sectors already known to be unreadable are never read again: retrying bad sectors stresses failing drives.
    private readonly HashSet<long> _knownBad = new();
    private readonly object _lock = new();

    public long TotalBadSectors { get; private set; }
    public bool HealthWarningRaised { get; private set; }
    public event Action<long>? HealthWarning;

    public IBlockSource Source => _src;

    public ResilientReader(IBlockSource src, IRegionSink? sink, ReadPolicy? policy = null)
    {
        _src = src;
        _sink = sink;
        _policy = policy ?? new ReadPolicy();
    }

    /// <summary>
    /// Fills buffer from offset. Returns number of bytes that exist on the media (may be less at end).
    /// <paramref name="hadErrors"/> is true if any part of the buffer is substituted zeros.
    /// </summary>
    public int Read(long offset, Span<byte> buffer, out bool hadErrors)
    {
        lock (_lock) return ReadLocked(offset, buffer, out hadErrors);
    }

    private bool OverlapsKnownBad(long offset, int length)
    {
        if (_knownBad.Count == 0) return false;
        int ss = _src.SectorSize;
        for (long s = offset / ss; s * ss < offset + length; s++) if (_knownBad.Contains(s)) return true;
        return false;
    }

    private int ReadLocked(long offset, Span<byte> buffer, out bool hadErrors)
    {
        hadErrors = false;
        if (offset >= _src.Length) return 0;
        int want = (int)Math.Min(buffer.Length, _src.Length - offset);
        var buf = buffer.Slice(0, want);

        for (int attempt = 0; attempt <= _policy.ChunkRetries; attempt++)
        {
            if (offset < _skipUntil || OverlapsKnownBad(offset, want)) break; // go straight to the sector path
            try
            {
                _src.ReadAt(offset, buf);
                _consecutiveBad = 0;
                return want;
            }
            catch (IOException) { }
        }

        // Sector-by-sector fallback.
        int ss = _src.SectorSize;
        int pos = 0;
        while (pos < want)
        {
            long abs = offset + pos;
            int n = (int)Math.Min(ss - (abs % ss), want - pos);
            if (abs < _skipUntil)
            {
                int skipN = (int)Math.Min(_skipUntil - abs, want - pos);
                buf.Slice(pos, skipN).Clear();
                hadErrors = true;
                pos += skipN;
                continue;
            }
            if (_knownBad.Contains(abs / ss))
            {
                buf.Slice(pos, n).Clear();
                hadErrors = true;
                pos += n;
                continue;
            }
            try
            {
                _src.ReadAt(abs, buf.Slice(pos, n));
                _consecutiveBad = 0;
            }
            catch (IOException)
            {
                hadErrors = true;
                buf.Slice(pos, n).Clear();
                _knownBad.Add(abs / ss);
                TotalBadSectors++;
                _consecutiveBad++;
                _sink?.Report(new DiskRegion(abs, n, RegionKind.Unreadable, "Media read error; zero-filled"));
                if (!HealthWarningRaised && TotalBadSectors >= _policy.HealthWarningThreshold)
                {
                    HealthWarningRaised = true;
                    HealthWarning?.Invoke(TotalBadSectors);
                }
                if (_consecutiveBad >= _policy.ConsecutiveBadSectorsBeforeSkip)
                {
                    long skipStart = abs + n;
                    long skipLen = Math.Min(_policy.SkipBytesAfterErrors, _src.Length - skipStart);
                    if (skipLen > 0)
                    {
                        _skipUntil = skipStart + skipLen;
                        _sink?.Report(new DiskRegion(skipStart, skipLen, RegionKind.SkippedAfterErrors,
                            $"Skipped after {_consecutiveBad} consecutive unreadable sectors"));
                    }
                    _consecutiveBad = 0;
                }
            }
            pos += n;
        }
        return want;
    }

    /// <summary>Convenience: read without caring about the error flag.</summary>
    public int Read(long offset, Span<byte> buffer) => Read(offset, buffer, out _);
}
