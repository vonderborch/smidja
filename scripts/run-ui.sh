#!/usr/bin/env bash
# Opens the afl desktop UI. It is system-wide: it shows every repo in ~/.afl (or $AFL_HOME).
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
exec dotnet run --project "$repo/tool/src/Afl.UI"
