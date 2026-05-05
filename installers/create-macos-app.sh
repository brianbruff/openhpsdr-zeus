#!/bin/bash
# Build Zeus.app and a drag-to-install DMG for macOS.
# Usage: ./create-macos-app.sh <version> <arch>
# Example: ./create-macos-app.sh 0.1.0 arm64

set -e

VERSION="${1:-0.0.0}"
ARCH="${2:-arm64}"  # arm64 or x64

if [ "$ARCH" != "arm64" ] && [ "$ARCH" != "x64" ]; then
    echo "Error: ARCH must be 'arm64' or 'x64'"
    exit 1
fi

echo "Creating Zeus.app for macOS ${ARCH} v${VERSION}..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="${REPO_ROOT}/Zeus.Server/bin/Release/net10.0/osx-${ARCH}/publish"
OUTPUT_DIR="${SCRIPT_DIR}/output"
APP_NAME="Zeus.app"
APP_BUNDLE="${OUTPUT_DIR}/${APP_NAME}"
ICON_SOURCE="${REPO_ROOT}/docs/pics/zeus.png"

# Clean and create output directory
rm -rf "${APP_BUNDLE}"
mkdir -p "${OUTPUT_DIR}"
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

# Copy published files
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

# Create Info.plist. CFBundleExecutable points at launch.sh (not Zeus.Server
# directly) so double-clicking the .app starts the backend AND opens the
# default browser at http://localhost:6060 — matching the in-browser dev
# experience that Zeus is designed around.
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
    <string>com.ei6lf.zeus</string>
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
</dict>
</plist>
EOF

# Make Zeus.Server executable (cp -r usually preserves mode but be defensive)
chmod +x "${APP_BUNDLE}/Contents/MacOS/Zeus.Server"

# Launcher: starts the backend, waits for the HTTP port to come up, opens
# the default browser, and tears the backend down when the .app is quit.
# Using /dev/tcp for the readiness probe avoids depending on curl / nc.
cat > "${APP_BUNDLE}/Contents/MacOS/launch.sh" << 'EOF'
#!/bin/bash
cd "$(dirname "$0")"

# Pin the bundled libwdsp.dylib so an older copy in /usr/local/lib or
# /opt/homebrew/lib (e.g. from a piHPSDR / DeskHPSDR install) cannot
# shadow it. macOS dlopen does not search the executable's directory by
# default, so without this line P/Invoke can bind against a stale dylib
# that pre-dates symbols Zeus relies on (e.g. SetRXAEMNRpost2*). Both
# arches are listed so the same launcher works on arm64 and x64 builds;
# the loader silently skips a path that does not exist.
export DYLD_LIBRARY_PATH="$(pwd)/runtimes/osx-arm64/native:$(pwd)/runtimes/osx-x64/native:${DYLD_LIBRARY_PATH}"

# Cmd-Q from the Dock sends SIGTERM here — propagate it to the backend
# so we don't leave Zeus.Server orphaned on port 6060. Set up the trap
# BEFORE launching the server to ensure we catch early termination.
cleanup() {
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill -TERM "$SERVER_PID" 2>/dev/null || true
        # Wait up to 5 seconds for graceful shutdown
        for i in $(seq 1 10); do
            if ! kill -0 "$SERVER_PID" 2>/dev/null; then
                break
            fi
            sleep 0.5
        done
        # Force kill if still running
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            kill -KILL "$SERVER_PID" 2>/dev/null || true
        fi
        wait "$SERVER_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM

./Zeus.Server &
SERVER_PID=$!

# Wait up to ~30s for the HTTP listener. First-run WDSP wisdom takes 1–3
# minutes, but the HTTP server binds before wisdom planning starts, so the
# port comes up quickly.
for _ in $(seq 1 60); do
    if (exec 3<>/dev/tcp/127.0.0.1/6060) 2>/dev/null; then
        exec 3>&-
        exec 3<&-
        break
    fi
    sleep 0.5
done

open "http://localhost:6060"
wait "$SERVER_PID"
EOF
chmod +x "${APP_BUNDLE}/Contents/MacOS/launch.sh"

echo "App bundle created at ${APP_BUNDLE}"

# --- DMG ----------------------------------------------------------------

DMG_NAME="Zeus-${VERSION}-macos-${ARCH}.dmg"
DMG_PATH="${OUTPUT_DIR}/${DMG_NAME}"

echo "Creating DMG..."
rm -f "${DMG_PATH}"

# Stage DMG contents:
#   Zeus.app                — the app
#   Applications -> /Applications  — drag-to-install target
#   README.txt              — xattr / first-launch instructions
DMG_TEMP="${OUTPUT_DIR}/dmg_temp"
rm -rf "${DMG_TEMP}"
mkdir -p "${DMG_TEMP}"
cp -R "${APP_BUNDLE}" "${DMG_TEMP}/"
ln -s /Applications "${DMG_TEMP}/Applications"

cat > "${DMG_TEMP}/README.txt" << 'EOF'
Zeus for macOS
==============

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
  Zeus starts a local server and opens its UI in your default browser
  at http://localhost:6060.

  Tip: in Chrome / Edge / Safari you can install the page as a
  Progressive Web App (the "Install" icon in the address bar) for a
  windowed, dock-friendly experience without using Zeus.app at all.

FIRST RUN — WDSP WISDOM
  The first launch builds an FFTW "wisdom" cache and can take 1-3
  minutes. The browser will load, but do NOT click Discover/Connect
  until the Zeus.Server process settles. Subsequent launches are
  instant.

More info: https://github.com/brianbruff/openhpsdr-zeus
EOF

hdiutil create -volname "Zeus v${VERSION}" \
    -srcfolder "${DMG_TEMP}" \
    -ov -format UDZO \
    "${DMG_PATH}"

rm -rf "${DMG_TEMP}"

echo "DMG created at ${DMG_PATH}"
echo
echo "NOTE: users must clear the quarantine flag on first launch:"
echo "  xattr -cr /Applications/Zeus.app"
