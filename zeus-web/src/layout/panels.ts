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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import type { ComponentType } from 'react';
import { HeroPanel } from './panels/HeroPanel';
import { VfoPanel } from './panels/VfoPanel';
import { SMeterPanel } from './panels/SMeterPanel';
import { QrzPanel } from './panels/QrzPanel';
import { AzimuthPanel } from './panels/AzimuthPanel';
import { DspFlexPanel } from './panels/DspFlexPanel';
import { CwPanel } from './panels/CwPanel';
import { LogbookPanel } from './panels/LogbookPanel';
import { TxMetersPanel } from './panels/TxMetersPanel';
import { FilterRibbonPanel } from './panels/FilterRibbonPanel';
import { PsFlexPanel } from './panels/PsFlexPanel';

export type PanelCategory = 'spectrum' | 'vfo' | 'meters' | 'dsp' | 'log' | 'tools';

export interface PanelDef {
  id: string;
  name: string;
  category: PanelCategory;
  tags: string[];
  component: ComponentType;
}

// Panel registry: maps component-id strings (used in the flexlayout JSON model)
// to panel metadata and the React component that renders the panel body.
// Phase 3 will add an "Add Panel" modal that reads this registry.
export const PANELS: Record<string, PanelDef> = {
  hero: {
    id: 'hero',
    name: 'Panadapter · World Map',
    category: 'spectrum',
    tags: ['panadapter', 'waterfall', 'spectrum', 'map'],
    component: HeroPanel,
  },
  vfo: {
    id: 'vfo',
    name: 'Frequency · VFO',
    category: 'vfo',
    tags: ['frequency', 'vfo', 'tuning'],
    component: VfoPanel,
  },
  smeter: {
    id: 'smeter',
    name: 'S-Meter',
    category: 'meters',
    tags: ['signal', 'meter', 'rx', 'smeter'],
    component: SMeterPanel,
  },
  qrz: {
    id: 'qrz',
    name: 'QRZ Lookup',
    category: 'tools',
    tags: ['qrz', 'callsign', 'lookup', 'station'],
    component: QrzPanel,
  },
  azimuth: {
    id: 'azimuth',
    name: 'Azimuth Map',
    category: 'tools',
    tags: ['azimuth', 'map', 'bearing', 'great-circle'],
    component: AzimuthPanel,
  },
  dsp: {
    id: 'dsp',
    name: 'DSP',
    category: 'dsp',
    tags: ['dsp', 'noise', 'filter', 'nr', 'anf'],
    component: DspFlexPanel,
  },
  cw: {
    id: 'cw',
    name: 'CW Keyer',
    category: 'tools',
    tags: ['cw', 'morse', 'keyer', 'wpm'],
    component: CwPanel,
  },
  logbook: {
    id: 'logbook',
    name: 'Logbook',
    category: 'log',
    tags: ['log', 'qso', 'logbook', 'adif'],
    component: LogbookPanel,
  },
  txmeters: {
    id: 'txmeters',
    name: 'TX Stage Meters',
    category: 'meters',
    tags: ['tx', 'power', 'swr', 'alc', 'meters'],
    component: TxMetersPanel,
  },
  filter: {
    id: 'filter',
    name: 'Bandwidth Filter',
    category: 'dsp',
    tags: ['filter', 'bandwidth', 'passband', 'ribbon'],
    component: FilterRibbonPanel,
  },
  ps: {
    id: 'ps',
    name: 'PureSignal',
    category: 'tools',
    tags: ['puresignal', 'ps', 'tx', 'predistortion', 'linearization', 'twotone'],
    component: PsFlexPanel,
  },
};
