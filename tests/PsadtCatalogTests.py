"""Shipped PSADT function catalog, its LGPL notice, and the refresh generator.

Stdlib only. Runs the generator on tests/fixtures/psadt-catalog/{release,dev} and checks its output.
  python3 tests/PsadtCatalogTests.py
Prints one line per check and exits non-zero when a check fails.
"""

import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "Packman" / "MonacoEditor" / "psadt-functions.json"
NOTICE = ROOT / "Packman" / "MonacoEditor" / "psadt-functions-NOTICE.txt"
GENERATOR = ROOT / "Packman" / "Tools" / "extract-psadt-functions.py"
FIXTURES = ROOT / "tests" / "fixtures" / "psadt-catalog"
DOCS_PREFIX = "https://psappdeploytoolkit.com/docs/reference/functions/"

TOP_KEYS = {"source", "release", "dev", "count", "functions"}
FUNCTION_KEYS = {"name", "verb", "synopsis", "description", "parameters", "examples", "outputs", "docs_url", "in_release", "in_dev"}
PARAMETER_KEYS = {"name", "type", "mandatory", "parameter_sets", "description"}

# Pinned by the 4.1.8 and 4.2 dev sources; a refresh that changes these should update the test on purpose.
RELEASE_COUNT = 134
DEV_COUNT = 141
TOTAL_COUNT = 143


def check_catalog_shape(catalog, release, dev):
    assert set(catalog) == TOP_KEYS, f"top-level keys {sorted(catalog)}"
    assert catalog["release"] == release, f"release {catalog['release']!r}"
    assert catalog["dev"] == dev, f"dev {catalog['dev']!r}"
    assert catalog["count"] == len(catalog["functions"]), f"count {catalog['count']}, {len(catalog['functions'])} functions"
    names = [f["name"] for f in catalog["functions"]]
    assert len(set(names)) == len(names), "duplicate function names"
    assert names == sorted(names), "functions are not sorted by name"
    for f in catalog["functions"]:
        assert set(f) == FUNCTION_KEYS, f"{f.get('name')} keys {sorted(f)}"
        assert f["verb"] == f["name"].split("-")[0], f"{f['name']} verb {f['verb']!r}"
        assert f["synopsis"].strip(), f"{f['name']} has no synopsis"
        assert f["docs_url"] == DOCS_PREFIX + f["name"], f"{f['name']} docs_url {f['docs_url']!r}"
        assert isinstance(f["in_release"], bool) and isinstance(f["in_dev"], bool), f"{f['name']} version flags"
        assert f["in_release"] or f["in_dev"], f"{f['name']} is in neither version"
        assert isinstance(f["examples"], list) and isinstance(f["outputs"], str), f"{f['name']} examples/outputs"
        for p in f["parameters"]:
            assert set(p) == PARAMETER_KEYS, f"{f['name']} parameter keys {sorted(p)}"
            assert isinstance(p["name"], str) and p["name"], f"{f['name']} parameter without a name"
            assert isinstance(p["type"], str), f"{f['name']}.{p['name']} type"
            assert isinstance(p["mandatory"], bool), f"{f['name']}.{p['name']} mandatory"
            assert isinstance(p["parameter_sets"], list), f"{f['name']}.{p['name']} parameter_sets"


def find(catalog, name):
    return next(f for f in catalog["functions"] if f["name"] == name)


def check_shipped_shape():
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    check_catalog_shape(catalog, "4.1.8", "4.2")
    assert catalog["count"] == TOTAL_COUNT, f"count {catalog['count']}"


def check_shipped_version_flags():
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    assert sum(f["in_release"] for f in catalog["functions"]) == RELEASE_COUNT
    assert sum(f["in_dev"] for f in catalog["functions"]) == DEV_COUNT
    assert find(catalog, "Show-ADTNotifyIcon")["in_release"] is False and find(catalog, "Show-ADTNotifyIcon")["in_dev"] is True
    assert find(catalog, "Add-ADTFont")["in_release"] is False and find(catalog, "Add-ADTFont")["in_dev"] is True
    assert find(catalog, "Get-ADTEnvironment")["in_release"] is True and find(catalog, "Get-ADTEnvironment")["in_dev"] is False
    assert find(catalog, "Get-ADTUniversalDate")["in_release"] is True and find(catalog, "Get-ADTUniversalDate")["in_dev"] is False


def check_shipped_mandatory_text():
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    edge = find(catalog, "Add-ADTEdgeExtension")
    assert [p["name"] for p in edge["parameters"] if p["mandatory"]] == ["ExtensionID", "UpdateUrl", "InstallationMode"]
    assert edge["parameters"][0]["type"] == "System.String"
    assert all(p["description"].strip() for p in edge["parameters"]), "Add-ADTEdgeExtension parameter without description"


