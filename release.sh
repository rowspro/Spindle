#!/usr/bin/env bash
#
# Eén commando voor een nieuwe Spindle-release:
#   ./release.sh 1.1.0
# Bouwt + notariseert beide macOS-apps, bouwt de Windows-zip, maakt de
# GitHub Release en bumpt de Homebrew-cask. Brew-gebruikers krijgen de
# update daarna via `brew upgrade`.
set -euo pipefail
V="${1:?gebruik: ./release.sh <versie>}"
cd "$(dirname "$0")"

export SPINDLE_VERSION="$V"
( cd Spindle && ./package-macos.sh osx-arm64 )
rm -f Spindle-macOS-AppleSilicon.zip
ditto -c -k --keepParent Spindle/dist/Spindle.app Spindle-macOS-AppleSilicon.zip
( cd Spindle && ./package-macos.sh osx-x64 )
rm -f Spindle-macOS-Intel.zip
ditto -c -k --keepParent Spindle/dist/Spindle.app Spindle-macOS-Intel.zip
rm -rf Spindle/dist
( cd Spindle && dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o bin/publish-win-x64 )
rm -f Spindle-windows-x64.zip
ditto -c -k --keepParent Spindle/bin/publish-win-x64 Spindle-windows-x64.zip

ARM_SHA="$(shasum -a 256 Spindle-macOS-AppleSilicon.zip | cut -d' ' -f1)"
INTEL_SHA="$(shasum -a 256 Spindle-macOS-Intel.zip | cut -d' ' -f1)"

gh release create "v$V" --repo rowspro/Spindle --target "$(git branch --show-current)" \
  --title "Spindle $V" --generate-notes \
  Spindle-macOS-AppleSilicon.zip Spindle-macOS-Intel.zip Spindle-windows-x64.zip

TAP="$(mktemp -d)"
gh repo clone rowspro/homebrew-spindle "$TAP" -- -q
sed -i '' \
  -e "s|version \".*\"|version \"$V\"|" \
  -e "s|sha256 arm:   \".*\",|sha256 arm:   \"$ARM_SHA\",|" \
  -e "s|intel: \".*\"|intel: \"$INTEL_SHA\"|" \
  "$TAP/Casks/spindle.rb"
git -C "$TAP" commit -aqm "Spindle $V"
git -C "$TAP" push -q
rm -rf "$TAP"
echo "✓ Release v$V staat live — brew-gebruikers updaten met 'brew upgrade spindle'."
