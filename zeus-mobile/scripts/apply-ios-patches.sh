#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Patch the freshly-scaffolded iOS project's Info.plist. Run after
# `npx cap add ios`. Uses /usr/libexec/PlistBuddy so it's idempotent.
#
# What it does:
#   - NSAppTransportSecurity → NSAllowsLocalNetworking = YES (iOS 14+ ATS
#     exception for plain-HTTP RFC1918 / .local Zeus.Servers).
#   - NSLocalNetworkUsageDescription (iOS 14+ system prompt copy).
#   - NSMicrophoneUsageDescription (PTT mic uplink).

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

PLIST="$ROOT/ios/App/App/Info.plist"
PB=/usr/libexec/PlistBuddy

if [[ ! -f "$PLIST" ]]; then
    echo "✗ Info.plist not found at $PLIST — run \`npx cap add ios\` first" >&2
    exit 1
fi

if ! command -v "$PB" >/dev/null 2>&1; then
    echo "✗ PlistBuddy not found (this script only runs on macOS)" >&2
    exit 1
fi

# 1) ATS exception for local networking (HTTP to RFC1918 / .local).
"$PB" -c "Delete :NSAppTransportSecurity" "$PLIST" 2>/dev/null || true
"$PB" -c "Add :NSAppTransportSecurity dict" "$PLIST"
"$PB" -c "Add :NSAppTransportSecurity:NSAllowsLocalNetworking bool YES" "$PLIST"
echo "✓ NSAppTransportSecurity → NSAllowsLocalNetworking=YES"

# 2) iOS 14+ local-network discovery prompt.
"$PB" -c "Delete :NSLocalNetworkUsageDescription" "$PLIST" 2>/dev/null || true
"$PB" -c "Add :NSLocalNetworkUsageDescription string Zeus needs to find your radio's Zeus.Server on the local network." "$PLIST"
echo "✓ NSLocalNetworkUsageDescription set"

# 3) Microphone permission for PTT mic uplink.
"$PB" -c "Delete :NSMicrophoneUsageDescription" "$PLIST" 2>/dev/null || true
"$PB" -c "Add :NSMicrophoneUsageDescription string Zeus uses the microphone to send your PTT audio to the radio." "$PLIST"
echo "✓ NSMicrophoneUsageDescription set"

echo
echo "iOS patches applied. Open the project with:"
echo "    npm run open:ios"
