import { useEffect, useRef } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { bearingDeg, destinationPoint, distanceKm, greatCircleSegments } from './geo';

type LeafletWorldMapProps = {
  home: { call: string; lat: number; lon: number };
  target: { call: string; lat: number; lon: number } | null;
  /** Beam bearing (deg, 0=N, CW). Defaults to initial great-circle bearing when target is set. */
  beamBearing?: number;
  /** Beam range in km — Log4YM uses 5000 km to reach across oceans. */
  beamRangeKm?: number;
  /** When true, arcs and markers are drawn; otherwise the map renders empty-ish. */
  active: boolean;
  /** When true, user can drag/zoom the map (zoom control appears). Off by
   *  default — the spectrum above owns pointer events for click-to-tune. */
  interactive?: boolean;
};

// Esri World Imagery — free satellite photo tiles, no API key. Dark oceans
// blend with the hero backdrop and continents carry enough colour to read
// clearly through the translucent spectrum/waterfall above. Matches the
// WebSDR-style reference the operator is used to.
const TILE_URL =
  'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
const TILE_ATTRIBUTION =
  'Tiles &copy; Esri &mdash; Source: Esri, i-cubed, USDA, USGS, AEX, GeoEye, Getmapping, Aerogrid, IGN, IGP, UPR-EGP, and the GIS User Community';

export function LeafletWorldMap({
  home,
  target,
  beamBearing,
  beamRangeKm = 5000,
  active,
  interactive = false,
}: LeafletWorldMapProps) {
  // Wrapper owns our dynamic className (`interactive`, aria-hidden). Leaflet
  // mounts into an inner div whose className we never touch, so the
  // `leaflet-container`/`leaflet-grab`/etc classes Leaflet writes directly to
  // the DOM survive React re-renders. Flattening the two into one element
  // makes React overwrite Leaflet's class additions on every prop change and
  // the tiles disappear after the first toggle.
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<L.Map | null>(null);
  const layerRef = useRef<L.LayerGroup | null>(null);
  const zoomCtrlRef = useRef<L.Control.Zoom | null>(null);

  // One-time init: Leaflet map, tile layer, attribution control in the corner.
  useEffect(() => {
    const el = containerRef.current;
    if (!el || mapRef.current) return;

    const map = L.map(el, {
      center: [30, -30],
      zoom: 2,
      minZoom: 2,
      maxZoom: 6,
      zoomControl: false,
      attributionControl: true,
      // Clamp to the world rectangle so the 'M'-modifier drag can't pan past
      // the ±85° tile cap or off the horizontal edge into empty space. Mercator
      // tiles don't exist above ~85° of latitude; viscosity 1 makes the pan
      // hit a hard wall rather than accelerate into the void.
      maxBounds: L.latLngBounds([-85, -180], [85, 180]),
      maxBoundsViscosity: 1.0,
      // Map is purely decorative background by default — the spectrum above
      // owns pointer events for click-to-tune. Handlers below are disabled at
      // init and the `interactive` effect enables them while 'M' is held.
      dragging: false,
      doubleClickZoom: false,
      scrollWheelZoom: false,
      boxZoom: false,
      keyboard: false,
      touchZoom: false,
      worldCopyJump: false,
      fadeAnimation: false,
      zoomAnimation: false,
    });

    L.tileLayer(TILE_URL, {
      attribution: TILE_ATTRIBUTION,
      maxZoom: 19,
    }).addTo(map);

    layerRef.current = L.layerGroup().addTo(map);
    mapRef.current = map;

    const ro = new ResizeObserver(() => map.invalidateSize());
    ro.observe(el);

    return () => {
      ro.disconnect();
      map.remove();
      mapRef.current = null;
      layerRef.current = null;
    };
  }, []);

  // Toggle pan/zoom handlers in response to the `interactive` prop. Keeping
  // the map mounted (rather than recreating it) preserves the current pan
  // position and tile cache across M-key toggles.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    const handlers = [
      map.dragging,
      map.scrollWheelZoom,
      map.doubleClickZoom,
      map.touchZoom,
      map.boxZoom,
      map.keyboard,
    ] as const;
    for (const h of handlers) {
      if (!h) continue;
      if (interactive) h.enable();
      else h.disable();
    }
    if (interactive && !zoomCtrlRef.current) {
      zoomCtrlRef.current = L.control.zoom({ position: 'topleft' }).addTo(map);
    } else if (!interactive && zoomCtrlRef.current) {
      zoomCtrlRef.current.remove();
      zoomCtrlRef.current = null;
    }
  }, [interactive]);

  // Redraw markers + arcs whenever props change. Cheap; runs on target swap.
  useEffect(() => {
    const map = mapRef.current;
    const layer = layerRef.current;
    if (!map || !layer) return;
    layer.clearLayers();
    if (!active) return;

    // Home marker — accent-blue filled circle with pulse halo.
    L.circleMarker([home.lat, home.lon], {
      radius: 6,
      color: '#ffffff',
      weight: 2,
      fillColor: '#4a9eff',
      fillOpacity: 1,
      opacity: 1,
    })
      .bindTooltip(home.call, { permanent: true, direction: 'right', className: 'lf-tt lf-tt-home' })
      .addTo(layer);

    if (target) {
      const dist = distanceKm(home.lat, home.lon, target.lat, target.lon);
      const bear = bearingDeg(home.lat, home.lon, target.lat, target.lon);

      // Great-circle path — amber dashed, antimeridian-safe.
      const segments = greatCircleSegments(
        { lat: home.lat, lon: home.lon },
        { lat: target.lat, lon: target.lon },
      );
      for (const seg of segments) {
        L.polyline(seg, {
          color: '#ffb432',
          weight: 2.5,
          opacity: 0.95,
          dashArray: '6, 10',
          lineCap: 'round',
        }).addTo(layer);
      }

      // Target marker — TX-red filled circle.
      L.circleMarker([target.lat, target.lon], {
        radius: 7,
        color: '#ffffff',
        weight: 2,
        fillColor: '#ff3838',
        fillOpacity: 1,
        opacity: 1,
      })
        .bindTooltip(
          `${target.call} · ${dist.toFixed(0)} km · brg ${bear.toFixed(0)}°`,
          { permanent: true, direction: 'left', className: 'lf-tt lf-tt-target' },
        )
        .addTo(layer);

      // Beam heading from home — static line toward the initial bearing of
      // the great-circle (when beamBearing is omitted). Cyan dashed, Log4YM
      // style. Beam is not wired to a rotator yet; this is just a visual cue.
      const beam = beamBearing ?? bear;
      const beamEnd = destinationPoint(home.lat, home.lon, beam, beamRangeKm);
      L.polyline(
        [
          [home.lat, home.lon],
          beamEnd,
        ],
        {
          color: '#00ddff',
          weight: 2.5,
          opacity: 0.8,
          dashArray: '10, 6',
          lineCap: 'round',
        },
      ).addTo(layer);
    }
  }, [home, target, beamBearing, beamRangeKm, active]);

  return (
    <div
      ref={wrapperRef}
      className={`leaflet-world-map${interactive ? ' interactive' : ''}`}
      aria-hidden={interactive ? undefined : true}
    >
      <div ref={containerRef} className="leaflet-host" />
    </div>
  );
}
