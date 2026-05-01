// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Workspace-tile chrome: 24 px header strip with a drag-handle grip on the
// left, the panel title in the middle, and an X remove button on the right.
// The drag handle's CSS class (`.workspace-tile-drag-handle`) is the only
// element RGL listens to via `dragConfig.handle` — clicks on the title or
// inside the panel body don't initiate a drag, so the panel's own controls
// keep working.

import { GripVertical, X } from 'lucide-react';
import type { ReactNode } from 'react';

export interface TileChromeProps {
  title: string;
  onRemove: () => void;
  /** Optional extra header buttons rendered between the title and the
   *  remove X (e.g. a panel-specific gear icon). */
  rightSlot?: ReactNode;
}

export function TileChrome({ title, onRemove, rightSlot }: TileChromeProps) {
  return (
    <div className="workspace-tile-header">
      <span
        className="workspace-tile-drag-handle"
        aria-hidden="true"
        title="Drag to reposition"
      >
        <GripVertical size={12} />
      </span>
      <span className="workspace-tile-title" title={title}>
        {title}
      </span>
      {rightSlot}
      <button
        type="button"
        className="workspace-tile-close"
        aria-label={`Remove ${title}`}
        title="Remove panel"
        onClick={(e) => {
          e.stopPropagation();
          onRemove();
        }}
        onPointerDown={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <X size={12} />
      </button>
    </div>
  );
}