def check_notice():
    text = NOTICE.read_text(encoding="utf-8")
    assert "PSAppDeployToolkit" in text, "notice does not name PSAppDeployToolkit"
    assert "LGPL-3.0" in text, "notice does not name LGPL-3.0"
    assert "github.com/PSAppDeployToolkit/PSAppDeployToolkit" in text, "notice does not link the source"


def run_generator(out_file):
    result = subprocess.run(
        [sys.executable, str(GENERATOR),
         "--release", str(FIXTURES / "release"), "--release-version", "4.1.8",
         "--dev", str(FIXTURES / "dev"), "--dev-version", "4.2",
         "--out", str(out_file)],
        capture_output=True, text=True, encoding="utf-8",
    )
    assert result.returncode == 0, f"generator exited {result.returncode}: {result.stderr.strip()}"
    lines = [line for line in result.stdout.splitlines() if line.strip()]
    assert len(lines) == 1, f"generator printed {len(lines)} lines, expected one summary line"
    return lines[0]


def generated(tmp):
    out = Path(tmp) / "out.json"
    run_generator(out)
    return out


def check_generator_summary_and_shape():
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "out.json"
        run_generator(out)
        catalog = json.loads(out.read_text(encoding="utf-8"))
    assert catalog["count"] == 6, f"generator count {catalog['count']}"
    check_catalog_shape(catalog, "4.1.8", "4.2")


def check_generator_version_flags():
    with tempfile.TemporaryDirectory() as tmp:
        catalog = json.loads(generated(tmp).read_text(encoding="utf-8"))
    flags = {f["name"]: (f["in_release"], f["in_dev"]) for f in catalog["functions"]}
    assert flags == {
        "Add-ADTEdgeExtension": (True, True),
        "Add-ADTFont": (False, True),
        "Get-ADTEnvironment": (True, False),
        "Get-ADTUniversalDate": (True, False),
        "Show-ADTInstallationWelcome": (True, True),
        "Show-ADTNotifyIcon": (False, True),
    }, flags


def check_generator_parameters():
    with tempfile.TemporaryDirectory() as tmp:
        catalog = json.loads(generated(tmp).read_text(encoding="utf-8"))
    edge = find(catalog, "Add-ADTEdgeExtension")
    assert edge["synopsis"] == "Adds an extension for Microsoft Edge using the ExtensionSettings policy."
    assert edge["docs_url"] == DOCS_PREFIX + "Add-ADTEdgeExtension"
    assert [(p["name"], p["mandatory"], p["type"]) for p in edge["parameters"]] == [
        ("ExtensionID", True, "System.String"),
        ("UpdateUrl", True, "System.String"),
        ("InstallationMode", True, "System.String"),
        ("MinimumVersionRequired", False, "System.String"),
    ]
    assert edge["parameters"][0]["description"] == "The ID of the extension to add."
    welcome = find(catalog, "Show-ADTInstallationWelcome")
    assert [(p["name"], p["mandatory"], p["type"]) for p in welcome["parameters"]] == [
        ("CloseProcesses", True, "System.String[]"),
        ("Silent", False, "System.Management.Automation.SwitchParameter"),
    ]
    assert find(catalog, "Get-ADTEnvironment")["parameters"] == []


def check_generator_refresh_is_stable():
    with tempfile.TemporaryDirectory() as tmp:
        first = generated(tmp)
        first_bytes = first.read_bytes()
        second = Path(tmp) / "second.json"
        run_generator(second)
        assert second.read_bytes() == first_bytes, "two runs over the same checkouts differ"
        assert first_bytes.endswith(b"\n"), "output has no trailing newline"
        assert b"in_4_1_8" not in first_bytes and b"in_4_2_dev" not in first_bytes, "output still uses the inbox flag names"


CHECKS = [
    ("Shipped catalog has the pinned schema, versions and counts", check_shipped_shape),
    ("Shipped catalog version flags match 4.1.8 and 4.2 dev", check_shipped_version_flags),
    ("Shipped Add-ADTEdgeExtension has its mandatory parameters and types", check_shipped_mandatory_text),
    ("psadt-functions-NOTICE.txt beside the catalog names PSAppDeployToolkit and LGPL-3.0", check_notice),
    ("Generator prints one summary line and exits 0 on the fixtures", check_generator_summary_and_shape),
    ("Generator flags 4.2-only, removed-in-4.2 and shared functions", check_generator_version_flags),
    ("Generator keeps mandatory flags, types, synopsis and docs link", check_generator_parameters),
    ("Generator output is byte-stable across runs, with the renamed flags", check_generator_refresh_is_stable),
]


def main():
    failures = []
    for name, check in CHECKS:
        try:
            check()
            print(f"PASS  {name}")
        except (AssertionError, OSError, json.JSONDecodeError, StopIteration) as ex:
            failures.append(f"{name}: {ex}")
            print(f"FAIL  {name}: {ex}")
    if failures:
        print(f"{len(failures)} of {len(CHECKS)} checks failed")
        return 1
    print(f"All {len(CHECKS)} checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
