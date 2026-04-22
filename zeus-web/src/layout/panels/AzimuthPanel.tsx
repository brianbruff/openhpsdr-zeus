import { AzimuthMap } from '../../components/design/AzimuthMap';
import { useWorkspace } from '../WorkspaceContext';

export function AzimuthPanel() {
  const { contact } = useWorkspace();

  return (
    <div style={{ flex: 1, overflow: 'hidden' }}>
      <AzimuthMap target={contact} myGrid="EM48" />
    </div>
  );
}
