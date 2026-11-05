"""The PSADT v4 template and IntuneWinAppUtil.exe that ship with Packman.

Stdlib only.
  python3 tests/BundledToolsTests.py
Prints one line per check and exits non-zero when a check fails.
"""

import hashlib
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACKMAN = ROOT / "Packman"
TEMPLATE = PACKMAN / "PSADT" / "Application"
TOOL_DIR = PACKMAN / "IntuneWinAppUtil"

# Microsoft Win32 Content Prep Tool v1.8.7; a refresh that changes it should update the test on purpose.
TOOL_SHA256 = "c1ba45b5cb939e84af064bb7ff4b38fb3dfe33c8dc1078fd9b157672eae671f6"


def check_template_has_runtime():
    for name in ("Invoke-AppDeployToolkit.ps1", "Invoke-AppDeployToolkit.exe",
                 "PSAppDeployToolkit/PSAppDeployToolkit.psd1", "PSAppDeployToolkit/PSAppDeployToolkit.psm1"):
        assert (TEMPLATE / name).is_file(), f"template is missing {name}"


def check_script_matches_runtime_version():
    manifest = (TEMPLATE / "PSAppDeployToolkit" / "PSAppDeployToolkit.psd1").read_text(encoding="utf-8-sig")
    runtime = re.search(r"ModuleVersion\s*=\s*'([\d.]+)'", manifest).group(1)
    script = (TEMPLATE / "Invoke-AppDeployToolkit.ps1").read_text(encoding="utf-8-sig")
    versions = set(re.findall(r"(?:ModuleVersion|DeployAppScriptVersion)\s*=\s*'([\d.]+)'", script))
    assert versions == {runtime}, f"script asks for {sorted(versions)}, the bundled runtime is {runtime}"


def check_tool_is_the_pinned_build():
    digest = hashlib.sha256((TOOL_DIR / "IntuneWinAppUtil.exe").read_bytes()).hexdigest()
    assert digest == TOOL_SHA256, f"IntuneWinAppUtil.exe SHA-256 is {digest}"
    assert TOOL_SHA256 in (TOOL_DIR / "README.txt").read_text(), "README.txt does not record the SHA-256"
    assert list(TOOL_DIR.glob("*.pdf")), "the Microsoft license terms are missing"


def check_project_copies_both():
    project = (PACKMAN / "Packman.csproj").read_text(encoding="utf-8")
    for folder in ("PSADT", "IntuneWinAppUtil"):
        assert f'Include="{folder}\\**\\*"' in project, f"Packman.csproj does not copy {folder}"


def main():
    failures = 0
    for name, check in sorted((n, f) for n, f in globals().items() if n.startswith("check_")):
        try:
            check()
            print(f"PASS  {name}")
        except Exception as ex:
            failures += 1
            print(f"FAIL  {name}: {ex}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
