import { useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';

// Translucent rectangle drawn inside the panadapter container to show the
// active receive filter passband, mapped from [filterLowHz, filterHighHz]
// relative to the VFO centre. Asymmetric by design: USB lives to the right
// of carrier, LSB to the left, CW narrow around zero, AM symmetric.
// Positioned by percentage of the total span so it tracks resize and tune
// without measuring DOM width.
export function PassbandOverlay() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  const filterLowHz = useConnectionStore((s) => s.filterLowHz);
  const filterHighHz = useConnectionStore((s) => s.filterHighHz);

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const center = Number(centerHz);
  const startHz = center - spanHz / 2;

  const passLowHz = center + filterLowHz;
  const passHighHz = center + filterHighHz;
  const leftPct = ((passLowHz - startHz) / spanHz) * 100;
  const rightPct = ((passHighHz - startHz) / spanHz) * 100;
  const widthPct = rightPct - leftPct;

  if (widthPct <= 0 || leftPct > 100 || rightPct < 0) return null;

  return (
    <div
      aria-hidden
      className="pointer-events-none absolute inset-y-0 z-[5] bg-neutral-300/10 ring-1 ring-inset ring-neutral-300/25"
      style={{
        left: `${leftPct}%`,
        width: `${widthPct}%`,
      }}
    />
  );
}
