// Vertical S-unit bar array matching the reference image: 12 bars with S-labels below.
// Driven by a simulated receive level in dBm so the visual matches the design even
// when the real backend isn't pushing an S-meter value. Real dBm readings can be
// dropped in as the `dbm` prop when SMeterLive is rewired into this panel later.

const LABELS = ['1', '3', '5', '7', '9', '+10', '+20', '+30', '+40', '+50', '+60'];

export function SMeterBars({ dbm }: { dbm: number }) {
  // Map -127 dBm → S0 .. -73 dBm → S9 → +60 over (-13 dBm).
  // 6 dB per S-unit below S9, 10 dB per step above S9.
  const sUnits = (() => {
    if (dbm <= -127) return 0;
    if (dbm <= -73) return (dbm + 127) / 6; // 0..9
    return 9 + (dbm + 73) / 10; // 9..~15
  })();

  return (
    <div className="smeter">
      <div className="smeter-scale">
        {Array.from({ length: 12 }, (_, i) => {
          const lit = sUnits >= i + 0.5;
          const over = i >= 9;
          return (
            <div key={i} className={`smeter-s ${lit ? 'lit' : ''} ${over ? 'over' : ''}`}>
              <div className="smeter-bar" />
              <div className="smeter-lbl">{LABELS[i] ?? ''}</div>
            </div>
          );
        })}
      </div>
      <div className="smeter-foot">
        <span className="label-xs">S-METER</span>
        <span className="smeter-val mono">
          {dbm.toFixed(0)}
          <span className="unit"> dBm</span>
        </span>
      </div>
    </div>
  );
}
