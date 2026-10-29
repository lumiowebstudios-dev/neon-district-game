namespace DeepVault.Core.IO;

/// <summary>
/// Small LRU page cache for random look-ahead reads (used by format validators while carving).
/// Memory use is bounded by PageSize * MaxPages.
/// </summary>
public sealed class CachedReader
{
    private const int PageSize = 64 * 1024;
    private readonly ResilientReader _reader;
    private readonly int _maxPages;
    private readonly Dictionary<long, LinkedListNode<(long page, byte[] data, int valid)>> _map = new();
    private readonly LinkedList<(long page, byte[] data, int valid)> _lru = new();

    public long Length => _reader.Source.Length;

    public CachedReader(ResilientReader reader, int maxPages = 128)
    {
        _reader = reader;
        _maxPages = maxPages;
    }

    private (byte[] data, int valid) GetPage(long page)
    {
        if (_map.TryGetValue(page, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return (node.Value.data, node.Value.valid);
        }
        byte[] data;
        if (_lru.Count >= _maxPages)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.page);
            data = last.Value.data;
        }
        else data = new byte[PageSize];
        int valid = _reader.Read(page * PageSize, data);
        var n = _lru.AddFirst((page, data, valid));
        _map[page] = n;
        return (data, valid);
    }

    /// <summary>Reads into buffer; returns bytes available (short only at end of media).</summary>
    public int Read(long offset, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            long abs = offset + done;
            if (abs >= Length) break;
            long page = abs / PageSize;
            int inPage = (int)(abs % PageSize);
            var (data, valid) = GetPage(page);
            if (inPage >= valid) break;
            int n = Math.Min(valid - inPage, buffer.Length - done);
            data.AsSpan(inPage, n).CopyTo(buffer.Slice(done));
            done += n;
        }
        return done;
    }

    public int ReadByte(long offset)
    {
        Span<byte> b = stackalloc byte[1];
        return Read(offset, b) == 1 ? b[0] : -1;
    }

    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
    }
}
