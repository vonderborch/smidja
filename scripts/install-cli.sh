#!/usr/bin/env bash
# Packs Afl.Cli and installs (or reinstalls) it as the global `afl` command.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"

dotnet pack "$repo/tool/src/Afl.Cli" -c Release -v q
# The version doesn't change between local builds, so drop NuGet's cached copy to pick up changes.
rm -rf "${NUGET_PACKAGES:-$HOME/.nuget/packages}/afl.cli"
dotnet tool uninstall -g Afl.Cli >/dev/null 2>&1 || true
dotnet tool install -g Afl.Cli --add-source "$repo/tool/nupkg"
echo "Installed. Make sure ~/.dotnet/tools is on your PATH, then run: afl --help"
