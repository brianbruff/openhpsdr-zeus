#!/bin/bash
# Build Zeus.app (Photino desktop shell) and a drag-to-install DMG for macOS.
# Usage: ./create-macos-desktop-app.sh <version> <arch>
# Example: ./create-macos-desktop-app.sh 0.4.1 arm64
#
# Companion to create-macos-app.sh, which packages the headless Zeus.Server
# (browser-based UI). This script packages Zeus.Desktop — the Photino-based
# in-process shell — so the operator sees a native window instead of having
# to open a browser.

set -e

VERSION="${1:-0.0.0}"
ARCH="${2:-arm64}"  # arm64 or x64

if [ "$ARCH" != "arm64" ] && [ "$ARCH" != "x64" ]; then
    echo "Error: ARCH must be 'arm64' or 'x64'"
    exit 1
fi

echo "Creating Zeus.app (desktop) for macOS ${ARCH} v${VERSION}..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="${REPO_ROOT}/Zeus.Desktop/bin/Release/net10.0/osx-${ARCH}/publish"
OUTPUT_DIR="${SCRIPT_DIR}/output"
APP_NAME="Zeus.app"
APP_BUNDLE="${OUTPUT_DIR}/${APP_NAME}"
ICON_SOURCE="${REPO_ROOT}/docs/pics/zeus.png"

# Self-contained publish so the .app runs on stock macOS without the user
# having to install the .NET 10 runtime. PublishSingleFile=false keeps each
# managed dll separate; Photino.Native.dylib + libwdsp.dylib + libfftw need
# to load from runtimes/osx-* anyway, so the single-file packaging would
# only save a few hundred KB at the cost of harder symbol resolution.
echo "Publishing Zeus.Desktop self-contained for osx-${ARCH}..."
dotnet publish "${REPO_ROOT}/Zeus.Desktop/Zeus.Desktop.csproj" \
    -c Release \
    -r "osx-${ARCH}" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:UseAppHost=true \
    -o "${PUBLISH_DIR}"

# Clean and create output directory
rm -rf "${APP_BUNDLE}"
mkdir -p "${OUTPUT_DIR}"
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

