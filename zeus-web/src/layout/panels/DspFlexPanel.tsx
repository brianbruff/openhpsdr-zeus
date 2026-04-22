import { DspPanel } from '../../components/DspPanel';

// Named DspFlexPanel to avoid collision with the DspPanel component it wraps.
export function DspFlexPanel() {
  return (
    <div style={{ flex: 1, overflow: 'auto' }}>
      <DspPanel />
    </div>
  );
}
