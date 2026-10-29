namespace DeepVault.Core.FileSystems.Ntfs;

/// <summary>LZNT1 decompressor (the algorithm used by NTFS file compression).</summary>
public static class Lznt1
{
    /// <summary>
    /// Decompresses <paramref name="src"/> into <paramref name="dst"/>. Returns bytes written.
    /// Malformed input never throws; decoding stops at the first inconsistency.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int sp = 0, dp = 0;
        while (sp + 2 <= src.Length && dp < dst.Length)
        {
            int header = src[sp] | (src[sp + 1] << 8);
            if (header == 0) break;
            int chunkLen = (header & 0x0FFF) + 1;
            bool compressed = (header & 0x8000) != 0;
            sp += 2;
            int chunkEnd = Math.Min(sp + chunkLen, src.Length);
            int chunkStart = dp;
            if (!compressed)
            {
                int n = Math.Min(Math.Min(chunkEnd - sp, 4096), dst.Length - dp);
                src.Slice(sp, n).CopyTo(dst.Slice(dp));
                dp += n;
                sp = chunkEnd;
                continue;
            }
            while (sp < chunkEnd && dp < dst.Length)
            {
                byte flags = src[sp++];
                for (int bit = 0; bit < 8 && sp < chunkEnd && dp < dst.Length; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        dst[dp++] = src[sp++];
                        continue;
                    }
                    if (sp + 2 > chunkEnd) return dp;
                    int token = src[sp] | (src[sp + 1] << 8);
                    sp += 2;
                    int pos = dp - chunkStart;
                    int lg = 0;
                    for (int i = pos - 1; i >= 0x10; i >>= 1) lg++;
                    int back = (token >> (12 - lg)) + 1;
                    int length = (token & (0xFFF >> lg)) + 3;
                    if (back > pos) return dp; // corrupt reference
                    for (int i = 0; i < length && dp < dst.Length; i++)
                    {
                        dst[dp] = dst[dp - back];
                        dp++;
                    }
                }
            }
            sp = chunkEnd;
            // Every chunk represents 4096 output bytes; a chunk that decodes short is zero-padded
            // when more chunks follow.
            bool more = sp + 2 <= src.Length && (src[sp] | (src[sp + 1] << 8)) != 0;
            if (more)
            {
                int pad = Math.Min(chunkStart + 4096, dst.Length) - dp;
                if (pad > 0) { dst.Slice(dp, pad).Clear(); dp += pad; }
            }
        }
        return dp;
    }
}
