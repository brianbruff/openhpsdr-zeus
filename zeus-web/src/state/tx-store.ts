import { create } from 'zustand';
import { persist } from 'zustand/middleware';

// TX-side state. Intentionally separate from connection-store so the TX panel
// can mount/unmount cleanly and so TX-specific fields (drivePercent, micGainDb,
// meter values, SWR alert) can accumulate here as subsequent slices land.
export type TxMeters = {
  fwdWatts: number;
  refWatts: number;
  swr: number;
  micDbfs: number;
  // TXA per-stage peak readings from WdspDspEngine.ProcessTxBlock. Valid
  // during MOX/TUN only — idle frames carry −Infinity levels and 0 GR, so
  // consumers can detect "no live data" by checking isFinite().
  eqPk: number;
  lvlrPk: number;
  alcPk: number;
  alcGr: number;
  outPk: number;
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
  // Meter telemetry pushed from the server's TxMetersService over WS (0x11).
  // Defaults look "quiet": 0 W forward/reflected, 1.0 SWR (matched), -100 dBfs
  // mic (near silence) so the SMeter/dBfs readouts don't spike on first paint.
  //
  // micDbfs is client-driven: set by the mic-uplink worklet's per-block peak
  // so the MicMeter animates even during RX. The server's TxMetersFrame no
  // longer writes this field — its placeholder -100f would clobber the live
  // capture. setMeters intentionally skips micDbfs.
  fwdWatts: number;
  refWatts: number;
  swr: number;
  micDbfs: number;
  eqPk: number;
  lvlrPk: number;
  alcPk: number;
  alcGr: number;
  outPk: number;
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
      fwdWatts: 0,
      refWatts: 0,
      swr: 1.0,
      micDbfs: -100,
      eqPk: -Infinity,
      lvlrPk: -Infinity,
      alcPk: -Infinity,
      alcGr: 0,
      outPk: -Infinity,
      setMeters: (m) => set({
        fwdWatts: m.fwdWatts,
        refWatts: m.refWatts,
        swr: m.swr,
        eqPk: m.eqPk,
        lvlrPk: m.lvlrPk,
        alcPk: m.alcPk,
        alcGr: m.alcGr,
        outPk: m.outPk,
      }),
      setMicDbfs: (dbfs) => set({ micDbfs: dbfs }),
      micError: null,
      setMicError: (msg) => set({ micError: msg }),
      rxDbm: -160,
      setRxDbm: (dbm) => set({ rxDbm: dbm }),
      alert: null,
      setAlert: (a) => set({ alert: a }),
    }),
    {
      name: 'nereus-tx',
      // Only persist the two fields the operator repeatedly sets. Everything
      // else (mox/tun/meters/alert) is transient per-session.
      partialize: (s) => ({
        drivePercent: s.drivePercent,
        micGainDb: s.micGainDb,
      }),
    },
  ),
);
