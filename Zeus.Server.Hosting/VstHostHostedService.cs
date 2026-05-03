// SPDX-License-Identifier: GPL-2.0-or-later
//
// VstHostHostedService — owns the PluginHost sidecar lifecycle and the
// engine-side seam handler.
//
// Responsibilities (Wave 6a):
//   - On Start: load persisted chain from VstChainStore. If MasterEnabled
//     is true, start the sidecar and replay each non-empty slot (load
//     plugin, set bypass, restore parameters). Always wire the seam
//     handler onto the current IDspEngine so a future Master flip routes
//     audio without an engine swap.
//   - Auto-save on every meaningful state change. Plain
//     marshal-current-state-to-DTO-then-save; calls debounced via a
//     lightweight 250 ms timer so a parameter sweep doesn't hammer disk.
//   - Subscribe to PluginHostManager events (SlotEditorClosed,
//     SlotEditorResized, SidecarExited) and broadcast SignalR-style
//     updates via StreamingHub.
//   - Subscribe to DspPipelineService.EngineChanged so the seam handler
//     follows engine swaps (Synthetic ↔ WDSP) across radio connect /
//     disconnect.
//   - On Stop: persist current state, then ask PluginHostManager to stop.
//
// The engine-side seam handler is a thin wrapper around
// PluginHostManager.TryProcess: when the master flag is enabled AND the
// host is running, it forwards audio. Otherwise returns false → engine
// uses bypass path. The seam delegate the engine sees is the SAME
// instance for the lifetime of this service — it captures `this` and
// reads volatile state on each call. Re-installation only happens on
// engine swaps.

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.PluginHost;
using Zeus.PluginHost.Chain;
using Zeus.PluginHost.Ipc;
using Zeus.PluginHost.Native;

namespace Zeus.Server;

/// <summary>
/// Hosted service that wires <see cref="PluginHostManager"/> into the
/// app lifecycle plus the WDSP engine seam.
/// </summary>
public sealed class VstHostHostedService : IHostedService, IDisposable
{
    private readonly IPluginHost _host;
    private readonly IVstChainPersistence _store;
    private readonly DspPipelineService _pipeline;
    private readonly StreamingHub _hub;
    private readonly ILogger<VstHostHostedService> _log;

    // Cached delegate the engine reads on every block. Stable for the
    // lifetime of this service so re-install on engine swap is just a
    // pointer write.
    private readonly VstChainHandler _seamHandler;

    // Auto-save debounce. We coalesce bursts of parameter changes into
    // one disk write per 250 ms — the audio thread doesn't write here
    // (REST endpoints / event handlers call ScheduleSave); the timer is
    // owned by this service.
    private readonly object _saveLock = new();
    private Timer? _saveTimer;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(250);

    // Custom search paths and master flag are persisted alongside slot
    // state. Master flag can be flipped on/off without restarting the
    // sidecar — when off, the seam returns false and the sidecar (if
    // running) sees no traffic.
    private readonly object _stateLock = new();
    private readonly List<string> _customSearchPaths = new();

    public VstHostHostedService(
        IPluginHost host,
        IVstChainPersistence store,
        DspPipelineService pipeline,
        StreamingHub hub,
        ILogger<VstHostHostedService> log)
    {
        _host     = host;
        _store    = store;
        _pipeline = pipeline;
        _hub      = hub;
        _log      = log;

        _seamHandler = SeamHandlerImpl;
    }

    /// <summary>Snapshot of the operator's custom search paths.</summary>
    public IReadOnlyList<string> CustomSearchPaths
    {
        get { lock (_stateLock) return _customSearchPaths.ToArray(); }
    }

