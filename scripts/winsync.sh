#!/usr/bin/env bash
# Copies the source tree (no binaries, no tools/, no .git) to a Windows folder the user authorised (Downloads) and runs a
# command there through powershell.exe. Usage: scripts/winsync.sh "<powershell command run inside the copy>"
set -euo pipefail
WIN_USER_DIR="${UNPAKZ_WIN_DIR:-/mnt/c/Users/bobier jean-francois/Downloads/unpakz-build}"
cd "$(dirname "$0")/.."
mkdir -p "$WIN_USER_DIR"
rsync -a --delete --exclude '.git/' --exclude 'bin/' --exclude 'obj/' --exclude 'bin-win/' --exclude 'obj-win/' \
      --exclude '/tools/' --exclude 'artifacts/' --exclude 'native/build*/' --exclude '__pycache__/' ./ "$WIN_USER_DIR/"
WIN_PATH=$(wslpath -w "$WIN_USER_DIR")
powershell.exe -NoProfile -Command "Set-Location -LiteralPath '$WIN_PATH'; $1"
