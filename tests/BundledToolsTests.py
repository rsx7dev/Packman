"""The PSADT v4 template that ships with Packman, and IntuneWinAppUtil.exe, which must not.

Stdlib only.
  python3 tests/BundledToolsTests.py
Prints one line per check and exits non-zero when a check fails.
"""

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACKMAN = ROOT / "Packman"
TEMPLATE = PACKMAN / "PSADT" / "Application"
TOOL_SOURCE = PACKMAN / "Services" / "ContentPrepTool.cs"

# Microsoft Win32 Content Prep Tool v1.8.7; a refresh that changes it should update the test on purpose.
TOOL_SHA256 = "c1ba45b5cb939e84af064bb7ff4b38fb3dfe33c8dc1078fd9b157672eae671f6"
TOOL_URL = "https://raw.githubusercontent.com/microsoft/Microsoft-Win32-Content-Prep-Tool/v1.8.7/IntuneWinAppUtil.exe"


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


def check_tool_download_is_pinned():
    source = TOOL_SOURCE.read_text(encoding="utf-8-sig")
    assert f'Sha256 = "{TOOL_SHA256}"' in source, "ContentPrepTool.cs does not pin the SHA-256"
    assert f'"{TOOL_URL}"' in source, "ContentPrepTool.cs does not download the pinned release"


def check_tool_is_not_redistributed():
    # Microsoft's license terms allow using the tool, not shipping it.
    tracked = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True).stdout.decode().split("\0")
    copies = [name for name in tracked if name.lower().endswith("intunewinapputil.exe")]
    assert not copies, f"IntuneWinAppUtil.exe is in the repository: {copies}"
    project = (PACKMAN / "Packman.csproj").read_text(encoding="utf-8")
    assert "IntuneWinAppUtil" not in project, "Packman.csproj still copies IntuneWinAppUtil"


def check_project_copies_template():
    project = (PACKMAN / "Packman.csproj").read_text(encoding="utf-8")
    assert 'Include="PSADT\\**\\*"' in project, "Packman.csproj does not copy PSADT"


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
