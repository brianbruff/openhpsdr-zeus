# Flex Layout Widget System

## Overview

Zeus flex layout now supports a dynamic widget system that allows users to add and remove panels at runtime. This improves flexibility and customization compared to the previous static layout.

## New Widget Panels

Three new widget panels have been added to the flex layout system:

### 1. Band Buttons Panel (`band`)
- Wraps the existing `BandButtons` component
- Provides HF band selection (160m-10m)
- Category: `controls`
- Can be added via the "+" button in flex layout

### 2. Mode Buttons Panel (`mode`)
- Wraps the existing `ModeBandwidth` component
- Provides mode selection (LSB, USB, CWL, CWU, AM, SAM, DSB, FM, DIGL, DIGU)
- Category: `controls`
- Can be added via the "+" button in flex layout

### 3. Tuning Step Panel (`step`)
- New `TuningStepWidget` component
- Provides tuning step selection and up/down tuning buttons
- Supports Thetis-compatible step sizes:
  - 1, 10, 50, 100, 250, 500 Hz
  - 1, 5, 9, 10, 100, 250 kHz
  - 1 MHz
- Category: `controls`
- Default step: 500 Hz

## Add Panel Feature

The flex layout now includes a "+" button in the top-right corner that opens an "Add Panel" modal.

### Features:
- **Search**: Filter panels by name or tags
- **Category filter**: Filter by spectrum, vfo, meters, dsp, log, tools, or controls
- **Duplicate prevention**: Each panel can only be added once to the layout
- **Dynamic addition**: Panels are added to the first tabset in the layout

### Usage:
1. Click the "+" button in the top-right of the flex layout
2. Search or browse available panels
3. Click a panel to add it to your layout
4. The panel appears as a new tab in an existing tabset

## Implementation Details

### Panel Registry (`panels.ts`)
All panels are registered in the `PANELS` object with metadata:
- `id`: Unique identifier used in the layout JSON
- `name`: Display name shown in tabs and modal
- `category`: Grouping category for filtering
- `tags`: Search keywords
- `component`: React component to render

### Duplicate Prevention
The `getExistingPanels()` function walks the layout tree and collects all panel IDs currently in use. The Add Panel modal filters out these panels to prevent duplicates.

### flexlayout-react Integration
- Uses `Actions.addNode()` to dynamically add panels
- Adds to `DockLocation.CENTER` of the first tabset
- Layout changes trigger automatic persistence to server

## Future Enhancements

Possible future improvements mentioned in the issue:
- Support for multiple instances of certain panels (e.g., multiple panadapters for G2 radios with up to 8 receivers)
- Drag-and-drop from a palette instead of modal
- Preset layouts for common use cases
- More granular widget configuration (e.g., which bands to show in Band Buttons)
