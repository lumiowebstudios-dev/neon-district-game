using System.Text.Json;
using DeepVault.Core.Content;
using DeepVault.Core.Engines;
using DeepVault.Core.FileSystems.Ntfs;
using DeepVault.Core.IO;
using DeepVault.Core.Partitions;
using Microsoft.Data.Sqlite;

namespace DeepVault.Core.Catalog;

public enum ResultView { All, Recoverable, Partial, Uncertain, Failed, Duplicates }
public enum SortField { Name, Path, Size, Type, Modified, Created, Confidence, Offset, Id }

public sealed class FileQuery
{
    public string? Search { get; set; }
    public FileCategory? Category { get; set; }
    public string? Extension { get; set; }
    public EngineId? Engine { get; set; }
    public Confidence? MinConfidence { get; set; }
    public ResultView View { get; set; } = ResultView.All;
    public bool IncludeExisting { get; set; } = true;
    public bool OnlyDeleted { get; set; }
    public bool HidePartitionNoise { get; set; }
    public long? MinSize { get; set; }
    public SortField Sort { get; set; } = SortField.Name;
    public bool Descending { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed record PhaseState(string Name, string Status, long Position, string? Detail);

public sealed record RestoreItemRecord(long RunId, long FileId, string Outcome, string? TargetPath, long Bytes, string? Sha256, string? Message);

/// <summary>
/// A DeepVault project: one SQLite file holding the source identity, scan state/checkpoints,
/// the unified results catalogue, issues/unreadable regions and restore history.
/// It never stores recovered file contents.
/// </summary>
public sealed class ProjectDatabase : IScanSink, IOrphanRecordStore, IDisposable
{
    public const string FileExtension = ".dvproj";
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private readonly List<FoundFile> _pendingFiles = new();
    private readonly List<ScanIssue> _pendingIssues = new();
    private readonly List<DiskRegion> _pendingRegions = new();
    private readonly List<OrphanRecord> _pendingOrphans = new();

    public string Path { get; }
    /// <summary>Tag written to rows so an interrupted phase can be rolled back to its last checkpoint.</summary>
    public string CurrentPhase { get; set; } = "";

    static ProjectDatabase() => SQLitePCL.Batteries_V2.Init();

    private ProjectDatabase(string path)
    {
        Path = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;");
        Exec(@"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS files(
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, name_lc TEXT NOT NULL, path TEXT, ext TEXT, category INTEGER, size INTEGER,
  created INTEGER, modified INTEGER, accessed INTEGER, engines INTEGER, source_detail TEXT, volume_label TEXT, volume_offset INTEGER,
  is_deleted INTEGER, state INTEGER, confidence INTEGER, structure INTEGER, overwritten REAL, first_offset INTEGER, layout TEXT,
  format_id TEXT, notes TEXT, duplicate_of INTEGER, content_hash TEXT, parent_key INTEGER, scan_unit INTEGER, phase TEXT);
CREATE INDEX IF NOT EXISTS ix_files_name ON files(name_lc);
CREATE INDEX IF NOT EXISTS ix_files_ext ON files(ext);
CREATE INDEX IF NOT EXISTS ix_files_cat ON files(category);
CREATE INDEX IF NOT EXISTS ix_files_size ON files(size);
CREATE INDEX IF NOT EXISTS ix_files_state ON files(state, confidence);
CREATE INDEX IF NOT EXISTS ix_files_offset ON files(first_offset);
CREATE INDEX IF NOT EXISTS ix_files_parent ON files(scan_unit, parent_key);
CREATE INDEX IF NOT EXISTS ix_files_mod ON files(modified);
CREATE INDEX IF NOT EXISTS ix_files_phase ON files(phase);
CREATE TABLE IF NOT EXISTS regions(id INTEGER PRIMARY KEY, offset INTEGER, length INTEGER, kind INTEGER, note TEXT, phase TEXT);
CREATE TABLE IF NOT EXISTS issues(id INTEGER PRIMARY KEY, time INTEGER, severity INTEGER, engine INTEGER, message TEXT, offset INTEGER, phase TEXT);
CREATE TABLE IF NOT EXISTS partitions(id INTEGER PRIMARY KEY, json TEXT);
CREATE TABLE IF NOT EXISTS phases(name TEXT PRIMARY KEY, status TEXT, position INTEGER, detail TEXT, max_file INTEGER, max_orphan INTEGER, max_region INTEGER, max_issue INTEGER, updated INTEGER);
CREATE TABLE IF NOT EXISTS orphan_records(id INTEGER PRIMARY KEY, json TEXT, phase TEXT);
CREATE TABLE IF NOT EXISTS restore_runs(id INTEGER PRIMARY KEY, started INTEGER, finished INTEGER, destination TEXT, summary TEXT);
CREATE TABLE IF NOT EXISTS restore_items(run_id INTEGER, file_id INTEGER, outcome TEXT, target TEXT, bytes INTEGER, sha256 TEXT, message TEXT);
CREATE INDEX IF NOT EXISTS ix_restore_items ON restore_items(run_id);
");
        if (GetMeta("schema") == null) SetMeta("schema", SchemaVersion.ToString());
    }

    public static ProjectDatabase Create(string path)
    {
        if (File.Exists(path)) throw new IOException($"A project already exists at {path}.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var db = new ProjectDatabase(path);
        db.SetMeta("created", DateTime.UtcNow.ToString("O"));
        return db;
    }

    public static ProjectDatabase Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Project not found", path);
        return new ProjectDatabase(path);
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = sql;
            foreach (var (n, v) in args) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    private T? Scalar<T>(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = sql;
            foreach (var (n, v) in args) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
            var r = c.ExecuteScalar();
            if (r == null || r is DBNull) return default;
            return (T)Convert.ChangeType(r, typeof(T));
        }
    }

    // ---------------------------------------------------------------- meta
    public string? GetMeta(string key) => Scalar<string>("SELECT value FROM meta WHERE key=$k", ("$k", key));
    public void SetMeta(string key, string? value) => Exec("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    public T? GetMetaJson<T>(string key) { var s = GetMeta(key); return s == null ? default : JsonSerializer.Deserialize<T>(s); }
    public void SetMetaJson<T>(string key, T value) => SetMeta(key, JsonSerializer.Serialize(value));

    // ---------------------------------------------------------------- IScanSink
    public void AddFile(FoundFile file) { lock (_lock) { _pendingFiles.Add(file); if (_pendingFiles.Count >= 20000) FlushLocked(); } }
    public void AddIssue(ScanIssue issue) { lock (_lock) _pendingIssues.Add(issue); }
    public void Report(DiskRegion region) { lock (_lock) _pendingRegions.Add(region); }
    public void Add(OrphanRecord r) { lock (_lock) { _pendingOrphans.Add(r); if (_pendingOrphans.Count >= 20000) FlushLocked(); } }

    public void Commit() { lock (_lock) FlushLocked(); }

    private static object? Ticks(DateTime? d) => d?.ToUniversalTime().Ticks;

    private void FlushLocked()
    {
        if (_pendingFiles.Count + _pendingIssues.Count + _pendingRegions.Count + _pendingOrphans.Count == 0) return;
        using var tx = _db.BeginTransaction();
        if (_pendingFiles.Count > 0)
        {
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = @"INSERT INTO files(name,name_lc,path,ext,category,size,created,modified,accessed,engines,source_detail,volume_label,volume_offset,
 is_deleted,state,confidence,structure,overwritten,first_offset,layout,format_id,notes,duplicate_of,content_hash,parent_key,scan_unit,phase)
 VALUES($name,$lc,$path,$ext,$cat,$size,$cr,$mo,$ac,$eng,$src,$vl,$vo,$del,$st,$conf,$str,$ow,$fo,$lay,$fmt,$notes,$dup,$hash,$pk,$su,$ph); SELECT last_insert_rowid();";
            var ps = new[] { "$name", "$lc", "$path", "$ext", "$cat", "$size", "$cr", "$mo", "$ac", "$eng", "$src", "$vl", "$vo", "$del", "$st", "$conf", "$str", "$ow", "$fo", "$lay", "$fmt", "$notes", "$dup", "$hash", "$pk", "$su", "$ph" }
                .Select(n => c.Parameters.Add(n, SqliteType.Text)).ToArray();
            foreach (var f in _pendingFiles)
            {
                object?[] v =
                {
                    f.Name, f.Name.ToLowerInvariant(), f.Path, f.Extension, (int)f.Category, f.Size, Ticks(f.Created), Ticks(f.Modified), Ticks(f.Accessed),
                    (int)f.Engines, f.SourceDetail, f.VolumeLabel, f.VolumeOffset, f.IsDeleted is null ? null : f.IsDeleted.Value ? 1 : 0,
                    (int)f.State, (int)f.Confidence, (int)f.Structure, f.OverwrittenFraction, f.FirstOffset, f.Layout.ToJson(), f.FormatId, f.Notes,
                    f.DuplicateOf, f.ContentHash, f.ParentKey >= 0 && f.Path == null ? f.ParentKey : null, f.ScanUnit, CurrentPhase,
                };
                for (int i = 0; i < v.Length; i++) ps[i].Value = v[i] ?? DBNull.Value;
                f.Id = (long)c.ExecuteScalar()!;
            }
        }
        if (_pendingIssues.Count > 0)
        {
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO issues(time,severity,engine,message,offset,phase) VALUES($t,$s,$e,$m,$o,$p)";
            var t = c.Parameters.Add("$t", SqliteType.Integer); var s = c.Parameters.Add("$s", SqliteType.Integer); var e = c.Parameters.Add("$e", SqliteType.Integer);
            var m = c.Parameters.Add("$m", SqliteType.Text); var o = c.Parameters.Add("$o", SqliteType.Integer); var p = c.Parameters.Add("$p", SqliteType.Text);
            foreach (var i in _pendingIssues)
            {
                t.Value = DateTime.UtcNow.Ticks; s.Value = (int)i.Severity; e.Value = (int)i.Engine; m.Value = i.Message; o.Value = (object?)i.Offset ?? DBNull.Value; p.Value = CurrentPhase;
                c.ExecuteNonQuery();
            }
        }
        if (_pendingRegions.Count > 0)
        {
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO regions(offset,length,kind,note,phase) VALUES($o,$l,$k,$n,$p)";
            var o = c.Parameters.Add("$o", SqliteType.Integer); var l = c.Parameters.Add("$l", SqliteType.Integer); var k = c.Parameters.Add("$k", SqliteType.Integer);
            var n = c.Parameters.Add("$n", SqliteType.Text); var p = c.Parameters.Add("$p", SqliteType.Text);
            foreach (var r in MergeRegions(_pendingRegions))
            {
                o.Value = r.Offset; l.Value = r.Length; k.Value = (int)r.Kind; n.Value = r.Note; p.Value = CurrentPhase;
                c.ExecuteNonQuery();
            }
        }
        if (_pendingOrphans.Count > 0)
        {
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT INTO orphan_records(json,phase) VALUES($j,$p)";
            var j = c.Parameters.Add("$j", SqliteType.Text); var p = c.Parameters.Add("$p", SqliteType.Text);
            foreach (var r in _pendingOrphans) { j.Value = r.ToJson(); p.Value = CurrentPhase; c.ExecuteNonQuery(); }
        }
        tx.Commit();
        _pendingFiles.Clear(); _pendingIssues.Clear(); _pendingRegions.Clear(); _pendingOrphans.Clear();
    }

    /// <summary>Unions overlapping or adjacent regions of the same kind (bad sectors are reported one by one).</summary>
    internal static List<DiskRegion> MergeRegions(IEnumerable<DiskRegion> regions)
    {
        var res = new List<DiskRegion>();
        foreach (var g in regions.GroupBy(r => r.Kind))
        {
            DiskRegion? cur = null;
            foreach (var r in g.OrderBy(r => r.Offset))
            {
                if (cur is { } c && r.Offset <= c.Offset + c.Length)
                {
                    long end = Math.Max(c.Offset + c.Length, r.Offset + r.Length);
                    cur = c with { Length = end - c.Offset };
                    continue;
                }
                if (cur != null) res.Add(cur.Value);
                cur = r;
            }
            if (cur != null) res.Add(cur.Value);
        }
        return res;
    }

    public void ResolvePaths(int scanUnit, Func<long, string> resolve)
    {
        lock (_lock)
        {
            FlushLocked();
            var keys = new List<long>();
            using (var c = _db.CreateCommand())
            {
                c.CommandText = "SELECT DISTINCT parent_key FROM files WHERE scan_unit=$u AND path IS NULL AND parent_key IS NOT NULL";
                c.Parameters.AddWithValue("$u", scanUnit);
                using var r = c.ExecuteReader();
                while (r.Read()) keys.Add(r.GetInt64(0));
            }
            using var tx = _db.BeginTransaction();
            using var u = _db.CreateCommand();
            u.Transaction = tx;
            u.CommandText = "UPDATE files SET path=$p, parent_key=NULL WHERE scan_unit=$u AND parent_key=$k AND path IS NULL";
            var pp = u.Parameters.Add("$p", SqliteType.Text); var pu = u.Parameters.Add("$u", SqliteType.Integer); var pk = u.Parameters.Add("$k", SqliteType.Integer);
            foreach (var k in keys)
            {
                pp.Value = resolve(k); pu.Value = scanUnit; pk.Value = k;
                u.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------------------------------------------------------------- orphan store
    public IEnumerable<OrphanRecord> All()
    {
        Commit();
        long last = 0;
        while (true)
        {
            var batch = new List<(long, string)>();
            lock (_lock)
            {
                using var c = _db.CreateCommand();
                c.CommandText = "SELECT id, json FROM orphan_records WHERE id > $l ORDER BY id LIMIT 5000";
                c.Parameters.AddWithValue("$l", last);
                using var r = c.ExecuteReader();
                while (r.Read()) batch.Add((r.GetInt64(0), r.GetString(1)));
            }
            if (batch.Count == 0) yield break;
            foreach (var (id, json) in batch) { last = id; yield return OrphanRecord.FromJson(json); }
        }
    }

    public long Count { get { Commit(); return Scalar<long>("SELECT COUNT(*) FROM orphan_records"); } }

    // ---------------------------------------------------------------- phases / checkpoints
    public PhaseState? GetPhase(string name)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT status, position, detail FROM phases WHERE name=$n";
            c.Parameters.AddWithValue("$n", name);
            using var r = c.ExecuteReader();
            return r.Read() ? new PhaseState(name, r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)) : null;
        }
    }

    public List<PhaseState> GetPhases()
    {
        var list = new List<PhaseState>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT name, status, position, detail FROM phases ORDER BY updated";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new PhaseState(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return list;
    }

    /// <summary>Flushes pending rows and records the phase position atomically.</summary>
    public void Checkpoint(string phase, string status, long position, string? detail = null)
    {
        lock (_lock)
        {
            FlushLocked();
            using var c = _db.CreateCommand();
            c.CommandText = @"INSERT INTO phases(name,status,position,detail,max_file,max_orphan,max_region,max_issue,updated)
 VALUES($n,$s,$p,$d,(SELECT IFNULL(MAX(id),0) FROM files),(SELECT IFNULL(MAX(id),0) FROM orphan_records),(SELECT IFNULL(MAX(id),0) FROM regions),(SELECT IFNULL(MAX(id),0) FROM issues),$u)
 ON CONFLICT(name) DO UPDATE SET status=excluded.status, position=excluded.position, detail=excluded.detail,
 max_file=excluded.max_file, max_orphan=excluded.max_orphan, max_region=excluded.max_region, max_issue=excluded.max_issue, updated=excluded.updated";
            c.Parameters.AddWithValue("$n", phase); c.Parameters.AddWithValue("$s", status); c.Parameters.AddWithValue("$p", position);
            c.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value); c.Parameters.AddWithValue("$u", DateTime.UtcNow.Ticks);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks a phase stopped without moving its checkpoint: what was found since the last checkpoint is
    /// saved (so it can be browsed) but is discarded and found again when the phase resumes there.
    /// </summary>
    public void MarkPhaseStopped(string phase, string status)
    {
        lock (_lock)
        {
            FlushLocked();
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE phases SET status=$s, updated=$u WHERE name=$n";
            c.Parameters.AddWithValue("$s", status); c.Parameters.AddWithValue("$u", DateTime.UtcNow.Ticks); c.Parameters.AddWithValue("$n", phase);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes raw-pass finds at or beyond a disk offset before the pass resumes from there, so nothing
    /// is listed twice. (Version 1.0.0 recorded a stopped scan's checkpoint after the finds that
    /// followed it; this also repairs projects stopped with that version.)
    /// </summary>
    public void DiscardFindsFrom(string phase, long diskOffset)
    {
        lock (_lock)
        {
            FlushLocked();
            using var tx = _db.BeginTransaction();
            foreach (var sql in new[]
            {
                "DELETE FROM files WHERE phase=$p AND first_offset >= $o",
                "DELETE FROM orphan_records WHERE phase=$p AND json_extract(json,'$.Phys') >= $o",
            })
            {
                using var d = _db.CreateCommand();
                d.Transaction = tx;
                d.CommandText = sql;
                d.Parameters.AddWithValue("$p", phase); d.Parameters.AddWithValue("$o", diskOffset);
                d.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Discards rows a phase wrote after its last checkpoint (e.g. after a crash), so resuming from the
    /// checkpoint cannot create duplicates. A phase without a checkpoint loses all of its rows.
    /// </summary>
    public void RollbackPhase(string phase)
    {
        lock (_lock)
        {
            _pendingFiles.Clear(); _pendingIssues.Clear(); _pendingRegions.Clear(); _pendingOrphans.Clear();
            long mf = 0, mo = 0, mr = 0, mi = 0;
            using (var c = _db.CreateCommand())
            {
                c.CommandText = "SELECT max_file, max_orphan, max_region, max_issue FROM phases WHERE name=$n";
                c.Parameters.AddWithValue("$n", phase);
                using var r = c.ExecuteReader();
                if (r.Read()) { mf = r.GetInt64(0); mo = r.GetInt64(1); mr = r.GetInt64(2); mi = r.GetInt64(3); }
            }
            using var tx = _db.BeginTransaction();
            foreach (var (t, m) in new[] { ("files", mf), ("orphan_records", mo), ("regions", mr), ("issues", mi) })
            {
                using var d = _db.CreateCommand();
                d.Transaction = tx;
                d.CommandText = $"DELETE FROM {t} WHERE phase=$p AND id > $m";
                d.Parameters.AddWithValue("$p", phase); d.Parameters.AddWithValue("$m", m);
                d.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------------------------------------------------------------- partitions
    public void SavePartitions(IEnumerable<PartitionInfo> parts)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var d = _db.CreateCommand()) { d.Transaction = tx; d.CommandText = "DELETE FROM partitions"; d.ExecuteNonQuery(); }
            foreach (var p in parts)
            {
                using var c = _db.CreateCommand();
                c.Transaction = tx;
                c.CommandText = "INSERT INTO partitions(json) VALUES($j)";
                c.Parameters.AddWithValue("$j", JsonSerializer.Serialize(PartitionRecord.From(p)));
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<PartitionRecord> LoadPartitions()
    {
        var list = new List<PartitionRecord>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT json FROM partitions ORDER BY id";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(JsonSerializer.Deserialize<PartitionRecord>(r.GetString(0))!);
        }
        return list;
    }

    // ---------------------------------------------------------------- queries
    private static string ViewClause(ResultView v) => v switch
    {
        ResultView.Recoverable => "duplicate_of IS NULL AND state NOT IN (6,7) AND confidence >= 2",
        ResultView.Partial => "duplicate_of IS NULL AND state = 6",
        ResultView.Uncertain => "duplicate_of IS NULL AND state NOT IN (6,7) AND confidence < 2",
        ResultView.Failed => "duplicate_of IS NULL AND state = 7",
        ResultView.Duplicates => "duplicate_of IS NOT NULL",
        _ => "duplicate_of IS NULL",
    };

    private static (string where, List<(string, object?)> args) Where(FileQuery q)
    {
        var w = new List<string> { ViewClause(q.View) };
        var a = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            w.Add("(name_lc LIKE $s ESCAPE '\\' OR LOWER(IFNULL(path,'')) LIKE $s ESCAPE '\\')");
            var s = q.Search.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace('*', '%').Replace('?', '_');
            a.Add(("$s", "%" + s + "%"));
        }
        if (q.Category != null) { w.Add("category = $c"); a.Add(("$c", (int)q.Category)); }
        if (!string.IsNullOrWhiteSpace(q.Extension)) { w.Add("ext = $e"); a.Add(("$e", q.Extension.TrimStart('.').ToLowerInvariant())); }
        if (q.Engine != null) { w.Add("(engines & $g) != 0"); a.Add(("$g", (int)q.Engine)); }
        if (q.MinConfidence != null) { w.Add("confidence >= $mc"); a.Add(("$mc", (int)q.MinConfidence)); }
        if (!q.IncludeExisting) w.Add("(is_deleted IS NULL OR is_deleted = 1)");
        if (q.OnlyDeleted) w.Add("is_deleted = 1");
        if (q.MinSize != null) { w.Add("size >= $ms"); a.Add(("$ms", q.MinSize)); }
        return (string.Join(" AND ", w), a);
    }

    public long CountFiles(FileQuery q)
    {
        var (w, a) = Where(q);
        Commit();
        return Scalar<long>($"SELECT COUNT(*) FROM files WHERE {w}", a.ToArray());
    }

    public List<FoundFile> QueryFiles(FileQuery q)
    {
        var (w, a) = Where(q);
        string order = q.Sort switch
        {
            SortField.Size => "size",
            SortField.Type => "ext",
            SortField.Modified => "modified",
            SortField.Created => "created",
            SortField.Path => "path",
            SortField.Confidence => "confidence",
            SortField.Offset => "first_offset",
            SortField.Id => "id",
            _ => "name_lc",
        };
        Commit();
        var list = new List<FoundFile>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT * FROM files WHERE {w} ORDER BY {order} {(q.Descending ? "DESC" : "ASC")}, id LIMIT $lim OFFSET $off";
            foreach (var (n, v) in a) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
            c.Parameters.AddWithValue("$lim", q.Limit);
            c.Parameters.AddWithValue("$off", q.Offset);
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
        }
        return list;
    }

    public IEnumerable<FoundFile> AllFiles(bool includeDuplicates = false)
    {
        Commit();
        long last = 0;
        while (true)
        {
            var batch = new List<FoundFile>();
            lock (_lock)
            {
                using var c = _db.CreateCommand();
                c.CommandText = $"SELECT * FROM files WHERE id > $l {(includeDuplicates ? "" : "AND duplicate_of IS NULL")} ORDER BY id LIMIT 2000";
                c.Parameters.AddWithValue("$l", last);
                using var r = c.ExecuteReader();
                while (r.Read()) batch.Add(Read(r));
            }
            if (batch.Count == 0) yield break;
            foreach (var f in batch) { last = f.Id; yield return f; }
        }
    }

    public List<long> QueryIds(FileQuery q)
    {
        var (w, a) = Where(q);
        Commit();
        var ids = new List<long>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT id FROM files WHERE {w}";
            foreach (var (n, v) in a) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = c.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        return ids;
    }

    public FoundFile? GetFile(long id)
    {
        Commit();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT * FROM files WHERE id=$i";
            c.Parameters.AddWithValue("$i", id);
            using var r = c.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public List<FoundFile> GetFiles(IEnumerable<long> ids) => ids.Select(GetFile).Where(f => f != null).Select(f => f!).ToList();

    public long SumSize(IReadOnlyCollection<long> ids)
    {
        long total = 0;
        foreach (var chunk in ids.Chunk(500))
            total += Scalar<long>($"SELECT IFNULL(SUM(size),0) FROM files WHERE id IN ({string.Join(",", chunk)})");
        return total;
    }

    public Dictionary<string, long> CountsBy(string column, ResultView view = ResultView.All)
    {
        Commit();
        var d = new Dictionary<string, long>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT {column}, COUNT(*) FROM files WHERE {ViewClause(view)} GROUP BY {column}";
            using var r = c.ExecuteReader();
            while (r.Read()) d[r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0))!] = r.GetInt64(1);
        }
        return d;
    }

    public long CountView(ResultView v) { Commit(); return Scalar<long>($"SELECT COUNT(*) FROM files WHERE {ViewClause(v)}"); }

    private static DateTime? FromTicks(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : new DateTime(r.GetInt64(i), DateTimeKind.Utc);

    private static FoundFile Read(SqliteDataReader r)
    {
        int O(string n) => r.GetOrdinal(n);
        return new FoundFile
        {
            Id = r.GetInt64(O("id")),
            Name = r.GetString(O("name")),
            Path = r.IsDBNull(O("path")) ? null : r.GetString(O("path")),
            Extension = r.IsDBNull(O("ext")) ? "" : r.GetString(O("ext")),
            Category = (FileCategory)r.GetInt32(O("category")),
            Size = r.GetInt64(O("size")),
            Created = FromTicks(r, O("created")),
            Modified = FromTicks(r, O("modified")),
            Accessed = FromTicks(r, O("accessed")),
            Engines = (EngineId)r.GetInt32(O("engines")),
            SourceDetail = r.IsDBNull(O("source_detail")) ? "" : r.GetString(O("source_detail")),
            VolumeLabel = r.IsDBNull(O("volume_label")) ? null : r.GetString(O("volume_label")),
            VolumeOffset = r.IsDBNull(O("volume_offset")) ? -1 : r.GetInt64(O("volume_offset")),
            IsDeleted = r.IsDBNull(O("is_deleted")) ? null : r.GetInt32(O("is_deleted")) == 1,
            State = (RecoveryState)r.GetInt32(O("state")),
            Confidence = (Confidence)r.GetInt32(O("confidence")),
            Structure = (StructureCheck)r.GetInt32(O("structure")),
            OverwrittenFraction = r.IsDBNull(O("overwritten")) ? null : r.GetDouble(O("overwritten")),
            Layout = DataLayout.FromJson(r.GetString(O("layout"))),
            FormatId = r.IsDBNull(O("format_id")) ? null : r.GetString(O("format_id")),
            Notes = r.IsDBNull(O("notes")) ? "" : r.GetString(O("notes")),
            DuplicateOf = r.IsDBNull(O("duplicate_of")) ? null : r.GetInt64(O("duplicate_of")),
            ContentHash = r.IsDBNull(O("content_hash")) ? null : r.GetString(O("content_hash")),
            ScanUnit = r.IsDBNull(O("scan_unit")) ? 0 : r.GetInt32(O("scan_unit")),
        };
    }

    // ---------------------------------------------------------------- updates
    public void UpdateState(long id, RecoveryState state, StructureCheck? structure = null, string? appendNote = null)
    {
        Exec(@"UPDATE files SET state=$s, structure=IFNULL($st, structure),
 notes = CASE WHEN $n IS NULL THEN notes WHEN notes IS NULL OR notes='' THEN $n ELSE notes || '; ' || $n END WHERE id=$i",
            ("$s", (int)state), ("$st", structure is null ? null : (int)structure.Value), ("$n", appendNote), ("$i", id));
    }

    public void UpdateConfidence(long id, Confidence c) => Exec("UPDATE files SET confidence=$c WHERE id=$i", ("$c", (int)c), ("$i", id));
    public void SetContentHash(long id, string hash) => Exec("UPDATE files SET content_hash=$h WHERE id=$i", ("$h", hash), ("$i", id));
    public void MarkDuplicate(long id, long of) => Exec("UPDATE files SET duplicate_of=$o WHERE id=$i", ("$o", of), ("$i", id));
    public void AddEngine(long id, EngineId e, StructureCheck? upgrade)
        => Exec("UPDATE files SET engines = engines | $e, structure = CASE WHEN $u IS NOT NULL AND (structure IN (0,2,3)) THEN $u ELSE structure END WHERE id=$i",
            ("$e", (int)e), ("$u", upgrade is null ? null : (int)upgrade.Value), ("$i", id));

    /// <summary>
    /// Correlation: a carved candidate starting exactly where a metadata-based candidate's data starts is the
    /// same file. The carved row is kept (as a duplicate, never deleted) and the metadata row gains the
    /// carving engine and its structural verdict when that is stronger.
    /// Returns the number of merged rows.
    /// </summary>
    public int CorrelateCarvedWithMetadata()
    {
        Commit();
        var pairs = new List<(long carved, long meta, int structure, long csize, long msize)>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = @"SELECT c.id, m.id, c.structure, c.size, m.size FROM files c JOIN files m
 ON m.first_offset = c.first_offset AND m.id != c.id
 WHERE c.engines = 16 AND c.duplicate_of IS NULL AND (m.engines & 16) = 0 AND m.duplicate_of IS NULL AND c.first_offset >= 0";
            using var r = c.ExecuteReader();
            while (r.Read()) pairs.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetInt64(3), r.GetInt64(4)));
        }
        var seen = new HashSet<long>();
        foreach (var p in pairs)
        {
            if (!seen.Add(p.carved)) continue;
            MarkDuplicate(p.carved, p.meta);
            var upgrade = p.csize == p.msize && p.structure == (int)StructureCheck.Verified ? StructureCheck.Verified : (StructureCheck?)null;
            AddEngine(p.meta, EngineId.SignatureCarving, upgrade);
        }
        return seen.Count;
    }

    /// <summary>Groups candidates with identical content (by SHA-256 hashes already computed) as duplicates.</summary>
    public int MarkContentDuplicates()
    {
        Commit();
        var rows = new List<(long id, string hash, int conf)>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, content_hash, confidence FROM files WHERE content_hash IS NOT NULL AND duplicate_of IS NULL ORDER BY content_hash, confidence DESC, id";
            using var r = c.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        }
        int n = 0;
        foreach (var g in rows.GroupBy(x => x.hash))
        {
            var keep = g.First();
            foreach (var d in g.Skip(1)) { MarkDuplicate(d.id, keep.id); n++; }
        }
        return n;
    }

    /// <summary>Deleted, metadata-found candidates whose content has not been checked yet, in disk order.</summary>
    public List<FoundFile> UncheckedDeletedCandidates()
    {
        Commit();
        var list = new List<FoundFile>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT * FROM files WHERE structure = 0 AND state IN (0,1) AND (is_deleted = 1 OR is_deleted IS NULL) AND (engines & 16) = 0 AND size > 0 AND first_offset >= 0 AND duplicate_of IS NULL ORDER BY first_offset";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
        }
        return list;
    }

    public List<DiskRegion> GetRegions()
    {
        Commit();
        var list = new List<DiskRegion>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT offset, length, kind, note FROM regions ORDER BY offset";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new DiskRegion(r.GetInt64(0), r.GetInt64(1), (RegionKind)r.GetInt32(2), r.IsDBNull(3) ? "" : r.GetString(3)));
        }
        return MergeRegions(list).OrderBy(r => r.Offset).ToList();
    }

    public List<(DateTime time, ScanIssue issue)> GetIssues()
    {
        Commit();
        var list = new List<(DateTime, ScanIssue)>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT time, severity, engine, message, offset FROM issues ORDER BY id";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add((new DateTime(r.GetInt64(0), DateTimeKind.Utc), new ScanIssue((IssueSeverity)r.GetInt32(1), (EngineId)r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4))));
        }
        return list;
    }

