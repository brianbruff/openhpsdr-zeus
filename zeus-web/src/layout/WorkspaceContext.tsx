import { createContext, useContext, type FormEvent, type ReactNode, type RefObject } from 'react';
import type { Contact } from '../components/design/data';

export interface EffectiveHome {
  call: string;
  lat: number;
  lon: number;
  grid: string;
  imageUrl: string | null;
}

// All workspace-level state and callbacks that panel components need.
// App.tsx creates this context; panels consume it via useWorkspace().
// Keeping state in App.tsx (rather than lifting to Zustand) is intentional
// for Phase 1 — the context is just plumbing, not a state refactor.
export interface WorkspaceCtx {
  // Connection
  connected: boolean;
  moxOn: boolean;
  tunOn: boolean;
  mode: string;
  vfoHz: number;

  // QRZ / Terminator
  callsign: string;
  setCallsign: (v: string) => void;
  terminatorActive: boolean;
  enriching: boolean;
  lookupKey: number;
  contact: Contact | null;
  qrzLookupError: string | null;
  qrzActive: boolean;
  mapAvailable: boolean;
  setMapAvailable: (v: boolean) => void;
  mapInteractive: boolean;
  effectiveHome: EffectiveHome;
  beamOverrideDeg: number | null;
  setBeamOverrideDeg: (v: number | null) => void;
  beamInputStr: string;
  setBeamInputStr: (v: string) => void;
  rotLiveAz: number | null;
  sp: number;
  lp: number;
  dist: number;
  heroTitle: ReactNode;
  csInputRef: RefObject<HTMLInputElement | null>;
  engageTerminator: (cs?: string) => void;
  disengageTerminator: () => void;
  onCallsignSubmit: (e: FormEvent<HTMLFormElement>) => void;
  submitBeam: (e: FormEvent<HTMLFormElement>) => void;
  handleLogQso: () => void;

  // DSP
  dspActive: boolean;

  // CW
  wpm: number;
  setWpm: (v: number) => void;

  // Logbook
  logbookTitle: string;
  logbookActions: ReactNode;
}

export const WorkspaceContext = createContext<WorkspaceCtx | null>(null);

export function useWorkspace(): WorkspaceCtx {
  const ctx = useContext(WorkspaceContext);
  if (!ctx) throw new Error('useWorkspace must be used inside WorkspaceContext.Provider');
  return ctx;
}
