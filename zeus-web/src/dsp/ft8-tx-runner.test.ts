// SPDX-License-Identifier: GPL-2.0-or-later
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '../components/meters/__tests__/harness';
import {
  beaconDisarm,
  decodesForSlot,
  measuredDxSnr,
  rowsForSlot,
  slotIndexOf,
  slotMsFor,
  slotParity,
  startFt8SlotDriver,
  useFt8TxRunner,
} from './ft8-tx-runner';
import { Ft8TxController } from './ft8-tx-controller';
import { startCq, type Slot } from './ft8-sequencer';
import { DIGITAL_PLUGIN_BASE } from '../api/digital-plugin';
import type { Ft8Row } from '../state/ft8-store';
import { useFt8TxStore, type Ft8TxStatus } from '../state/ft8-tx-store';

interface PostCall {
  url: string;
  body: Record<string, unknown>;
}

function makeFetch(): { fn: typeof fetch; calls: PostCall[] } {
  const calls: PostCall[] = [];
  const fn = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
    calls.push({ url: String(url), body });
    return {} as Response;
  }) as unknown as typeof fetch;
  return { fn, calls };
}

const armPosts = (calls: PostCall[]) => calls.filter((c) => c.url === `${DIGITAL_PLUGIN_BASE}/ft8/tx/arm`);

function installBeaconSpy(): { calls: Array<{ url: string; data?: BodyInit }>; restore: () => void } {
  const calls: Array<{ url: string; data?: BodyInit }> = [];
  const orig = navigator.sendBeacon;
  (navigator as unknown as { sendBeacon: (u: string, d?: BodyInit) => boolean }).sendBeacon = (
    url: string,
    data?: BodyInit,
  ) => {
    calls.push({ url: String(url), data });
    return true;
  };
  return {
    calls,
    restore: () => {
      (navigator as unknown as { sendBeacon?: unknown }).sendBeacon = orig;
    },
  };
}

function txStatus(armed: boolean): Ft8TxStatus {
  return {
    armed,
    transmitting: false,
    mode: 'FT8',
    message: null,
    audioHz: 1500,
    slot: '',
    watchdogSecsRemaining: 0,
    lastTxSlotMs: null,
    nativeAvailable: true,
  };
}

function row(slotStartUnixMs: number, text: string, i = 0, snrDb = -10): Ft8Row {
  return {
    id: `0:${slotStartUnixMs}:${i}`,
    receiver: 0,
    protocol: 'FT8',
    slotStartUnixMs,
    snrDb,
    dtSec: 0.1,
    freqHz: 1000,
    score: 20,
    text,
  };
}

beforeEach(() => {
  useFt8TxStore.setState({ status: null, txEcho: [] });
});

afterEach(() => {
  vi.useRealTimers();
  useFt8TxStore.setState({ status: null, txEcho: [] });
});

describe('ft8-tx-runner slot helpers', () => {
  it('slotMsFor: FT8 = 15 s, FT4 = 7.5 s', () => {
    expect(slotMsFor('FT8')).toBe(15_000);
    expect(slotMsFor('FT4')).toBe(7_500);
  });

  it('slotIndexOf floors epoch to the slot grid', () => {
    expect(slotIndexOf(0, 15_000)).toBe(0);
    expect(slotIndexOf(14_999, 15_000)).toBe(0);
    expect(slotIndexOf(15_000, 15_000)).toBe(1);
    expect(slotIndexOf(30_001, 15_000)).toBe(2);
  });

  it('slotParity alternates even/odd', () => {
    expect(slotParity(0)).toBe('even');
    expect(slotParity(1)).toBe('odd');
    expect(slotParity(2)).toBe('even');
  });

  it('decodesForSlot returns only the texts in the requested slot index', () => {
    const slotMs = 15_000;
    const rows = [
      row(30_000, 'CQ K1ABC FN42', 0), // slot 2
      row(30_000, 'CQ W2XYZ EM10', 1), // slot 2
      row(15_000, 'CQ N0OLD AA00', 0), // slot 1 (older)
    ];
    expect(decodesForSlot(rows, 2, slotMs)).toEqual(['CQ K1ABC FN42', 'CQ W2XYZ EM10']);
    expect(decodesForSlot(rows, 1, slotMs)).toEqual(['CQ N0OLD AA00']);
    expect(decodesForSlot(rows, 0, slotMs)).toEqual([]);
  });

  it('rowsForSlot returns the full rows (text + snr) for a slot', () => {
    const slotMs = 15_000;
    const rows = [
      row(30_000, 'CQ K1ABC FN42', 0, -7),
      row(15_000, 'CQ N0OLD AA00', 0, -3),
    ];
    const slot2 = rowsForSlot(rows, 2, slotMs);
    expect(slot2).toHaveLength(1);
    expect(slot2[0]?.snrDb).toBe(-7);
  });
});

