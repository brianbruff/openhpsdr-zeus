import { useEffect, useState } from 'react';
import { getAudioClient, type AudioClientState } from '../audio/audio-client';

export function AudioToggle() {
  const [state, setState] = useState<AudioClientState>({ kind: 'idle' });

  useEffect(() => {
    return getAudioClient().subscribe((s) => {
      setState(s);
    });
  }, []);

  const onClick = async () => {
    const client = getAudioClient();
    if (state.kind === 'playing' || state.kind === 'loading') {
      await client.stop();
    } else {
      await client.start();
    }
  };

  const playing = state.kind === 'playing';
  const loading = state.kind === 'loading';
  const label = loading
    ? 'Loading…'
    : playing
    ? '■ Mute'
    : '▶ Unmute';

  const title = state.kind === 'error' ? state.message : undefined;

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
      <button
        type="button"
        onPointerUp={onClick}
        disabled={loading}
        className={`btn sm ${playing ? 'active' : ''}`}
        title={title}
      >
        <span className={`led ${playing ? 'on' : ''}`} style={{ marginRight: 6 }} />
        {label}
      </button>
      {state.kind === 'error' && (
        <span className="label-xs" style={{ color: 'var(--tx)' }}>
          audio error
        </span>
      )}
    </div>
  );
}
