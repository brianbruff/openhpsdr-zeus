import { create } from 'zustand';
import {
  NR_CONFIG_DEFAULT,
  type ConnectionStatus,
  type NrConfigDto,
  type RadioStateDto,
  type RxMode,
  type ZoomLevel,
} from '../api/client';

// WDSP wisdom bootstrap phase, mirroring the server's WisdomPhase enum.
// 'idle' = initializer hasn't started yet (first ms after boot),
// 'building' = WDSPwisdom is running (up to ~2 min on a fresh machine),
// 'ready' = FFTW plans are cached and /api/connect is accepting. The
// ConnectPanel disables + pulses Connect while !== 'ready'.
export type WisdomPhase = 'idle' | 'building' | 'ready';

export type ConnectionState = {
  status: ConnectionStatus;
  endpoint: string | null;
  vfoHz: number;
  mode: RxMode;
  filterLowHz: number;
  filterHighHz: number;
  sampleRate: number;
  agcTopDb: number;
  attenDb: number;
  autoAttEnabled: boolean;
  attOffsetDb: number;
  adcOverloadWarning: boolean;
  // Board kind only known from the discovery list at connect time — StateDto
  // doesn't echo it. Null after a page reload while already connected; the
  // preamp guard treats null as "show", which is the safe default (an HL2
  // preamp toggle does nothing harmful, just nothing useful).
  boardId: string | null;
  preampOn: boolean;
  nr: NrConfigDto;
  zoomLevel: ZoomLevel;
  inflight: boolean;
  // Endpoint of the most recently successful /api/connect. Survives a
  // disconnect so ConnectPanel can float it to the top of the next scan.
  // Intentionally in-memory only — no localStorage yet.
  lastConnectedEndpoint: string | null;
  wisdomPhase: WisdomPhase;
  applyState: (s: RadioStateDto) => void;
  setInflight: (v: boolean) => void;
  setBoardId: (id: string | null) => void;
  setPreampOn: (on: boolean) => void;
  setNr: (nr: NrConfigDto) => void;
  setZoomLevel: (level: ZoomLevel) => void;
  setLastConnectedEndpoint: (ep: string | null) => void;
  setWisdomPhase: (phase: WisdomPhase) => void;
};

export const useConnectionStore = create<ConnectionState>((set) => ({
  status: 'Disconnected',
  endpoint: null,
  vfoHz: 14_200_000,
  mode: 'USB',
  filterLowHz: 150,
  filterHighHz: 2850,
  sampleRate: 192_000,
  agcTopDb: 80,
  attenDb: 0,
  autoAttEnabled: true,
  attOffsetDb: 0,
  adcOverloadWarning: false,
  boardId: null,
  preampOn: false,
  nr: { ...NR_CONFIG_DEFAULT },
  zoomLevel: 1,
  inflight: false,
  lastConnectedEndpoint: null,
  // Default to 'ready' so a page-load before the WS attach doesn't show the
  // pulse spuriously. The server overrides on attach with the real phase.
  wisdomPhase: 'ready',
  applyState: (s) =>
    set({
      status: s.status,
      endpoint: s.endpoint,
      vfoHz: s.vfoHz,
      mode: s.mode,
      filterLowHz: s.filterLowHz,
      filterHighHz: s.filterHighHz,
      sampleRate: s.sampleRate,
      agcTopDb: s.agcTopDb,
      attenDb: s.attenDb,
      autoAttEnabled: s.autoAttEnabled,
      attOffsetDb: s.attOffsetDb,
      adcOverloadWarning: s.adcOverloadWarning,
      nr: s.nr,
      zoomLevel: s.zoomLevel,
    }),
  setInflight: (inflight) => set({ inflight }),
  setBoardId: (boardId) => set({ boardId }),
  setPreampOn: (preampOn) => set({ preampOn }),
  setNr: (nr) => set({ nr }),
  setZoomLevel: (zoomLevel) => set({ zoomLevel }),
  setLastConnectedEndpoint: (lastConnectedEndpoint) =>
    set({ lastConnectedEndpoint }),
  setWisdomPhase: (wisdomPhase) => set({ wisdomPhase }),
}));
