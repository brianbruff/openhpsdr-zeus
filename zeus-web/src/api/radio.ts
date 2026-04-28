// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

// String-valued board kinds are mirrored from Zeus.Protocol1.Discovery.
// HpsdrBoardKind; we intentionally use strings over the wire (not numeric
// bytes) so the JSON is legible when debugging with curl and resilient
// to new boards being added on the backend without a frontend recompile.
export type BoardKind =
  | 'Auto'
  | 'Metis'
  | 'Hermes'
  | 'Griffin'
  | 'Angelia'
  | 'Orion'
  | 'HermesLite2'
  | 'OrionMkII'
  | 'Unknown';

export interface RadioSelection {
  preferred: BoardKind;
  connected: BoardKind;
  effective: BoardKind;
  overrideDetection: boolean;
}

// Operator-facing labels per board. Keep in sync with BOARD_OPTIONS in
// RadioSelector.tsx.
export const BOARD_LABELS: Record<BoardKind, string> = {
  Auto: 'Auto-detect',
  Metis: 'Metis',
  Hermes: 'Hermes / ANAN-10/10E',
  Griffin: 'Griffin',
  Angelia: 'ANAN-100 / 100B / 8000D',
  Orion: 'ANAN-100D / 200D',
  HermesLite2: 'Hermes Lite 2',
  OrionMkII: 'ANAN G2 / 7000D / G1',
  Unknown: 'Unknown',
};

function normalizeBoard(v: unknown): BoardKind {
  if (typeof v !== 'string') return 'Unknown';
  switch (v) {
    case 'Auto':
    case 'Metis':
    case 'Hermes':
    case 'Griffin':
    case 'Angelia':
    case 'Orion':
    case 'HermesLite2':
    case 'OrionMkII':
    case 'Unknown':
      return v;
    default:
      return 'Unknown';
  }
}

export async function fetchRadioSelection(signal?: AbortSignal): Promise<RadioSelection> {
  const res = await fetch('/api/radio/selection', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/selection → ${res.status}`);
  const raw = (await res.json()) as {
    preferred?: unknown;
    connected?: unknown;
    effective?: unknown;
    overrideDetection?: unknown;
  };
  return {
    preferred: normalizeBoard(raw.preferred),
    connected: normalizeBoard(raw.connected),
    effective: normalizeBoard(raw.effective),
    overrideDetection: typeof raw.overrideDetection === 'boolean' ? raw.overrideDetection : false,
  };
}

export async function updateRadioSelection(
  preferred: BoardKind,
  overrideDetection?: boolean,
  signal?: AbortSignal,
): Promise<RadioSelection> {
  const body: { preferred: string; overrideDetection?: boolean } = { preferred };
  if (overrideDetection !== undefined) {
    body.overrideDetection = overrideDetection;
  }
  const res = await fetch('/api/radio/selection', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/selection → ${res.status}`);
  const raw = (await res.json()) as {
    preferred?: unknown;
    connected?: unknown;
    effective?: unknown;
    overrideDetection?: unknown;
  };
  return {
    preferred: normalizeBoard(raw.preferred),
    connected: normalizeBoard(raw.connected),
    effective: normalizeBoard(raw.effective),
    overrideDetection: typeof raw.overrideDetection === 'boolean' ? raw.overrideDetection : false,
  };
}
