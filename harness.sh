#!/usr/bin/env bash
# Single entry point for verification. Usage: ./harness.sh fast|full
set -euo pipefail
export UNPAKZ_REQUIRE_ORACLE=1
cd "$(dirname "$0")"
mode="${1:-fast}"
case "$mode" in
  fast)
    dotnet build --nologo -v q -warnaserror
    dotnet test --nologo --no-build -v q
    ;;
  full)
    "$0" fast
    echo "full: conformance/fuzz-smoke/mutation stages not implemented yet (M1+)"
    ;;
  *) echo "usage: $0 fast|full" >&2; exit 2 ;;
esac
