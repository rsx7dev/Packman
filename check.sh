#!/usr/bin/env bash
# Restore from the vendored packages/ feed, build, then run the tests.
set -euo pipefail
cd "$(dirname "$0")"

dotnet restore Packman.sln
dotnet build Packman.sln --no-restore -c Release -p:EnableWindowsTargeting=true
dotnet test Packman.Tests/Packman.Tests.csproj --no-build -c Release
