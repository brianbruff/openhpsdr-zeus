export function TerminatorLines({ active }: { active: boolean }) {
  if (!active) return null;
  return (
    <div className="t1000-overlay" aria-hidden>
      <div
        className="t1000-line enter-x"
        style={{ top: '20%', left: 0, width: '100%', height: 1, animationDelay: '40ms' }}
      />
      <div
        className="t1000-line enter-x"
        style={{ top: '80%', left: 0, width: '100%', height: 1, animationDelay: '120ms' }}
      />
      <div
        className="t1000-line vertical enter-y"
        style={{ left: '25%', top: 0, height: '100%', width: 1, animationDelay: '80ms' }}
      />
      <div
        className="t1000-line vertical enter-y"
        style={{ left: '75%', top: 0, height: '100%', width: 1, animationDelay: '160ms' }}
      />
    </div>
  );
}
