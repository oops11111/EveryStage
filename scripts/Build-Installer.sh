#!/usr/bin/env bash
# Builds the EveryStage Windows installer (NSIS) on Linux or macOS.
# Requires: .NET 8 SDK (dotnet), NSIS 3 (makensis; Debian/Ubuntu: apt-get install nsis).
# Usage: scripts/Build-Installer.sh   -> artifacts/installer/EveryStage-Setup-<version>-win-x64.exe
# The version is read from Directory.Build.props (the single version source).
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's:.*<EveryStageVersion>\(.*\)</EveryStageVersion>.*:\1:p' "$repo/Directory.Build.props")"
test -n "$version" || { echo "Directory.Build.props has no EveryStageVersion" >&2; exit 1; }
commit="$(git -C "$repo" rev-parse HEAD 2>/dev/null || echo dev)"
build="${commit:0:7}"
out="$repo/artifacts/installer"
publish="$out/publish"
rm -rf "$publish" && mkdir -p "$publish"

# Self-contained: the installer carries the .NET runtime, so target machines need no separate .NET 8.
for component in Terminal Caster; do
  dotnet publish "$repo/src/$component/EveryStage.$component/EveryStage.$component.csproj" \
    -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:SourceRevisionId="$commit" --nologo -o "$publish/$component"
done
test -f "$publish/Terminal/x64/pdfium.dll" || { echo "Terminal publish is missing x64/pdfium.dll" >&2; exit 1; }

setup="$out/EveryStage-Setup-$version-win-x64.exe"
makensis -V2 -DPUBLISH_DIR="$publish" -DVERSION="$version" -DBUILD="$build" -DOUTFILE="$setup" -DUNSIGNED=1 \
  "$repo/deployment/installer/EveryStage.nsi"
echo "Installer: $setup"
