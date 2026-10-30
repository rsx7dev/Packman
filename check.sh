#!/usr/bin/env bash
# Portable parser/CSV and package-link checks everywhere; Windows also builds Packman and checks WPF.
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
if [ "$check_target" = tests/DeviceStatusTests.cs ]; then dotnet run --file tests/PackageLinkWpfTests.cs -c Release; fi
