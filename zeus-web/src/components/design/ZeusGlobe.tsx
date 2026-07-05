// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { useEffect, useMemo, useRef, useState, type PointerEvent, type WheelEvent } from 'react';
import type { LogEntry } from '../../api/log';
import { useOperatorStore } from '../../state/operator-store';
import { useQrzStore } from '../../state/qrz-store';
import { bearingDeg, destinationPoint, distanceKm, gridToLatLon } from './geo';
import { geoPoints, type GeoPoint } from './logbook-stats';
import { WORLD_LAND_RINGS } from './world-land-110m';
import {
  clampView,
  clipVisibleSegments,
  easeInOutCubic,
  frameViewForPoints,
  greatCircleArc,
  interpolateView,
  projectOrthographic,
  solarSubpoint,
  type GlobeView,
  type LonLat,
} from './globe-math';

type Props = {
  entries: LogEntry[];
};

const SIGNAL_AMBER = '#FFA028';

type GlobeTarget = LonLat & { callsign: string };
type WorkedRoute = { key: string; count: number; arc: LonLat[] };
type GlobeData = {
  workedRoutes: WorkedRoute[];
  points: GeoPoint[];
  targetArc: LonLat[] | null;
  target: GlobeTarget | null;
  home: LonLat | null;
  maxCount: number;
};

function ringToLonLat(ring: number[]): LonLat[] {
  const out: LonLat[] = [];
  for (let i = 0; i < ring.length - 1; i += 2)
    out.push({ lon: ring[i]!, lat: ring[i + 1]! });
  return out;
}

const LAND_RINGS = WORLD_LAND_RINGS.map(ringToLonLat);

function drawProjectedPath(
  ctx: CanvasRenderingContext2D,
  points: LonLat[],
  view: GlobeView,
  cx: number,
  cy: number,
  r: number,
) {
  let moved = false;
  for (const p of points) {
    const projected = projectOrthographic(p, view);
    if (!projected.visible) {
      moved = false;
      continue;
    }
    const x = cx + projected.x * r * view.zoom;
    const y = cy - projected.y * r * view.zoom;
    if (!moved) {
      ctx.moveTo(x, y);
      moved = true;
    } else {
      ctx.lineTo(x, y);
    }
  }
}

function token(name: string): string {
  if (typeof document === 'undefined') return 'CanvasText';
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || 'CanvasText';
}

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined'
    && Boolean(window.matchMedia?.('(prefers-reduced-motion: reduce)').matches);
}

