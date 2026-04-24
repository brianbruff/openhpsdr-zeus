// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect } from 'react';
import { HF_BANDS, usePaStore } from '../state/pa-store';
import { useConnectionStore } from '../state/connection-store';

const OC_PINS = [1, 2, 3, 4, 5, 6, 7] as const;

// HL2 uses a percentage-based PA model (mi0bot openhpsdr-thetis) — the
// PaGainDb DTO field is interpreted as output % 0..100 rather than dB
// forward gain. Backend HermesLite2DriveProfile enforces this; frontend
// relabels the input and widens the clamp so the operator can actually
// type 100. See docs/lessons/hl2-drive-model.md.
const HL2_BOARD_ID = 'HermesLite2';

// Physical sanity bounds — guards against typos like "100" (intended as a
// percentage) landing in the dB field on non-HL2 radios, which collapses
// the drive byte to 0.
const PA_GAIN_MIN_DB  = 0;
const PA_GAIN_MAX_DB  = 70;    // G2-class radios top out ~51 dB; 70 leaves headroom
const PA_GAIN_MAX_PCT = 100;   // HL2: value is an output percentage
const PA_MAX_W_MIN    = 0;
const PA_MAX_W_MAX    = 1500;  // Covers Shared Apex / 1 kW + amps

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

function OcBitCheckbox({
  label,
  bit,
  mask,
  onToggle,
}: {
  label: string;
  bit: number;
  mask: number;
  onToggle: (nextMask: number) => void;
}) {
  const active = (mask & (1 << (bit - 1))) !== 0;
  return (
    <label
      title={`${label} pin ${bit}`}
      className="inline-flex select-none items-center gap-1 text-[10px] text-neutral-400"
    >
      <input
        type="checkbox"
        checked={active}
        onChange={(e) => {
          const b = 1 << (bit - 1);
          onToggle(e.target.checked ? mask | b : mask & ~b);
        }}
        className="h-3 w-3 accent-[#4a9eff]"
      />
      {bit}
    </label>
  );
}

