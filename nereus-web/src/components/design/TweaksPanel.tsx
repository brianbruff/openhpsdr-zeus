type TweaksPanelProps = {
  variant: string;
  setVariant: (v: string) => void;
  fonts: string;
  setFonts: (v: string) => void;
  onClose: () => void;
};

const VARIANTS = [{ k: 'console', name: 'Console', desc: 'Cool dark, electric cyan' }];
const FONTS = [{ k: 'geist', name: 'Archivo Narrow' }];

export function TweaksPanel({ variant, setVariant, fonts, setFonts, onClose }: TweaksPanelProps) {
  return (
    <div className="tweaks-panel">
      <div className="tweaks-head">
        <span className="mono" style={{ fontSize: 11, letterSpacing: '0.14em' }}>
          TWEAKS
        </span>
        <span style={{ flex: 1 }} />
        <button type="button" className="btn ghost sm" onClick={onClose}>
          ×
        </button>
      </div>
      <div className="tweaks-body">
        <div className="tweaks-section">
          <div className="label-xs" style={{ marginBottom: 6 }}>
            VISUAL VARIANT
          </div>
          <div className="variant-grid">
            {VARIANTS.map((v) => (
              <button
                key={v.k}
                type="button"
                className={`variant-card ${variant === v.k ? 'active' : ''}`}
                data-variant={v.k}
                onClick={() => setVariant(v.k)}
              >
                <div className="variant-swatch">
                  <div className="sw bg0" />
                  <div className="sw bg1" />
                  <div className="sw accent" />
                  <div className="sw tx" />
                </div>
                <div className="variant-meta">
                  <div className="mono variant-name">{v.name}</div>
                  <div className="label-xs variant-desc">{v.desc}</div>
                </div>
              </button>
            ))}
          </div>
        </div>

        <div className="tweaks-section">
          <div className="label-xs" style={{ marginBottom: 6 }}>
            FONT PAIRING
          </div>
          <div className="font-list">
            {FONTS.map((f) => (
              <button
                key={f.k}
                type="button"
                className={`font-row ${fonts === f.k ? 'active' : ''}`}
                data-fonts={f.k}
                onClick={() => setFonts(f.k)}
              >
                <span className="mono font-name">{f.name}</span>
                <span className="mono font-preview">14.210.000 MHz · USB · S9+20</span>
              </button>
            ))}
          </div>
        </div>

        <div className="tweaks-section">
          <div className="label-xs" style={{ marginBottom: 6 }}>
            KEYBOARD
          </div>
          <div className="kb-hints">
            <div>
              <kbd>/</kbd> focus callsign lookup
            </div>
            <div>
              <kbd>Space</kbd> toggle TX
            </div>
            <div>
              <kbd>Scroll</kbd> on digit to tune
            </div>
            <div>
              <kbd>Drag</kbd> panel header to rearrange
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
