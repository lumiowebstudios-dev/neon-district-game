using System.Text;
using DeepVault.Core.Util;

namespace DeepVault.Core.FileSystems.Ntfs;

public readonly record struct DataRun(long Lcn, long Clusters)
{
    public bool IsSparse => Lcn < 0;
}

public sealed class NtfsAttribute
{
    public uint Type { get; init; }
    public string Name { get; init; } = "";
    public bool NonResident { get; init; }
    public ushort Flags { get; init; }
    public ushort Instance { get; init; }

    // Resident
    public int ValueOffsetInRecord { get; init; }
    public int ValueLength { get; init; }

    // Non-resident
    public long StartVcn { get; init; }
    public long LastVcn { get; init; }
    public int CompressionUnitLog2 { get; init; }
    public long AllocatedSize { get; init; }
    public long RealSize { get; init; }
    public long InitializedSize { get; init; }
    public List<DataRun> Runs { get; init; } = new();
    public bool RunlistValid { get; init; } = true;

    public bool IsCompressed => (Flags & 0x0001) != 0;
    public bool IsEncrypted => (Flags & 0x4000) != 0;
    public bool IsSparse => (Flags & 0x8000) != 0;
}

public sealed class FileNameInfo
{
    public ulong ParentRef { get; init; }
    public string Name { get; init; } = "";
    public byte Namespace { get; init; }
    public long Created { get; init; }
    public long Modified { get; init; }
    public long Accessed { get; init; }
    public long RealSize { get; init; }
    public uint Flags { get; init; }

    public long ParentRecord => (long)(ParentRef & 0xFFFFFFFFFFFF);
    public ushort ParentSeq => (ushort)(ParentRef >> 48);
}

public sealed class AttrListEntry
{
    public uint Type { get; init; }
    public string Name { get; init; } = "";
    public long StartVcn { get; init; }
    public ulong RecordRef { get; init; }
    public ushort Instance { get; init; }
    public long RecordNumber => (long)(RecordRef & 0xFFFFFFFFFFFF);
}

