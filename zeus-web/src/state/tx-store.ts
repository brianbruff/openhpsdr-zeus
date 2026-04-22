import { create } from 'zustand';
import { persist } from 'zustand/middleware';

// TX-side state. Intentionally separate from connection-store so the TX panel
// can mount/unmount cleanly and so TX-specific fields (drivePercent, micGainDb,
// meter values, SWR alert) can accumulate here as subsequent slices land.
// TxMetersV2 wire payload (MsgType 0x16, 20 f32 LE). TXA per-stage peak/average
// readings from WdspDspEngine.ProcessTxBlock. Valid during MOX/TUN only — idle
// or bypassed WDSP stages emit ≤ −200 dBFS (near the −400 sentinel) and `*Gr`
// fields stay at 0 when the stage is idle. Consumers should treat ≤ −200 as
// "bypassed" rather than a real level (see P1.4).
export type TxMeters = {
  fwdWatts: number;
  refWatts: number;
  swr: number;
  micPk: number;
  micAv: number;
  eqPk: number;
  eqAv: number;
  lvlrPk: number;
  lvlrAv: number;
  lvlrGr: number;
  cfcPk: number;
  cfcAv: number;
  cfcGr: number;
  compPk: number;
  compAv: number;
  alcPk: number;
  alcAv: number;
  alcGr: number;
  outPk: number;
  outAv: number;
};

export enum AlertKind {
  SwrTrip = 0,
}

export type Alert = {
  kind: AlertKind;
  message: string;
};

export type TxState = {
  moxOn: boolean;
  setMoxOn: (on: boolean) => void;
  // PRD FR-7: TUN keys a single-tone carrier via WDSP SetTXAPostGen*.
  // Mutually exclusive with MOX — one is always canceled by the other so the
  // backend never sees both keyed. Exclusion is enforced inside the setters.
  tunOn: boolean;
  setTunOn: (on: boolean) => void;
  // PRD FR-4: drive starts at 10% so a first MOX click on an un-touched slider
  // can't flash full power into the PA.
  drivePercent: number;
  setDrivePercent: (p: number) => void;
  // PRD FR-3: mic-gain slider 0..+20 dB (default 0). Server applies via
  // WDSP SetTXAPanelGain1(TXA, 10^(db/20)). Kept as int dB on the wire.
  micGainDb: number;
  setMicGainDb: (db: number) => void;
  // Leveler max-gain slider 0..+15 dB (default +5 — matches backend default
  // and HL2 community starting point). Higher = more aggressive voice
  // leveling; can push ALC into hard limiting. Persisted — a user preference
  // that should survive reload. Server clamps [0, 15]; we clamp here too so
  // persisted / race-condition writes can't poison the store.
  levelerMaxGainDb: number;
  setLevelerMaxGainDb: (db: number) => void;
  // Meter telemetry pushed from the server's TxMetersService over WS (0x16 v2).
  // Defaults look "quiet": 0 W forward/reflected, 1.0 SWR (matched), -100 dBfs
  // mic (near silence) so the SMeter/dBfs readouts don't spike on first paint.
  //
  // micDbfs is client-driven: set by the mic-uplink worklet's per-block peak
  // so the MicMeter animates even during RX. setMeters does not overwrite it.
  // wdspMicPk is server-driven: WDSP TXA_MIC_PK (post-panel-gain) carried in
  // TxMetersFrame.MicPk at 10 Hz during MOX; −Infinity when idle.
  fwdWatts: number;
  refWatts: number;
  swr: number;
  micDbfs: number;
  wdspMicPk: number;
  micAv: number;
  eqPk: number;
  eqAv: number;
  lvlrPk: number;
  lvlrAv: number;
  lvlrGr: number;
  cfcPk: number;
  cfcAv: number;
  cfcGr: number;
  compPk: number;
  compAv: number;
  alcPk: number;
  alcAv: number;
  alcGr: number;
  outPk: number;
  outAv: number;
  setMeters: (m: TxMeters) => void;
  setMicDbfs: (dbfs: number) => void;
  // Surfaced when getUserMedia / AudioWorklet init fails so MicMeter can
  // render a "click to enable" / "permission denied" hint instead of a
  // silent dead bar. null when mic capture is running normally.
  micError: string | null;
  setMicError: (msg: string | null) => void;
  // RX S-meter reading in dBm (MsgType.RxMeter / 0x14 pushed at ~5 Hz from
  // DspPipelineService). −160 floor matches the server's clamp so the SMeter
  // component never has to reason about -inf / tiny doubles.
  rxDbm: number;
  setRxDbm: (dbm: number) => void;
  // PRD FR-6: SWR trip alert. Server emits an AlertFrame (0x13) when SWR > 2.5
  // sustained ≥500 ms. Dismissable amber banner in UI; sticks until dismissed.
  alert: Alert | null;
  setAlert: (a: Alert | null) => void;
  // HL2 PA temperature (°C), from MsgType 0x17 at 2 Hz. Server clamps to
  // [-40, 125]. null means "no reading yet" (server hasn't sampled or we
  // haven't connected). Transient per-session — not persisted.
  paTempC: number | null;
  setPaTempC: (c: number) => void;
};

