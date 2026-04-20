import { Slider } from './Slider';

type CwKeyerProps = {
  wpm: number;
  setWpm: (v: number) => void;
  onSend?: (macro: string) => void;
};

const MACROS = ['CQ CQ CQ', 'TU 73', 'QRZ?', 'AGN?', '5NN TU', 'UR RST'];

export function CwKeyer({ wpm, setWpm, onSend }: CwKeyerProps) {
  return (
    <div className="cw">
      <div className="cw-row">
        <Slider label="WPM" value={wpm} onChange={setWpm} min={5} max={40} />
      </div>
      <div className="cw-macros">
        {MACROS.map((m) => (
          <button key={m} type="button" className="btn sm" onClick={() => onSend?.(m)}>
            {m}
          </button>
        ))}
      </div>
      <div className="cw-stream mono">
        <span className="cw-cursor">▮</span>
        <span style={{ color: 'var(--fg-2)' }}>·  — ·  — · — ·  · — · ·  — — —</span>
      </div>
    </div>
  );
}