describe('measuredDxSnr', () => {
  const base = startCq({ myCall: 'KB2UKA', myGrid4: 'FN12', mode: 'FT8' });

  it('uses the SNR of the station calling us while still calling CQ', () => {
    const rows = [
      row(0, 'CQ DL9XX JO31', 0, -1), // not addressed to us
      row(0, 'KB2UKA K1ABC FN31', 1, -7), // the answerer
    ];
    expect(measuredDxSnr(rows, { ...base, dxCall: null })).toBe(-7);
  });

  it('matches the latched DX station once a QSO is in progress', () => {
    const rows = [
      row(0, 'KB2UKA W9ZZZ -05', 0, -3), // someone else calling us
      row(0, 'KB2UKA K1ABC R-12', 1, -9), // our DX
    ];
    expect(measuredDxSnr(rows, { ...base, dxCall: 'K1ABC' })).toBe(-9);
  });

  it('returns undefined when no matching row exists (sequencer keeps its fallback)', () => {
    expect(measuredDxSnr([row(0, 'CQ DL9XX JO31')], { ...base, dxCall: 'K1ABC' })).toBeUndefined();
  });
});

describe('startFt8SlotDriver (boundary → settle → decodes pipeline)', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('hands the just-ended slot decodes + parity to onWindow after settle', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0); // epoch — slot 0 (even)
    const slotMs = 15_000;
    const settleMs = 2_000;
    const rows = [row(0, 'CQ K1ABC FN42')]; // a decode in slot 0
    const windows: Array<{ texts: string[]; senderSlot: Slot }> = [];

    const stop = startFt8SlotDriver({
      slotMs,
      settleMs,
      getRows: () => rows,
      onWindow: (r, senderSlot) => windows.push({ texts: r.map((x) => x.text), senderSlot }),
    });

    // Cross into slot 1 (odd): a boundary tick fires and schedules the settle.
    vi.advanceTimersByTime(slotMs);
    expect(windows).toHaveLength(0); // settle hasn't elapsed yet

    vi.advanceTimersByTime(settleMs);
    expect(windows).toHaveLength(1);
    expect(windows[0]?.texts).toEqual(['CQ K1ABC FN42']);
    expect(windows[0]?.senderSlot).toBe('even'); // slot 0 parity

    stop();
  });

  it('drives an armed controller to stage the reply in the opposite slot', () => {
    // End-to-end frontend half: a CQ heard in an even slot, fed through the driver
    // after settle, makes the armed controller stage a reply with ODD parity — the
    // parity the backend keyer requires for the answer slot.
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const slotMs = 15_000;
    const settleMs = 2_000;
    const rows = [row(0, 'CQ K1ABC FN42')];

    const posts: Array<{ url: string; body: Record<string, unknown> }> = [];
    const fetchFn = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
      posts.push({
        url: String(url),
        body: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {},
      });
      return {} as Response;
    }) as unknown as typeof fetch;

    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn });
    ctrl.setCallFirst(true); // auto-answer the first CQ while armed + idle
    ctrl.enableTx();

    const stop = startFt8SlotDriver({
      slotMs,
      settleMs,
      getRows: () => rows,
      onWindow: (r, senderSlot) => ctrl.onWindow(r.map((x) => x.text), undefined, senderSlot),
    });

    vi.advanceTimersByTime(slotMs + settleMs);

    const stage = posts.filter((p) => p.url === `${DIGITAL_PLUGIN_BASE}/ft8/tx`).at(-1);
    expect(stage).toBeDefined();
    expect(stage?.body.slot).toBe('odd'); // reply goes in the slot opposite the CQ
    expect(String(stage?.body.message)).toContain('K1ABC');

    stop();
  });

  it('threads the decoded DX SNR into the logged report (not a constant)', () => {
    // A station answers our CQ with a grid (no report in the message) measured at
    // -7 dB. The runner must report/log -7, not the sequencer's -10 fallback.
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const slotMs = 15_000;
    const settleMs = 2_000;
    const rows = [row(0, 'KB2UKA K1ABC FN31', 0, -7)];

    const fetchFn = vi.fn(async () => ({}) as Response) as unknown as typeof fetch;
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn });
    ctrl.enableTx(); // CQ caller, armed

    const stop = startFt8SlotDriver({
      slotMs,
      settleMs,
      getRows: () => rows,
      onWindow: (r, senderSlot) =>
        ctrl.onWindow(r.map((x) => x.text), measuredDxSnr(r, ctrl.getState()), senderSlot),
    });

    vi.advanceTimersByTime(slotMs + settleMs);

    expect(ctrl.getState().dxCall).toBe('K1ABC');
    expect(ctrl.getState().sentReportToHim).toBe(-7);

    stop();
  });
});

