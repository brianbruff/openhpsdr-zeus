import type { Contact } from './data';

type QrzCardProps = {
  contact: Contact | null;
  enriching: boolean;
  lookupError?: string | null;
  onLogQso?: () => void;
  canLogQso?: boolean;
};

export function QrzCard({ contact, enriching, lookupError, onLogQso, canLogQso }: QrzCardProps) {
  // Show "Not found" if there's a lookup error
  if (lookupError) {
    return (
      <div className="qrz-empty">
        <div className="label-xs" style={{ color: 'var(--fg-error)', opacity: 0.8 }}>
          Not found: {lookupError}
        </div>
      </div>
    );
  }

  if (!contact) {
    return (
      <div className="qrz-empty">
        <div className="label-xs" style={{ opacity: 0.5 }}>
          No callsign — click "Engage QRZ" or type a callsign
        </div>
      </div>
    );
  }
  const rows: [string, string][] = [
    ['Grid', contact.grid],
    ['Rig', contact.rig],
    ['Lat/Lon', contact.latlon],
    ['Antenna', contact.ant],
    ['CQ·ITU', `${contact.cq} · ${contact.itu}`],
    ['Power', contact.power],
    ['Local', contact.local],
    ['QSL', contact.qsl],
  ];
  return (
    <div className="qrz-card">
      <div className="qrz-header">
        <div className="qrz-portrait">
          <div className="qrz-portrait-bg" aria-hidden>
            <div className="qrz-grid" />
          </div>
          {contact.photoUrl ? (
            <img
              className="qrz-portrait-img"
              src={contact.photoUrl}
              alt={`${contact.callsign} operator portrait`}
              loading="lazy"
              referrerPolicy="no-referrer"
            />
          ) : (
            <div className="qrz-portrait-initials">{contact.initials}</div>
          )}
          <div className="qrz-portrait-flag">{contact.flag}</div>
          {!contact.photoUrl && (
            <div className="qrz-portrait-placeholder label-xs">[ operator photo ]</div>
          )}
          {enriching && <div className="qrz-scan" />}
        </div>
        <div className="qrz-id">
          <div className="qrz-call">{contact.callsign}</div>
          <div className="qrz-name">{contact.name}</div>
          <div className="qrz-loc">
            {contact.flag} {contact.location}
          </div>
          <div className="qrz-tags">
            <span className="qrz-tag">{contact.class}</span>
            <span className="qrz-tag">Lic. {contact.licensed}</span>
            <span className="qrz-tag">Age {contact.age}</span>
          </div>
        </div>
      </div>

      <div className="qrz-section-label">Location · Station</div>
      <div className="qrz-grid-rows">
        {rows.map(([k, v]) => (
          <div key={k} className="qrz-row">
            <span className="k label-xs">{k}</span>
            <span className="v mono">{v}</span>
          </div>
        ))}
      </div>

      <div className="qrz-footer">
        <span className="mono" style={{ color: 'var(--fg-2)', fontSize: 10 }}>
          {contact.email}
        </span>
        <span style={{ flex: 1 }} />
        {onLogQso && canLogQso && (
          <button
            type="button"
            onClick={onLogQso}
            className="btn sm"
            style={{ marginRight: '0.5rem', padding: '0.25rem 0.5rem', fontSize: 10 }}
          >
            Log QSO
          </button>
        )}
        {contact.qrzUrl ? (
          <a
            className="mono"
            href={contact.qrzUrl}
            target="_blank"
            rel="noreferrer"
            style={{ color: 'var(--accent)', fontSize: 10, fontWeight: 700, textDecoration: 'none' }}
          >
            QRZ.COM ↗
          </a>
        ) : (
          <span className="mono" style={{ color: 'var(--accent)', fontSize: 10, fontWeight: 700 }}>
            QRZ.COM ✓
          </span>
        )}
      </div>
    </div>
  );
}
