#!/usr/bin/env bash
# Offline restore, Release build and the device status checks. Packman is a WPF app
# (net10.0-windows), so this runs only on Windows with the .NET 10 SDK; anywhere else it fails.
set -euo pipefail
cd "$(dirname "$0")"
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) ;;
    *) echo "check.sh: needs Windows with the .NET 10 SDK (WPF cannot build or run on $(uname -s)). Run it on the Windows node." >&2; exit 1 ;;
esac
command -v dotnet >/dev/null || { echo "check.sh: dotnet not found; install the .NET 10 SDK." >&2; exit 1; }
dotnet restore Packman.sln
dotnet build Packman.sln --no-restore -c Release
dotnet run --file tests/DeviceStatusTests.cs -c Release
