#!/usr/bin/env python3
"""
Builds docs/TEST_RESULTS.md from recorded test output only:
  --trx <file> [--trx-label <name>]...   xunit TRX result files (per environment)
  --scenarios <dir>                       scenario-reports/*.json written by the scenario tests
  --e2e <windows-e2e-result.json>         Windows real-disk end-to-end result
  --selftest <selftest-result.json> [--selftest-label <name>]...
"""
import argparse, glob, json, os, xml.etree.ElementTree as ET

ap = argparse.ArgumentParser()
ap.add_argument("--trx", action="append", default=[])
ap.add_argument("--trx-label", action="append", default=[])
ap.add_argument("--scenarios")
ap.add_argument("--e2e")
ap.add_argument("--selftest", action="append", default=[])
ap.add_argument("--selftest-label", action="append", default=[])
ap.add_argument("--out", required=True)
a = ap.parse_args()
out = ["# Test results", "",
       "Everything below was produced by actually running the tests; nothing is hand-entered. "
       "Regenerate with `tools/make_test_results.py`.", ""]
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
for path, label in zip(a.trx, a.trx_label):
    root = ET.parse(path).getroot()
    c = root.find(".//t:ResultSummary/t:Counters", ns)
    times = root.find("t:Times", ns)
    out += [f"## Automated tests — {label}", "",
            f"Run {times.get('start', '')[:19]} — total {c.get('total')}, passed {c.get('passed')}, failed {c.get('failed')}.", "",
            "| Test | Outcome | Duration |", "|---|---|---|"]
    for r in sorted(root.findall(".//t:UnitTestResult", ns), key=lambda r: r.get("testName")):
        out.append(f"| {r.get('testName').replace('DeepVault.Tests.', '')} | {r.get('outcome')} | {r.get('duration', '')[:11]} |")
    out.append("")
if a.scenarios:
    out += ["## Recovery scenarios (Test G records)", ""]
    for f in sorted(glob.glob(os.path.join(a.scenarios, "*.json"))):
        s = json.load(open(f))
        out += [f"### {s['Scenario']}", "", f"*Setup:* {s['Setup']}  ", f"*Methods:* {s.get('Methods') or 'n/a'}  ", f"*Outcome:* {s['Outcome']}", ""]
        if s.get("TrackedFiles"):
            out += ["| Tracked | Detected | Reconstructed | Verified by SHA-256 | Partial/flagged | Not recovered | Candidates in catalogue |", "|---|---|---|---|---|---|---|",
                    f"| {s['TrackedFiles']} | {s['Detected']} | {s['Reconstructed']} | {s['VerifiedByHash']} | {s['PartialOrFlagged']} | {s['NotRecovered']} | {s['TotalCandidates']} |", ""]
        for k, title in (("Details", "Details"), ("Failures", "Failures"), ("Limitations", "Limitations"), ("SkippedRegions", "Skipped/unreadable regions")):
            if s.get(k):
                out.append(f"**{title}:**")
                out += [f"- {d}" for d in s[k]]
                out.append("")
        if not s.get("Failures"):
            out += ["**Failures:** none", ""]
if a.e2e:
    e = json.load(open(a.e2e, encoding="utf-8-sig"))
    out += ["## Windows end-to-end: files deleted by Windows NTFS, recovered from \\\\.\\PhysicalDriveN", "",
            f"Result: **{'PASSED' if e['ok'] else 'FAILED'}**", "", "| File | Original folder | Byte-exact (SHA-256) |", "|---|---|---|"]
    out += [f"| {x['name']} | {x['originalFolder']} | {'yes' if x['hashMatch'] else 'NO'} |" for x in e["files"]]
    out += ["", "Steps: " + "; ".join(e["steps"]), ""]
for path, label in zip(a.selftest, a.selftest_label):
    r = json.load(open(path, encoding="utf-8-sig"))
    out += [f"## GUI self-test — {label}", "",
            f"Result: **{'PASSED' if r['ok'] else 'FAILED'}**; scan {r['scan']}; restore {r['restore']}; WPF binding errors: {len(r['bindingErrors'])}", "",
            "| Page | Note |", "|---|---|"]
    out += [f"| {s['step']} | {(s.get('note') or '').replace('|', '/')} |" for s in r["steps"]]
    out.append("")
open(a.out, "w").write("\n".join(out))
print("written", a.out)
