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
          <span className="m-brand-text">ZEUS</span>
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

        <Section label="S-Meter" meta="RX" tight>
          <SMeterLive />
        </Section>

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

function Section({
  label,
  meta,
  tight,
  children,
}: {
  label: string;
  meta?: string;
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
