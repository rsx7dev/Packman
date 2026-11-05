#!/usr/bin/env bash
# Portable parser/CSV, package-link and documentation checks everywhere; Windows also builds Packman, checks WPF
# and runs tests/Packman.Tests. CI runs this script as is.
set -euo pipefail
cd "$(dirname "$0")"
command -v dotnet >/dev/null || { echo "check.sh: dotnet not found; install the .NET 10 SDK." >&2; exit 1; }
check_target=tests/DeviceStatusUnitTests.cs
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) check_target=tests/DeviceStatusTests.cs ;;
    *)
        # File-app caches and .NET named mutexes need writable locations in CI sandboxes.
        export XDG_DATA_HOME="${XDG_DATA_HOME:-$PWD/artifacts/check-data}"
        export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-$PWD/artifacts/check-runtime}"
        mkdir -p "$XDG_DATA_HOME" "$XDG_RUNTIME_DIR"
        ;;
esac
dotnet restore "$check_target"
dotnet build "$check_target" --no-restore -c Release
dotnet run --file "$check_target" --no-build -c Release
dotnet restore tests/PackageLinkTests.cs
dotnet build tests/PackageLinkTests.cs --no-restore -c Release
dotnet run --file tests/PackageLinkTests.cs --no-build -c Release
dotnet restore tests/SetupUnitTests.cs
dotnet build tests/SetupUnitTests.cs --no-restore -c Release
dotnet run --file tests/SetupUnitTests.cs --no-build -c Release
dotnet restore tests/InstallerInsightTests.cs
dotnet build tests/InstallerInsightTests.cs --no-restore -c Release
dotnet run --file tests/InstallerInsightTests.cs --no-build -c Release
dotnet restore tests/UploadConsoleTests.cs
dotnet build tests/UploadConsoleTests.cs --no-restore -c Release
dotnet run --file tests/UploadConsoleTests.cs --no-build -c Release
dotnet restore tests/SettingsTemplateTests.cs
dotnet build tests/SettingsTemplateTests.cs --no-restore -c Release
dotnet run --file tests/SettingsTemplateTests.cs --no-build -c Release
dotnet restore tests/ScriptEditorTests.cs
dotnet build tests/ScriptEditorTests.cs --no-restore -c Release
dotnet run --file tests/ScriptEditorTests.cs --no-build -c Release
# Windows usually has only "python", and may have a python3 Store stub that runs nothing.
python=""
for candidate in python3 python; do
    if "$candidate" -c "import sys" >/dev/null 2>&1; then python=$candidate; break; fi
done
if [ -n "$python" ]; then
    "$python" tests/PsadtCatalogTests.py
    "$python" tests/BundledToolsTests.py
    "$python" tests/DocsLinkTests.py
    if "$python" -c "import playwright" 2>/dev/null; then
        "$python" tests/MonacoEditorTests.py
    else
        echo "check.sh: tests/MonacoEditorTests.py requires Playwright (install with: pip install playwright && playwright install chromium)" >&2
        exit 1
    fi
else
    echo "check.sh: Python is required for PSADT catalog, bundled tools, documentation and Monaco tests." >&2
    exit 1
fi
if [ "$check_target" = tests/DeviceStatusTests.cs ]; then
    dotnet run --file tests/PackageLinkWpfTests.cs -c Release
    dotnet run --file tests/SetupTests.cs -c Release
    dotnet run --file tests/UploadConsoleWpfTests.cs -c Release
    dotnet run --file tests/ApplicationRelationshipViewTests.cs -c Release
    dotnet test tests/Packman.Tests/Packman.Tests.csproj -c Release
fi
