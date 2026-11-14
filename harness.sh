#!/usr/bin/env bash
# Single entry point for verification on Linux/WSL. Usage: ./harness.sh fast|native|full
set -euo pipefail
cd "$(dirname "$0")"
mode="${1:-fast}"
case "$mode" in
  fast)
    # native library (optimised, no sanitizers) + its tests, then the managed layers over it
    UNPAKZ_NATIVE_BUILD=release scripts/native-test.sh
    dotnet build Unpakz.slnx -c Release --nologo -v q -warnaserror
    dotnet test Unpakz.slnx -c Release --nologo --no-build -v q
    ;;
  native)
    scripts/native-test.sh             # Debug + AddressSanitizer + UBSan
    ;;
  full)
    "$0" fast
    "$0" native
    python3 harness/conformance.py --corpus   # differential vs unrar / 7-Zip / tar; needs tools/rar and corpus/external (see HARNESS.md)
    UNPAKZ_MUTATE_MODE=native python3 harness/mutate.py
    ;;
  *) echo "usage: $0 fast|native|full" >&2; exit 2 ;;
esac