    // ---------------------------------------------------------------- restore history
    public long BeginRestoreRun(string destination)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO restore_runs(started, destination) VALUES($s,$d); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("$s", DateTime.UtcNow.Ticks);
            c.Parameters.AddWithValue("$d", destination);
            return (long)c.ExecuteScalar()!;
        }
    }

    public void AddRestoreItem(RestoreItemRecord i) => Exec("INSERT INTO restore_items(run_id,file_id,outcome,target,bytes,sha256,message) VALUES($r,$f,$o,$t,$b,$h,$m)",
        ("$r", i.RunId), ("$f", i.FileId), ("$o", i.Outcome), ("$t", i.TargetPath), ("$b", i.Bytes), ("$h", i.Sha256), ("$m", i.Message));

    public void FinishRestoreRun(long id, string summaryJson) => Exec("UPDATE restore_runs SET finished=$f, summary=$s WHERE id=$i", ("$f", DateTime.UtcNow.Ticks), ("$s", summaryJson), ("$i", id));

    public List<(long id, DateTime started, DateTime? finished, string destination, string? summary)> GetRestoreRuns()
    {
        var list = new List<(long, DateTime, DateTime?, string, string?)>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT id, started, finished, destination, summary FROM restore_runs ORDER BY id DESC";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc), r.IsDBNull(2) ? null : new DateTime(r.GetInt64(2), DateTimeKind.Utc), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }
        return list;
    }

    public List<RestoreItemRecord> GetRestoreItems(long runId)
    {
        var list = new List<RestoreItemRecord>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT run_id, file_id, outcome, target, bytes, sha256, message FROM restore_items WHERE run_id=$r";
            c.Parameters.AddWithValue("$r", runId);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new RestoreItemRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return list;
    }

    public void Dispose()
    {
        try { Commit(); } catch (SqliteException) { }
        _db.Dispose();
    }
}

/// <summary>Serializable partition summary persisted with a project.</summary>
public sealed class PartitionRecord
{
    public int Index { get; set; }
    public string Scheme { get; set; } = "";
    public string Status { get; set; } = "";
    public long Offset { get; set; }
    public long Length { get; set; }
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string FileSystem { get; set; } = "";
    public int ClusterSize { get; set; }
    public string? Label { get; set; }
    public string? Evidence { get; set; }

    public static PartitionRecord From(PartitionInfo p) => new()
    {
        Index = p.Index, Scheme = p.Scheme.ToString(), Status = p.Status.ToString(), Offset = p.Offset, Length = p.Length,
        Type = p.TypeDescription, Name = p.Name, FileSystem = FileSystemProbe.Describe(p.FileSystem), ClusterSize = p.Boot?.ClusterSize ?? 0,
        Label = p.Boot?.Label, Evidence = p.Evidence,
    };
}
