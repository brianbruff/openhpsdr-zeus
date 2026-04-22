import { LogbookLive } from '../../components/design/LogbookLive';
import { useWorkspace } from '../WorkspaceContext';

export function LogbookPanel() {
  const { logbookActions } = useWorkspace();

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'auto' }}>
      <div style={{ padding: '4px 8px', borderBottom: '1px solid var(--panel-border)', display: 'flex', gap: 4, justifyContent: 'flex-end' }}>
        {logbookActions}
      </div>
      <div style={{ flex: 1, overflow: 'auto' }}>
        <LogbookLive />
      </div>
    </div>
  );
}
