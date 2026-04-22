import { SMeterLive } from '../../components/SMeterLive';

export function SMeterPanel() {
  return (
    <div style={{ flex: 1, overflow: 'auto' }}>
      <SMeterLive />
    </div>
  );
}