# Copy published files into Contents/MacOS — the bundle's working dir at
# launch is Contents/MacOS, so the relative wwwroot/, appsettings.json,
# zetaHat.bin etc. land where ZeusHost expects.
echo "Copying published files..."
cp -r "${PUBLISH_DIR}"/* "${APP_BUNDLE}/Contents/MacOS/"

# Generate Zeus.icns from docs/pics/zeus.png so Finder, Dock, and Cmd-Tab
# show the Zeus artwork. iconutil + sips ship with Xcode CLT (present on
# every GitHub macos-latest runner and any dev box that has built native
# code on macOS before).
if [ -f "${ICON_SOURCE}" ]; then
    echo "Generating Zeus.icns from ${ICON_SOURCE}..."
    ICONSET_DIR="${OUTPUT_DIR}/Zeus.iconset"
    rm -rf "${ICONSET_DIR}"
    mkdir -p "${ICONSET_DIR}"
    sips -z 16 16     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_16x16.png"     >/dev/null
    sips -z 32 32     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_16x16@2x.png"  >/dev/null
    sips -z 32 32     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_32x32.png"     >/dev/null
    sips -z 64 64     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_32x32@2x.png"  >/dev/null
    sips -z 128 128   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_128x128.png"   >/dev/null
    sips -z 256 256   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_128x128@2x.png">/dev/null
    sips -z 256 256   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_256x256.png"   >/dev/null
    sips -z 512 512   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_256x256@2x.png">/dev/null
    sips -z 512 512   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_512x512.png"   >/dev/null
    sips -z 1024 1024 "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_512x512@2x.png">/dev/null
    iconutil -c icns "${ICONSET_DIR}" -o "${APP_BUNDLE}/Contents/Resources/Zeus.icns"
    rm -rf "${ICONSET_DIR}"
else
    echo "Warning: ${ICON_SOURCE} not found — building Zeus.app without an icon."
fi

# Info.plist. CFBundleExecutable points at launch.sh (not Zeus.Desktop
# directly) so we can pin DYLD_LIBRARY_PATH before the .NET runtime loads
# libwdsp.dylib — same reason the service-mode bundle uses a launcher.
cat > "${APP_BUNDLE}/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>launch.sh</string>
    <key>CFBundleIconFile</key>
    <string>Zeus</string>
    <key>CFBundleIdentifier</key>
    <string>com.ei6lf.zeus.desktop</string>
    <key>CFBundleName</key>
    <string>Zeus</string>
    <key>CFBundleDisplayName</key>
    <string>Zeus</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>ZEUS</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSUIElement</key>
    <false/>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>

    <!-- Privacy descriptors — required because Zeus.Desktop is the Photino
         webview process and the SPA uses getUserMedia for TX mic uplink.
         Without these, macOS TCC SIGKILLs the app on first launch via
         LaunchServices ('open Zeus.app') even before anything is recorded:
         WKWebView probes mic+cam capabilities at page-load. Camera is
         declared defensively — the SPA does not capture video today, but
         WKWebView's enumerateDevices() touches both buckets.
         The service-mode bundle does not need these because its webview is
         the user's browser (separate TCC profile). -->
    <key>NSMicrophoneUsageDescription</key>
    <string>Zeus uses the microphone for SSB / digital-mode TX uplink to your radio when you key MOX.</string>
    <key>NSCameraUsageDescription</key>
    <string>Zeus does not record video. The OS asks because the embedded webview lists media devices.</string>
</dict>
</plist>
EOF

# Make Zeus.Desktop executable (cp -r usually preserves mode but be defensive)
chmod +x "${APP_BUNDLE}/Contents/MacOS/Zeus.Desktop"

# Launcher: pins DYLD_LIBRARY_PATH so the bundled libwdsp.dylib wins over
# any older copy in /usr/local/lib or /opt/homebrew/lib (e.g. from a
# piHPSDR / DeskHPSDR install). exec replaces the shell with Zeus.Desktop
# so Cmd-Q / Dock-Quit / Force-Quit tear down the right process — there's
# nothing else to clean up because the Photino window IS the UI and the
# backend lives in-process.
cat > "${APP_BUNDLE}/Contents/MacOS/launch.sh" << 'EOF'
#!/bin/bash
cd "$(dirname "$0")"

# Pin the bundled libwdsp.dylib. macOS dlopen does not search the
# executable's directory by default, so without this line P/Invoke can
# bind against a stale dylib that pre-dates symbols Zeus relies on (e.g.
# SetRXAEMNRpost2*). Both arches are listed so the same launcher works on
# arm64 and x64 builds; the loader silently skips a path that does not
# exist.
export DYLD_LIBRARY_PATH="$(pwd)/runtimes/osx-arm64/native:$(pwd)/runtimes/osx-x64/native:${DYLD_LIBRARY_PATH}"

exec ./Zeus.Desktop
EOF
chmod +x "${APP_BUNDLE}/Contents/MacOS/launch.sh"

echo "App bundle created at ${APP_BUNDLE}"

# --- Codesigning hook (opt-in) ------------------------------------------
#
# Ad-hoc signing or Developer ID signing happens here. Default behaviour
# is to do nothing — the bundle ships unsigned and the user clears the
# quarantine xattr on first launch (see DMG README).
#
# To produce a Developer ID Application signed bundle:
#   export APPLE_DEVELOPER_ID="Developer ID Application: Brian Keating (TEAMID)"
#   ./create-macos-desktop-app.sh 0.4.1 arm64
# Notarisation is a separate step (notarytool submit ... --wait) once the
# Apple ID + app-specific password is configured locally — keep that out
# of the script for now so the unsigned path stays the default and CI
# doesn't trip over missing secrets.
if [ -n "${APPLE_DEVELOPER_ID:-}" ]; then
    echo "Codesigning with: ${APPLE_DEVELOPER_ID}"
    codesign --force --deep --options runtime --timestamp \
        --sign "${APPLE_DEVELOPER_ID}" "${APP_BUNDLE}"
    codesign --verify --verbose=2 "${APP_BUNDLE}"
else
    echo "(unsigned — set APPLE_DEVELOPER_ID to enable codesigning)"
fi

# --- DMG ----------------------------------------------------------------

DMG_NAME="Zeus-Desktop-${VERSION}-macos-${ARCH}.dmg"
DMG_PATH="${OUTPUT_DIR}/${DMG_NAME}"

echo "Creating DMG..."
rm -f "${DMG_PATH}"

# Stage DMG contents:
#   Zeus.app                — the app
#   Applications -> /Applications  — drag-to-install target
#   README.txt              — xattr / first-launch instructions
DMG_TEMP="${OUTPUT_DIR}/dmg_temp_desktop"
rm -rf "${DMG_TEMP}"
mkdir -p "${DMG_TEMP}"
cp -R "${APP_BUNDLE}" "${DMG_TEMP}/"
ln -s /Applications "${DMG_TEMP}/Applications"

cat > "${DMG_TEMP}/README.txt" << 'EOF'
Zeus for macOS — Desktop edition
================================

INSTALL
  Drag Zeus.app onto the Applications shortcut in this window.

FIRST LAUNCH (important — Zeus is not signed)
  Zeus is distributed without an Apple Developer ID, so macOS Gatekeeper
  will block it on first launch. To clear the quarantine flag, open
  Terminal and run:

      xattr -cr /Applications/Zeus.app

  Then launch Zeus from Applications.

  If you still see a security warning, go to:
      System Settings -> Privacy & Security
  and click "Open Anyway".

WHAT HAPPENS WHEN YOU LAUNCH
  Zeus opens a native window. There is no browser tab, no separate
  server process to manage — the radio backend runs in-process inside
  the same window. Closing the window stops Zeus completely.

FIRST RUN — WDSP WISDOM
  The first launch builds an FFTW "wisdom" cache and can take 1-3
  minutes. The window will load, but do NOT click Discover/Connect
  until the wisdom build settles. Subsequent launches are instant.

ALSO AVAILABLE
  If you want to access Zeus from your phone or another machine on
  the same LAN, use the "Server" edition (Zeus-<version>-macos-*.dmg)
  instead — it runs as a headless service and you connect via browser.

More info: https://github.com/brianbruff/openhpsdr-zeus
EOF

hdiutil create -volname "Zeus Desktop v${VERSION}" \
    -srcfolder "${DMG_TEMP}" \
    -ov -format UDZO \
    "${DMG_PATH}"

rm -rf "${DMG_TEMP}"

echo "DMG created at ${DMG_PATH}"
echo
echo "NOTE: users must clear the quarantine flag on first launch:"
echo "  xattr -cr /Applications/Zeus.app"
