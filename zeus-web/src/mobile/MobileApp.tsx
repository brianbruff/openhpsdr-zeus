// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Mobile single-column shell. Renders the same widgets and stores the
// desktop layout uses, in a vertical stack tuned for a touch viewport.
// The layout shape comes from the Zeus Mobile design hand-off; colours,
// type, and controls are the existing Zeus surface (tokens.css + the
// component library) per the maintainer's brief.

import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { setMode, type RxMode } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useQrzStore } from '../state/qrz-store';
import { useTxStore } from '../state/tx-store';
import { VfoDisplay } from '../components/VfoDisplay';
import { SMeterLive } from '../components/SMeterLive';
import { Panadapter } from '../components/Panadapter';
import { Waterfall } from '../components/Waterfall';
import { MobilePttButton } from '../components/MobilePttButton';
import { TunButton } from '../components/TunButton';
import { PsToggleButton } from '../components/PsToggleButton';
import { AudioToggle } from '../components/AudioToggle';
import { BandButtons } from '../components/BandButtons';
import { ConnectPanel } from '../components/ConnectPanel';
import { LeafletWorldMap } from '../components/design/LeafletWorldMap';
import { LeafletMapErrorBoundary } from '../components/design/LeafletMapErrorBoundary';
import { bandOf } from '../components/design/data';
import './mobile.css';

const MODES: readonly RxMode[] = ['LSB', 'USB', 'CWL', 'CWU', 'AM', 'FM', 'DIGU'];

const MOBILE_QUERY = '(max-width: 900px)';

