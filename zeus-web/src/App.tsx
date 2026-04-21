import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { AgcSlider } from './components/AgcSlider';
import { AlertBanner } from './components/AlertBanner';
import { AttenuatorSlider } from './components/AttenuatorSlider';
import { AudioToggle } from './components/AudioToggle';
import { ConnectPanel } from './components/ConnectPanel';
import { DriveSlider } from './components/DriveSlider';
import { MicGainSlider } from './components/MicGainSlider';
import { MicMeter } from './components/MicMeter';
import { MobilePttButton } from './components/MobilePttButton';
import { ModeBandwidth } from './components/ModeBandwidth';
import { MoxButton } from './components/MoxButton';
import { Panadapter } from './components/Panadapter';
import { PreampButton } from './components/PreampButton';
import { QrzStatusPill } from './components/QrzStatusPill';
import { RotatorStatusPill } from './components/RotatorStatusPill';
import { SMeterLive } from './components/SMeterLive';
import { TxStageMeters } from './components/TxStageMeters';
import { TunButton } from './components/TunButton';
import { VfoDisplay } from './components/VfoDisplay';
import { Waterfall } from './components/Waterfall';
import { ZoomControl } from './components/ZoomControl';
import { AzimuthMap } from './components/design/AzimuthMap';
import { CONTACTS, HOME, bandOf, type LogEntry } from './components/design/data';
import { CwKeyer } from './components/design/CwKeyer';
import { Dockable } from './components/design/Dockable';
import { DspPanel } from './components/DspPanel';
import { Logbook } from './components/design/Logbook';
import { QrzCard } from './components/design/QrzCard';
import { TerminatorLines } from './components/design/TerminatorLines';
import { bearingDeg, distanceKm } from './components/design/geo';
import { LeafletWorldMap } from './components/design/LeafletWorldMap';
import { startRealtime } from './realtime/ws-client';
import { getAudioClient } from './audio/audio-client';
import { useMicUplink } from './audio/use-mic-uplink';
import { fetchState } from './api/client';
import { useConnectionStore } from './state/connection-store';
import { useQrzStore } from './state/qrz-store';
import { useRotatorStore } from './state/rotator-store';
import { useTxStore } from './state/tx-store';
import { useKeyboardShortcuts } from './util/use-keyboard-shortcuts';
import type { QrzStation } from './api/qrz';
import type { Contact } from './components/design/data';

// See ../state/connection-store.ts — StateDto is REST-poll only; WS is binary
// frames. 333 ms poll keeps slow state (atten offset, adc overload) fresh.
const STATE_POLL_MS = 333;

