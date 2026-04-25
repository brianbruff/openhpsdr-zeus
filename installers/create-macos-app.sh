#!/bin/bash
# Script to create Zeus.app bundle for macOS
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

# Clean and create output directory
rm -rf "${APP_BUNDLE}"
mkdir -p "${OUTPUT_DIR}"
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

# Copy published files
echo "Copying published files..."
cp -r "${PUBLISH_DIR}"/* "${APP_BUNDLE}/Contents/MacOS/"

# Create Info.plist
cat > "${APP_BUNDLE}/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>Zeus.Server</string>
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

# Make the executable actually executable
chmod +x "${APP_BUNDLE}/Contents/MacOS/Zeus.Server"

# Create a simple launcher script that opens the default browser
cat > "${APP_BUNDLE}/Contents/MacOS/launch.sh" << 'EOF'
#!/bin/bash
cd "$(dirname "$0")"
./Zeus.Server &
SERVER_PID=$!
sleep 2
open http://localhost:6060
wait $SERVER_PID
EOF
chmod +x "${APP_BUNDLE}/Contents/MacOS/launch.sh"

echo "App bundle created at ${APP_BUNDLE}"

# Create DMG
DMG_NAME="Zeus-${VERSION}-macos-${ARCH}.dmg"
DMG_PATH="${OUTPUT_DIR}/${DMG_NAME}"

echo "Creating DMG..."
rm -f "${DMG_PATH}"

# Create a temporary directory for DMG contents
DMG_TEMP="${OUTPUT_DIR}/dmg_temp"
rm -rf "${DMG_TEMP}"
mkdir -p "${DMG_TEMP}"
cp -r "${APP_BUNDLE}" "${DMG_TEMP}/"

# Create a README for macOS users about the xattr requirement
cat > "${DMG_TEMP}/README.txt" << 'EOF'
Zeus for macOS

IMPORTANT: After installation, you need to remove the quarantine flag:

1. Copy Zeus.app to your Applications folder
2. Open Terminal
3. Run: xattr -cr /Applications/Zeus.app
4. Launch Zeus from Applications folder

This is required because the app is not signed by a registered Apple Developer.

For more information, visit:
https://github.com/brianbruff/openhpsdr-zeus
EOF

# Create DMG
hdiutil create -volname "Zeus v${VERSION}" \
    -srcfolder "${DMG_TEMP}" \
    -ov -format UDZO \
    "${DMG_PATH}"

rm -rf "${DMG_TEMP}"

echo "DMG created at ${DMG_PATH}"
echo ""
echo "IMPORTANT NOTE FOR USERS:"
echo "After installing Zeus.app, users must run:"
echo "  xattr -cr /Applications/Zeus.app"
echo "to remove the quarantine flag (unsigned app)."
