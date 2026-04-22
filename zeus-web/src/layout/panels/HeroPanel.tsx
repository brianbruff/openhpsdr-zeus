import { Panadapter } from '../../components/Panadapter';
import { Waterfall } from '../../components/Waterfall';
import { LeafletWorldMap } from '../../components/design/LeafletWorldMap';
import { LeafletMapErrorBoundary } from '../../components/design/LeafletMapErrorBoundary';
import { useConnectionStore } from '../../state/connection-store';
import { useRotatorStore } from '../../state/rotator-store';
import { useDisplayStore } from '../../state/display-store';
import { useWorkspace } from '../WorkspaceContext';

function useDisplayHzPerPixel(): string {
  const v = useDisplayStore((s) => s.hzPerPixel);
  if (!Number.isFinite(v) || v <= 0) return '—';
  return v >= 1 ? `${v.toFixed(1)} Hz` : `${(v * 1000).toFixed(0)} mHz`;
}

// Hero panel: Panadapter + Waterfall with optional Leaflet world-map overlay.
// Rendered inside a flexlayout tabset — the panel-head chrome is intentionally
// kept here for Phase 1 so beam controls stay accessible. Phase 2+ can move
// beam controls to flexlayout's onRenderTabSet header slot.
export function HeroPanel() {
  const {
    terminatorActive,
    moxOn,
    tunOn,
    contact,
    mapAvailable,
    setMapAvailable,
    mapInteractive,
    effectiveHome,
    beamOverrideDeg,
    setBeamOverrideDeg,
    beamInputStr,
    setBeamInputStr,
    rotLiveAz,
    sp,
    lp,
    heroTitle,
    submitBeam,
  } = useWorkspace();
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const hzPerPixel = useDisplayHzPerPixel();

  const handleRotateToBearing = (brg: number) => {
    const rot = useRotatorStore.getState();
    const normalized = ((brg % 360) + 360) % 360;
    setBeamOverrideDeg(normalized);
    setBeamInputStr(normalized.toFixed(0));
    if (rot.config.enabled && rot.status?.connected) {
      void rot.setAzimuth(normalized);
    }
  };

  return (
    <div
      className={`panel hero ${terminatorActive ? 'qrz-mode' : ''} ${mapInteractive ? 'map-mode' : ''}`}
      style={{ display: 'flex', flexDirection: 'column', height: '100%' }}
    >
      <div className="panel-head">
        <span className={`dot ${moxOn || tunOn ? 'tx' : 'on'}`} />
        <span className="title">{heroTitle}</span>
        <span className="spacer" style={{ flex: 1 }} />
        {terminatorActive && contact && mapAvailable && (
          <>
            <button
              type="button"
              className="chip mono"
              onClick={() => setBeamInputStr((((sp % 360) + 360) % 360).toFixed(0))}
            >
              <span className="k">SP</span>
              <span className="v">{sp.toFixed(0)}°</span>
            </button>
            <button
              type="button"
              className="chip mono"
              onClick={() => setBeamInputStr((((lp % 360) + 360) % 360).toFixed(0))}
            >
              <span className="k">LP</span>
              <span className="v">{lp.toFixed(0)}°</span>
            </button>
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
        {terminatorActive && mapAvailable && (
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
          <span className="v">{hzPerPixel}</span>
        </span>
      </div>
      <div className="panel-body hero-body" style={{ flex: 1, position: 'relative' }}>
        <div className={`map-layer ${terminatorActive ? 'visible' : ''}`}>
          <LeafletMapErrorBoundary
            onError={(error) => {
              console.warn('Leaflet map unavailable:', error.message);
              setMapAvailable(false);
            }}
            fallback={null}
          >
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
              beamBearing={rotLiveAz ?? beamOverrideDeg ?? undefined}
              active={terminatorActive}
              interactive={mapInteractive}
              onRotateToBearing={handleRotateToBearing}
            />
          </LeafletMapErrorBoundary>
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
  );
}

