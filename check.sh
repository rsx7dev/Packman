#!/usr/bin/env bash
# Offline restore, Release build and the device status checks. Needs the .NET 10 SDK on Windows (WPF).
set -euo pipefail
cd "$(dirname "$0")"
dotnet restore Packman.sln
dotnet build Packman.sln --no-restore -c Release
dotnet run --file tests/DeviceStatusTests.cs -c Release
