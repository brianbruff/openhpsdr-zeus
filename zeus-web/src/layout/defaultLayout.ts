// Default flexlayout-react model that replicates the current CSS grid:
//   left column (75%): hero spectrum (70%) + bottom row [logbook + tx meters] (30%)
//   right column (25%): VFO + SMeter + QRZ/Azimuth + DSP + CW (stacked)
//
// Phase 1 — operators who never drag panels see the same screen as today.
// Weights are approximate; flexlayout distributes remaining space proportionally.
export const DEFAULT_LAYOUT = {
  global: {
    tabEnableClose: true,
    tabSetMinHeight: 60,
    tabSetMinWidth: 80,
    tabSetTabStripHeight: 28,
  },
  borders: [],
  layout: {
    type: 'row',
    children: [
      {
        // Left column: hero above, bottom row below
        type: 'row',
        weight: 75,
        children: [
          {
            type: 'tabset',
            weight: 70,
            children: [
              { type: 'tab', name: 'Panadapter · World Map', component: 'hero' },
            ],
          },
          {
            // Bottom row: logbook + TX meters side by side
            type: 'row',
            weight: 30,
            children: [
              {
                type: 'tabset',
                weight: 60,
                children: [
                  { type: 'tab', name: 'Logbook', component: 'logbook' },
                ],
              },
              {
                type: 'tabset',
                weight: 40,
                children: [
                  { type: 'tab', name: 'TX Stage Meters', component: 'txmeters' },
                ],
              },
            ],
          },
        ],
      },
      {
        // Right column: side stack panels stacked vertically
        type: 'row',
        weight: 25,
        children: [
          {
            type: 'tabset',
            weight: 15,
            children: [
              { type: 'tab', name: 'Frequency · VFO', component: 'vfo' },
            ],
          },
          {
            type: 'tabset',
            weight: 15,
            children: [
              { type: 'tab', name: 'S-Meter', component: 'smeter' },
            ],
          },
          {
            type: 'tabset',
            weight: 40,
            children: [
              { type: 'tab', name: 'QRZ Lookup', component: 'qrz' },
              { type: 'tab', name: 'Azimuth Map', component: 'azimuth' },
            ],
          },
          {
            type: 'tabset',
            weight: 15,
            children: [
              { type: 'tab', name: 'DSP', component: 'dsp' },
            ],
          },
          {
            type: 'tabset',
            weight: 15,
            children: [
              { type: 'tab', name: 'CW Keyer', component: 'cw' },
            ],
          },
        ],
      },
    ],
  },
} as const;
