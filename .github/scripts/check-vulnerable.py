"""Summarise `dotnet list package --vulnerable --format json` and fail on a chosen severity.

`dotnet list --vulnerable` always exits 0, so a CI step that only runs it can never fail. This reads
its JSON, writes every finding to the GitHub job summary, and exits 1 if any finding is at or above
--fail-on (issue #11: critical findings carry a 7-day patch SLA, see docs/MASTER_PLAN.md §7).
"""

import argparse
import json
import os
import sys

SEVERITIES = ["Low", "Moderate", "High", "Critical"]


def findings(report):
    for project in report.get("projects", []):
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    for vuln in package.get("vulnerabilities", []):
                        yield {
                            "project": os.path.basename(project.get("path", "?")),
                            "package": package.get("id", "?"),
                            "version": package.get("resolvedVersion", "?"),
                            "severity": vuln.get("severity", "Unknown"),
                            "advisory": vuln.get("advisoryurl", ""),
                            "transitive": kind == "transitivePackages",
                        }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("report")
    parser.add_argument("--fail-on", default="Critical", choices=SEVERITIES)
    args = parser.parse_args()

    with open(args.report, encoding="utf-8") as handle:
        report = json.load(handle)

    rows = list(findings(report))
    threshold = SEVERITIES.index(args.fail_on)
    blocking = [r for r in rows if r["severity"] in SEVERITIES and SEVERITIES.index(r["severity"]) >= threshold]

    lines = ["## Dependency scan", ""]
    if not rows:
        lines.append("No vulnerable packages found.")
    else:
        lines += ["| Severity | Package | Version | Project | Transitive | Advisory |", "|---|---|---|---|---|---|"]
        for r in sorted(rows, key=lambda r: -SEVERITIES.index(r["severity"]) if r["severity"] in SEVERITIES else 1):
            lines.append(
                f"| {r['severity']} | {r['package']} | {r['version']} | {r['project']} | "
                f"{'yes' if r['transitive'] else 'no'} | {r['advisory']} |"
            )
        lines += ["", f"{len(rows)} finding(s); {len(blocking)} at or above {args.fail_on} (build fails on those)."]

    summary = "\n".join(lines) + "\n"
    print(summary)
    target = os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a", encoding="utf-8") as handle:
            handle.write(summary)

    return 1 if blocking else 0


if __name__ == "__main__":
    sys.exit(main())
