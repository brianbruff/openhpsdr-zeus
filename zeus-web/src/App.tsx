import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { AgcSlider } from './components/AgcSlider';
import { AlertBanner } from './components/AlertBanner';
import { AttenuatorSlider } from './components/AttenuatorSlider';
import { AudioToggle } from './components/AudioToggle';
import { ConnectPanel } from './components/ConnectPanel';
import { DriveSlider } from './components/DriveSlider';
import { MicGainSlider } from './components/MicGainSlider';
import { MicMeter } from './components/MicMeter';
import { ModeBandwidth } from './components/ModeBandwidth';
import { MoxButton } from './components/MoxButton';
import { Panadapter } from './components/Panadapter';
import { PreampButton } from './components/PreampButton';
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
import { TweaksPanel } from './components/design/TweaksPanel';
import { bearingDeg, distanceKm } from './components/design/geo';
import { LeafletWorldMap } from './components/design/LeafletWorldMap';
import { startRealtime } from './realtime/ws-client';
import { getAudioClient } from './audio/audio-client';
import { useMicUplink } from './audio/use-mic-uplink';
import { fetchState } from './api/client';
import { useConnectionStore } from './state/connection-store';
import { useTxStore } from './state/tx-store';
import { useKeyboardShortcuts } from './util/use-keyboard-shortcuts';

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

  // --- Variant / fonts (Tweaks panel) ---
  const [variant, setVariant] = useState<string>(
    () => localStorage.getItem('zeus.variant') || 'console',
  );
  const [fonts, setFonts] = useState<string>(
    () => localStorage.getItem('zeus.fonts') || 'geist',
  );
  const [tweaksOpen, setTweaksOpen] = useState(false);
  useEffect(() => {
    document.documentElement.setAttribute('data-variant', variant);
    localStorage.setItem('zeus.variant', variant);
  }, [variant]);
  useEffect(() => {
    document.documentElement.setAttribute('data-fonts', fonts);
    localStorage.setItem('zeus.fonts', fonts);
  }, [fonts]);

  // --- Design-mock state (QRZ, DSP grid toggles, CW WPM, memories) ---
  const [callsign, setCallsign] = useState('EI6LF');
  const [terminatorActive, setTerminatorActive] = useState(true);
  // While 'M' is held and QRZ is engaged, the spectrum canvas stack goes
  // pointer-events:none and the Leaflet map underneath takes drag/zoom input.
  // Click-to-tune is suspended for the duration of the modifier.
  const [mapModifier, setMapModifier] = useState(false);
  const [enriching, setEnriching] = useState(false);
  const [lookupKey, setLookupKey] = useState(0);
  const contact = CONTACTS[callsign.toUpperCase()] ?? null;

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
    setTimeout(() => setEnriching(false), 700);
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

  // --- Hero title
  const heroTitle = terminatorActive && contact ? (() => {
    const d = distanceKm(HOME.lat, HOME.lon, contact.lat, contact.lon);
    const b = bearingDeg(HOME.lat, HOME.lon, contact.lat, contact.lon);
    return (
      <>
        Panadapter · World Map ·{' '}
        <span style={{ color: 'var(--accent)' }}>{contact.callsign}</span> ·{' '}
        {Math.round(d).toLocaleString()} km · brg {b.toFixed(0)}°
      </>
    );
  })() : (
    <>Panadapter · {(vfoHz / 1e6).toFixed(3)} MHz · {bandLabel}</>
  );

  const disconnectedOverlay = useMemo(() => {
    if (connected) return null;
    return (
      <div
        style={{
          position: 'absolute',
          inset: 0,
          background: 'rgba(0,0,0,0.55)',
          backdropFilter: 'blur(4px)',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          zIndex: 200,
        }}
      >
        <ConnectPanel />
      </div>
    );
  }, [connected]);

  return (
    <div className="app" data-screen-label="01 Main Console" style={{ position: 'relative' }}>
      {/* Top bar */}
      <div className="topbar">
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
            <div className="brand-sub label-xs">HERMES LITE 2 · 0.1–54 MHz</div>
          </div>
        </div>

        <div className="topbar-divider" />

        <div className="vfo-group">
          <div className="vfo-tab active">
            <div className="vfo-label label-xs">VFO A</div>
            <div className="vfo-freq mono">{(vfoHz / 1e6).toFixed(3)}</div>
          </div>
        </div>

        <div className="topbar-divider" />

        <div className="status-chips">
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

        <ConnectPanel />

        <button
          type="button"
          className={`btn qrz-btn ${terminatorActive ? 'active' : ''}`}
          onClick={() => (terminatorActive ? disengageTerminator() : engageTerminator())}
        >
          <span className="led on" style={{ marginRight: 6 }} />
          {terminatorActive ? 'QRZ ENGAGED' : 'Engage QRZ'}
        </button>
        <button type="button" className="btn ghost" onClick={() => setTweaksOpen((o) => !o)}>
          ⚙
        </button>
      </div>

      {/* Control strip — real wired controls rebuilt into the design's chassis */}
      <div className="control-strip">
        <ModeBandwidth />
        <div className="ctrl-group" style={{ minWidth: 220 }}>
          <div className="label-xs ctrl-lbl">FRONT-END</div>
          <div className="btn-row" style={{ gap: 6, alignItems: 'center' }}>
            <PreampButton />
            <AttenuatorSlider />
          </div>
        </div>
        <div className="ctrl-group" style={{ minWidth: 180 }}>
          <div className="label-xs ctrl-lbl">AGC</div>
          <AgcSlider />
        </div>
        <div className="spacer" style={{ flex: 1 }} />
        <div className="ctrl-group" style={{ minWidth: 360 }}>
          <div className="label-xs ctrl-lbl">ZOOM · DRIVE · MIC</div>
          <div className="btn-row" style={{ gap: 10 }}>
            <ZoomControl />
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
            {terminatorActive && contact && (() => {
              const b = bearingDeg(HOME.lat, HOME.lon, contact.lat, contact.lon);
              const lp = (360 - b) % 360;
              return (
                <>
                  <span className="chip mono">
                    <span className="k">LP</span>
                    <span className="v">{lp.toFixed(0)}°</span>
                  </span>
                  <span className="chip mono">
                    <span className="k">LOCAL</span>
                    <span className="v">{contact.local}</span>
                  </span>
                </>
              );
            })()}
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
                home={{ call: HOME.call, lat: HOME.lat, lon: HOME.lon }}
                target={
                  contact ? { call: contact.callsign, lat: contact.lat, lon: contact.lon } : null
                }
                active={terminatorActive}
                interactive={mapInteractive}
              />
              {terminatorActive && contact && (() => {
                const dist = distanceKm(HOME.lat, HOME.lon, contact.lat, contact.lon);
                const brg = bearingDeg(HOME.lat, HOME.lon, contact.lat, contact.lon);
                const lp = (360 - brg) % 360;
                return (
                  <div className="map-readout">
                    <div className="mr-row">
                      <span className="mr-k">FROM</span>
                      <span className="mr-v accent">{HOME.call}</span>
                      <span className="mr-sub">{HOME.grid} · St Louis MO</span>
                    </div>
                    <div className="mr-divider" />
                    <div className="mr-row">
                      <span className="mr-k">TO</span>
                      <span className="mr-v tx">{contact.callsign}</span>
                      <span className="mr-sub">
                        {contact.grid} · {contact.location}
                      </span>
                    </div>
                    <div className="mr-row">
                      <span className="mr-k">DIST</span>
                      <span className="mr-v mono">
                        {Math.round(dist).toLocaleString()} km
                      </span>
                      <span className="mr-sub">
                        {Math.round(dist * 0.621).toLocaleString()} mi
                      </span>
                    </div>
                    <div className="mr-row">
                      <span className="mr-k">BRG</span>
                      <span className="mr-v mono">{brg.toFixed(0)}°</span>
                      <span className="mr-sub">SP / LP {lp.toFixed(0)}°</span>
                    </div>
                    <div className="mr-row">
                      <span className="mr-k">LOCAL</span>
                      <span className="mr-v mono">{contact.local}</span>
                    </div>
                  </div>
                );
              })()}
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
            <div className="side-slot grow">
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
            <div className="side-slot">
              <Dockable title="Great-Circle Map" ledOn={!!contact}>
                <AzimuthMap target={contact} myGrid="EM48" />
              </Dockable>
            </div>
          )}

          <div className="side-slot">
            <Dockable title="DSP" ledOn={dspActive}>
              <DspPanel />
            </Dockable>
          </div>

          <div className="side-slot">
            <Dockable title={`CW Keyer · ${wpm} WPM`} ledOn={mode === 'CWU' || mode === 'CWL'}>
              <CwKeyer wpm={wpm} setWpm={setWpm} />
            </Dockable>
          </div>
        </div>

        {/* Bottom row — Logbook + Memory (mock, read-only) */}
        <div className="bottom-row">
          <div className="bottom-slot">
            <Dockable title="Logbook" ledOn>
              <Logbook onPick={onLogPick} />
            </Dockable>
          </div>
          <div className="bottom-slot">
            <Dockable title="TX Stage Meters" ledOn={moxOn || tunOn}>
              <TxStageMeters />
            </Dockable>
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
        <div className="transport-sep" />
        <button type="button" className="btn ghost">SPLIT</button>
        <button type="button" className="btn ghost">RIT</button>
        <button type="button" className="btn ghost">SAVE MEM</button>
        <div className="spacer" style={{ flex: 1 }} />
        <div className="chip">
          <span className="k">LINK</span>
          <span className="v mono">{connected ? 'UP' : 'DOWN'}</span>
        </div>
        <div className="chip">
          <span className="k">PRE</span>
          <span className="v">{preampOn ? 'ON' : 'OFF'}</span>
        </div>
      </div>

      {tweaksOpen && (
        <TweaksPanel
          variant={variant}
          setVariant={setVariant}
          fonts={fonts}
          setFonts={setFonts}
          onClose={() => setTweaksOpen(false)}
        />
      )}
      {!tweaksOpen && (
        <button
          type="button"
          className="tweaks-fab btn"
          onClick={() => setTweaksOpen(true)}
        >
          Tweaks
        </button>
      )}

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