export function ZeusGlobe({ entries }: Props) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<GlobeView>({ lon: -35, lat: 25, zoom: 1 });
  const velocityRef = useRef({ lon: 0, lat: 0 });
  const dragRef = useRef<{ x: number; y: number; view: GlobeView; t: number } | null>(null);
  const rafRef = useRef<number | null>(null);
  const minuteRef = useRef<number | null>(null);
  const reducedMotionRef = useRef(prefersReducedMotion());
  const dataRef = useRef<GlobeData>({
    workedRoutes: [],
    points: [],
    targetArc: null,
    target: null,
    home: null,
    maxCount: 1,
  });
  const animationRef = useRef<{
    start: number;
    from: GlobeView;
    to: GlobeView;
    duration: number;
    arcUntil: number;
  } | null>(null);
  const [readout, setReadout] = useState<string>('');

  const points = useMemo(() => geoPoints(entries), [entries]);
  const qrzHomeLat = useQrzStore((s) => s.home?.lat ?? null);
  const qrzHomeLon = useQrzStore((s) => s.home?.lon ?? null);
  const lastLookupLat = useQrzStore((s) => s.lastLookup?.lat ?? null);
  const lastLookupLon = useQrzStore((s) => s.lastLookup?.lon ?? null);
  const lastLookupCallsign = useQrzStore((s) => s.lastLookup?.callsign ?? null);
  const resolvedGrid = useOperatorStore((s) => s.resolvedGrid);
  const home = useMemo<LonLat | null>(() => {
    if (qrzHomeLat != null && qrzHomeLon != null) return { lat: qrzHomeLat, lon: qrzHomeLon };
    const ll = resolvedGrid ? gridToLatLon(resolvedGrid) : null;
    return ll ? { lat: ll.lat, lon: ll.lon } : null;
  }, [qrzHomeLat, qrzHomeLon, resolvedGrid]);
  const target = useMemo<GlobeTarget | null>(
    () => lastLookupLat != null && lastLookupLon != null
      ? { lat: lastLookupLat, lon: lastLookupLon, callsign: lastLookupCallsign ?? '' }
      : null,
    [lastLookupLat, lastLookupLon, lastLookupCallsign],
  );
  const workedRoutes = useMemo(() => {
    if (!home) return [];
    return points
      .map((point) => {
        const km = distanceKm(home.lat, home.lon, point.lat, point.lon);
        if (km < 5) return null;
        return {
          key: `${Math.round(point.lat * 10)}:${Math.round(point.lon * 10)}`,
          count: point.count,
          arc: greatCircleArc(home, { lat: point.lat, lon: point.lon }, 72),
        };
      })
      .filter((route): route is WorkedRoute => route != null);
  }, [home, points]);
  const targetArc = useMemo(
    () => (home && target ? greatCircleArc(home, target, 96) : null),
    [home, target?.lat, target?.lon],
  );
  const maxCount = useMemo(() => points.reduce((m, p) => Math.max(m, p.count), 1), [points]);

  dataRef.current = { workedRoutes, points, targetArc, target, home, maxCount };

  const schedule = () => {
    if (typeof requestAnimationFrame === 'undefined') return;
    if (rafRef.current != null) return;
    rafRef.current = requestAnimationFrame(draw);
  };

  const draw = (now: number) => {
    rafRef.current = null;
    const canvas = canvasRef.current;
    const root = rootRef.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !root || !ctx) return;

    const rect = root.getBoundingClientRect();
    const dpr = Math.max(1, Math.min(3, window.devicePixelRatio || 1));
    const width = Math.max(1, Math.floor(rect.width * dpr));
    const height = Math.max(1, Math.floor(rect.height * dpr));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, rect.width, rect.height);

    const size = Math.min(rect.width, rect.height);
    const cx = rect.width / 2;
    const cy = rect.height / 2;
    const r = size * 0.42;
    const fg2 = token('--fg-2');
    const fg3 = token('--fg-3');
    const bg0 = token('--bg-0');
    const oceanDeep = token('--bg-inset');
    const oceanLit = token('--accent-bright');
    const landFill = token('--ok');
    const landHighlight = token('--power');
    const accent = token('--accent');
    const reducedMotion = reducedMotionRef.current;
    const data = dataRef.current;
    const { workedRoutes, points, targetArc, target, home, maxCount } = data;
    let view = viewRef.current;
    let keepAnimating = false;
    let arcProgress = 1;
    const anim = animationRef.current;
    if (anim) {
      const t = Math.min(1, (now - anim.start) / anim.duration);
      viewRef.current = interpolateView(anim.from, anim.to, easeInOutCubic(t));
      view = viewRef.current;
      arcProgress = Math.min(1, (now - anim.start) / anim.arcUntil);
      keepAnimating = t < 1 || arcProgress < 1;
      if (!keepAnimating) animationRef.current = null;
    }

    const sphere = ctx.createRadialGradient(cx - r * 0.35, cy - r * 0.45, r * 0.15, cx, cy, r * 1.15);
    sphere.addColorStop(0, oceanLit);
    sphere.addColorStop(0.5, accent);
    sphere.addColorStop(1, oceanDeep);
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.fillStyle = sphere;
    ctx.fill();
    ctx.save();
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.clip();

    ctx.globalAlpha = 0.22;
    ctx.strokeStyle = fg3;
    ctx.lineWidth = 0.6;
    for (let lon = -180; lon <= 180; lon += 30) {
      const line: LonLat[] = [];
      for (let lat = -90; lat <= 90; lat += 3) line.push({ lon, lat });
      ctx.beginPath();
      drawProjectedPath(ctx, line, view, cx, cy, r);
      ctx.stroke();
    }
    for (let lat = -60; lat <= 60; lat += 30) {
      const line: LonLat[] = [];
      for (let lon = -180; lon <= 180; lon += 3) line.push({ lon, lat });
      ctx.beginPath();
      drawProjectedPath(ctx, line, view, cx, cy, r);
      ctx.stroke();
    }
    ctx.globalAlpha = 1;

    ctx.strokeStyle = fg2;
    ctx.lineWidth = 0.7;
    for (const ring of LAND_RINGS) {
      for (const segment of clipVisibleSegments(ring, view)) {
        if (segment.length < 2) continue;
        ctx.beginPath();
        drawProjectedPath(ctx, segment, view, cx, cy, r);
        ctx.closePath();
        ctx.globalAlpha = 0.46;
        ctx.fillStyle = landFill;
        ctx.fill();
        ctx.globalAlpha = 0.16;
        ctx.fillStyle = landHighlight;
        ctx.fill();
        ctx.globalAlpha = 0.55;
        ctx.stroke();
      }
    }
    ctx.globalAlpha = 1;

    const sun = solarSubpoint(new Date());
    const antiSun = { lat: -sun.lat, lon: sun.lon + 180 };
    const shade = projectOrthographic(antiSun, view);
    ctx.globalAlpha = 0.34;
    ctx.fillStyle = bg0;
    const shadeGradient = ctx.createRadialGradient(
      cx + shade.x * r * view.zoom,
      cy - shade.y * r * view.zoom,
      r * 0.08,
      cx + shade.x * r * view.zoom,
      cy - shade.y * r * view.zoom,
      r * 1.3,
    );
    shadeGradient.addColorStop(0, bg0);
    shadeGradient.addColorStop(1, 'transparent');
    ctx.fillStyle = shadeGradient;
    ctx.fillRect(cx - r, cy - r, r * 2, r * 2);
    ctx.globalAlpha = 0.55;
    ctx.strokeStyle = accent;
    ctx.lineWidth = 0.8;
    const terminator: LonLat[] = [];
    for (let brg = 0; brg <= 360; brg += 3) {
      const [lat, lon] = destinationPoint(sun.lat, sun.lon, brg, 10007.5);
      terminator.push({ lat, lon });
    }
    ctx.beginPath();
    drawProjectedPath(ctx, terminator, view, cx, cy, r);
    ctx.stroke();
    ctx.globalAlpha = 1;

    if (home && workedRoutes.length > 0) {
      const routeGradient = ctx.createLinearGradient(cx - r, cy - r * 0.25, cx + r, cy + r * 0.35);
      routeGradient.addColorStop(0, SIGNAL_AMBER);
      routeGradient.addColorStop(1, accent);
      ctx.strokeStyle = routeGradient;
      ctx.shadowColor = SIGNAL_AMBER;
      ctx.shadowBlur = 5;
      ctx.lineWidth = 1.15;
      for (const route of workedRoutes) {
        ctx.globalAlpha = 0.16 + Math.min(1, route.count / maxCount) * 0.14;
        ctx.beginPath();
        drawProjectedPath(ctx, route.arc, view, cx, cy, r);
        ctx.stroke();
      }
      ctx.shadowBlur = 0;
      ctx.globalAlpha = 1;
    }

    ctx.fillStyle = SIGNAL_AMBER;
    ctx.shadowColor = SIGNAL_AMBER;
    ctx.shadowBlur = 12;
    for (const p of points) {
      const projected = projectOrthographic({ lat: p.lat, lon: p.lon }, view);
      if (!projected.visible) continue;
      const weight = p.count / maxCount;
      ctx.globalAlpha = 0.45 + weight * 0.55;
      ctx.beginPath();
      ctx.arc(cx + projected.x * r * view.zoom, cy - projected.y * r * view.zoom, 2 + weight * 4, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.shadowBlur = 0;
    ctx.globalAlpha = 1;

    if (home) {
      const p = projectOrthographic(home, view);
      if (p.visible) {
        const pulse = reducedMotion ? 1 : 1 + Math.sin(now / 500) * 0.18;
        ctx.strokeStyle = accent;
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(cx + p.x * r * view.zoom, cy - p.y * r * view.zoom, 7 * pulse, 0, Math.PI * 2);
        ctx.stroke();
      }
    }

    if (home && target && targetArc) {
      const drawCount = Math.max(2, Math.floor(targetArc.length * arcProgress));
      ctx.strokeStyle = SIGNAL_AMBER;
      ctx.shadowColor = SIGNAL_AMBER;
      ctx.shadowBlur = 14;
      ctx.lineWidth = 2;
      ctx.beginPath();
      drawProjectedPath(ctx, targetArc.slice(0, drawCount), view, cx, cy, r);
      ctx.stroke();
      const head = targetArc[Math.min(drawCount - 1, targetArc.length - 1)];
      if (head) {
        const hp = projectOrthographic(head, view);
        if (hp.visible) {
          ctx.fillStyle = SIGNAL_AMBER;
          ctx.beginPath();
          ctx.arc(cx + hp.x * r * view.zoom, cy - hp.y * r * view.zoom, 4.5, 0, Math.PI * 2);
          ctx.fill();
        }
      }
      ctx.shadowBlur = 0;
      const tp = projectOrthographic(target, view);
      if (tp.visible) {
        ctx.strokeStyle = SIGNAL_AMBER;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(cx + tp.x * r * view.zoom, cy - tp.y * r * view.zoom, 10 + (reducedMotion ? 0 : Math.sin(now / 450) * 2), 0, Math.PI * 2);
        ctx.stroke();
        ctx.fillStyle = token('--fg-0');
        ctx.font = '600 12px Archivo Narrow, sans-serif';
        ctx.fillText(target.callsign, cx + tp.x * r * view.zoom + 12, cy - tp.y * r * view.zoom - 8);
      }
    }

    ctx.restore();
    ctx.strokeStyle = fg2;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.stroke();

    if (reducedMotion) {
      velocityRef.current = { lon: 0, lat: 0 };
    } else if (velocityRef.current.lon || velocityRef.current.lat) {
      viewRef.current = clampView({
        ...viewRef.current,
        lon: viewRef.current.lon + velocityRef.current.lon,
        lat: viewRef.current.lat + velocityRef.current.lat,
      });
      velocityRef.current.lon *= 0.94;
      velocityRef.current.lat *= 0.94;
      if (Math.abs(velocityRef.current.lon) < 0.005) velocityRef.current.lon = 0;
      if (Math.abs(velocityRef.current.lat) < 0.005) velocityRef.current.lat = 0;
      keepAnimating = true;
    }

    if (keepAnimating) schedule();
  };

  useEffect(() => () => {
    if (rafRef.current != null && typeof cancelAnimationFrame !== 'undefined') {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
  }, []);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
    const update = (event?: MediaQueryListEvent) => {
      reducedMotionRef.current = event?.matches ?? mq.matches;
      schedule();
    };
    update();
    if (typeof mq.addEventListener === 'function') {
      mq.addEventListener('change', update);
      return () => mq.removeEventListener('change', update);
    }
    mq.addListener(update);
    return () => mq.removeListener(update);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const root = rootRef.current;
    if (!root || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(schedule);
    ro.observe(root);
    schedule();
    return () => {
      ro.disconnect();
      if (rafRef.current != null && typeof cancelAnimationFrame !== 'undefined') {
        cancelAnimationFrame(rafRef.current);
        rafRef.current = null;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    schedule();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workedRoutes, targetArc]);

  useEffect(() => {
    if (minuteRef.current != null) window.clearInterval(minuteRef.current);
    minuteRef.current = window.setInterval(schedule, 60_000);
    return () => {
      if (minuteRef.current != null) window.clearInterval(minuteRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!home || !target) return;
    const next = frameViewForPoints([home, target], viewRef.current);
    if (reducedMotionRef.current) {
      viewRef.current = next;
      animationRef.current = null;
    } else {
      animationRef.current = {
        start: performance.now(),
        from: viewRef.current,
        to: next,
        duration: 1400,
        arcUntil: 1900,
      };
    }
    const km = distanceKm(home.lat, home.lon, target.lat, target.lon);
    const deg = bearingDeg(home.lat, home.lon, target.lat, target.lon);
    setReadout(`${Math.round(km).toLocaleString()} km · ${Math.round(deg)}°`);
    schedule();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [home?.lat, home?.lon, target?.lat, target?.lon, target?.callsign]);

  const onPointerDown = (event: PointerEvent<HTMLCanvasElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    dragRef.current = { x: event.clientX, y: event.clientY, view: viewRef.current, t: performance.now() };
    velocityRef.current = { lon: 0, lat: 0 };
  };

  const onPointerMove = (event: PointerEvent<HTMLCanvasElement>) => {
    const drag = dragRef.current;
    if (!drag) return;
    const dx = event.clientX - drag.x;
    const dy = event.clientY - drag.y;
    const next = clampView({
      ...drag.view,
      lon: drag.view.lon - dx * 0.35 / drag.view.zoom,
      lat: drag.view.lat + dy * 0.25 / drag.view.zoom,
    });
    if (reducedMotionRef.current) {
      velocityRef.current = { lon: 0, lat: 0 };
    } else {
      const dt = Math.max(16, performance.now() - drag.t);
      velocityRef.current = {
        lon: (next.lon - viewRef.current.lon) / (dt / 16),
        lat: (next.lat - viewRef.current.lat) / (dt / 16),
      };
    }
    viewRef.current = next;
    schedule();
  };

  const onPointerUp = (event: PointerEvent<HTMLCanvasElement>) => {
    event.currentTarget.releasePointerCapture(event.pointerId);
    dragRef.current = null;
    schedule();
  };

  const onWheel = (event: WheelEvent<HTMLCanvasElement>) => {
    event.preventDefault();
    viewRef.current = clampView({
      ...viewRef.current,
      zoom: viewRef.current.zoom * (event.deltaY < 0 ? 1.08 : 0.92),
    });
    schedule();
  };

  const onDoubleClick = () => {
    if (!home) return;
    viewRef.current = frameViewForPoints([home], { ...viewRef.current, lon: home.lon, lat: home.lat });
    schedule();
  };

  return (
    <div ref={rootRef} className="zeus-globe">
      <canvas
        ref={canvasRef}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onWheel={onWheel}
        onDoubleClick={onDoubleClick}
        aria-label="Interactive logbook globe"
      />
      <div className="zeus-globe-readout mono">
        {target ? `${target.callsign} · ${readout}` : points.length === 0 ? 'No gridded QSOs yet' : `${points.length} worked locations`}
      </div>
    </div>
  );
}
