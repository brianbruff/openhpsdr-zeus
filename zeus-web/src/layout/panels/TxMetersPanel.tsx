import { TxStageMeters } from '../../components/TxStageMeters';

export function TxMetersPanel() {
  return (
    <div style={{ flex: 1, overflow: 'auto' }}>
      <TxStageMeters />
    </div>
  );
}
