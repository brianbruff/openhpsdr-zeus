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

const OC_PINS = [1, 2, 3, 4, 5, 6, 7] as const;

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
  const save = usePaStore((s) => s.save);
  const setGlobal = usePaStore((s) => s.setGlobal);
  const setBand = usePaStore((s) => s.setBand);

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

          <label className="flex items-center gap-2 text-xs text-neutral-300">
            Max Power (W)
            <input
              type="number"
              min={0}
              max={2000}
              step={1}
              value={settings.global.paMaxPowerWatts}
              onChange={(e) => setGlobal({ paMaxPowerWatts: Number(e.target.value) || 0 })}
              className="w-20 rounded border border-neutral-700 bg-neutral-900 px-2 py-0.5 text-right text-xs text-neutral-100"
            />
            <span className="text-[10px] text-neutral-500">
              (0 = legacy, no watts math)
            </span>
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
                <th className="px-2 py-2 text-right">Gain (dB)</th>
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
                        step={0.1}
                        min={-10}
                        max={80}
                        value={b.paGainDb}
                        onChange={(e) =>
                          setBand(b.band, { paGainDb: Number(e.target.value) || 0 })
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

      <div className="flex items-center justify-between">
        <span className="text-[11px] text-neutral-500">
          {inflight ? 'Saving…' : loaded ? 'Loaded from server' : 'Loading…'}
          {error ? ` · error: ${error}` : ''}
        </span>
        <button
          type="button"
          className="btn sm"
          onClick={save}
          disabled={inflight}
          title="Persist PA settings and push to the active radio"
        >
          Apply
        </button>
      </div>
    </div>
  );
}
