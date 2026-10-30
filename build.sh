#!/usr/bin/env bash
# Cross-builds StigGpoManager.exe (.NET Framework 4.8) and runs the unit tests on Linux/macOS.
# Needs the Microsoft .NET 8 SDK (the distro "dotnet-sdk-8.0" package lacks the WindowsDesktop SDK).
set -euo pipefail
cd "$(dirname "$0")"
if [[ "${1:-}" != "--skip-tests" ]]; then
  dotnet test tests/StigGpoManager.Tests/StigGpoManager.Tests.csproj -c Release
fi
dotnet build src/App/StigGpoManager.csproj -c Release
mkdir -p dist
cp src/App/bin/Release/net48/StigGpoManager.exe config.sample.json dist/
(cd dist && sha256sum StigGpoManager.exe | tee StigGpoManager.exe.sha256)
