import { useDisplayStore } from '../state/display-store';
import { AgcSlider } from './AgcSlider';
import { AttenuatorSlider } from './AttenuatorSlider';
import { AudioToggle } from './AudioToggle';
import { DriveSlider } from './DriveSlider';
import { MicGainSlider } from './MicGainSlider';
import { MicMeter } from './MicMeter';
import { MoxButton } from './MoxButton';
import { NrControls } from './NrControls';
import { PreampButton } from './PreampButton';
import { TunButton } from './TunButton';

export function TopBar() {
  const connected = useDisplayStore((s) => s.connected);

  return (
    <header className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-neutral-800 px-3 py-2 sm:px-4">
      <div className="flex items-center gap-3">
        <span className="font-semibold tracking-wider">ZEUS</span>
        <span className="hidden text-xs text-neutral-500 sm:inline">Phase 0</span>
      </div>
      <span
        className={
          (connected
            ? 'bg-emerald-700/40 text-emerald-300'
            : 'bg-neutral-800 text-neutral-400') +
          ' ml-auto rounded px-2 py-0.5 text-xs'
        }
      >
        {connected ? '● CONNECTED' : '○ DISCONNECTED'}
      </span>
      <div className="flex w-full flex-wrap items-center gap-x-4 gap-y-2 text-xs sm:w-auto">
        <PreampButton />
        <AttenuatorSlider />
        <AgcSlider />
        <NrControls />
        <AudioToggle />
        <MoxButton />
        <TunButton />
        <DriveSlider />
        <MicGainSlider />
        <MicMeter />
      </div>
    </header>
  );
}
