// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useEffect } from 'react';
import { BOARD_LABELS, type BoardKind } from '../api/radio';
import {
  ORION_MKII_VARIANT_LABELS,
  type OrionMkIIVariant,
} from '../api/orion-mkii-variant';
import { useRadioStore } from '../state/radio-store';
import { useMultiSliceStore } from '../state/multi-slice-store';
import { useTxStore } from '../state/tx-store';

// Order reflects the Thetis hardware dropdown — newer / more common boards
// first, then legacy (Metis / HermesII) at the bottom. Matches
// Zeus.Contracts.HpsdrBoardKind wire values on the backend; Auto maps to
// "no preference stored". Post-#218 Phase 4: Griffin renamed → HermesII,
// HermesC10 (ANAN-G2E) added.
const BOARD_OPTIONS: ReadonlyArray<BoardKind> = [
  'Auto',
  'HermesLite2',
  'OrionMkII',
  'HermesC10',
  'Orion',
  'Angelia',
  'Hermes',
  'HermesII',
  'Metis',
];

// Order reflects the most-common-first heuristic — G2 (default) at the top,
// homebrew / community boards at the bottom. Only meaningful when the
// selected/connected board is OrionMkII (#218 Phase 3).
const VARIANT_OPTIONS: ReadonlyArray<OrionMkIIVariant> = [
  'G2',
  'G2_1K',
  'Anan7000DLE',
  'Anan8000DLE',
  'OrionMkII',
  'AnvelinaPro3',
  'RedPitaya',
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
  const variant = useRadioStore((s) => s.variant);
  const loaded = useRadioStore((s) => s.loaded);
  const inflight = useRadioStore((s) => s.inflight);
  const error = useRadioStore((s) => s.error);
  const load = useRadioStore((s) => s.load);
  const setPreferred = useRadioStore((s) => s.setPreferred);
  const setOverrideDetection = useRadioStore((s) => s.setOverrideDetection);
  const setVariant = useRadioStore((s) => s.setVariant);
  const maxReceivers = useRadioStore((s) => s.capabilities.maxReceivers);

  // HL2 multi-slice (issue #251). Loaded once on mount; the toggle is hidden
  // when the connected board's MaxReceivers ≤ 1. PureSignal master arm gates
  // the toggle's enabled state — backend will refuse to enable multi-slice
  // while PS is on, and surfacing it client-side as a tooltip avoids the
  // round-trip and confusing 409 banner.
  const multiSliceConfig = useMultiSliceStore((s) => s.config);
  const multiSliceLoaded = useMultiSliceStore((s) => s.loaded);
  const multiSliceInflight = useMultiSliceStore((s) => s.inflight);
  const multiSliceConflict = useMultiSliceStore((s) => s.conflict);
  const loadMultiSlice = useMultiSliceStore((s) => s.load);
  const setMultiSliceEnabled = useMultiSliceStore((s) => s.setEnabled);
  const setMultiSliceCount = useMultiSliceStore((s) => s.setNumActiveSlices);
  const psEnabled = useTxStore((s) => s.psEnabled);

  useEffect(() => {
    load();
    loadMultiSlice();
  }, [load, loadMultiSlice]);

  const connectedKnown = selection.connected !== 'Unknown';
  const overrideOn = selection.overrideDetection && selection.preferred !== 'Auto';
  // Variant dropdown only makes sense when the active board is OrionMkII
  // (the 0x0A wire-byte alias family). For every other board the selected
  // variant has no effect, so we hide it to avoid implying it does.
  const showVariant =
    selection.effective === 'OrionMkII' || selection.preferred === 'OrionMkII';
  // "Mismatch" without override = discovery wins, badge is a warning.
  // With override = the operator deliberately chose this; the OVERRIDE badge
  // takes the warning slot instead.
  const mismatch =
    !overrideOn &&
    selection.preferred !== 'Auto' &&
    connectedKnown &&
    selection.preferred !== selection.connected;

  // Multi-slice surfaces only when the connected board can do >1 DDC.
  // Hermes-class / G2E radios skip this entire row — bit-identical to the
  // pre-multi-slice settings for those operators.
  const showMultiSlice = maxReceivers > 1;
  const multiSliceDisabled =
    !multiSliceLoaded || multiSliceInflight || (psEnabled && !multiSliceConfig.enabled);
  const multiSliceTooltip = psEnabled
    ? 'PureSignal in use — disable PS to enable multi-slice.'
    : multiSliceConflict === 'puresignal'
      ? 'PureSignal in use — disable PS to enable multi-slice.'
      : `Open up to ${maxReceivers} simultaneous DDC receivers. Phase 1: HL2 only, RX0 audio only.`;

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        background: 'var(--bg-0)',
        borderBottom: '1px solid var(--panel-border)',
      }}
    ><div
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 12,
        padding: '10px 22px',
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

      {showVariant && (
        <label
          htmlFor="radio-variant"
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 6,
            fontSize: 10,
            fontWeight: 700,
            letterSpacing: '0.14em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
          }}
          title={
            'Variant — disambiguates the 0x0A wire byte\n\n' +
            'ANAN-G2 / G2 MkII / G2-1K / 7000DLE / 8000DLE / Apache OrionMkII /\n' +
            'ANVELINA-PRO3 / Red Pitaya all report the same board ID on discovery.\n' +
            'Pick the actual variant so PA gain / rated watts / forward-power\n' +
            'calibration match your hardware. G2 is the shipping default — leave\n' +
            'it unless you know the radio is one of the others.'
          }
        >
          Variant
          <select
            id="radio-variant"
            value={variant}
            disabled={!loaded || inflight}
            onChange={(e) => setVariant(e.target.value as OrionMkIIVariant)}
            style={{
              minWidth: 200,
              padding: '4px 8px',
              fontSize: 12,
              background: 'var(--bg-2)',
              color: 'var(--fg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm, 3px)',
              textTransform: 'none',
              letterSpacing: 0,
              fontWeight: 400,
            }}
          >
            {VARIANT_OPTIONS.map((v) => (
              <option key={v} value={v}>
                {ORION_MKII_VARIANT_LABELS[v]}
              </option>
            ))}
          </select>
        </label>
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
    {showMultiSlice && (
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          padding: '8px 22px 10px',
          fontSize: 12,
          color: 'var(--fg-1)',
          borderTop: '1px solid var(--panel-border)',
        }}
      >
        <label
          style={{
            fontSize: 10,
            fontWeight: 700,
            letterSpacing: '0.14em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
          }}
        >
          Multi-Slice (RX)
        </label>
        <label
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 6,
            fontSize: 11,
            color: multiSliceDisabled ? 'var(--fg-3)' : 'var(--fg-1)',
            cursor: multiSliceDisabled ? 'not-allowed' : 'pointer',
          }}
          title={multiSliceTooltip}
        >
          <input
            type="checkbox"
            checked={multiSliceConfig.enabled}
            disabled={multiSliceDisabled}
            onChange={(e) => setMultiSliceEnabled(e.target.checked)}
          />
          <span>Enable Multi-Slice (RX)</span>
        </label>

        {multiSliceConfig.enabled && (
          <label
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: 6,
              fontSize: 11,
              color: 'var(--fg-2)',
            }}
            title={`Number of DDC receivers to open (max ${maxReceivers} for this board). Audio mixing is RX0-only in Phase 1.`}
          >
            <span>Slices</span>
            <input
              type="number"
              min={1}
              max={maxReceivers}
              value={Math.min(multiSliceConfig.numActiveSlices, maxReceivers)}
              disabled={multiSliceInflight || !multiSliceLoaded}
              onChange={(e) => {
                const next = Number(e.target.value);
                if (!Number.isFinite(next)) return;
                const clamped = Math.max(1, Math.min(maxReceivers, Math.floor(next)));
                setMultiSliceCount(clamped);
              }}
              style={{
                width: 56,
                padding: '4px 6px',
                fontSize: 12,
                background: 'var(--bg-2)',
                color: 'var(--fg-0)',
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-sm, 3px)',
              }}
            />
            <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
              of {maxReceivers}
            </span>
          </label>
        )}

        {multiSliceConflict === 'puresignal' && (
          <span
            style={{
              fontSize: 10,
              color: 'var(--tx)',
              background: 'var(--tx-soft)',
              padding: '2px 6px',
              borderRadius: 2,
            }}
            title="Backend refused: PureSignal owns DDC slots. Disable PS first, then enable multi-slice."
          >
            BLOCKED BY PS
          </span>
        )}

        <span
          style={{
            marginLeft: 'auto',
            fontSize: 10,
            color: 'var(--fg-3)',
          }}
          title="Phase 1 limits: HL2 only, audio always from RX0, TX always on RX0. Per-slice AGC/filter UI is deferred."
        >
          Phase 1 · HL2 only · RX0 audio + TX
        </span>
      </div>
    )}
    </div>
  );
}
