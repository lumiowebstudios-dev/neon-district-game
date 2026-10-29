using System.Net;
using System.Text;
using DeepVault.Core.Scanning;
using DeepVault.Core.Util;

namespace DeepVault.Core.Restore;

/// <summary>Writes the human-readable (HTML) and machine-readable (CSV) recovery report next to the restored files.</summary>
public static class RecoveryReport
{
    public static (string html, string csv) Write(string folder, RestoreResult r, SourceIdentity src, DestinationInfo dest, IRestoreTarget target)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string html = Path.Combine(folder, $"DeepVault-Recovery-Report-{stamp}.html");
        string csv = Path.Combine(folder, $"DeepVault-Recovery-Report-{stamp}.csv");
        using (var s = target.CreateNew(csv))
        using (var w = new StreamWriter(s, new UTF8Encoding(true)))
        {
            w.WriteLine("outcome,original_path,restored_path,expected_size,written_bytes,sha256,structure,message");
            foreach (var i in r.Items)
                w.WriteLine(string.Join(",", new[] { i.Outcome.ToString(), i.OriginalPath, i.TargetPath ?? "", i.ExpectedSize.ToString(), i.WrittenBytes.ToString(), i.DestinationSha256 ?? i.SourceSha256 ?? "", i.Structure ?? "", i.Message }.Select(Csv)));
        }
        var sb = new StringBuilder();
        string E(string? x) => WebUtility.HtmlEncode(x ?? "");
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>DeepVault recovery report</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1b1f24}table{border-collapse:collapse;width:100%;font-size:13px}")
          .Append("td,th{border:1px solid #d0d7de;padding:4px 6px;text-align:left;vertical-align:top}th{background:#f3f4f6}")
          .Append(".Verified{color:#116329}.RestoredWithWarnings{color:#9a6700}.Failed{color:#cf222e}.Skipped,.NotAttempted{color:#57606a}")
          .Append(".note{background:#fff8c5;border:1px solid #d4a72c;padding:8px 12px;margin:12px 0}</style></head><body>");
        sb.Append("<h1>DeepVault recovery report</h1>");
        sb.Append($"<p><b>Started:</b> {r.Started.ToLocalTime():F}<br><b>Finished:</b> {r.Finished.ToLocalTime():F}<br>");
        sb.Append($"<b>Source:</b> {E(src.Describe())}<br><b>Destination:</b> {E(r.SessionFolder)} ({E(dest.Label)}, {E(dest.FileSystem)}, {Bytes.FormatSize(dest.FreeBytes)} free before restore)</p>");
        sb.Append("<table style=\"width:auto\"><tr><th>Outcome</th><th>Files</th></tr>");
        foreach (RestoreOutcome o in Enum.GetValues<RestoreOutcome>()) sb.Append($"<tr><td class=\"{o}\">{o}</td><td>{r.Count(o)}</td></tr>");
        sb.Append("</table>");
        if (r.StoppedDestinationFull) sb.Append("<p class=\"note\">The destination became full. Remaining files were not attempted. Nothing on the destination was deleted.</p>");
        if (r.Cancelled) sb.Append("<p class=\"note\">The restore was cancelled before all files were processed.</p>");
        sb.Append("<p class=\"note\"><b>What “Verified” means:</b> the restored file was read back from the destination and its SHA-256 matches the bytes read from the source, and its format structure was checked where possible. ")
          .Append("This proves the copy is faithful to what is on the source disk now; it cannot prove that the data on the source was not already partially overwritten. Open important files to confirm them.</p>");
        sb.Append("<table><tr><th>Outcome</th><th>Original</th><th>Restored as</th><th>Size</th><th>SHA-256</th><th>Structure</th><th>Notes</th></tr>");
        foreach (var i in r.Items)
            sb.Append($"<tr><td class=\"{i.Outcome}\">{i.Outcome}</td><td>{E(i.OriginalPath)}</td><td>{E(i.TargetPath)}</td><td>{Bytes.FormatSize(i.WrittenBytes > 0 ? i.WrittenBytes : i.ExpectedSize)}</td><td><code>{E(i.DestinationSha256 ?? i.SourceSha256)}</code></td><td>{E(i.Structure)}</td><td>{E(i.Message)}</td></tr>");
        sb.Append("</table></body></html>");
        using (var s = target.CreateNew(html))
        using (var w = new StreamWriter(s, new UTF8Encoding(false)))
            w.Write(sb.ToString());
        return (html, csv);
    }

    private static string Csv(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
