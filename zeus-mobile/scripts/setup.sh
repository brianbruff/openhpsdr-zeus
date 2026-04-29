#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# One-shot bootstrap for the Zeus Capacitor wrapper. Run this once after
# cloning. Adds android/ and ios/ platform projects and applies the
# Zeus-specific manifest / Info.plist patches.
#
# Prerequisites (you'll need these locally):
#   - Node 18+ (already required for zeus-web)
#   - Android Studio + Android SDK (for android target)
#   - Xcode + CocoaPods (for ios target, macOS only)

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
cd "$ROOT"

echo "→ installing zeus-mobile dependencies"
npm install

echo "→ building zeus-web bundle"
npm --prefix ../zeus-web ci
npm --prefix ../zeus-web run build

if [[ ! -d ./android ]]; then
    echo "→ adding android platform"
    npx cap add android
    bash scripts/apply-android-patches.sh
else
    echo "✓ android/ already exists; skipping cap add android"
fi

if [[ "$(uname)" == "Darwin" ]]; then
    if [[ ! -d ./ios ]]; then
        echo "→ adding ios platform"
        npx cap add ios
        bash scripts/apply-ios-patches.sh
    else
        echo "✓ ios/ already exists; skipping cap add ios"
    fi
else
    echo "↷ skipping ios platform (only supported on macOS)"
fi

echo "→ syncing web bundle into native projects"
npx cap sync

echo
echo "Done. Next steps:"
echo "  npm run open:android   # opens Android Studio"
[[ "$(uname)" == "Darwin" ]] && echo "  npm run open:ios       # opens Xcode"