export const useTxStore = create<TxState>()(
  persist(
    (set) => ({
      moxOn: false,
      setMoxOn: (on) => set(on ? { moxOn: true, tunOn: false } : { moxOn: false }),
      tunOn: false,
      setTunOn: (on) => set(on ? { tunOn: true, moxOn: false } : { tunOn: false }),
      drivePercent: 10,
      setDrivePercent: (p) => set({ drivePercent: p }),
      micGainDb: 0,
      setMicGainDb: (db) => set({ micGainDb: db }),
      levelerMaxGainDb: 5,
      setLevelerMaxGainDb: (db) =>
        set({ levelerMaxGainDb: Math.max(0, Math.min(15, db)) }),
      fwdWatts: 0,
      refWatts: 0,
      swr: 1.0,
      micDbfs: -100,
      wdspMicPk: -Infinity,
      micAv: -Infinity,
      eqPk: -Infinity,
      eqAv: -Infinity,
      lvlrPk: -Infinity,
      lvlrAv: -Infinity,
      lvlrGr: 0,
      cfcPk: -Infinity,
      cfcAv: -Infinity,
      cfcGr: 0,
      compPk: -Infinity,
      compAv: -Infinity,
      alcPk: -Infinity,
      alcAv: -Infinity,
      alcGr: 0,
      outPk: -Infinity,
      outAv: -Infinity,
      setMeters: (m) => set({
        fwdWatts: m.fwdWatts,
        refWatts: m.refWatts,
        swr: m.swr,
        wdspMicPk: m.micPk,
        micAv: m.micAv,
        eqPk: m.eqPk,
        eqAv: m.eqAv,
        lvlrPk: m.lvlrPk,
        lvlrAv: m.lvlrAv,
        lvlrGr: m.lvlrGr,
        cfcPk: m.cfcPk,
        cfcAv: m.cfcAv,
        cfcGr: m.cfcGr,
        compPk: m.compPk,
        compAv: m.compAv,
        alcPk: m.alcPk,
        alcAv: m.alcAv,
        alcGr: m.alcGr,
        outPk: m.outPk,
        outAv: m.outAv,
      }),
      setMicDbfs: (dbfs) => set({ micDbfs: dbfs }),
      micError: null,
      setMicError: (msg) => set({ micError: msg }),
      rxDbm: -160,
      setRxDbm: (dbm) => set({ rxDbm: dbm }),
      alert: null,
      setAlert: (a) => set({ alert: a }),
      paTempC: null,
      setPaTempC: (c) => set({ paTempC: c }),
    }),
    {
      name: 'zeus-tx',
      // Only persist the two fields the operator repeatedly sets. Everything
      // else (mox/tun/meters/alert) is transient per-session.
      partialize: (s) => ({
        drivePercent: s.drivePercent,
        micGainDb: s.micGainDb,
        levelerMaxGainDb: s.levelerMaxGainDb,
      }),
    },
  ),
);