/// <summary>A parsed NTFS MFT FILE record (read-only).</summary>
public sealed class MftRecord
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrIndexRoot = 0x90;

    public byte[] Raw { get; }
    public bool FixupOk { get; private set; }
    public uint RecordNumber { get; private set; }
    public bool HasRecordNumber { get; private set; }
    public ushort Sequence { get; private set; }
    public ushort Flags { get; private set; }
    public ulong BaseRef { get; private set; }
    public List<NtfsAttribute> Attributes { get; } = new();
    public List<FileNameInfo> FileNames { get; } = new();
    public List<AttrListEntry> AttributeList { get; } = new();
    public bool AttributeListNonResident { get; private set; }
    public NtfsAttribute? AttributeListAttr { get; private set; }
    public long SiCreated { get; private set; }
    public long SiModified { get; private set; }
    public long SiAccessed { get; private set; }
    public uint SiAttributes { get; private set; }
    public bool Truncated { get; private set; }

    public bool InUse => (Flags & 0x0001) != 0;
    public bool IsDirectory => (Flags & 0x0002) != 0;
    public long BaseRecordNumber => (long)(BaseRef & 0xFFFFFFFFFFFF);
    public bool IsExtension => BaseRef != 0;

    private MftRecord(byte[] raw) { Raw = raw; }

    public static bool HasSignature(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 'F' && b[1] == 'I' && b[2] == 'L' && b[3] == 'E';

    /// <summary>Parses a record (copies the bytes). Returns null if it is not a structurally plausible FILE record.</summary>
    public static MftRecord? Parse(ReadOnlySpan<byte> data, int recordSize)
    {
        if (data.Length < recordSize || recordSize < 256 || !HasSignature(data)) return null;
        var rec = new MftRecord(data.Slice(0, recordSize).ToArray());
        return rec.ParseInternal() ? rec : null;
    }

    private bool ParseInternal()
    {
        var b = Raw.AsSpan();
        int usaOfs = Bytes.U16(b, 4);
        int usaCount = Bytes.U16(b, 6);
        int expectedCount = Raw.Length / 512 + 1;
        if (usaOfs < 0x28 || usaOfs + usaCount * 2 > Raw.Length || usaCount != expectedCount) return false;
        FixupOk = true;
        ushort usn = Bytes.U16(b, usaOfs);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * 512 - 2;
            if (Bytes.U16(b, end) != usn) FixupOk = false;
            b[end] = b[usaOfs + i * 2];
            b[end + 1] = b[usaOfs + i * 2 + 1];
        }
        Sequence = Bytes.U16(b, 0x10);
        int firstAttr = Bytes.U16(b, 0x14);
        Flags = Bytes.U16(b, 0x16);
        int bytesUsed = (int)Bytes.U32(b, 0x18);
        BaseRef = Bytes.U64(b, 0x20);
        if (usaOfs >= 0x30)
        {
            RecordNumber = Bytes.U32(b, 0x2C);
            HasRecordNumber = true;
        }
        if (firstAttr < 0x30 - 8 || firstAttr >= Raw.Length) return false;
        int limit = Math.Min(bytesUsed > 0 && bytesUsed <= Raw.Length ? bytesUsed : Raw.Length, Raw.Length);

        int off = firstAttr;
        for (int guard = 0; guard < 128; guard++)
        {
            if (off + 8 > limit) { Truncated = true; break; }
            uint type = Bytes.U32(b, off);
            if (type == 0xFFFFFFFF) break;
            int len = (int)Bytes.U32(b, off + 4);
            if (len < 0x18 || off + len > limit || (len & 7) != 0) { Truncated = true; break; }
            try { ParseAttribute(b.Slice(off, len), off, type); }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) { Truncated = true; }
            off += len;
        }
        return true;
    }

    private void ParseAttribute(ReadOnlySpan<byte> a, int recOff, uint type)
    {
        bool nonRes = a[8] != 0;
        int nameLen = a[9];
        int nameOff = Bytes.U16(a, 0x0A);
        ushort flags = Bytes.U16(a, 0x0C);
        ushort inst = Bytes.U16(a, 0x0E);
        string name = nameLen > 0 && nameOff + nameLen * 2 <= a.Length ? Encoding.Unicode.GetString(a.Slice(nameOff, nameLen * 2)) : "";
        NtfsAttribute attr;
        if (!nonRes)
        {
            int vlen = (int)Bytes.U32(a, 0x10);
            int voff = Bytes.U16(a, 0x14);
            if (voff + vlen > a.Length) throw new ArgumentOutOfRangeException();
            attr = new NtfsAttribute
            {
                Type = type, Name = name, NonResident = false, Flags = flags, Instance = inst,
                ValueOffsetInRecord = recOff + voff, ValueLength = vlen,
            };
            var v = a.Slice(voff, vlen);
            switch (type)
            {
                case AttrStandardInformation when vlen >= 0x24:
                    SiCreated = Bytes.I64(v, 0);
                    SiModified = Bytes.I64(v, 8);
                    SiAccessed = Bytes.I64(v, 24);
                    SiAttributes = Bytes.U32(v, 32);
                    break;
                case AttrFileName when vlen >= 0x42:
                {
                    int nl = v[0x40];
                    if (0x42 + nl * 2 <= vlen)
                        FileNames.Add(new FileNameInfo
                        {
                            ParentRef = Bytes.U64(v, 0),
                            Created = Bytes.I64(v, 8),
                            Modified = Bytes.I64(v, 0x10),
                            Accessed = Bytes.I64(v, 0x20),
                            RealSize = Bytes.I64(v, 0x30),
                            Flags = Bytes.U32(v, 0x38),
                            Namespace = v[0x41],
                            Name = Encoding.Unicode.GetString(v.Slice(0x42, nl * 2)),
                        });
                    break;
                }
                case AttrAttributeList:
                    ParseAttributeList(v);
                    AttributeListAttr = attr;
                    break;
            }
        }
        else
        {
            long startVcn = Bytes.I64(a, 0x10);
            long lastVcn = Bytes.I64(a, 0x18);
            int runOff = Bytes.U16(a, 0x20);
            int cu = Bytes.U16(a, 0x22);
            var runs = new List<DataRun>();
            bool ok = runOff < a.Length && DecodeRunlist(a.Slice(runOff), runs);
            attr = new NtfsAttribute
            {
                Type = type, Name = name, NonResident = true, Flags = flags, Instance = inst,
                StartVcn = startVcn, LastVcn = lastVcn, CompressionUnitLog2 = cu,
                AllocatedSize = Bytes.I64(a, 0x28), RealSize = Bytes.I64(a, 0x30), InitializedSize = Bytes.I64(a, 0x38),
                Runs = runs, RunlistValid = ok,
            };
            if (type == AttrAttributeList)
            {
                AttributeListNonResident = true;
                AttributeListAttr = attr;
            }
        }
        Attributes.Add(attr);
    }

    /// <summary>Parses attribute-list entries (from a resident value or from bytes read from disk).</summary>
    public void ParseAttributeList(ReadOnlySpan<byte> v)
    {
        int p = 0;
        while (p + 0x1A <= v.Length)
        {
            uint t = Bytes.U32(v, p);
            int rl = Bytes.U16(v, p + 4);
            if (rl < 0x1A || p + rl > v.Length) break;
            int nl = v[p + 6];
            int no = v[p + 7];
            string n = nl > 0 && p + no + nl * 2 <= v.Length ? Encoding.Unicode.GetString(v.Slice(p + no, nl * 2)) : "";
            AttributeList.Add(new AttrListEntry
            {
                Type = t, Name = n, StartVcn = Bytes.I64(v, p + 8), RecordRef = Bytes.U64(v, p + 0x10), Instance = Bytes.U16(v, p + 0x18),
            });
            p += rl;
        }
    }

    /// <summary>Decodes an NTFS mapping-pairs array. Sparse runs get Lcn = -1.</summary>
    public static bool DecodeRunlist(ReadOnlySpan<byte> rl, List<DataRun> runs)
    {
        int p = 0;
        long lcn = 0;
        while (p < rl.Length)
        {
            byte h = rl[p++];
            if (h == 0) return true;
            int lenBytes = h & 0x0F;
            int offBytes = h >> 4;
            if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || p + lenBytes + offBytes > rl.Length) return false;
            long len = 0;
            for (int i = 0; i < lenBytes; i++) len |= (long)rl[p + i] << (8 * i);
            p += lenBytes;
            if (len <= 0) return false;
            if (offBytes == 0)
            {
                runs.Add(new DataRun(-1, len));
                continue;
            }
            long delta = 0;
            for (int i = 0; i < offBytes; i++) delta |= (long)rl[p + i] << (8 * i);
            if ((rl[p + offBytes - 1] & 0x80) != 0 && offBytes < 8) delta |= -1L << (8 * offBytes);
            p += offBytes;
            lcn += delta;
            if (lcn < 0) return false;
            runs.Add(new DataRun(lcn, len));
        }
        return false;
    }

    /// <summary>Best display name: prefer Win32 / Win32+DOS, then POSIX, then DOS 8.3.</summary>
    public FileNameInfo? BestFileName()
    {
        FileNameInfo? best = null;
        int bestRank = -1;
        foreach (var fn in FileNames)
        {
            int rank = fn.Namespace switch { 1 => 3, 3 => 3, 0 => 2, 2 => 1, _ => 0 };
            if (rank > bestRank) { best = fn; bestRank = rank; }
        }
        return best;
    }

    /// <summary>The unnamed $DATA attribute(s) (several when the runlist is split across records).</summary>
    public IEnumerable<NtfsAttribute> UnnamedData() => Attributes.Where(a => a.Type == AttrData && a.Name.Length == 0);
}