// Reactive viewport check. `?mobile=1` forces the mobile shell on for desktop
// previews; everything else honours the matchMedia breakpoint and updates
// when the window is resized or the device rotates.
export function useIsMobileViewport(): boolean {
  const [mobile, setMobile] = useState<boolean>(() => {
    if (typeof window === 'undefined') return false;
    const params = new URLSearchParams(window.location.search);
    if (params.get('mobile') === '1') return true;
    return window.matchMedia(MOBILE_QUERY).matches;
  });

  useEffect(() => {
    if (typeof window === 'undefined') return;
    const params = new URLSearchParams(window.location.search);
    if (params.get('mobile') === '1') return; // forced — no listener needed
    const mq = window.matchMedia(MOBILE_QUERY);
    const onChange = (e: MediaQueryListEvent) => setMobile(e.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);

  return mobile;
}

export function MobileApp() {
  const status = useConnectionStore((s) => s.status);
  const endpoint = useConnectionStore((s) => s.endpoint);
  const lastEndpoint = useConnectionStore((s) => s.lastConnectedEndpoint);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const qrzHome = useQrzStore((s) => s.home);
  const qrzHasXml = useQrzStore((s) => s.hasXmlSubscription);
  const connected = status === 'Connected';

  const radioLabel = endpoint || lastEndpoint || '—';
  const bandLabel = bandOf(vfoHz);
  const freqMHz = (vfoHz / 1_000_000).toFixed(3);

  // Radio selector overlay. Open from the topbar; auto-close once a connect
  // *transition* completes (status flips Disconnected → Connected) so the
  // operator lands back on the radio screen without an extra dismiss tap.
  // Watching the edge — not the steady state — lets the operator open the
  // selector while already connected (to disconnect or switch radios)
  // without it slamming shut on first render.
  const [selectorOpen, setSelectorOpen] = useState(false);
  const prevConnectedRef = useRef(connected);
  useEffect(() => {
    if (selectorOpen && !prevConnectedRef.current && connected) {
      setSelectorOpen(false);
    }
    prevConnectedRef.current = connected;
  }, [selectorOpen, connected]);

  // QRZ map background — mirrors desktop's behaviour. The map renders behind
  // the spectrum stack when the operator has a QTH home pinned and an XML
  // subscription (same gate as App.tsx's qrzActive). LeafletWorldMap takes a
  // MapStation shape (call, not callsign), so we adapt the QrzStation here
  // the same way App.tsx:446-454 does.
  const effectiveHome =
    qrzHome && qrzHome.lat != null && qrzHome.lon != null && qrzHasXml
      ? {
          call: qrzHome.callsign,
          lat: qrzHome.lat,
          lon: qrzHome.lon,
          grid: qrzHome.grid ?? '',
          imageUrl: qrzHome.imageUrl ?? null,
        }
      : null;

  return (
    <div className="m-app">
      <header className="m-topbar">
        <div className="m-brand">
          <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden>
            <circle cx="12" cy="12" r="3" fill="var(--accent)" />
            <circle cx="12" cy="12" r="7" fill="none" stroke="var(--accent)" strokeWidth="1" opacity="0.5" />
            <circle cx="12" cy="12" r="11" fill="none" stroke="var(--accent)" strokeWidth="1" opacity="0.25" />
          </svg>
          <span className="m-brand-text">OpenHpsdr Zeus</span>
        </div>
        <button
          type="button"
          className="m-conn-btn"
          data-connected={connected}
          onClick={() => setSelectorOpen(true)}
          title={connected ? `Connected to ${radioLabel} — tap to manage` : 'Tap to discover radios'}
        >
          {connected ? (
            <>
              <span className="m-conn-led" aria-hidden />
              <span className="m-conn-v">{radioLabel}</span>
              <span className="m-conn-action">Disconnect</span>
            </>
          ) : (
            <>
              <span className="m-conn-led m-conn-led--off" aria-hidden />
              <span className="m-conn-action m-conn-action--primary">Connect</span>
            </>
          )}
        </button>
      </header>

      {selectorOpen && (
        <div
          className="m-selector-backdrop"
          role="dialog"
          aria-modal="true"
          aria-label="Radio selector"
          onClick={(e) => {
            // Backdrop click dismisses; clicks inside the sheet bubble through
            // to here too, so guard on currentTarget.
            if (e.target === e.currentTarget) setSelectorOpen(false);
          }}
        >
          <div className="m-selector-sheet">
            <header className="m-selector-head">
              <span className="m-selector-title">Radio</span>
              <button
                type="button"
                className="m-selector-close"
                onClick={() => setSelectorOpen(false)}
                aria-label="Close radio selector"
              >
                ✕
              </button>
            </header>
            <div className="m-selector-body">
              <ConnectPanel />
            </div>
          </div>
        </div>
      )}

      <main className="m-stack">
        <Section label="Frequency" meta="VFO A">
          <div className="m-vfo-wrap">
            <VfoDisplay />
          </div>
        </Section>

        <SMeterSection />


        <Section label="Panadapter" meta={`${freqMHz} MHz · ${bandLabel}`}>
          <div className="m-pan-stack">
            {effectiveHome && (
              // The "map-layer visible" pair pulls in the desktop sizing
              // chain in layout.css:451-470 — without it the inner Leaflet
              // .leaflet-container collapses to 0×0 and tiles never paint.
              // .m-map-layer still applies for any mobile-only tweaks.
              <div className="m-map-layer map-layer visible">
                <LeafletMapErrorBoundary fallback={null} onError={() => undefined}>
                  <LeafletWorldMap
                    home={effectiveHome}
                    target={null}
                    active
                    interactive={false}
                  />
                </LeafletMapErrorBoundary>
              </div>
            )}
            <div className="m-pan-spectrum">
              <div className="m-pan">
                <Panadapter />
              </div>
              <div className="m-wf">
                {/* Opaque on mobile even when QRZ is on. With a transparent
                    waterfall + dark Esri tiles + the colormap's near-black
                    noise floor, signal traces blended into the map and the
                    waterfall read as solid black. The map remains visible
                    behind the spectrum (top half), where it's primarily
                    contextual decoration. */}
                <Waterfall transparent={false} />
              </div>
            </div>
          </div>
        </Section>

        <div className="m-mox-block">
          <MicGate />
          <MobilePttButton />
          <div className="m-mox-row">
            <TunButton />
            <PsToggleButton />
            <AudioToggle />
          </div>
        </div>

        <Section label="Mode · Band">
          <div className="m-modeband">
            <Segmented
              options={MODES}
              value={mode}
              disabled={!connected}
              onChange={(m) => setMode(m).catch(() => undefined)}
            />
            <div className="m-band-row">
              <BandButtons />
            </div>
          </div>
        </Section>
      </main>
    </div>
  );
}

// Mic permission gate. useMicUplink() in App.tsx kicks getUserMedia at mount
// — that call falls outside any user gesture, and on iOS Safari over plain
// HTTP-on-LAN-IP the origin isn't a secure context either, so the call is
// silently rejected. Surfaces the error and offers an "Allow microphone"
// button that re-requests permission FROM the user gesture, where Safari
// will actually present the prompt. Reloads on success so the existing
// uplink hook picks up the granted permission cleanly.
function MicGate() {
  const micError = useTxStore((s) => s.micError);
  const [granting, setGranting] = useState(false);

  if (!micError) return null;

  // Non-secure-context detection. Localhost is treated as secure even over
  // HTTP, but a LAN IP like 192.168.x.y over plain HTTP fails this and is
  // unrecoverable without an HTTPS scheme.
  const insecure =
    typeof window !== 'undefined' && window.isSecureContext === false;

  const onAllow = async () => {
    setGranting(true);
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      // Tear the temp stream down — useMicUplink will open its own once we
      // reload. Holding it would tie up the mic.
      for (const t of stream.getTracks()) t.stop();
      useTxStore.getState().setMicError(null);
      // Reload so useMicUplink's mount-effect re-runs with permission
      // already granted; in-place retry would need plumbing the hook to
      // accept a reset token.
      window.location.reload();
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      useTxStore.getState().setMicError(msg);
    } finally {
      setGranting(false);
    }
  };

  return (
    <div className="m-mic-gate" role="alert">
      <div className="m-mic-gate__msg">
        <strong>Microphone unavailable.</strong>{' '}
        {insecure
          ? 'Mobile browsers require HTTPS for microphone access. The Zeus server prints an https:// LAN URL at startup — open that one on your phone instead. The first visit will warn that the certificate is self-signed; tap through to proceed.'
          : micError}
      </div>
      {!insecure && (
        <button
          type="button"
          className="m-mic-gate__btn"
          onClick={onAllow}
          disabled={granting}
        >
          {granting ? 'Requesting…' : 'Allow microphone'}
        </button>
      )}
    </div>
  );
}

// S-Meter card. Wrapped in its own component so subscribing to TX state
// for the in-header SWR + MIC chips doesn't re-render the whole MobileApp
// at the meter's update rate. During TX, the chips render in the section
// header (right-aligned) instead of below the meter — keeping the body
// height fixed so keying MOX doesn't push the PTT button down.
function SMeterSection() {
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const swr = useTxStore((s) => s.swr);
  const micDbfs = useTxStore((s) => s.micDbfs);
  const transmitting = moxOn || tunOn;
  const swrColor = swr >= 3 ? 'var(--tx)' : swr >= 2 ? 'var(--power)' : 'var(--fg-0)';

  const chips = transmitting ? (
    <>
      <span className="chip mono">
        <span className="k">SWR</span>
        <span className="v" style={{ color: swrColor }}>{swr.toFixed(2)}</span>
      </span>
      <span className="chip mono">
        <span className="k">MIC</span>
        <span className="v">{micDbfs.toFixed(0)} dBfs</span>
      </span>
    </>
  ) : null;

  return (
    <Section label="S-Meter" meta={transmitting ? 'TX' : 'RX'} extra={chips} tight>
      <SMeterLive hideChips />
    </Section>
  );
}

function Section({
  label,
  meta,
  extra,
  tight,
  children,
}: {
  label: string;
  meta?: string;
  /** Right-aligned slot in the section header. Used by the S-Meter card to
   *  surface SWR + MIC dBfs chips during TX without growing the body and
   *  shifting the PTT button below it. */
  extra?: ReactNode;
  /** Strip the body padding — used by the SMeter section so the meter
   *  fills the chrome edge to edge. */
  tight?: boolean;
  children: ReactNode;
}) {
  return (
    <section className={`m-section${tight ? ' m-section--tight' : ''}`}>
      <header className="m-section-head">
        <span className="m-section-led" />
        <span className="m-section-label">{label}</span>
        {meta && <span className="m-section-meta">· {meta}</span>}
        {extra && <span className="m-section-extra">{extra}</span>}
      </header>
      <div className="m-section-body">{children}</div>
    </section>
  );
}

function Segmented<T extends string>({
  options,
  value,
  disabled,
  onChange,
}: {
  options: readonly T[];
  value: T;
  disabled?: boolean;
  onChange: (v: T) => void;
}) {
  // Optimistic local highlight so the segment lights immediately even before
  // the StateDto echo lands; the prop value reasserts itself once the server
  // confirms (or contradicts) the choice.
  const [pending, setPending] = useState<T | null>(null);
  const active = pending ?? value;

  const handle = useCallback(
    (v: T) => {
      if (disabled) return;
      setPending(v);
      Promise.resolve(onChange(v)).finally(() => setPending(null));
    },
    [disabled, onChange],
  );

  return (
    <div className="m-segmented" role="radiogroup">
      {options.map((o) => {
        const on = o === active;
        return (
          <button
            key={o}
            type="button"
            role="radio"
            aria-checked={on}
            disabled={disabled}
            onClick={() => handle(o)}
            className={`m-seg-btn ${on ? 'on' : ''}`}
          >
            {o}
          </button>
        );
      })}
    </div>
  );
}
