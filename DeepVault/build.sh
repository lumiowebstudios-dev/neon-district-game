#!/usr/bin/env bash
# Cross-builds the Windows package from Linux/macOS (WPF can be compiled, not run, off Windows).
# Requirements: .NET 10 SDK. Usage: ./build.sh [--skip-tests]
set -euo pipefail
cd "$(dirname "$0")"
PKG=artifacts/DeepVault-1.0.2-win-x64
rm -rf "$PKG" "$PKG.zip"
[[ "${1:-}" == "--skip-tests" ]] || dotnet test tests/DeepVault.Tests -c Release
dotnet publish src/DeepVault.App -c Release -r win-x64 --self-contained true -o "$PKG"
dotnet publish src/DeepVault.Cli -c Release -r win-x64 --self-contained true -o "$PKG"
cp README.md LICENSE.txt THIRD_PARTY_NOTICES.txt "$PKG"/
cp -r docs "$PKG"/docs
mkdir -p "$PKG/test-images" && cp tests/fixtures/images/*.gz tests/fixtures/images/manifest.json "$PKG/test-images"/
(cd artifacts && zip -qr "DeepVault-1.0.2-win-x64.zip" "DeepVault-1.0.2-win-x64")
echo "Package: $PKG.zip"
