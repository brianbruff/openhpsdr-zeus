// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { useEffect, useRef, useState } from 'react';
import { useLoggerStore } from '../../state/logger-store';
import { LogbookLive } from './LogbookLive';
import { LogbookDetail } from './LogbookDetail';
import { LogbookDashboard } from './LogbookDashboard';
import { LogbookHero } from './LogbookHero';

// Responsive shell for the Logbook. Docked as a narrow panel it is exactly the
// compact table it has always been — just restyled. Given real estate (the
// operator pops the Logbook tab out onto its own monitor) it blooms into the
// full workspace: table + live station-detail card + an analytics dashboard,
// and it hydrates the ENTIRE log so those stats are honest.
//
// The breakpoint is measured on the panel itself (not the viewport) because the
// same panel can be a slim dock strip or a full-screen pop-out.
const RICH_MIN_WIDTH = 880;
const RICH_MIN_HEIGHT = 540;

type Props = {
  searchText: string;
  hideQrzPublished: boolean;
};

export function LogbookWorkspace({ searchText, hideQrzPublished }: Props) {
  const rootRef = useRef<HTMLDivElement>(null);
  const [rich, setRich] = useState(false);
  const [activeId, setActiveId] = useState<string | null>(null);

  const entries = useLoggerStore((s) => s.entries);
  const totalCount = useLoggerStore((s) => s.totalCount);
  const fullyLoaded = useLoggerStore((s) => s.fullyLoaded);
  const loadAllEntries = useLoggerStore((s) => s.loadAllEntries);
  const requestedFullLoad = useRef(false);

  // Measure the panel and flip between compact and rich. Guarded so the unit
  // test environment (no ResizeObserver in jsdom) simply stays compact.
  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const measure = (w: number, h: number) => {
      setRich(w >= RICH_MIN_WIDTH && h >= RICH_MIN_HEIGHT);
    };
    measure(el.clientWidth, el.clientHeight);
    const ro = new ResizeObserver((observed) => {
      const rect = observed[0]?.contentRect;
      if (rect) measure(rect.width, rect.height);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // First time the workspace opens rich, pull the whole log so the table and
  // dashboard cover every QSO. Once loaded, post-mutation refreshes keep it full
  // (logger-store.refreshEntries), so this only needs to fire the initial fetch.
  useEffect(() => {
    if (rich && !fullyLoaded && !requestedFullLoad.current) {
      requestedFullLoad.current = true;
      void loadAllEntries();
    }
  }, [rich, fullyLoaded, loadAllEntries]);

  // Keep a valid focused QSO for the detail card: default to the first entry,
  // and recover if the focused one is filtered/deleted away.
  useEffect(() => {
    const first = entries[0];
    if (!rich || !first) return;
    if (!activeId || !entries.some((e) => e.id === activeId)) {
      setActiveId(first.id);
    }
  }, [rich, entries, activeId]);

  if (!rich) {
    return (
      <div ref={rootRef} className="logbook-workspace logbook-workspace--compact">
        <LogbookLive searchText={searchText} hideQrzPublished={hideQrzPublished} />
      </div>
    );
  }

  const activeEntry = entries.find((e) => e.id === activeId) ?? null;

  return (
    <div ref={rootRef} className="logbook-workspace logbook-workspace--rich">
      <div className="lb-main">
        <div className="lb-table">
          <LogbookLive
            searchText={searchText}
            hideQrzPublished={hideQrzPublished}
            activeId={activeId}
            onActivate={setActiveId}
          />
        </div>
        <LogbookHero entries={entries} totalCount={totalCount} />
        <div className="lb-detail">
          <LogbookDetail entry={activeEntry} />
        </div>
      </div>
      <LogbookDashboard entries={entries} totalCount={totalCount} fullyLoaded={fullyLoaded} />
    </div>
  );
}
