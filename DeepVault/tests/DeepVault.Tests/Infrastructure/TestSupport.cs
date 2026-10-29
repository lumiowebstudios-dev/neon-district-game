using System.Text.Json;
using DeepVault.Core.IO;

namespace DeepVault.Tests.Infrastructure;

/// <summary>Wraps a source and fails reads that touch configured ranges (simulates bad sectors).</summary>
public sealed class FaultyBlockSource : IBlockSource
{
    private readonly IBlockSource _inner;
    private readonly List<(long start, long end)> _bad;
    public long FailedReads { get; private set; }

    public FaultyBlockSource(IBlockSource inner, params (long start, long length)[] bad)
    {
        _inner = inner;
        _bad = bad.Select(b => (b.start, b.start + b.length)).ToList();
    }

    public long Length => _inner.Length;
    public int SectorSize => _inner.SectorSize;
    public string Description => "faulty:" + _inner.Description;

    public int Read(long offset, Span<byte> buffer)
    {
        foreach (var (s, e) in _bad)
            if (offset < e && offset + buffer.Length > s)
            {
                FailedReads++;
                throw new SourceReadException(offset, buffer.Length, "simulated CRC error");
            }
        return _inner.Read(offset, buffer);
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>Test G: a structured, machine-readable record of each recovery scenario run.</summary>
public sealed class ScenarioReport
{
    public string Scenario { get; set; } = "";
    public string Setup { get; set; } = "";
    public string Methods { get; set; } = "";
    public int TrackedFiles { get; set; }
    public int Detected { get; set; }
    public int Reconstructed { get; set; }
    public int VerifiedByHash { get; set; }
    public int PartialOrFlagged { get; set; }
    public int NotRecovered { get; set; }
    public long TotalCandidates { get; set; }
    public List<string> Failures { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
    public List<string> SkippedRegions { get; set; } = new();
    public List<string> Details { get; set; } = new();
    public string Outcome { get; set; } = "";
    public DateTime RanAtUtc { get; set; } = DateTime.UtcNow;

    public static string Dir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("DEEPVAULT_SCENARIO_DIR") ?? Path.Combine(AppContext.BaseDirectory, "scenario-reports");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public void Save() => File.WriteAllText(Path.Combine(Dir, Scenario.Replace(' ', '_').Replace('/', '_') + ".json"),
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
