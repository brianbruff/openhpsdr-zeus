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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useRef, useState } from 'react';
import {
  useDisplaySettingsStore,
  type BackgroundImageFit,
  type PanBackgroundMode,
} from '../state/display-settings-store';

const MODE_OPTIONS: ReadonlyArray<{ id: PanBackgroundMode; label: string; help: string }> = [
  { id: 'basic', label: 'Basic', help: 'Plain panadapter and waterfall — no overlay.' },
  { id: 'beam-map', label: 'Beam Map', help: 'World map behind the panadapter, with QRZ contact and rotator overlays when configured.' },
  { id: 'image', label: 'Image', help: 'Show a custom image behind the panadapter.' },
];

const FIT_OPTIONS: ReadonlyArray<{ id: BackgroundImageFit; label: string }> = [
  { id: 'fit', label: 'Fit' },
  { id: 'fill', label: 'Fill' },
  { id: 'stretch', label: 'Stretch' },
];

// Downscale large images on the way into localStorage so a phone-camera
// JPEG doesn't blow the ~5 MB browser quota. We never upscale; if the
// source is already <= MAX_DIM on its longest edge it passes through
// unchanged. JPEG quality 0.85 is a sweet spot for photographs; PNGs are
// re-encoded as JPEG since the panadapter background never benefits from
// alpha or lossless edges.
const MAX_DIM = 1920;
const JPEG_QUALITY = 0.85;

async function fileToCompressedDataUrl(file: File): Promise<string> {
  const url = URL.createObjectURL(file);
  try {
    const img = await loadImage(url);
    const longest = Math.max(img.naturalWidth, img.naturalHeight);
    const scale = longest > MAX_DIM ? MAX_DIM / longest : 1;
    const w = Math.max(1, Math.round(img.naturalWidth * scale));
    const h = Math.max(1, Math.round(img.naturalHeight * scale));
    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('Canvas 2D unavailable');
    ctx.drawImage(img, 0, 0, w, h);
    return canvas.toDataURL('image/jpeg', JPEG_QUALITY);
  } finally {
    URL.revokeObjectURL(url);
  }
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('Image decode failed'));
    img.src = src;
  });
}