export function PaSettingsPanel() {
  const settings = usePaStore((s) => s.settings);
  const loaded = usePaStore((s) => s.loaded);
  const inflight = usePaStore((s) => s.inflight);
  const error = usePaStore((s) => s.error);
  const load = usePaStore((s) => s.load);
  const setGlobal = usePaStore((s) => s.setGlobal);
  const setBand = usePaStore((s) => s.setBand);
  const boardId = useConnectionStore((s) => s.boardId);

  // HL2 overloads the "PA Gain" field into an output percentage. Switch
  // label + clamp range + step accordingly. Non-HL2 radios keep the dB
  // convention (Hermes / ANAN / Orion).
  const isHl2 = boardId === HL2_BOARD_ID;
  const paFieldLabel = isHl2 ? 'PA Output (%)' : 'PA Gain (dB)';
  const paFieldMax   = isHl2 ? PA_GAIN_MAX_PCT : PA_GAIN_MAX_DB;
  const paFieldStep  = isHl2 ? 1 : 0.1;
  const paFieldTitle = isHl2
    ? 'HL2 output percentage per band (0..100). HL2 uses a different PA model than other HPSDR radios: 100 = no attenuation (rated power); lower values soft-cap output for weaker bands (6 m stock is ~38.8). NOT decibels.'
    : 'PA forward gain in dB per band — the amplifier\'s own gain from DUC output to antenna. NOT a trim. Seeded from the board kind (e.g. G2 MkII ≈ 48-51 dB on HF). Used together with Rated PA Output (W) to compute the drive byte: lower gain here → more drive byte → more output at a given slider %.';

  useEffect(() => {
    load();
  }, [load]);

  return (
    <div className="space-y-6">
      <section>
        <h3 className="mb-2 text-xs font-semibold uppercase tracking-widest text-neutral-300">
          Global
        </h3>
        <div className="grid grid-cols-1 gap-4 rounded bg-neutral-800/40 p-3 md:grid-cols-3">
          <label className="flex items-center gap-2 text-xs text-neutral-300">
            <input
              type="checkbox"
              checked={settings.global.paEnabled}
              onChange={(e) => setGlobal({ paEnabled: e.target.checked })}
              className="h-4 w-4 accent-[#4a9eff]"
            />
            PA Enabled
          </label>

          <label
            className="flex items-center gap-2 text-xs text-neutral-300"
            title="Rated PA output in watts. Slider 100% targets this wattage. Seeded from the connected board kind — HL2 = 5 W, Hermes-class = 10 W, ANAN/Orion/G2 = 100 W. Set to 0 to fall back to the raw drive-byte mode (PA Gain field is ignored)."
          >
            Rated PA Output (W)
            <input
              type="number"
              min={PA_MAX_W_MIN}
              max={PA_MAX_W_MAX}
              step={1}
              value={settings.global.paMaxPowerWatts}
              onChange={(e) =>
                setGlobal({
                  paMaxPowerWatts: clamp(Number(e.target.value) || 0, PA_MAX_W_MIN, PA_MAX_W_MAX),
                })
              }
              className="w-20 rounded border border-neutral-700 bg-neutral-900 px-2 py-0.5 text-right text-xs text-neutral-100"
            />
            {settings.global.paMaxPowerWatts === 0 && (
              <span className="text-[10px] text-amber-400">
                (0 = raw drive-byte mode — PA Gain ignored)
              </span>
            )}
          </label>

          <div className="flex flex-col gap-1 text-xs text-neutral-300">
            <span>OC bits while Tune</span>
            <div className="flex gap-2">
              {OC_PINS.map((bit) => (
                <OcBitCheckbox
                  key={bit}
                  label="OC-Tune"
                  bit={bit}
                  mask={settings.global.ocTune}
                  onToggle={(next) => setGlobal({ ocTune: next })}
                />
              ))}
            </div>
          </div>
        </div>
      </section>

      <section>
        <h3 className="mb-2 text-xs font-semibold uppercase tracking-widest text-neutral-300">
          Per Band
        </h3>
        <div className="overflow-x-auto rounded bg-neutral-800/40">
          <table className="w-full border-collapse text-xs">
            <thead className="text-[10px] uppercase tracking-wider text-neutral-500">
              <tr>
                <th className="px-2 py-2 text-left">Band</th>
                <th className="px-2 py-2 text-right" title={paFieldTitle}>
                  {paFieldLabel}
                </th>
                <th className="px-2 py-2 text-center">Disable PA</th>
                <th className="px-2 py-2 text-left">OC TX (1..7)</th>
                <th className="px-2 py-2 text-left">OC RX (1..7)</th>
              </tr>
            </thead>
            <tbody>
              {HF_BANDS.map((bandName) => {
                const b = settings.bands.find((x) => x.band === bandName);
                if (!b) return null;
                return (
                  <tr key={bandName} className="border-t border-neutral-800 text-neutral-300">
                    <td className="px-2 py-1 font-mono">{b.band}</td>
                    <td className="px-2 py-1 text-right">
                      <input
                        type="number"
                        step={paFieldStep}
                        min={PA_GAIN_MIN_DB}
                        max={paFieldMax}
                        value={b.paGainDb}
                        onChange={(e) =>
                          setBand(b.band, {
                            paGainDb: clamp(Number(e.target.value) || 0, PA_GAIN_MIN_DB, paFieldMax),
                          })
                        }
                        className="w-20 rounded border border-neutral-700 bg-neutral-900 px-2 py-0.5 text-right text-neutral-100"
                      />
                    </td>
                    <td className="px-2 py-1 text-center">
                      <input
                        type="checkbox"
                        checked={b.disablePa}
                        onChange={(e) => setBand(b.band, { disablePa: e.target.checked })}
                        className="h-3 w-3 accent-[#4a9eff]"
                      />
                    </td>
                    <td className="px-2 py-1">
                      <div className="flex gap-2">
                        {OC_PINS.map((bit) => (
                          <OcBitCheckbox
                            key={bit}
                            label={`${bandName} OC-TX`}
                            bit={bit}
                            mask={b.ocTx}
                            onToggle={(next) => setBand(b.band, { ocTx: next })}
                          />
                        ))}
                      </div>
                    </td>
                    <td className="px-2 py-1">
                      <div className="flex gap-2">
                        {OC_PINS.map((bit) => (
                          <OcBitCheckbox
                            key={bit}
                            label={`${bandName} OC-RX`}
                            bit={bit}
                            mask={b.ocRx}
                            onToggle={(next) => setBand(b.band, { ocRx: next })}
                          />
                        ))}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <p className="text-[11px] text-neutral-500">
        {inflight ? 'Saving…' : loaded ? 'Loaded from server — use APPLY below to persist edits' : 'Loading…'}
        {error ? ` · error: ${error}` : ''}
      </p>
    </div>
  );
}
