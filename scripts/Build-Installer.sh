#!/usr/bin/env bash
# Builds the EveryStage Windows installer (NSIS) on Linux or macOS.
# Requires: .NET 8 SDK (dotnet), NSIS 3 (makensis; Debian/Ubuntu: apt-get install nsis).
# Usage: scripts/Build-Installer.sh [version]   -> artifacts/installer/EveryStage-Setup-<version>-win-x64.exe
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.9.0}"
build="$(git -C "$repo" rev-parse --short HEAD 2>/dev/null || echo dev)"
out="$repo/artifacts/installer"
publish="$out/publish"
rm -rf "$publish" && mkdir -p "$publish"

# Self-contained: the installer carries the .NET runtime, so target machines need no separate .NET 8.
for component in Terminal Caster; do
  dotnet publish "$repo/src/$component/EveryStage.$component/EveryStage.$component.csproj" \
    -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true --nologo -o "$publish/$component"
done
test -f "$publish/Terminal/x64/pdfium.dll" || { echo "Terminal publish is missing x64/pdfium.dll" >&2; exit 1; }

setup="$out/EveryStage-Setup-$version-win-x64.exe"
makensis -V2 -DPUBLISH_DIR="$publish" -DVERSION="$version" -DBUILD="$build" -DOUTFILE="$setup" \
  "$repo/deployment/installer/EveryStage.nsi"
echo "Installer: $setup"
