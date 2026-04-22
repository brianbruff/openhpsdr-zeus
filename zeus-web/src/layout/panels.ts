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
};