describe('beaconDisarm', () => {
  it('disarms the FT8 keyer via sendBeacon on the arm endpoint', () => {
    const calls: Array<{ url: string; body: string }> = [];
    const orig = navigator.sendBeacon;
    // jsdom has no sendBeacon; install a spy.
    (navigator as unknown as { sendBeacon: (u: string, d?: BodyInit) => boolean }).sendBeacon = (
      url: string,
      data?: BodyInit,
    ) => {
      calls.push({ url: String(url), body: String(data) });
      return true;
    };
    try {
      beaconDisarm();
    } finally {
      (navigator as unknown as { sendBeacon?: unknown }).sendBeacon = orig;
    }
    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toBe(`${DIGITAL_PLUGIN_BASE}/ft8/tx/arm`);
  });
});

describe('useFt8TxRunner lifecycle safety', () => {
  it('unmount of a never-armed runner does not POST /arm or send a beacon', () => {
    const { fn, calls } = makeFetch();
    const beacon = installBeaconSpy();
    try {
      const hook = renderHook(() =>
        useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
      );

      hook.unmount();

      expect(armPosts(calls)).toHaveLength(0);
      expect(beacon.calls).toHaveLength(0);
    } finally {
      beacon.restore();
    }
  });

  it('unmount of an armed runner disarms the backend and sends the safety beacon', () => {
    const { fn, calls } = makeFetch();
    const beacon = installBeaconSpy();
    try {
      const hook = renderHook(() =>
        useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
      );
      act(() => hook.result.current.enableTx());
      calls.length = 0;
      beacon.calls.length = 0;

      hook.unmount();

      expect(armPosts(calls)).toEqual([
        expect.objectContaining({ body: { enabled: false } }),
      ]);
      expect(beacon.calls).toHaveLength(1);
      expect(beacon.calls[0]?.url).toBe(`${DIGITAL_PLUGIN_BASE}/ft8/tx/arm`);
    } finally {
      beacon.restore();
    }
  });

  it('pagehide only beacons from a locally armed runner', () => {
    const { fn } = makeFetch();
    const beacon = installBeaconSpy();
    try {
      const hook = renderHook(() =>
        useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
      );

      act(() => window.dispatchEvent(new Event('pagehide')));
      expect(beacon.calls).toHaveLength(0);

      act(() => hook.result.current.enableTx());
      beacon.calls.length = 0;
      act(() => window.dispatchEvent(new Event('pagehide')));

      expect(beacon.calls).toHaveLength(1);
      expect(beacon.calls[0]?.url).toBe(`${DIGITAL_PLUGIN_BASE}/ft8/tx/arm`);
      hook.unmount();
    } finally {
      beacon.restore();
    }
  });
});

