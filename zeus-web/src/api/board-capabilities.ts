// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

// Per-board capability fingerprint (post-#218 Phase 2). Mirrors
// Zeus.Contracts.BoardCapabilities — see
// docs/references/protocol-1/thetis-board-matrix.md for the per-board
// values. The web reads this once at connect via /api/radio/capabilities
// and gates feature panels on the flags (Phase 5).
export interface BoardCapabilities {
  rxAdcCount: number;
  mkiiBpf: boolean;
  adcSupplyMv: number;
  lrAudioSwap: boolean;
  hasVolts: boolean;
  hasAmps: boolean;
  hasAudioAmplifier: boolean;
  hasSteppedAttenuationRx2: boolean;
  supportsPathIllustrator: boolean;
}

// Safe defaults matching Zeus.Contracts.BoardCapabilities.UnknownDefaults —
// used when the server hasn't responded yet or when the JSON is malformed.
// Every flag false / single ADC / 33 mV supply: no panels appear by
// default, which is the right behaviour for "we don't know what this is yet".
export const UNKNOWN_BOARD_CAPABILITIES: BoardCapabilities = {
  rxAdcCount: 1,
  mkiiBpf: false,
  adcSupplyMv: 33,
  lrAudioSwap: false,
  hasVolts: false,
  hasAmps: false,
  hasAudioAmplifier: false,
  hasSteppedAttenuationRx2: false,
  supportsPathIllustrator: false,
};

export function parseBoardCapabilities(raw: unknown): BoardCapabilities {
  if (!raw || typeof raw !== 'object') return UNKNOWN_BOARD_CAPABILITIES;
  const r = raw as Record<string, unknown>;
  return {
    rxAdcCount: typeof r.rxAdcCount === 'number' ? r.rxAdcCount : UNKNOWN_BOARD_CAPABILITIES.rxAdcCount,
    mkiiBpf: typeof r.mkiiBpf === 'boolean' ? r.mkiiBpf : UNKNOWN_BOARD_CAPABILITIES.mkiiBpf,
    adcSupplyMv: typeof r.adcSupplyMv === 'number' ? r.adcSupplyMv : UNKNOWN_BOARD_CAPABILITIES.adcSupplyMv,
    lrAudioSwap: typeof r.lrAudioSwap === 'boolean' ? r.lrAudioSwap : UNKNOWN_BOARD_CAPABILITIES.lrAudioSwap,
    hasVolts: typeof r.hasVolts === 'boolean' ? r.hasVolts : UNKNOWN_BOARD_CAPABILITIES.hasVolts,
    hasAmps: typeof r.hasAmps === 'boolean' ? r.hasAmps : UNKNOWN_BOARD_CAPABILITIES.hasAmps,
    hasAudioAmplifier:
      typeof r.hasAudioAmplifier === 'boolean'
        ? r.hasAudioAmplifier
        : UNKNOWN_BOARD_CAPABILITIES.hasAudioAmplifier,
    hasSteppedAttenuationRx2:
      typeof r.hasSteppedAttenuationRx2 === 'boolean'
        ? r.hasSteppedAttenuationRx2
        : UNKNOWN_BOARD_CAPABILITIES.hasSteppedAttenuationRx2,
    supportsPathIllustrator:
      typeof r.supportsPathIllustrator === 'boolean'
        ? r.supportsPathIllustrator
        : UNKNOWN_BOARD_CAPABILITIES.supportsPathIllustrator,
  };
}

export async function fetchBoardCapabilities(signal?: AbortSignal): Promise<BoardCapabilities> {
  const res = await fetch('/api/radio/capabilities', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/capabilities → ${res.status}`);
  return parseBoardCapabilities(await res.json());
}
