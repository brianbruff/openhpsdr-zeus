# zeus-mobile — Capacitor wrapper

Native Android and iOS shells for the Zeus web frontend. The web bundle
that `zeus-web` already produces (vite outputs to
`../Zeus.Server.Hosting/wwwroot/` so the .NET server can serve it directly)
is loaded from a Capacitor WebView; everything else runs unchanged. The
user picks a LAN-side `Zeus.Server` host at first launch via
**Settings → Server**.

This directory is JS-side only. Platform projects (`android/`, `ios/`) are
gitignored; they're scaffolded by `npm run setup` on first checkout.

## Skip the local toolchain — grab a CI-built APK

If you just want to install Zeus on an Android device, you don't need a
local Android SDK. The
[**Build Android APK**](../../../actions/workflows/build-mobile-android.yml)
workflow on GitHub Actions builds an unsigned debug APK on every push to
`develop` / `main` (and on demand). Open the latest run, download the
`zeus-android-debug` artefact, and sideload the APK with
`adb install zeus-android-debug.apk` (or transfer it to the phone and tap
to install — you'll need to enable "Install unknown apps" for your file
manager).

iOS builds are not yet on CI — they need a macOS runner and signing certs.
Build locally with `npm run open:ios` for now.

## Prerequisites for local builds

- Node 18+ (same as `zeus-web`)
- For Android: Android Studio + Android SDK (Java 17 in PATH)
- For iOS: Xcode + CocoaPods (macOS only)

## First-run setup

```sh
npm run setup
```

The script will:
1. `npm install` Capacitor's CLI + platform packages
2. Build `zeus-web` (`../zeus-web/dist`)
3. Run `npx cap add android` and apply Zeus patches
4. On macOS: run `npx cap add ios` and apply Zeus patches
5. Run `npx cap sync` to copy the web bundle into both platforms

## Day-to-day workflows

| Task                            | Command                  |
|---------------------------------|--------------------------|
| Rebuild web + sync into apps    | `npm run sync`           |
| Open Android Studio             | `npm run open:android`   |
| Open Xcode                      | `npm run open:ios`       |
| Build + launch on Android       | `npm run run:android`    |
| Build + launch on iOS           | `npm run run:ios`        |

## How the server URL works

The Capacitor WebView serves the bundled web app from `http://localhost`
(Android) or `capacitor://localhost` (iOS). It can't reach the radio that
way. On first launch the app detects the Capacitor runtime
(`window.Capacitor.isNativePlatform()`) and pops Settings → **Server** so
the operator can paste an address like `http://192.168.1.23:6060`.

The address is persisted to `localStorage["zeus.serverUrl"]`. A small
fetch-interceptor + WebSocket builder in `zeus-web/src/serverUrl.ts`
prepends the configured base to every `/api/*`, `/ws`, and `/hub/*`
request. Web (browser) users leave the field blank — relative paths still
work because Zeus.Server is the same origin.

## Permissions

| Permission             | Why                                                           |
|------------------------|---------------------------------------------------------------|
| `INTERNET`             | Talk to the LAN-side Zeus.Server (HTTP + WS)                  |
| `ACCESS_NETWORK_STATE` | Detect when the device drops off the LAN                      |
| `RECORD_AUDIO`         | PTT / TX mic uplink (the same path the browser uses)          |
| iOS Local Network      | iOS 14+ system prompt for `192.168.*` reachability            |

## Cleartext to RFC1918

A LAN HPSDR radio doesn't have a public hostname or a TLS cert, so the
WebView has to talk plain HTTP to addresses like `192.168.1.23`. Both
platforms scope cleartext narrowly:

- **Android** — `network_security_config.xml` (in
  `templates/android/network_security_config.xml`) whitelists the RFC1918
  ranges, link-local IPv4, mDNS `.local`, and the Android emulator
  loopback. Public hosts still require HTTPS.
- **iOS** — `NSAppTransportSecurity → NSAllowsLocalNetworking = YES` in
  `Info.plist`. iOS handles the public-host restriction itself.

## Re-applying patches after `cap add`

If you ever delete and re-add a platform, run the apply scripts manually:

```sh
bash scripts/apply-android-patches.sh
bash scripts/apply-ios-patches.sh
```

They're idempotent — safe to run repeatedly.

## Out of scope (separate tickets)

- Store signing / distribution
- Push notifications
- Native radio drivers (this is a network-only client)
- Custom splash screens / app icons (using Capacitor defaults until art is approved)
