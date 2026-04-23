# Greyline and Day/Night Overlay — Antimeridian Handling

## Problem

When rendering solar terminator lines and night polygons on a Leaflet world map, naive polyline/polygon drawing breaks at the ±180° antimeridian. Leaflet interprets a longitude jump from +179° to −179° as a request to draw a line wrapping the long way around the globe (through the Pacific instead of a short arc), producing visual artifacts—a spurious line spanning the entire map width.

## Solution

The greyline and day/night overlay components use **triplication** and **longitude unwrapping** to ensure continuous rendering across the antimeridian. The approach is inherited from Log4YM and adapted to Zeus's Leaflet integration.

### Triplication

Each polyline or polygon is rendered **three times**, offset by −360°, 0°, and +360° longitude:

```typescript
// zeus-web/src/utils/geoUtils.ts
export function triplicatePath<T extends { lon: number }>(
  points: T[],
): Array<Array<{ lat: number; lon: number }>> {
  const paths: Array<Array<{ lat: number; lon: number }>> = [];
  for (const offset of [-360, 0, 360]) {
    paths.push(
      points.map((p) => ({
        lat: 'lat' in p ? (p.lat as number) : 0,
        lon: p.lon + offset,
      })),
    );
  }
  return paths;
}
```

Leaflet's viewport clipping automatically selects the copy(s) currently visible, so panning the map across the antimeridian shows a seamless continuation of the overlay. For example:
- When centered on 0° longitude, the 0° copy renders.
- When panned to view −180° (left edge), the −360° copy becomes visible.
- When panned to view +180° (right edge), the +360° copy becomes visible.

### Longitude Unwrapping (Used in Other Contexts)

For paths that need to be drawn as a single continuous polyline (e.g., great-circle arcs in `LeafletWorldMap.tsx`), the `unwrapLongitudes` helper detects antimeridian crossings and adjusts subsequent longitudes by ±360° to avoid the wrap:

```typescript
// zeus-web/src/utils/geoUtils.ts
export function unwrapLongitudes(lons: number[]): number[] {
  if (lons.length === 0) return [];
  const unwrapped = [lons[0]!];
  let offset = 0;
  for (let i = 1; i < lons.length; i++) {
    const prev = lons[i - 1]!;
    const curr = lons[i]!;
    const diff = curr - prev;
    if (diff > 180) offset -= 360;
    else if (diff < -180) offset += 360;
    unwrapped.push(curr + offset);
  }
  return unwrapped;
}
```

This transforms `[+170, +175, −175, −170]` → `[+170, +175, +185, +190]`, so Leaflet draws a short arc instead of a wraparound line.

## Why Not Use `worldCopyJump`?

Leaflet's `worldCopyJump` option handles panning continuity for tile layers but does **not** automatically replicate vector overlays (polylines, polygons) across world copies. Manual triplication is the standard workaround for custom overlays, as documented in Leaflet discussions and used in projects like Log4YM.

## Components Using This Pattern

1. **`DayNightOverlay.tsx`** (`zeus-web/src/components/design/`)
   - Renders a night-side polygon (terminator + pole detour).
   - Terminator polyline rendered as a dashed amber line.
   - Both use `triplicatePath` to ensure continuity.

2. **`GrayLineOverlay.tsx`** (`zeus-web/src/components/design/`)
   - Renders the solar terminator (altitude = 0°).
   - Enhanced DX zone polygon (between +5° and −5° solar altitude).
   - Optional civil/nautical/astronomical twilight lines.
   - All paths use `triplicatePath`.

3. **`LeafletWorldMap.tsx`** (great-circle arcs)
   - Beam and target arcs use `greatCircleSegments` from `geo.ts`, which splits paths at antimeridian crossings into separate polyline segments (not triplication, but conceptually related).

## Testing Notes

To verify antimeridian handling:
1. Enable the greyline or day/night overlay via the HeroPanel toggles (GREY / D/N).
2. Pan the map to view the Pacific region where the terminator crosses ±180° longitude.
3. The overlay should render seamlessly without spurious wrap lines or gaps.

If a visual artifact appears (e.g., a straight line spanning the map horizontally), suspect:
- Missing triplication in a newly added polyline/polygon.
- Polygon stroke not set to `transparent` (stroke width > 0 can create seam lines at ±180°).

## References

- Log4YM `GrayLineOverlay.tsx` / `DayNightOverlay.tsx` — original implementation by EI6LF
- Leaflet GitHub discussions on antimeridian handling (e.g., [#1717](https://github.com/Leaflet/Leaflet/issues/1717))
- Zeus `geo.ts` — `greatCircleSegments` function for splitting arcs at the antimeridian
