#!/usr/bin/env python3
"""Inventory the offline NuGet feed and retain license files without executing packages.

Packages whose terms do not allow this repository to redistribute them are not in packages/.
They are restored from nuget.org (see NuGet.config) and recorded by hand in
third-party/nuget-external.json, with their notice files kept in third-party/nuget-notices.
"""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = Path("third-party/nuget-packages.json")
NOTICE_ROOT = Path("third-party/nuget-notices")
EXTERNAL = Path("third-party/nuget-external.json")


def collect():
    packages = []
    files = {}
    for archive in sorted((ROOT / "packages").glob("*.nupkg")):
        with zipfile.ZipFile(archive) as package:
            spec = next(name for name in package.namelist() if name.endswith(".nuspec"))
            elements = {e.tag.rsplit("}", 1)[-1]: e for e in ET.fromstring(package.read(spec)).iter()}
            identity = elements["id"].text
            version = elements["version"].text
            license_element = elements.get("license")
            if license_element is None:
                license_element = elements.get("licenseUrl")
            project = elements.get("projectUrl")
            entry = {
                "id": identity,
                "version": version,
                "archive": archive.relative_to(ROOT).as_posix(),
                "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                "licenseType": license_element.get("type", "url") if license_element is not None else "unspecified",
                "license": license_element.text if license_element is not None else None,
                "projectUrl": project.text if project is not None else None,
                "notices": [],
            }
            # Keep the original text, filename and archive hierarchy, including nested notices.
            for name in sorted(package.namelist()):
                source = PurePosixPath(name)
                if name.endswith("/") or not any(word in source.name.lower() for word in ("license", "notice")):
                    continue
                if source.is_absolute() or ".." in source.parts:
                    raise ValueError(f"Unsafe notice path in {archive.name}: {name}")
                folder = f"{identity}-{version}"
                if "/" in folder or "\\" in folder or folder in (".", ".."):
                    raise ValueError(f"Unsafe package identity in {archive.name}")
                destination = NOTICE_ROOT / folder / Path(*source.parts)
                files[destination] = package.read(name)
                entry["notices"].append(destination.as_posix())
            packages.append(entry)
    vendored = {(entry["id"].lower(), entry["version"]) for entry in packages}
    for entry in json.loads((ROOT / EXTERNAL).read_text(encoding="utf-8"))["packages"]:
        if (entry["id"].lower(), entry["version"]) in vendored:
            raise ValueError(f"{entry['id']} {entry['version']} is listed as external but is in packages/")
        for notice in entry["notices"]:
            if not (ROOT / notice).is_file():
                raise ValueError(f"Missing notice for external package {entry['id']}: {notice}")
        packages.append(entry)
    # The order of the archive names, which is what the vendored entries already follow.
    packages.sort(key=lambda entry: f"{entry['id']}.{entry['version']}".lower())
    files[MANIFEST] = (json.dumps({"schemaVersion": 1, "packages": packages}, indent=2) + "\n").encode()
    return packages, files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Verify tracked output without modifying files")
    args = parser.parse_args()
    packages, files = collect()
    if not packages:
        raise ValueError("The offline packages directory is empty")
    changed = [str(path) for path, data in files.items() if not (ROOT / path).is_file() or (ROOT / path).read_bytes() != data]
    if args.check:
        if changed:
            print("Third-party inventory needs regeneration:\n" + "\n".join(changed), file=sys.stderr)
            return 1
    else:
        for path, data in files.items():
            target = ROOT / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    print(f"Verified {len(packages)} NuGet packages and {len(files) - 1} bundled notice files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