export function BackgroundSettingsPanel() {
  const panBackground = useDisplaySettingsStore((s) => s.panBackground);
  const setPanBackground = useDisplaySettingsStore((s) => s.setPanBackground);
  const backgroundImage = useDisplaySettingsStore((s) => s.backgroundImage);
  const setBackgroundImage = useDisplaySettingsStore((s) => s.setBackgroundImage);
  const backgroundImageFit = useDisplaySettingsStore((s) => s.backgroundImageFit);
  const setBackgroundImageFit = useDisplaySettingsStore((s) => s.setBackgroundImageFit);

  const fileInputRef = useRef<HTMLInputElement>(null);
  const [dropActive, setDropActive] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleFile = async (file: File | null | undefined) => {
    if (!file) return;
    if (!file.type.startsWith('image/')) {
      setError('Not an image file.');
      return;
    }
    setError(null);
    setBusy(true);
    try {
      const dataUrl = await fileToCompressedDataUrl(file);
      const ok = setBackgroundImage(dataUrl);
      if (!ok) {
        setError('Browser refused to store the image (quota exceeded). Try a smaller picture.');
      } else if (panBackground !== 'image') {
        setPanBackground('image');
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const onDragEnter = (e: React.DragEvent) => {
    e.preventDefault();
    if (e.dataTransfer?.types.includes('Files')) setDropActive(true);
  };
  const onDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  };
  const onDragLeave = (e: React.DragEvent) => {
    if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
    setDropActive(false);
  };
  const onDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDropActive(false);
    const file = e.dataTransfer.files?.[0];
    void handleFile(file);
  };

  return (
    <section>
      <h3 style={sectionH3}>Panadapter Background</h3>
      <p style={sectionP}>
        Choose what's drawn behind the panadapter and waterfall. Beam Map needs
        QRZ configured under the QRZ tab to populate contact lookups.
      </p>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {MODE_OPTIONS.map((opt) => {
          const active = panBackground === opt.id;
          return (
            <label key={opt.id} style={modeRowStyle(active)}>
              <input
                type="radio"
                name="pan-background"
                value={opt.id}
                checked={active}
                onChange={() => setPanBackground(opt.id)}
                style={{ cursor: 'pointer' }}
              />
              <div style={{ flex: 1 }}>
                <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-0)' }}>{opt.label}</div>
                <div style={{ fontSize: 11, color: 'var(--fg-2)', marginTop: 2 }}>{opt.help}</div>
              </div>
            </label>
          );
        })}
      </div>

      {panBackground === 'image' && (
        <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div
            onDragEnter={onDragEnter}
            onDragOver={onDragOver}
            onDragLeave={onDragLeave}
            onDrop={onDrop}
            onClick={() => fileInputRef.current?.click()}
            role="button"
            tabIndex={0}
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') fileInputRef.current?.click(); }}
            style={dropZoneStyle(dropActive)}
          >
            {backgroundImage ? (
              <img
                src={backgroundImage}
                alt="Background preview"
                style={{
                  maxWidth: '100%',
                  maxHeight: 140,
                  borderRadius: 'var(--r-xs)',
                  display: 'block',
                  margin: '0 auto',
                }}
              />
            ) : (
              <div style={{ textAlign: 'center', color: 'var(--fg-2)', fontSize: 12 }}>
                {dropActive ? 'Release to load image' : 'Drop an image here, or click to choose a file'}
              </div>
            )}
            <input
              ref={fileInputRef}
              type="file"
              accept="image/*"
              style={{ display: 'none' }}
              onChange={(e) => {
                void handleFile(e.target.files?.[0]);
                e.currentTarget.value = '';
              }}
            />
          </div>

          {busy && <div style={{ fontSize: 11, color: 'var(--fg-2)' }}>Processing…</div>}
          {error && <div style={{ fontSize: 11, color: 'var(--tx)' }}>{error}</div>}

          <div>
            <div style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-1)', marginBottom: 6 }}>
              Sizing
            </div>
            <div style={{ display: 'flex', gap: 6 }}>
              {FIT_OPTIONS.map((opt) => {
                const active = backgroundImageFit === opt.id;
                return (
                  <button
                    key={opt.id}
                    type="button"
                    className={`btn sm ${active ? 'active' : ''}`}
                    onClick={() => setBackgroundImageFit(opt.id)}
                  >
                    {opt.label}
                  </button>
                );
              })}
            </div>
          </div>

          {backgroundImage && (
            <button
              type="button"
              className="btn sm"
              style={{ alignSelf: 'flex-start' }}
              onClick={() => setBackgroundImage(null)}
            >
              CLEAR IMAGE
            </button>
          )}
        </div>
      )}
    </section>
  );
}

const sectionH3: React.CSSProperties = {
  margin: '0 0 10px 0',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.12em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};
const sectionP: React.CSSProperties = {
  margin: '0 0 12px 0',
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};
function modeRowStyle(active: boolean): React.CSSProperties {
  return {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    padding: '8px 12px',
    borderRadius: 'var(--r-sm)',
    background: active ? 'var(--bg-2)' : 'transparent',
    border: '1px solid',
    borderColor: active ? 'var(--accent)' : 'var(--line)',
    cursor: 'pointer',
    transition: 'all var(--dur-fast)',
  };
}
function dropZoneStyle(active: boolean): React.CSSProperties {
  return {
    border: '2px dashed',
    borderColor: active ? 'var(--accent)' : 'var(--line)',
    borderRadius: 'var(--r-sm)',
    padding: 14,
    cursor: 'pointer',
    background: active ? 'rgba(74, 158, 255, 0.06)' : 'rgba(255,255,255,0.02)',
    transition: 'all var(--dur-fast)',
  };
}