describe('useFt8TxRunner backend arm reconcile', () => {
  it('flips local state disarmed on backend armed true→false outside the arm grace window without POSTing', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    act(() => useFt8TxStore.setState({ status: txStatus(true) }));
    vi.advanceTimersByTime(5_001);
    calls.length = 0;

    act(() => useFt8TxStore.setState({ status: txStatus(false) }));

    expect(hook.result.current.qso.enableTx).toBe(false);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });

  it('keeps local state armed on backend true→false inside the local arm grace window', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    act(() => useFt8TxStore.setState({ status: txStatus(true) }));
    vi.advanceTimersByTime(4_999);
    calls.length = 0;

    act(() => useFt8TxStore.setState({ status: txStatus(false) }));

    expect(hook.result.current.qso.enableTx).toBe(true);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });

  it('rechecks an inside-grace backend disarm when the grace window expires', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    act(() => useFt8TxStore.setState({ status: txStatus(true) }));
    act(() => vi.advanceTimersByTime(3_000));
    calls.length = 0;

    act(() => useFt8TxStore.setState({ status: txStatus(false) }));
    expect(hook.result.current.qso.enableTx).toBe(true);
    expect(armPosts(calls)).toHaveLength(0);

    act(() => vi.advanceTimersByTime(2_001));

    expect(hook.result.current.qso.enableTx).toBe(false);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });

  it('ignores a pending inside-grace recheck after a newer local arm starts', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    act(() => useFt8TxStore.setState({ status: txStatus(true) }));
    act(() => vi.advanceTimersByTime(3_000));
    act(() => useFt8TxStore.setState({ status: txStatus(false) }));
    act(() => vi.advanceTimersByTime(1_000));
    act(() => hook.result.current.enableTx());
    calls.length = 0;

    act(() => vi.advanceTimersByTime(1_001));

    expect(hook.result.current.qso.enableTx).toBe(true);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });

  it('reconciles a null stream resume to backend disarmed while locally armed outside grace', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    act(() => useFt8TxStore.setState({ status: null }));
    act(() => vi.advanceTimersByTime(5_001));
    calls.length = 0;

    act(() => useFt8TxStore.setState({ status: txStatus(false) }));

    expect(hook.result.current.qso.enableTx).toBe(false);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });

  it('defers a null stream resume to backend disarmed inside grace, then reconciles at expiry', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    act(() => useFt8TxStore.setState({ status: null }));
    act(() => vi.advanceTimersByTime(3_000));
    calls.length = 0;

    act(() => useFt8TxStore.setState({ status: txStatus(false) }));
    // Inside grace: the resume must defer, not disarm immediately.
    expect(hook.result.current.qso.enableTx).toBe(true);

    act(() => vi.advanceTimersByTime(2_001));

    expect(hook.result.current.qso.enableTx).toBe(false);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });

  it('leaves local state unchanged when a null stream resumes backend armed', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    act(() => vi.advanceTimersByTime(5_001));
    calls.length = 0;

    act(() => useFt8TxStore.setState({ status: txStatus(true) }));

    expect(hook.result.current.qso.enableTx).toBe(true);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });

  it('cleans up a pending inside-grace recheck on unmount', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const { fn, calls } = makeFetch();
      const hook = renderHook(() =>
        useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
      );

      act(() => hook.result.current.enableTx());
      act(() => useFt8TxStore.setState({ status: txStatus(true) }));
      act(() => vi.advanceTimersByTime(3_000));
      act(() => useFt8TxStore.setState({ status: txStatus(false) }));
      expect(vi.getTimerCount()).toBe(1);

      hook.unmount();
      expect(vi.getTimerCount()).toBe(0);
      calls.length = 0;
      act(() => vi.advanceTimersByTime(3_000));

      expect(calls).toHaveLength(0);
      expect(consoleError).not.toHaveBeenCalled();
    } finally {
      consoleError.mockRestore();
    }
  });

  it('does not reconcile on initial backend disarmed status with no prior armed state', () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    act(() => useFt8TxStore.setState({ status: txStatus(false) }));
    const { fn, calls } = makeFetch();
    const hook = renderHook(() =>
      useFt8TxRunner({ myCall: 'KB2UKA', myGrid: 'FN12', mode: 'FT8', active: false, fetchFn: fn }),
    );

    act(() => hook.result.current.enableTx());
    vi.advanceTimersByTime(5_001);
    calls.length = 0;
    act(() => useFt8TxStore.setState({ status: txStatus(false) }));

    expect(hook.result.current.qso.enableTx).toBe(true);
    expect(armPosts(calls)).toHaveLength(0);
    hook.unmount();
  });
});
