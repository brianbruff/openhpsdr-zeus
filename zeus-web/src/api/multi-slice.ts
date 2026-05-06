// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Re-exports for the multi-slice (multi-receiver) operator preference. The
// transport lives on `client.ts` (`setMultiSliceConfig`) — this module
// re-exposes the shapes and default so call sites that don't otherwise
// touch the radio-state DTO have a stable import path.
//
// Wire contract:
//   POST /api/multi-slice  body: MultiSliceConfigDto  → full RadioStateDto
//
// The initial value is read from `/api/state.multiSlice` on connect; there
// is no separate GET. Backend refuses to enable while PureSignal is on
// (logs a warning and returns Enabled=false in the snapshot) — clients
// detect that by comparing the request to the response.

export type {
  MultiSliceConfigDto as MultiSliceConfig,
} from './client';
export { MULTI_SLICE_DEFAULT, setMultiSliceConfig } from './client';
