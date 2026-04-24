// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { FilterRibbon } from '../../components/filter/FilterRibbon';

export function FilterRibbonPanel() {
  return (
    <div className="filter-ribbon-panel" style={{ flex: 1, overflow: 'auto', padding: 8 }}>
      <FilterRibbon embedded />
    </div>
  );
}