export default function App() {
  const status = useConnectionStore((s) => s.status);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const agcTop = useConnectionStore((s) => s.agcTopDb);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const preampOn = useConnectionStore((s) => s.preampOn);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const connected = status === 'Connected';

  useKeyboardShortcuts();
  useMicUplink();

  useEffect(() => {
    const stop = startRealtime();
    return () => {
      stop();
    };
  }, []);

  useEffect(() => {
    if (!connected) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let ctrl: AbortController | null = null;
    const tick = async () => {
      ctrl = new AbortController();
      try {
        const next = await fetchState(ctrl.signal);
        if (!cancelled) useConnectionStore.getState().applyState(next);
      } catch {
        /* transient errors reconcile on the next tick */
      }
      if (!cancelled) timer = setTimeout(tick, STATE_POLL_MS);
    };
    tick();
    return () => {
      cancelled = true;
      if (timer != null) clearTimeout(timer);
      ctrl?.abort();
    };
  }, [connected]);

  useEffect(() => {
    return useConnectionStore.subscribe((state, prev) => {
      if (state.mode !== prev.mode) getAudioClient().reset();
    });
  }, []);

  useEffect(() => {
    return useTxStore.subscribe((state, prev) => {
      if (state.moxOn !== prev.moxOn) getAudioClient().reset();
    });
  }, []);

  // Apply saved theme attributes to <html> on first render. The Tweaks panel
  // used to toggle these at runtime; now the defaults are fixed.
  useEffect(() => {
    const variant = localStorage.getItem('zeus.variant') || 'console';
    const fonts = localStorage.getItem('zeus.fonts') || 'geist';
    document.documentElement.setAttribute('data-variant', variant);
    document.documentElement.setAttribute('data-fonts', fonts);
  }, []);

  // --- Design-mock state (QRZ, DSP grid toggles, CW WPM, memories) ---
  const [callsign, setCallsign] = useState('EI6LF');
  const [terminatorActive, setTerminatorActive] = useState(true);
  // While 'M' is held and QRZ is engaged, the spectrum canvas stack goes
  // pointer-events:none and the Leaflet map underneath takes drag/zoom input.
  // Click-to-tune is suspended for the duration of the modifier.
  const [mapModifier, setMapModifier] = useState(false);
  const [enriching, setEnriching] = useState(false);
  const [lookupKey, setLookupKey] = useState(0);
  const [beamOverrideDeg, setBeamOverrideDeg] = useState<number | null>(null);
  const [beamInputStr, setBeamInputStr] = useState('');

  const qrzHome = useQrzStore((s) => s.home);
  const qrzLookup = useQrzStore((s) => s.lastLookup);
  const qrzHasXml = useQrzStore((s) => s.hasXmlSubscription);
  const qrzActive = !!qrzHome && qrzHasXml;

  // Live rotator heading — drives the map's beam lines when rotctld is up so
  // the beam shows the actual antenna direction, not the great-circle bearing
  // to the current QRZ lookup.
  const rotStatus = useRotatorStore((s) => s.status);
  const rotLiveAz = rotStatus?.connected ? rotStatus.currentAz : null;
  const contact: Contact | null = qrzActive
    ? qrzStationToContact(qrzLookup, qrzHome)
    : (CONTACTS[callsign.toUpperCase()] ?? null);

  const [wpm, setWpm] = useState(22);
  const nrState = useConnectionStore((s) => s.nr);
  const dspActive =
    nrState.nrMode !== 'Off' ||
    nrState.nbMode !== 'Off' ||
    nrState.anfEnabled ||
    nrState.snbEnabled ||
    nrState.nbpNotchesEnabled;

  const csInputRef = useRef<HTMLInputElement | null>(null);

  const engageTerminator = useCallback((cs?: string) => {
    const target = (cs ?? callsign).toUpperCase();
    setCallsign(target);
    setTerminatorActive(true);
    setEnriching(true);
    setLookupKey((k) => k + 1);
    setBeamOverrideDeg(null);
    setBeamInputStr('');
    const qrz = useQrzStore.getState();
    if (qrz.connected && qrz.hasXmlSubscription) {
      // Real QRZ lookup. The store updates lastLookup on success; the enriching
      // scan animation keeps playing until the promise settles.
      qrz.lookup(target).finally(() => setEnriching(false));
    } else {
      // Design-mock fallback: CONTACTS lookup is synchronous; just run the scan
      // briefly so the card doesn't pop in without the visual beat.
      setTimeout(() => setEnriching(false), 700);
    }
  }, [callsign]);

  const disengageTerminator = useCallback(() => {
    setTerminatorActive(false);
    setEnriching(false);
  }, []);

  // `/` focuses the callsign input so the operator can type a call and hit Enter.
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (e.key === '/' && !(t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement)) {
        e.preventDefault();
        csInputRef.current?.focus();
        csInputRef.current?.select();
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, []);

  // Hold-to-steer: while 'M' is down (outside a text field), the Leaflet map
  // becomes interactive and the spectrum canvas stops intercepting events.
  // Keyup — and a defensive blur/visibilitychange — release the modifier so
  // you don't get stuck if focus leaves the window mid-press.
  useEffect(() => {
    const inField = (t: EventTarget | null) =>
      t instanceof HTMLInputElement ||
      t instanceof HTMLTextAreaElement ||
      (t instanceof HTMLElement && t.isContentEditable);
    const onDown = (e: KeyboardEvent) => {
      if (e.repeat) return;
      if ((e.key === 'm' || e.key === 'M') && !inField(e.target)) {
        setMapModifier(true);
      }
    };
    const onUp = (e: KeyboardEvent) => {
      if (e.key === 'm' || e.key === 'M') setMapModifier(false);
    };
    const release = () => setMapModifier(false);
    window.addEventListener('keydown', onDown);
    window.addEventListener('keyup', onUp);
    window.addEventListener('blur', release);
    document.addEventListener('visibilitychange', release);
    return () => {
      window.removeEventListener('keydown', onDown);
      window.removeEventListener('keyup', onUp);
      window.removeEventListener('blur', release);
      document.removeEventListener('visibilitychange', release);
    };
  }, []);

  const mapInteractive = terminatorActive && mapModifier;

  const onCallsignSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    engageTerminator();
  };

  const onLogPick = (r: LogEntry) => engageTerminator(r.call);

  const bandLabel = bandOf(vfoHz);

  // --- Tx status chip
  const txChip = moxOn || tunOn ? 'TX' : 'RX';

  // --- Effective home station for the map + bearing math. Real QRZ profile
  // takes precedence; otherwise the design-mock HOME constant keeps the
  // disconnected demo looking populated.
  const effectiveHome = qrzHome && qrzHome.lat != null && qrzHome.lon != null
    ? {
        call: qrzHome.callsign,
        lat: qrzHome.lat,
        lon: qrzHome.lon,
        grid: qrzHome.grid ?? '',
        imageUrl: qrzHome.imageUrl ?? null,
      }
    : { call: HOME.call, lat: HOME.lat, lon: HOME.lon, grid: HOME.grid, imageUrl: null as string | null };

  const sp = contact ? bearingDeg(effectiveHome.lat, effectiveHome.lon, contact.lat, contact.lon) : 0;
  const lp = (sp + 180) % 360;
  const dist = contact ? distanceKm(effectiveHome.lat, effectiveHome.lon, contact.lat, contact.lon) : 0;

  function submitBeam(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const trimmed = beamInputStr.trim();
    if (!trimmed) {
      setBeamOverrideDeg(null);
      return;
    }
    const parsed = Number(trimmed);
    if (!Number.isFinite(parsed)) return;
    const normalized = ((parsed % 360) + 360) % 360;
    setBeamOverrideDeg(normalized);
    // Also command the rotator if it's enabled — visual override + physical
    // rotation in one click, same UX as Log4YM's map beam control.
    const rot = useRotatorStore.getState();
    if (rot.config.enabled && rot.status?.connected) {
      void rot.setAzimuth(normalized);
    }
  }

  // --- Hero title
  const heroTitle = terminatorActive && contact ? (() => {
    return (
      <>
        Panadapter · World Map ·{' '}
        <span style={{ color: 'var(--accent)' }}>{contact.callsign}</span> ·{' '}
        {Math.round(dist).toLocaleString()} km · brg {sp.toFixed(0)}°
      </>
    );
  })() : (
    <>Panadapter · {(vfoHz / 1e6).toFixed(3)} MHz · {bandLabel}</>
  );

  // When no radio is connected, dim the workspace and centre the full
  // ConnectPanel on top so the eye lands on it. The backdrop is
  // pointer-events:none so the topbar stays interactive (QRZ sign-in,
  // Tweaks, etc.); the ConnectPanel itself re-enables pointer events so
  // Discover / Connect buttons still click through.
  const disconnectedOverlay = useMemo(() => {
    if (connected) return null;
    return (
      <div
        style={{
          position: 'absolute',
          inset: 0,
          background: 'rgba(0,0,0,0.55)',
          backdropFilter: 'blur(4px)',
          pointerEvents: 'none',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          zIndex: 200,
        }}
      >
        <div style={{ pointerEvents: 'auto' }}>
          <ConnectPanel />
        </div>
      </div>
    );
  }, [connected]);

  return (
    <div className="app" data-screen-label="01 Main Console" style={{ position: 'relative' }}>
      {/* Top bar — sits above the disconnected overlay (zIndex 200) so QRZ
          sign-in and Discover Radio stay usable before the HL2 is connected. */}
      <div className="topbar" style={{ position: 'relative', zIndex: 300 }}>
        <div className="brand">
          <div className="brand-mark">
            <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden>
              <circle cx="12" cy="12" r="3" fill="var(--accent)" />
              <circle
                cx="12"
                cy="12"
                r="7"
                fill="none"
                stroke="var(--accent)"
                strokeWidth="1"
                opacity="0.5"
              />
              <circle
                cx="12"
                cy="12"
                r="11"
                fill="none"
                stroke="var(--accent)"
                strokeWidth="1"
                opacity="0.25"
              />
            </svg>
          </div>
          <div className="brand-text">
            <div className="brand-name mono">ZEUS</div>
            <div className="brand-sub label-xs hide-mobile">HERMES LITE 2 · 0.1–54 MHz</div>
          </div>
        </div>

        <div className="topbar-divider hide-mobile" />

        <div className="vfo-group hide-mobile">
          <div className="vfo-tab active">
            <div className="vfo-label label-xs">VFO A</div>
            <div className="vfo-freq mono">{(vfoHz / 1e6).toFixed(3)}</div>
          </div>
        </div>

        <div className="topbar-divider hide-mobile" />

        <div className="status-chips hide-mobile">
          <div className="chip">
            <span className="k">MODE</span>
            <span className="v">{mode}</span>
          </div>
          <div className="chip">
            <span className="k">BAND</span>
            <span className="v">{bandLabel}</span>
          </div>
          <div className="chip accent">
            <span className="k">AGC-T</span>
            <span className="v mono">{agcTop}</span>
          </div>
          <div className="chip">
            <span className="k">BW</span>
            <span className="v mono">
              {Math.min(Math.abs(filterLow), Math.abs(filterHigh))}…
              {Math.max(Math.abs(filterLow), Math.abs(filterHigh))} Hz
            </span>
          </div>
          <div className={`chip ${moxOn || tunOn ? 'tx' : ''}`}>
            <span className="k">TX</span>
            <span className="v">{txChip}</span>
          </div>
        </div>

        <div className="spacer" style={{ flex: 1 }} />

        {/* While disconnected the centre overlay owns the Discover UI; the
            topbar slot holds the connected-state Disconnect control. Mounting
            just one ConnectPanel at a time also avoids doubled 10s discovery
            polls. */}
        {connected && <ConnectPanel />}

        <div className="hide-mobile" style={{ display: 'contents' }}>
          <RotatorStatusPill />
          <QrzStatusPill />
        </div>

        <button
          type="button"
          className={`btn qrz-btn hide-mobile ${terminatorActive ? 'active' : ''}`}
          onClick={() => (terminatorActive ? disengageTerminator() : engageTerminator())}
        >
          <span className="led on" style={{ marginRight: 6 }} />
          {terminatorActive ? 'QRZ ENGAGED' : 'Engage QRZ'}
        </button>
      </div>

      {/* Control strip — real wired controls rebuilt into the design's chassis */}
      <div className="control-strip">
        <div className="hide-mobile" style={{ display: 'contents' }}>
          <ModeBandwidth />
        </div>
        <div className="ctrl-group hide-mobile" style={{ minWidth: 220 }}>
          <div className="label-xs ctrl-lbl">FRONT-END</div>
          <div className="btn-row" style={{ gap: 6, alignItems: 'center' }}>
            <PreampButton />
            <AttenuatorSlider />
          </div>
        </div>
        <div className="ctrl-group hide-mobile" style={{ minWidth: 180 }}>
          <div className="label-xs ctrl-lbl">AGC</div>
          <AgcSlider />
        </div>
        <div className="spacer hide-mobile" style={{ flex: 1 }} />
        <div className="ctrl-group" style={{ minWidth: 360 }}>
          <div className="label-xs ctrl-lbl">ZOOM · DRIVE · MIC</div>
          <div className="btn-row" style={{ gap: 10 }}>
            <div className="hide-mobile" style={{ display: 'contents' }}>
              <ZoomControl />
            </div>
            <DriveSlider />
            <MicGainSlider />
          </div>
        </div>
      </div>

      <AlertBanner />

      <div className={`workspace ${terminatorActive ? 'terminator' : ''}`}>
        {/* Hero — spectrum + waterfall with QRZ world-map layer */}
        <div className={`panel hero ${terminatorActive ? 'qrz-mode' : ''} ${mapInteractive ? 'map-mode' : ''}`}>
          <div className="panel-head">
            <span className={`dot ${moxOn || tunOn ? 'tx' : 'on'}`} />
            <span className="title">{heroTitle}</span>
            <span className="spacer" style={{ flex: 1 }} />
            {terminatorActive && contact && (
              <>
                <span className="chip mono">
                  <span className="k">SP</span>
                  <span className="v">{sp.toFixed(0)}°</span>
                </span>
                <span className="chip mono">
                  <span className="k">LP</span>
                  <span className="v">{lp.toFixed(0)}°</span>
                </span>
                <form onSubmit={submitBeam} className="chip mono" style={{ gap: 4 }}>
                  <span className="k">BEAM</span>
                  <input
                    type="text"
                    inputMode="decimal"
                    value={beamInputStr}
                    onChange={(e) => setBeamInputStr(e.target.value)}
                    placeholder={(((rotLiveAz ?? beamOverrideDeg ?? sp) % 360 + 360) % 360).toFixed(0)}
                    style={{
                      width: 40,
                      background: 'transparent',
                      border: '1px solid var(--line)',
                      color: 'inherit',
                      fontFamily: 'inherit',
                      fontSize: 'inherit',
                      padding: '0 2px',
                    }}
                  />
                  <button type="submit" className="btn sm" style={{ padding: '0 6px' }}>
                    Go
                  </button>
                </form>
              </>
            )}
            {terminatorActive && (
              <span
                className={`chip mono ${mapInteractive ? 'accent' : ''}`}
                title="Hold M to pan/zoom the map (click-to-tune paused)"
              >
                <span className="k">M</span>
                <span className="v">{mapInteractive ? 'MAP' : 'hold'}</span>
              </span>
            )}
            <span className="chip mono">
              <span className="k">HZ/PX</span>
              <span className="v">
                {useDisplayHzPerPixel()}
              </span>
            </span>
          </div>
          <div className="panel-body hero-body">
            <div className={`map-layer ${terminatorActive ? 'visible' : ''}`}>
              <LeafletWorldMap
                home={{
                  call: effectiveHome.call,
                  lat: effectiveHome.lat,
                  lon: effectiveHome.lon,
                  grid: effectiveHome.grid,
                  imageUrl: effectiveHome.imageUrl,
                }}
                target={
                  contact
                    ? {
                        call: contact.callsign,
                        lat: contact.lat,
                        lon: contact.lon,
                        grid: contact.grid,
                        imageUrl: contact.photoUrl ?? null,
                      }
                    : null
                }
                beamBearing={
                  // Priority: rotctld live azimuth (ground truth — where the
                  // antenna is actually pointing, including mid-rotation); then
                  // the manual Go override (used when rotator is offline);
                  // finally undefined, so LeafletWorldMap falls back to the
                  // great-circle bearing to the target.
                  rotLiveAz ?? beamOverrideDeg ?? undefined
                }
                active={terminatorActive}
                interactive={mapInteractive}
                onRotateToBearing={(brg) => {
                  const rot = useRotatorStore.getState();
                  if (!rot.config.enabled || !rot.status?.connected) return;
                  const normalized = ((brg % 360) + 360) % 360;
                  setBeamOverrideDeg(normalized);
                  setBeamInputStr(normalized.toFixed(0));
                  void rot.setAzimuth(normalized);
                }}
              />
            </div>
            <div
              data-spectrum-stack
              style={{
                position: 'absolute',
                inset: 0,
                display: 'grid',
                gridTemplateRows: '1fr 1fr',
                zIndex: 1,
              }}
            >
              {connected && <Panadapter />}
              {connected && <Waterfall transparent={terminatorActive} />}
            </div>
          </div>
        </div>

        {/* Side stack — Freq, S-Meter, QRZ, DSP, CW (and Map when QRZ off) */}
        <div className="side-stack">
          <div className="side-slot">
            <Dockable title="Frequency · VFO" ledOn>
              <div className="freq-panel">
                <VfoDisplay />
              </div>
            </Dockable>
          </div>
          <div className="side-slot">
            <Dockable title={moxOn || tunOn ? 'Power Out' : 'S-Meter · RX'} ledTx={moxOn || tunOn} ledOn={!(moxOn || tunOn)}>
              <SMeterLive />
            </Dockable>
          </div>

          {terminatorActive ? (
            <div className="side-slot grow hide-mobile">
              <Dockable
                title="QRZ.com Lookup"
                ledOn={!!contact}
                key={'qrz-' + lookupKey}
                className={terminatorActive ? 't1000-panel' : ''}
                actions={
                  <form
                    onSubmit={onCallsignSubmit}
                    style={{ display: 'flex', gap: 4 }}
                  >
                    <input
                      ref={csInputRef}
                      className="cs-input mono"
                      value={callsign}
                      onChange={(e) => setCallsign(e.target.value.toUpperCase())}
                      placeholder="CALL?"
                    />
                    <button type="submit" className="btn sm">Lookup</button>
                  </form>
                }
              >
                <QrzCard contact={contact} enriching={enriching} />
              </Dockable>
            </div>
          ) : (
            <div className="side-slot hide-mobile">
              <Dockable title="Great-Circle Map" ledOn={!!contact}>
                <AzimuthMap target={contact} myGrid="EM48" />
              </Dockable>
            </div>
          )}

          <div className="side-slot hide-mobile">
            <Dockable title="DSP" ledOn={dspActive}>
              <DspPanel />
            </Dockable>
          </div>

          <div className="side-slot hide-mobile">
            <Dockable title={`CW Keyer · ${wpm} WPM`} ledOn={mode === 'CWU' || mode === 'CWL'}>
              <CwKeyer wpm={wpm} setWpm={setWpm} />
            </Dockable>
          </div>
        </div>

        {/* Bottom row — Logbook + TX Stage Meters on desktop; big PTT on mobile. */}
        <div className="bottom-row">
          <div className="bottom-slot hide-mobile">
            <Dockable title="Logbook" ledOn>
              <Logbook onPick={onLogPick} />
            </Dockable>
          </div>
          <div className="bottom-slot hide-mobile">
            <Dockable title="TX Stage Meters" ledOn={moxOn || tunOn}>
              <TxStageMeters />
            </Dockable>
          </div>
          <div className="bottom-slot show-mobile mobile-ptt-slot">
            <MobilePttButton />
          </div>
        </div>

        <TerminatorLines active={terminatorActive} />
      </div>

      {/* Transport — MOX/TUN + audio + drive + mic meter + chips */}
      <div className="transport">
        <MoxButton />
        <TunButton />
        <div className="transport-sep" />
        <AudioToggle />
        <MicMeter />
        <div className="transport-sep hide-mobile" />
        <button type="button" className="btn ghost hide-mobile">SPLIT</button>
        <button type="button" className="btn ghost hide-mobile">RIT</button>
        <button type="button" className="btn ghost hide-mobile">SAVE MEM</button>
        <div className="spacer" style={{ flex: 1 }} />
        <div className="chip hide-mobile">
          <span className="k">LINK</span>
          <span className="v mono">{connected ? 'UP' : 'DOWN'}</span>
        </div>
        <div className="chip hide-mobile">
          <span className="k">PRE</span>
          <span className="v">{preampOn ? 'ON' : 'OFF'}</span>
        </div>
      </div>

      {disconnectedOverlay}
    </div>
  );
}

