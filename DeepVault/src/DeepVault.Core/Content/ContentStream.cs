using DeepVault.Core.FileSystems.Ntfs;
using DeepVault.Core.IO;

namespace DeepVault.Core.Content;

/// <summary>
/// Read-only, seekable stream over a candidate's content, reconstructed directly from the source
/// using its <see cref="DataLayout"/>. Nothing is written anywhere.
/// </summary>
public sealed class ContentStream : Stream
{
    private readonly ResilientReader _reader;
    private readonly DataLayout _layout;
    private readonly long _length;
    private long _pos;
    private byte[]? _resident;
    private readonly long[] _extentStarts;

    // Compressed state
    private long _cuIndex = -1;
    private byte[]? _cuData;

    /// <summary>True if any byte returned so far came from an unreadable sector (zero-filled).</summary>
    public bool HadReadErrors { get; private set; }
    /// <summary>Set when the layout could not be resolved (e.g. resident record no longer parses).</summary>
    public string? LayoutError { get; private set; }

    public ContentStream(ResilientReader reader, DataLayout layout)
    {
        _reader = reader;
        _layout = layout;
        _length = Math.Max(0, layout.Length);
        _extentStarts = new long[layout.Extents.Count];
        long p = 0;
        for (int i = 0; i < layout.Extents.Count; i++) { _extentStarts[i] = p; p += layout.Extents[i].Length; }
        if (layout.Kind == LayoutKind.NtfsResident) LoadResident();
    }

    private void LoadResident()
    {
        var raw = new byte[_layout.RecordSize];
        _reader.Read(_layout.RecordOffset, raw, out bool err);
        HadReadErrors |= err;
        var rec = MftRecord.Parse(raw, _layout.RecordSize);
        var attr = rec?.Attributes.FirstOrDefault(a => a.Type == MftRecord.AttrData && !a.NonResident && a.Instance == _layout.AttributeInstance)
                   ?? rec?.Attributes.FirstOrDefault(a => a.Type == MftRecord.AttrData && !a.NonResident && a.Name.Length == 0);
        if (rec == null || attr == null)
        {
            LayoutError = "The MFT record no longer contains the resident data (record changed since the scan).";
            _resident = Array.Empty<byte>();
            return;
        }
        _resident = rec.Raw.AsSpan(attr.ValueOffsetInRecord, Math.Min(attr.ValueLength, (int)_length)).ToArray();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, _length); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= _length || buffer.Length == 0) return 0;
        int want = (int)Math.Min(buffer.Length, _length - _pos);
        var dst = buffer.Slice(0, want);
        int n = _layout.Kind switch
        {
            LayoutKind.NtfsResident => ReadResident(dst),
            LayoutKind.NtfsCompressed => ReadCompressed(dst),
            _ => ReadExtents(dst),
        };
        // Bytes beyond the NTFS initialized length are defined to read as zero.
        if (_layout.InitializedLength >= 0 && _pos + n > _layout.InitializedLength)
        {
            long zs = Math.Max(_layout.InitializedLength, _pos);
            dst.Slice((int)(zs - _pos), (int)(_pos + n - zs)).Clear();
        }
        _pos += n;
        return n;
    }

    private int ReadResident(Span<byte> dst)
    {
        var r = _resident!;
        if (_pos >= r.Length) return 0;
        int n = (int)Math.Min(dst.Length, r.Length - _pos);
        r.AsSpan((int)_pos, n).CopyTo(dst);
        return n;
    }

    private int FindExtent(long logical)
    {
        int lo = 0, hi = _extentStarts.Length - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_extentStarts[mid] <= logical) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    private int ReadExtents(Span<byte> dst)
    {
        int done = 0;
        while (done < dst.Length)
        {
            long logical = _pos + done;
            int i = FindExtent(logical);
            if (i < 0) break;
            var e = _layout.Extents[i];
            long inExt = logical - _extentStarts[i];
            if (inExt >= e.Length) break; // past the described extents: content unknown
            int n = (int)Math.Min(e.Length - inExt, dst.Length - done);
            if (e.IsSparse) dst.Slice(done, n).Clear();
            else
            {
                _reader.Read(e.Offset + inExt, dst.Slice(done, n), out bool err);
                HadReadErrors |= err;
            }
            done += n;
        }
        return done;
    }

    private int ReadCompressed(Span<byte> dst)
    {
        int cuBytes = _layout.CompressionUnitClusters * _layout.ClusterSize;
        if (cuBytes <= 0) return ReadExtents(dst);
        int done = 0;
        while (done < dst.Length)
        {
            long logical = _pos + done;
            long cu = logical / cuBytes;
            if (cu != _cuIndex) LoadCompressionUnit(cu, cuBytes);
            int inCu = (int)(logical % cuBytes);
            int n = Math.Min(cuBytes - inCu, dst.Length - done);
            _cuData.AsSpan(inCu, n).CopyTo(dst.Slice(done));
            done += n;
        }
        return done;
    }

    private void LoadCompressionUnit(long cu, int cuBytes)
    {
        _cuData ??= new byte[cuBytes];
        _cuIndex = cu;
        long start = cu * cuBytes;
        // Gather the physical pieces that map this compression unit.
        var pieces = new List<Extent>();
        long allocated = 0;
        long cursor = start;
        long end = start + cuBytes;
        while (cursor < end)
        {
            int i = FindExtent(cursor);
            if (i < 0) break;
            var e = _layout.Extents[i];
            long inExt = cursor - _extentStarts[i];
            if (inExt >= e.Length) break;
            long n = Math.Min(e.Length - inExt, end - cursor);
            if (e.IsSparse) pieces.Add(new Extent(-1, n));
            else { pieces.Add(new Extent(e.Offset + inExt, n)); allocated += n; }
            cursor += n;
        }
        Array.Clear(_cuData);
        if (allocated == 0) return; // fully sparse: zeros
        var raw = new byte[allocated];
        long p = 0;
        foreach (var pc in pieces.Where(x => !x.IsSparse))
        {
            _reader.Read(pc.Offset, raw.AsSpan((int)p, (int)pc.Length), out bool err);
            HadReadErrors |= err;
            p += pc.Length;
        }
        if (allocated >= cuBytes) raw.AsSpan(0, cuBytes).CopyTo(_cuData); // stored uncompressed
        else Lznt1.Decompress(raw, _cuData);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => _length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Source content is read-only.");
}
