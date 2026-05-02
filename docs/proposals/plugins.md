# Zeus Plugin System — Architecture Proposal

> **Status:** Draft — awaiting maintainer decisions on red-light items (see §8)
>
> Related: Issue [#185](https://github.com/brianbruff/openhpsdr-zeus/issues/185), [#106](https://github.com/brianbruff/openhpsdr-zeus/issues/106), PR [#216](https://github.com/brianbruff/openhpsdr-zeus/pull/216)

---

## 1. Problem Statement

All features currently must be baked into the core Zeus application. This creates two compounding pressures:

- **Operator burden:** every optional feature (VST host, amp managers, MIDI, logging exporters, …) ships to everyone, including those who never need it and are running on minimal hardware.
- **Maintainer burden:** every new integration is a core PR, a code-review obligation, a compatibility surface to protect forever.

The plugin system described here lets optional features live outside the core: third parties can develop, publish (via a GitHub-hosted registry), or sideload plugins without a core PR.

---

## 2. Core Recommendation

**AssemblyLoadContext (ALC)** for server-side .NET plugins + **dynamic ES `import()`** for frontend UI widgets.

This mirrors what SDR++, OBS, and Winamp do — in-process shared libraries with a typed entry-point interface — adapted for Zeus's .NET + React stack.

**The plugin host itself ships as an optional addon.** `Zeus.Server` needs only a one-line null-safe seam; the actual loader DLL (`Zeus.PluginHost.dll`) is dropped in by the installer (or not). No plugin support = zero overhead. This mechanic is already prefigured by PR [#216](https://github.com/brianbruff/openhpsdr-zeus/pull/216)'s `features.vstHost.available` gate: the sidecar either exists or it doesn't, and the capabilities endpoint reports accordingly.

---

## 3. Three Plugin Surfaces

| Surface | Mechanism | First use case |
|---|---|---|
| **Server DSP stage** | ALC-loaded .NET assembly implementing `IDspStagePlugin` | *(see Phase 0 below)* VST Host: loads VST2/3 native DLLs via an out-of-process sidecar, inserts into audio pipeline |
| **UI widget** | Dynamic `import()` of an ES module served at `/api/plugins/{id}/assets/` | Community flex-layout panels without a core PR |
| **Amp vendor sub-plugin** | Nested extension point (`IAmpManagerExtensionPoint`) exposed by `Zeus.Plugins.AmpManager` | KXPA100, SPE Expert, etc. register without touching Zeus core |

### Nested plugin model (amp manager)

```
Zeus.Server
  └── Plugin Host (optional addon)
       └── Amp Manager Plugin (optional)
            ├── Built-in profiles
            └── IAmpManagerExtensionPoint
                 ├── com.elecraft.kxpa100 plugin
                 └── com.spe.expert-1.5kfa plugin
```

**Open design question (flagged by KB2UKA, needs a dedicated RFC):** When an inner plugin wants to register against an outer plugin's extension point, does the inner plugin's manifest declare its parent by ID, or does the outer plugin scan a sub-directory? Manifest declaration is more rigorous but couples the inner to a string parent ID; directory scanning is looser but invisible in a registry. This should be resolved before Phase 3 implementation begins.

---

## 4. Delivery Phases

### Phase 0 — VST Host (Reference Implementation — DELIVERED ✓)

The VST host (issue #106, PR #212) is already merged into `release/0.5.0` and serves as the **canonical reference implementation** for the plugin system. It prefigures the full architecture in two load-bearing ways:

1. **Out-of-process sidecar isolation** — plugin crashes can't take Zeus down; per-arch native plugin compatibility is the sidecar's problem, not Zeus's. ALC-loaded .NET plugins don't get crash isolation for free — Phase 1 must explicitly decide whether the plugin host defaults to in-process ALC or also pushes toward sidecar isolation.

2. **Capabilities-gate pattern** — `GET /api/capabilities` with `features.vstHost.{ available, reason, sidecarPath }` is the shape every future plugin gate will follow. Drop in `ampManager`, `midi`, `cleverDsp` — same pattern, same locality test, frontend never duplicates the OS-support matrix.

Lessons from the VST sidecar work that generalise:
- Log what's on the wire (`drv=`, capability JSON) before theorising about misbehaviour.
- `SidecarLocator.Probe()` non-spawning at startup lets capabilities answer immediately.
- Remote vs. local distinction (`host !== 'desktop'`) already gates whether plugin editors are reachable — every future plugin with a native GUI window needs the same test.

### Phase 1 — Plugin Contracts (interfaces only)

~3 days. Zero user-visible change.

- New assembly `Zeus.Plugin.Contracts` (no external deps).
- Defines `IDspStagePlugin`, `IPluginManifest`, `IPluginHost`, `IAmpManagerExtensionPoint`.
- Null seam added to `Zeus.Server` — `IPluginHost?` resolved from DI; all call sites null-guarded.
- `reason` field in capability responses typed as a C# enum from day one (see §6, item 1).

### Phase 2 — Plugin Host addon

~1 week.

- `Zeus.PluginHost.dll` — dropped in by installer, registered via `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` (or `appsettings.json` key — maintainer decision needed, §8).
- ALC directory scanner: reads `plugins/` (per §6, item 3 — path TBD by maintainer), loads `plugin.json` manifest, validates SHA-256 against registry.
- `--no-plugins` launch flag bypasses all loading (safe mode for crash recovery).
- Settings page stub: installed plugins list, enable/disable toggles.

**ALC unloading / hot-swap (must document before shipping Phase 2):**  
Hot-swap without a Zeus restart requires `AssemblyLoadContext(isCollectible: true)` + `WeakReference` + `GC.WaitForPendingFinalizers()` loop. The failure mode is silent and permanent: any `static` field in the plugin assembly or a referenced library pins the context and prevents collection. v1 policy should be explicit — **"restart Zeus to swap a plugin"** — and documented in the settings page before operators ask. If hot-swap is added later, a collectible-ALC integration test is a prerequisite.

**Cross-platform plugin path resolution:**

```csharp
// Correct pattern — never concatenate raw strings
var pluginDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "zeus", "plugins");
// Linux/macOS → ~/.local/share/zeus/plugins
// Windows     → %LOCALAPPDATA%\zeus\plugins
```

Plugin scanner must use case-insensitive `StringComparer` for manifest IDs and dll names — Linux filesystems are case-sensitive; two plugins shipping `Plugin.dll` and `plugin.dll` in the same directory must be detected and rejected with a diagnostic.

### Phase 3 — AmpManager reference plugin

~1 week.

- `Zeus.Plugins.AmpManager` — first real plugin shipped via the Phase 2 loader.
- Demonstrates nested vendor model; documents `IAmpManagerExtensionPoint` contract.
- Amp vendor sub-plugins are standalone assemblies discovered in `plugins/amp-manager/` (or by manifest parent ID — decision deferred to the amp-manager RFC).

### Phase 4 — Frontend widget loading

~3 days.

- `Zeus.PluginHost` serves plugin JS assets at `/api/plugins/{id}/assets/`.
- Frontend adds dynamic `import()` call; plugin panels appear in "Add Panel" modal.
- ES module dynamic imports must be verified against Photino's three WebView engines (WebKit on Linux, WebView2 on Windows, WebKit on macOS-arm64) before architecture is committed — the browser-side story looks uniform but Photino surface area differs.

### Phase 5 — Registry and browser UI

~3 days.

- GitHub-hosted registry (subfolder in this repo or `openhpsdr-zeus-plugins` repo — maintainer decision needed, §8).
- Plugin browser UI: search, install, SHA-256 verification.
- Registry format: `registry.json` with per-plugin `{ id, name, version, sha256, assets_url, manifest_url }`.

---

## 5. Comparison — Plugin Systems in Similar Products

| Product | Isolation model | Plugin discovery | Notes |
|---|---|---|---|
| VS Code | Separate Node.js process (extension host) | Marketplace + local VSIX | Crash isolation; slow IPC for hot path |
| OBS | In-process `.so`/`.dll` | Directory scan | Same pattern Zeus proposes; crash takes host down |
| SDR++ | In-process `.so`/`.dll` | Directory scan | Zero IPC overhead; no isolation |
| GIMP | In-process + Script-Fu subprocess | Directory scan | Script-Fu gives isolation for scripted plugins |
| Winamp | In-process `.dll` | Directory scan | Classic reference for Zeus's DSP stage model |
| VST2/3 hosts | In-process (historically) / out-of-process sidecar (modern) | DAW-specific scan | Zeus's VST sidecar already uses out-of-process model — most modern hosts follow suit after DAWs started crashing |

**Zeus position:** In-process ALC for pure-.NET plugins (low IPC overhead, suitable for DSP-adjacent stages); out-of-process sidecar already proven for native/VST plugins. This is the correct hybrid: don't add a process boundary where it costs without benefit, but keep the sidecar where crash isolation matters (native code, third-party DLLs).

---

## 6. Near-Term Follow-Up Items

These don't block Phase 1 but should be filed as issues before Phase 2 ships:

1. **`reason` field → structured enum.** Today `features.vstHost.reason` is a free-form string. Once the frontend needs to localise messages or programmatically branch (`if reason === SIDECAR_MISSING, show install link`), a string becomes brittle. Fix while there's exactly one consumer.

2. **Amp-manager nested extension-point RFC.** The inner-plugin discovery question (manifest parent ID vs. sub-directory scan) is a dedicated design exercise. KB2UKA has offered to draft this RFC — coordinate in issue #185 before Phase 3 begins.

3. **ALC hot-swap integration test.** If hot-swap without restart is ever added, gate it behind a collectible-ALC test that verifies no statics pin the context. Add as a Phase 2 follow-up issue.

4. **Photino WebView ES-module validation.** Verify `import()` works in WebKit (Linux), WebView2 (Windows), and WebKit (macOS-arm64) before committing the frontend widget architecture.

5. **Plugin scanner case-sensitivity test.** CI runs on Linux (case-sensitive) — add a test that exercises duplicate-casing detection so it doesn't silently fail on Windows/macOS.

---

## 7. Security and Resource Governance

**Threat model:** Zeus does not sandbox plugins. Plugins loaded via ALC run in the same process with full CLR trust. The security posture is:
- **Registry-hosted plugins:** the project runs a SHA-256 sweep before listing; responsibility ends there. "We audit, but the buck stops with the installer."
- **Sideloaded plugins:** fully operator responsibility; no implicit trust.
- **Resource limits:** no OS-level enforcement in v1. Runaway plugins can consume CPU/RAM. Document this clearly; consider a watchdog thread that logs plugin CPU samples at 1 Hz in DEBUG builds.
- **Crash recovery:** out-of-process sidecars (VST model) already give crash isolation. ALC-loaded plugins do not — a plugin that throws in `ProcessAudio()` unwinds into the DSP pipeline. Phase 2 should wrap plugin calls in try/catch with automatic disable-on-repeated-failure.

---

## 8. Decisions Needed from Brian (Red-Light Items)

1. Add `Zeus.Plugin.Contracts` project to the solution? (Phase 1 prerequisite)
2. Plugin host activation: `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` env var (set by installer) vs. `appsettings.json` key for the loader DLL path?
3. Plugin directory location: `~/.zeus/plugins/` (user-level) vs. next to the binary vs. both?
4. Registry governance: subfolder in this repo (`plugins/registry.json`) or separate `openhpsdr-zeus-plugins` repo?
5. Amp-manager nested discovery: manifest parent ID or sub-directory scan? (Dedicated RFC from KB2UKA recommended first.)
6. VST editor GUI in v1: parameter-scrape-only (React panel, no native window) vs. native window wrapper?