    /// <summary>Add a custom search path. Returns true if newly added.</summary>
    public bool AddCustomSearchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (_stateLock)
        {
            if (_customSearchPaths.Contains(path)) return false;
            _customSearchPaths.Add(path);
        }
        ScheduleSave();
        return true;
    }

    /// <summary>Remove a custom search path. Returns true if removed.</summary>
    public bool RemoveCustomSearchPath(string path)
    {
        bool removed;
        lock (_stateLock)
        {
            removed = _customSearchPaths.Remove(path);
        }
        if (removed) ScheduleSave();
        return removed;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Load persisted state first; the sidecar may or may not need
        // launching depending on MasterEnabled. We do this synchronously
        // so the first request to /api/plughost/state sees consistent
        // data even if the sidecar starts asynchronously below.
        VstChainEntry doc;
        try { doc = _store.Load(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "vsthost.persistence.load failed; starting empty");
            doc = new VstChainEntry();
        }

        lock (_stateLock)
        {
            _customSearchPaths.Clear();
            _customSearchPaths.AddRange(doc.CustomSearchPaths ?? new List<string>());
        }

        // Wire the seam handler on the current engine and follow swaps.
        InstallSeamOnCurrentEngine();
        _pipeline.EngineChanged += OnEngineChanged;

        // Wire host events for SignalR push.
        _host.SlotEditorClosed  += OnSlotEditorClosed;
        _host.SlotEditorResized += OnSlotEditorResized;
        _host.SlotParamChanged  += OnSlotParamChanged;
        if (_host is PluginHostManager mgr)
        {
            mgr.SidecarExited += OnSidecarExited;
        }

        if (doc.MasterEnabled)
        {
            try
            {
                using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startCts.CancelAfter(TimeSpan.FromSeconds(8));
                await _host.StartAsync(startCts.Token).ConfigureAwait(false);

                await _host.SetChainEnabledAsync(true, startCts.Token)
                    .ConfigureAwait(false);
                SetEngineSeamMasterFlag(true);

                // Replay non-empty slots in order. A failure on one slot
                // is logged and skipped — the chain comes up with that
                // slot empty rather than aborting the whole replay.
                foreach (var slot in doc.Slots ?? Enumerable.Empty<VstChainSlotEntry>())
                {
                    if (string.IsNullOrEmpty(slot.PluginPath)) continue;
                    try
                    {
                        var outcome = await _host.LoadSlotAsync(
                            slot.Index, slot.PluginPath, startCts.Token)
                            .ConfigureAwait(false);
                        if (!outcome.Ok)
                        {
                            _log.LogWarning(
                                "vsthost.replay slot {Idx} load failed: {Err}",
                                slot.Index, outcome.Error);
                            continue;
                        }
                        RecordSlotPath(slot.Index, slot.PluginPath);

                        if (slot.Bypass)
                        {
                            await _host.SetSlotBypassAsync(
                                slot.Index, true, startCts.Token)
                                .ConfigureAwait(false);
                        }

                        // Restore cached parameter values. ListSlotParameters
                        // refreshes the host's internal cache first so the
                        // SetSlotParameter calls land against valid ids.
                        if (slot.Parameters != null && slot.Parameters.Count > 0)
                        {
                            await _host.ListSlotParametersAsync(
                                slot.Index, startCts.Token).ConfigureAwait(false);
                            foreach (var (idStr, value) in slot.Parameters)
                            {
                                if (uint.TryParse(idStr, out var paramId))
                                {
                                    try
                                    {
                                        await _host.SetSlotParameterAsync(
                                            slot.Index, paramId, value, startCts.Token)
                                            .ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.LogWarning(ex,
                                            "vsthost.replay slot {Idx} param {Id} restore failed",
                                            slot.Index, paramId);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "vsthost.replay slot {Idx} unexpected failure", slot.Index);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "vsthost.start sidecar failed; chain disabled, no audio path change");
                // Leave the master flag false on the engine — bypass.
                SetEngineSeamMasterFlag(false);
            }
            // Always flush whatever in-memory state we ended up with after
            // the replay loop. If a slot's load failed (plugin moved on
            // disk, plugin hung during init, sidecar timeout, etc), the
            // in-memory _slots[idx].Plugin is null but LiteDB still has
            // the broken plugin path — without this flush, every subsequent
            // startup retries the broken plugin and re-hits the same
            // failure. Save now so a failure-to-load self-heals: next
            // startup sees an empty slot and the operator can move on.
            FlushSave();
        }
        else
        {
            // Sidecar stays cold until the operator enables the chain.
            SetEngineSeamMasterFlag(false);
        }

        BroadcastChainSnapshotEvent();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // Save once more so any unflushed changes land.
        FlushSave();

        // Take down the engine seam first so the audio thread doesn't see
        // a half-stopped sidecar mid-block.
        SetEngineSeamMasterFlag(false);

        try
        {
            await _host.StopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "vsthost.stop failed");
        }

        // Detach pipeline / host events.
        _pipeline.EngineChanged -= OnEngineChanged;
        _host.SlotEditorClosed  -= OnSlotEditorClosed;
        _host.SlotEditorResized -= OnSlotEditorResized;
        _host.SlotParamChanged  -= OnSlotParamChanged;
        if (_host is PluginHostManager mgr)
        {
            mgr.SidecarExited -= OnSidecarExited;
        }
    }

    public void Dispose()
    {
        Timer? t;
        lock (_saveLock) { t = _saveTimer; _saveTimer = null; }
        t?.Dispose();
    }

    // --------------------------------------------------------------
    // Public API used by the REST endpoints
    // --------------------------------------------------------------

    /// <summary>Master toggle. Starts sidecar lazily when first enabled.</summary>
    public async Task SetMasterEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (enabled && !_host.IsRunning)
        {
            await _host.StartAsync(ct).ConfigureAwait(false);
        }

        if (_host.IsRunning)
        {
            await _host.SetChainEnabledAsync(enabled, ct).ConfigureAwait(false);
        }

        SetEngineSeamMasterFlag(enabled);
        ScheduleSave();
        BroadcastChainEnabledChanged(enabled);
        BroadcastChainSnapshotEvent();
    }

    /// <summary>Load a plugin into a slot. Auto-starts the sidecar.</summary>
    public async Task<LoadPluginOutcome> LoadSlotAsync(
        int slotIdx, string path, CancellationToken ct)
    {
        bool autoStarted = false;
        if (!_host.IsRunning)
        {
            await _host.StartAsync(ct).ConfigureAwait(false);
            autoStarted = true;
        }
        // After an auto-start (or any path that brings up a fresh sidecar),
        // re-push the chain-enabled state so the sidecar's master toggle
        // matches the operator's persisted intent. Without this, a sidecar
        // that respawned after a crash / kill would come up with its chain
        // master OFF — slot loads would succeed but Process() would memcpy
        // bypass and audio would never go through plugins, even though the
        // .NET-side cache and UI would still say MasterEnabled=true (the
        // host's _chainEnabled was reset by the sidecar exit but never
        // re-pushed). The Load case is the trigger because LoadSlot from
        // the UI auto-starts the sidecar without going through the master
        // toggle path.
        if (autoStarted && _host.IsRunning)
        {
            VstChainEntry? doc = null;
            try { doc = _store.Load(); }
            catch { /* best effort */ }
            if (doc?.MasterEnabled == true)
            {
                try
                {
                    await _host.SetChainEnabledAsync(true, ct).ConfigureAwait(false);
                    SetEngineSeamMasterFlag(true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "vsthost.loadSlot auto-start: failed to re-push chain enable");
                }
            }
        }
        var outcome = await _host.LoadSlotAsync(slotIdx, path, ct).ConfigureAwait(false);
        if (outcome.Ok)
        {
            RecordSlotPath(slotIdx, path);
            // Refresh parameter list for the freshly loaded plugin so the
            // cache is populated for the next persistence flush.
            try { await _host.ListSlotParametersAsync(slotIdx, ct).ConfigureAwait(false); }
            catch { /* best effort */ }
        }
        ScheduleSave();
        BroadcastSlotStateChanged(slotIdx);
        return outcome;
    }

    public async Task UnloadSlotAsync(int slotIdx, CancellationToken ct)
    {
        await _host.UnloadSlotAsync(slotIdx, ct).ConfigureAwait(false);
        RecordSlotPath(slotIdx, null);
        ScheduleSave();
        BroadcastSlotStateChanged(slotIdx);
    }

    public async Task SetSlotBypassAsync(int slotIdx, bool bypass, CancellationToken ct)
    {
        await _host.SetSlotBypassAsync(slotIdx, bypass, ct).ConfigureAwait(false);
        ScheduleSave();
        BroadcastSlotStateChanged(slotIdx);
    }

    public async Task<IReadOnlyList<PluginParameter>> ListSlotParametersAsync(
        int slotIdx, CancellationToken ct)
    {
        return await _host.ListSlotParametersAsync(slotIdx, ct).ConfigureAwait(false);
    }

    public async Task SetSlotParameterAsync(
        int slotIdx, uint paramId, double value, CancellationToken ct)
    {
        await _host.SetSlotParameterAsync(slotIdx, paramId, value, ct)
            .ConfigureAwait(false);
        ScheduleSave();

        // Echo the actually-applied value (read from the host's cached
        // ChainSlot.Parameters) on the wire so UI can settle on the
        // plugin's quantization.
        if (slotIdx >= 0 && slotIdx < _host.MaxChainSlots)
        {
            var slot = _host.Slots[slotIdx];
            foreach (var p in slot.Parameters)
            {
                if (p.Id == paramId)
                {
                    BroadcastParameterChanged(slotIdx, paramId, p.CurrentValue);
                    break;
                }
            }
        }
    }

    public Task<EditorOpenOutcome> ShowSlotEditorAsync(int slotIdx, CancellationToken ct)
        => _host.ShowSlotEditorAsync(slotIdx, ct);

    public Task<bool> HideSlotEditorAsync(int slotIdx, CancellationToken ct)
        => _host.HideSlotEditorAsync(slotIdx, ct);

    /// <summary>Snapshot of the current host state for /api/plughost/state.</summary>
    public PlugHostStateSnapshot GetState()
    {
        var slots = _host.Slots;
        var slotDtos = new List<PlugHostSlotSnapshot>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            // Path is only known to us, not to PluginHostManager.Slots — pull
            // it from our own _slotPaths cache so the frontend's
            // hasNativeEditor check (which gates the EDIT/CLOSE buttons by
            // file extension) works against snapshot refreshes too. Before
            // this the snapshot endpoint omitted path entirely; the next
            // SignalR-driven refresh after a slot was loaded would silently
            // wipe the path field and the operator's editor buttons would
            // disappear (sidecar / audio unaffected — pure UI gating bug).
            string? path = null;
            lock (_stateLock) { _slotPaths.TryGetValue(i, out path); }
            slotDtos.Add(new PlugHostSlotSnapshot(
                Index: slots[i].Index,
                Plugin: slots[i].Plugin is { } p
                    ? new PlugHostPluginSnapshot(p.Name, p.Vendor, p.Version, path ?? string.Empty)
                    : null,
                Bypass: slots[i].Bypass,
                ParameterCount: slots[i].Parameters.Count));
        }
        return new PlugHostStateSnapshot(
            MasterEnabled: _host.IsChainEnabled,
            IsRunning: _host.IsRunning,
            Slots: slotDtos,
            CustomSearchPaths: CustomSearchPaths);
    }

    // --------------------------------------------------------------
    // Engine seam wiring
    // --------------------------------------------------------------
    //
    // The audio thread checks _host.IsChainEnabled directly rather than a
    // local cache so the seam can never drift out of sync with the host.
    // OnSidecarExited used to clear a separate _seamMasterEnabled field;
    // if a slot was then auto-loaded (which auto-starts the sidecar but
    // does NOT re-toggle master enable), the host's chain-enabled state
    // would come back true while the engine seam stayed false — audio
    // bypassed the chain even though the API and UI both said the chain
    // was on. Reading _host.IsChainEnabled inside the seam handler closes
    // that gap. The WDSP-side _vstChainEnabled flag is kept in sync as a
    // perf hint (it short-circuits ProcessTxMicVstChain without crossing
    // the delegate boundary), but the authoritative truth lives in the
    // host's cached state.

    private bool SeamHandlerImpl(Span<float> audio, int frames, int sampleRateHz)
    {
        if (!_host.IsChainEnabled) return false;
        // Best-effort dispatch into the host. PluginHostManager.TryProcess
        // is non-blocking on the audio thread (50 ms semaphore timeout
        // inside, but the call returns false immediately if anything is
        // wrong — ring full, sidecar not running, block size out of range).
        return _host.TryProcess(audio, audio, frames);
    }

    private void SetEngineSeamMasterFlag(bool enabled)
    {
        var engine = _pipeline.CurrentEngine;
        if (engine is WdspDspEngine wdsp)
        {
            wdsp.SetVstChainEnabled(enabled);
        }
    }

    private void InstallSeamOnCurrentEngine()
    {
        var engine = _pipeline.CurrentEngine;
        if (engine is WdspDspEngine wdsp)
        {
            wdsp.SetVstChainHandler(_seamHandler);
            wdsp.SetVstChainEnabled(_host.IsChainEnabled);
        }
    }

    private void OnEngineChanged(IDspEngine newEngine)
    {
        if (newEngine is WdspDspEngine wdsp)
        {
            wdsp.SetVstChainHandler(_seamHandler);
            wdsp.SetVstChainEnabled(_host.IsChainEnabled);
        }
    }

    // --------------------------------------------------------------
    // Persistence (auto-save with debounce)
    // --------------------------------------------------------------

    private void ScheduleSave()
    {
        lock (_saveLock)
        {
            _saveTimer ??= new Timer(_ => FlushSave(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushSave()
    {
        try
        {
            var doc = SnapshotForSave();
            _store.Save(doc);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "vsthost.persistence.save failed");
        }
    }

    private VstChainEntry SnapshotForSave()
    {
        var slots = _host.Slots;
        var entry = new VstChainEntry
        {
            Id = 1,
            SchemaVersion = 1,
            MasterEnabled = _host.IsChainEnabled,
            CustomSearchPaths = CustomSearchPaths.ToList(),
            Slots = new List<VstChainSlotEntry>(slots.Count),
            UpdatedUtc = DateTime.UtcNow,
        };
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            // For path: the host doesn't surface the path directly off
            // ChainSlot today, but we cached it on load via LoadSlotAsync.
            // Track it ourselves in _slotPaths.
            string? path;
            lock (_stateLock) { _slotPaths.TryGetValue(i, out path); }
            // If host has no plugin, force-clear the path so the next
            // load doesn't see a stale entry.
            if (s.Plugin == null) path = null;

            var paramDict = new Dictionary<string, double>();
            foreach (var p in s.Parameters)
            {
                paramDict[p.Id.ToString()] = p.CurrentValue;
            }
            entry.Slots.Add(new VstChainSlotEntry
            {
                Index = i,
                PluginPath = path,
                Bypass = s.Bypass,
                Parameters = paramDict,
            });
        }
        return entry;
    }

    // Slot-path tracking. PluginHostManager.Slots[i].Plugin doesn't carry
    // the load path (only Name/Vendor/Version), so we keep our own map
    // here, written on every successful load and cleared on unload.
    private readonly ConcurrentDictionary<int, string> _slotPaths = new();

    public string? GetSlotPath(int slotIdx)
        => _slotPaths.TryGetValue(slotIdx, out var p) ? p : null;

    public void RecordSlotPath(int slotIdx, string? path)
    {
        if (path is null) _slotPaths.TryRemove(slotIdx, out _);
        else _slotPaths[slotIdx] = path;
    }

    // --------------------------------------------------------------
    // SignalR broadcasts
    // --------------------------------------------------------------

    private void OnSlotEditorClosed(object? sender, EditorClosedEventArgs e)
    {
        _hub.BroadcastVstHostEvent($"slotEditorClosed:{e.SlotIdx}");
    }

    private void OnSlotEditorResized(object? sender, EditorResizedEventArgs e)
    {
        _hub.BroadcastVstHostEvent(
            $"slotEditorResized:{e.SlotIdx}:{e.Width}:{e.Height}");
    }

    // Wave 7 — plugin reported an editor / automation parameter change.
    // PluginHostManager already updated ChainSlot.Parameters before this
    // fires; we just need to flush the cache to LiteDB so the value
    // survives a server restart. Debounced timer (250 ms) coalesces rapid
    // knob drags into a single disk write.
    private void OnSlotParamChanged(object? sender, ParamChangedEventArgs e)
    {
        ScheduleSave();
        _hub.BroadcastVstHostEvent(
            $"slotParamChanged:{e.SlotIdx}:{e.ParamId}:{e.NormalizedValue}");
    }

    private void OnSidecarExited(object? sender, SidecarExitedEventArgs e)
    {
        // Clear the engine seam flag so the audio thread stops trying to
        // dispatch to a dead sidecar. Master flag stays true in our state
        // (operator's intent); SetMasterEnabledAsync re-launches.
        SetEngineSeamMasterFlag(false);
        _hub.BroadcastVstHostEvent($"sidecarExited:{e.ExitCode}");
        BroadcastChainSnapshotEvent();
    }

    private void BroadcastSlotStateChanged(int slotIdx)
    {
        _hub.BroadcastVstHostEvent($"slotStateChanged:{slotIdx}");
    }

    private void BroadcastChainEnabledChanged(bool enabled)
    {
        _hub.BroadcastVstHostEvent($"chainEnabledChanged:{(enabled ? 1 : 0)}");
    }

    private void BroadcastParameterChanged(int slotIdx, uint paramId, double value)
    {
        _hub.BroadcastVstHostEvent(
            $"parameterChanged:{slotIdx}:{paramId}:{value:F6}");
    }

    private void BroadcastChainSnapshotEvent()
    {
        _hub.BroadcastVstHostEvent("snapshot");
    }
}

// --------------------------------------------------------------------
// REST DTOs
// --------------------------------------------------------------------

public sealed record PlugHostPluginSnapshot(string Name, string Vendor, string Version, string Path);

public sealed record PlugHostSlotSnapshot(
    int Index,
    PlugHostPluginSnapshot? Plugin,
    bool Bypass,
    int ParameterCount);

public sealed record PlugHostStateSnapshot(
    bool MasterEnabled,
    bool IsRunning,
    IReadOnlyList<PlugHostSlotSnapshot> Slots,
    IReadOnlyList<string> CustomSearchPaths);
