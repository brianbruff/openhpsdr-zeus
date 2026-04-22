import { VfoDisplay } from '../../components/VfoDisplay';

export function VfoPanel() {
  return (
    <div className="freq-panel" style={{ flex: 1, overflow: 'auto' }}>
      <VfoDisplay />
    </div>
  );
}
