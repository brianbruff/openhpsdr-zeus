import { QrzCard } from '../../components/design/QrzCard';
import { useWorkspace } from '../WorkspaceContext';

export function QrzPanel() {
  const {
    callsign,
    setCallsign,
    contact,
    enriching,
    qrzLookupError,
    handleLogQso,
    qrzActive,
    csInputRef,
    onCallsignSubmit,
  } = useWorkspace();

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'auto' }}>
      <div style={{ padding: '4px 8px', borderBottom: '1px solid var(--panel-border)', display: 'flex', gap: 4 }}>
        <form onSubmit={onCallsignSubmit} style={{ display: 'flex', gap: 4, flex: 1 }}>
          <input
            ref={csInputRef}
            className="cs-input mono"
            value={callsign}
            onChange={(e) => setCallsign(e.target.value.toUpperCase())}
            placeholder="CALL?"
            style={{ flex: 1 }}
          />
          <button type="submit" className="btn sm">Lookup</button>
        </form>
      </div>
      <div style={{ flex: 1, overflow: 'auto' }}>
        <QrzCard
          contact={contact}
          enriching={enriching}
          lookupError={qrzLookupError}
          onLogQso={handleLogQso}
          canLogQso={qrzActive && !!contact}
        />
      </div>
    </div>
  );
}
