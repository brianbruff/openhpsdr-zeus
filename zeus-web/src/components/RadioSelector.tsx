// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useEffect } from 'react';
import { BOARD_LABELS, type BoardKind } from '../api/radio';
import { useRadioStore } from '../state/radio-store';

// Order reflects the Thetis hardware dropdown — newer / more common boards
// first, then legacy (Metis / Griffin) at the bottom. Matches HpsdrBoardKind
// wire values on the backend; Auto maps to "no preference stored".
const BOARD_OPTIONS: ReadonlyArray<BoardKind> = [
  'Auto',
  'HermesLite2',
  'OrionMkII',
  'Orion',
  'Angelia',
  'Hermes',
  'Metis',
  'Griffin',
];

// Header block for SettingsMenu. Owns the radio-selection dropdown + shows
// what discovery actually found on the wire, so an operator who selects
// "ANAN G2" while plugged into an HL2 can see the mismatch and pick the
// right one.
//
// Side-effect: changing the dropdown PUTs the preference and reloads the
// PA panel's view with that board's defaults for empty bands. Saved
// per-band calibration is not touched — only the fallback values for
// rows the operator has never edited.
export function RadioSelector() {
  const selection = useRadioStore((s) => s.selection);
  const loaded = useRadioStore((s) => s.loaded);
  const inflight = useRadioStore((s) => s.inflight);
  const error = useRadioStore((s) => s.error);
  const load = useRadioStore((s) => s.load);
  const setPreferred = useRadioStore((s) => s.setPreferred);
  const setOverrideDetection = useRadioStore((s) => s.setOverrideDetection);

  useEffect(() => {
    load();
  }, [load]);

  const connectedKnown = selection.connected !== 'Unknown';
  const overrideOn = selection.overrideDetection && selection.preferred !== 'Auto';
  // "Mismatch" without override = discovery wins, badge is a warning.
  // With override = the operator deliberately chose this; the OVERRIDE badge
  // takes the warning slot instead.
  const mismatch =
    !overrideOn &&
    selection.preferred !== 'Auto' &&
    connectedKnown &&
    selection.preferred !== selection.connected;

  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 12,
        padding: '10px 22px',
        background: 'var(--bg-0)',
        borderBottom: '1px solid var(--panel-border)',
        fontSize: 12,
        color: 'var(--fg-1)',
      }}
    >
      <label
        htmlFor="radio-selector"
        style={{
          fontSize: 10,
          fontWeight: 700,
          letterSpacing: '0.14em',
          textTransform: 'uppercase',
          color: 'var(--fg-2)',
        }}
      >
        Radio
      </label>
      <select
        id="radio-selector"
        value={selection.preferred}
        disabled={!loaded || inflight}
        onChange={(e) => setPreferred(e.target.value as BoardKind)}
        style={{
          minWidth: 220,
          padding: '4px 8px',
          fontSize: 12,
          background: 'var(--bg-2)',
          color: 'var(--fg-0)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-sm, 3px)',
        }}
      >
        {BOARD_OPTIONS.map((b) => (
          <option key={b} value={b}>
            {BOARD_LABELS[b]}
          </option>
        ))}
      </select>

      <span
        title={
          connectedKnown
            ? `Discovery reports ${BOARD_LABELS[selection.connected]} on the wire`
            : 'No radio connected'
        }
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 6,
          fontSize: 11,
          color: connectedKnown ? 'var(--accent)' : 'var(--fg-3)',
        }}
      >
        <span
          style={{
            width: 6,
            height: 6,
            borderRadius: '50%',
            background: connectedKnown ? 'var(--accent)' : 'var(--fg-3)',
            boxShadow: connectedKnown ? '0 0 6px var(--accent)' : 'none',
          }}
        />
        {connectedKnown ? `Detected: ${BOARD_LABELS[selection.connected]}` : 'No radio connected'}
      </span>

      {mismatch && (
        <span
          style={{
            fontSize: 10,
            color: 'var(--tx)',
            background: 'var(--tx-soft)',
            padding: '2px 6px',
            borderRadius: 2,
          }}
          title="Discovery disagrees with your selection. Discovery always wins for drive-byte math and PA defaults while connected — flip Override Detection on if you really want the selected board to win."
        >
          MISMATCH
        </span>
      )}

      {selection.preferred !== 'Auto' && (
        <label
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 6,
            fontSize: 11,
            color: 'var(--fg-2)',
            cursor: loaded && !inflight ? 'pointer' : 'default',
          }}
          title={
            'Override Detection (Advanced)\n\n' +
            'When ON, Zeus uses the selected board for ALL behavior — drive-byte\n' +
            'encoding, ATT, filter switching, PA defaults — ignoring what discovery\n' +
            'reports on the wire.\n\n' +
            'Use this for hardware combinations that report a misleading board ID\n' +
            '(e.g. Anvelina SDR + ANAN 200D PA detected as ANAN G2). A wrong choice\n' +
            'here can produce incorrect drive levels or no output power. Test at low\n' +
            'power first.'
          }
        >
          <input
            type="checkbox"
            checked={selection.overrideDetection}
            disabled={!loaded || inflight}
            onChange={(e) => setOverrideDetection(e.target.checked)}
          />
          <span>Override detection</span>
        </label>
      )}

      {overrideOn && (
        <span
          style={{
            fontSize: 10,
            color: 'var(--tx)',
            background: 'var(--tx-soft)',
            padding: '2px 6px',
            borderRadius: 2,
          }}
          title="Override is active. Zeus uses the selected board for drive-byte math, ATT, filters, and PA defaults — discovery is ignored."
        >
          OVERRIDE ACTIVE
        </span>
      )}

      {error && (
        <span style={{ fontSize: 10, color: 'var(--tx)' }}>· {error}</span>
      )}

      <span
        style={{
          marginLeft: 'auto',
          fontSize: 10,
          color: 'var(--fg-3)',
        }}
        title="Without override, changing the radio only reseeds defaults for bands you haven't calibrated. Your saved per-band PA Gain values are not touched."
      >
        {overrideOn
          ? 'Selected board wins for drive / ATT / filters.'
          : 'Seeds PA defaults. Saved calibration is preserved.'}
      </span>
    </div>
  );
}
