#!/usr/bin/env bash
# Assembles the single deliverable ZIP: complete source (git-tracked files), docs, tests/fixtures,
# build scripts, and the built Windows package (run ./build.sh first).
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=artifacts/DeepVault-1.0.0-complete
rm -rf "$OUT" "$OUT.zip"
mkdir -p "$OUT/source"
git ls-files . | while read -r f; do mkdir -p "$OUT/source/$(dirname "$f")"; cp "$f" "$OUT/source/$f"; done
cp -r artifacts/DeepVault-1.0.0-win-x64 "$OUT/DeepVault-1.0.0-win-x64"
cp README.md docs/USER_GUIDE.txt "$OUT/"
(cd artifacts && zip -qr DeepVault-1.0.0-complete.zip DeepVault-1.0.0-complete)
ls -la "$OUT.zip"
