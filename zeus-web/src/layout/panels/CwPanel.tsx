import { CwKeyer } from '../../components/design/CwKeyer';
import { useWorkspace } from '../WorkspaceContext';

export function CwPanel() {
  const { wpm, setWpm } = useWorkspace();

  return (
    <div style={{ flex: 1, overflow: 'auto' }}>
      <CwKeyer wpm={wpm} setWpm={setWpm} />
    </div>
  );
}
