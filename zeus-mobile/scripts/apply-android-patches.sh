#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Apply Zeus mobile patches to the freshly-scaffolded Android project. Run
# after `npx cap add android`. Idempotent — safe to re-run.
#
# What it does:
#   1) Drops a network_security_config.xml that whitelists RFC1918 + .local
#      for cleartext HTTP (the WebView talks plain HTTP to LAN Zeus.Servers).
#   2) Patches AndroidManifest.xml to:
#        - reference networkSecurityConfig
#        - request INTERNET, ACCESS_NETWORK_STATE
#        - request RECORD_AUDIO (PTT mic uplink)
#        - keep the Capacitor default usesCleartextTraffic="true" intact.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

ANDROID_DIR="$ROOT/android"
APP_DIR="$ANDROID_DIR/app"
RES_XML_DIR="$APP_DIR/src/main/res/xml"
MANIFEST="$APP_DIR/src/main/AndroidManifest.xml"

if [[ ! -d "$ANDROID_DIR" ]]; then
    echo "✗ android/ not found — run \`npx cap add android\` first" >&2
    exit 1
fi

mkdir -p "$RES_XML_DIR"
cp "$ROOT/templates/android/network_security_config.xml" \
   "$RES_XML_DIR/network_security_config.xml"
echo "✓ network_security_config.xml installed"

if [[ ! -f "$MANIFEST" ]]; then
    echo "✗ AndroidManifest.xml not found at $MANIFEST" >&2
    exit 1
fi

# Patches are idempotent — they grep first and skip if already present.

# 1) Reference the network security config from <application>.
if ! grep -q 'networkSecurityConfig' "$MANIFEST"; then
    # Insert into the <application ...> open tag.
    perl -0777 -i -pe 's/(<application\b)/$1 android:networkSecurityConfig="\@xml\/network_security_config"/' "$MANIFEST"
    echo "✓ networkSecurityConfig wired into <application>"
else
    echo "✓ networkSecurityConfig already present"
fi

# 2) Add RECORD_AUDIO if missing. Capacitor 6 already adds INTERNET +
#    ACCESS_NETWORK_STATE; we only top up what's needed for PTT mic.
add_permission() {
    local perm="$1"
    if ! grep -q "android.permission.${perm}" "$MANIFEST"; then
        perl -0777 -i -pe "s/(<manifest\b[^>]*>)/\$1\n    <uses-permission android:name=\"android.permission.${perm}\"\\/>/" "$MANIFEST"
        echo "✓ added <uses-permission ${perm}>"
    else
        echo "✓ ${perm} already present"
    fi
}

add_permission RECORD_AUDIO
add_permission INTERNET
add_permission ACCESS_NETWORK_STATE

echo
echo "Android patches applied. Open the project with:"
echo "    npm run open:android"