// Small hook hoisted to a name so the Hero chip reads the current bin width
// without blocking re-render of siblings that don't care about hzPerPixel.
import { useDisplayStore } from './state/display-store';
function useDisplayHzPerPixel(): string {
  const v = useDisplayStore((s) => s.hzPerPixel);
  if (!Number.isFinite(v) || v <= 0) return '—';
  return v >= 1 ? `${v.toFixed(1)} Hz` : `${(v * 1000).toFixed(0)} mHz`;
}

// QRZ XML gives us a sparser record than the design-time Contact type; fill
// the design-only fields ("local", "rig", "ant", "age"…) with em-dashes so
// QrzCard still renders without needing a schema change.
function qrzStationToContact(s: QrzStation | null, home: QrzStation | null): Contact | null {
  if (!s || s.lat == null || s.lon == null) return null;
  const bearing = home?.lat != null && home?.lon != null
    ? bearingDeg(home.lat, home.lon, s.lat, s.lon)
    : 0;
  const distance = home?.lat != null && home?.lon != null
    ? distanceKm(home.lat, home.lon, s.lat, s.lon)
    : 0;
  const first = (s.firstName || s.name || '').trim().charAt(0).toUpperCase();
  const last = (s.name || '').trim().split(/\s+/).pop()?.charAt(0).toUpperCase() ?? '';
  const initials = (first + last) || s.callsign.slice(0, 2);
  const location = [s.city, s.state, s.country].filter(Boolean).join(', ') || (s.country ?? '—');
  return {
    callsign: s.callsign,
    name: s.name ?? '—',
    location,
    grid: s.grid ?? '—',
    cq: s.cqZone != null ? String(s.cqZone).padStart(2, '0') : '—',
    itu: s.ituZone != null ? String(s.ituZone).padStart(2, '0') : '—',
    latlon: `${Math.abs(s.lat).toFixed(2)}°${s.lat >= 0 ? 'N' : 'S'} / ${Math.abs(s.lon).toFixed(2)}°${s.lon >= 0 ? 'E' : 'W'}`,
    lat: s.lat,
    lon: s.lon,
    local: '—',
    qsl: '—',
    licensed: '—',
    initials,
    flag: '',
    bearing,
    distance,
    age: 0,
    class: '—',
    rig: '—',
    ant: '—',
    power: '—',
    qth: s.city ?? s.country ?? '—',
    email: '—',
    photoUrl: s.imageUrl ?? undefined,
    qrzUrl: `https://www.qrz.com/db/${s.callsign}`,
  };
}
