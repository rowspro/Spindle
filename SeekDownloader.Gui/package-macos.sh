#!/usr/bin/env bash
#
# Bouwt een dubbelklikbare macOS .app-bundle voor SeekDownloader (self-contained,
# .NET hoeft niet geïnstalleerd te zijn op de doel-Mac).
#
# Gebruik:   ./package-macos.sh [osx-arm64|osx-x64]
# Resultaat: dist/SeekDownloader.app
#
set -euo pipefail

RID="${1:-osx-arm64}"
APP_NAME="Spindle"
EXE_NAME="Spindle"
BUNDLE_ID="com.musicmovearr.spindle"
VERSION="1.0.0"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

PUBLISH_DIR="$SCRIPT_DIR/bin/publish-$RID"
APP="$SCRIPT_DIR/dist/$APP_NAME.app"

echo ">> Publishing self-contained ($RID)…"
rm -rf "$PUBLISH_DIR"
dotnet publish -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -o "$PUBLISH_DIR"

echo ">> Assembling $APP …"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE_NAME"
cp "$SCRIPT_DIR/Assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>     <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>      <string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key>         <string>$VERSION</string>
    <key>CFBundleShortVersionString</key> <string>$VERSION</string>
    <key>CFBundleExecutable</key>      <string>$EXE_NAME</string>
    <key>CFBundleIconFile</key>        <string>AppIcon</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key> <string>6.0</string>
    <key>LSMinimumSystemVersion</key>  <string>11.0</string>
    <key>NSHighResolutionCapable</key> <true/>
    <key>LSApplicationCategoryType</key> <string>public.app-category.music</string>
</dict>
</plist>
PLIST

# Developer ID-signing + notarisatie als de spullen aanwezig zijn; anders ad-hoc.
ENTITLEMENTS="$SCRIPT_DIR/Assets/Spindle.entitlements"
IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null | grep -o '"Developer ID Application:[^"]*"' | head -1 | tr -d '"')"
if [ -n "$IDENTITY" ]; then
    echo ">> Signing with: $IDENTITY (hardened runtime)…"
    # eerst alle losse Mach-O's (dylibs + helpers), daarna de bundle zelf
    find "$APP/Contents/MacOS" -type f -print0 | while IFS= read -r -d '' f; do
        codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$f" >/dev/null 2>&1 || true
    done
    codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$APP"
    if xcrun notarytool history --keychain-profile spindle-notary >/dev/null 2>&1; then
        echo ">> Notarizing (kan een paar minuten duren)…"
        NZIP="$(mktemp -d)/Spindle-notarize.zip"
        ditto -c -k --keepParent "$APP" "$NZIP"
        xcrun notarytool submit "$NZIP" --keychain-profile spindle-notary --wait
        xcrun stapler staple "$APP"
        rm -f "$NZIP"
        echo ">> Genotariseerd en gestapled — installeert zonder Gatekeeper-gedoe."
    else
        echo "   (geen notary-profiel 'spindle-notary' — getekend maar niet genotariseerd)"
    fi
elif command -v codesign >/dev/null 2>&1; then
    echo ">> Ad-hoc signing (geen Developer ID-certificaat gevonden)…"
    codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || \
        echo "   (codesign overgeslagen — niet kritiek voor lokaal gebruik)"
fi

echo ">> Klaar: $APP"
