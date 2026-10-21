"""Fail when a NuGet or npm dependency is not under a permissive licence (dual licensing needs it).

Usage: py scripts/check_licences.py   (after `dotnet restore` in api/; npm reads the lockfile only)
"""
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

PERMISSIVE = {"MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD",
              "CC0-1.0", "Unlicense", "BlueOak-1.0.0", "PostgreSQL", "Python-2.0", "CC-BY-4.0",
              "blessing"}
FONT = {"OFL-1.1"}
TEST_ONLY = {"MS-PL"}

# Packages whose nuspec names a URL or bundled file instead of an SPDX expression.
KNOWN = {"SQLite": "blessing",
         "System.Security.Cryptography.ProtectedData": "MIT",
         "xunit.abstractions": "Apache-2.0"}


def allowed(expression: str, extra: set[str]) -> bool:
    ok = PERMISSIVE | extra
    return any(all(term.strip("() ") in ok for term in alt.split(" AND "))
               for alt in expression.split(" OR "))


def npm() -> list[str]:
    lock = json.loads((ROOT / "app/package-lock.json").read_text(encoding="utf-8"))
    bad = []
    for path, pkg in lock["packages"].items():
        if not path:
            continue
        name = path.rsplit("node_modules/", 1)[-1]
        lic = pkg.get("license", "UNKNOWN")
        if not allowed(lic, FONT if name.startswith("@fontsource") else set()):
            bad.append(f"npm {name}@{pkg.get('version')}: {lic}")
    return bad


def nuget() -> list[str]:
    out = subprocess.run(["dotnet", "list", "package", "--include-transitive", "--format", "json"],
                         cwd=ROOT / "api", check=True, capture_output=True, text=True).stdout
    cache = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget/packages")
    bad = []
    for project in json.loads(out)["projects"]:
        tests = ".Tests" in Path(project["path"]).stem
        for framework in project.get("frameworks", []):
            for pkg in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                pid, ver = pkg["id"], pkg["resolvedVersion"]
                nuspec = cache / pid.lower() / ver.lower() / f"{pid.lower()}.nuspec"
                text = nuspec.read_text(encoding="utf-8-sig") if nuspec.exists() else ""
                m = re.search(r'<license type="expression">([^<]+)</license>', text)
                lic = m.group(1) if m else KNOWN.get(pid, "UNKNOWN")
                if not allowed(lic, TEST_ONLY if tests else set()):
                    bad.append(f"nuget {pid} {ver} ({Path(project['path']).stem}): {lic}")
    return sorted(set(bad))


def main() -> int:
    bad = npm() + nuget()
    if bad:
        print(f"{len(bad)} dependencies without an allowed licence:")
        print("\n".join(bad))
        return 1
    print("dependency licences: all allowed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
