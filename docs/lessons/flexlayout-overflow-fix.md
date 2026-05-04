# Flex Layout Overflow Fix

## Issue

When using the Flex Layout mode (`?layout=flex`), button presses and frequency changes on the panadapter were blocked or not registering properly. Users reported that some UI buttons did not respond to clicks and frequency changes via click-to-tune or drag gestures would fail or be ignored.

## Root Cause

The `.flexlayout__tab` CSS class in `zeus-web/src/styles/flex-layout.css` had `overflow: hidden` set. This was clipping or interfering with absolutely positioned interactive overlay components inside the Panadapter:

1. **FreqAxis** (`zeus-web/src/components/FreqAxis.tsx`) - Frequency tick marks and labels at the top, with `z-10` and `pointer-events-none`
2. **DbScale** (`zeus-web/src/components/DbScale.tsx`) - Draggable dB scale on the left edge, with `z-10` and **interactive** (has pointer event handlers)
3. **PassbandOverlay** (`zeus-web/src/components/PassbandOverlay.tsx`) - Filter passband rectangle, with `z-[5]` and `pointer-events-none`

When the HeroPanel (containing the Panadapter) was rendered inside a flexlayout tab, the `overflow: hidden` on the parent `.flexlayout__tab` was preventing proper hit testing of these absolutely positioned elements, especially the interactive DbScale component and potentially affecting the gesture handling on the underlying canvas.

## Solution

Added a targeted CSS rule that allows overflow **only** for tabs containing the HeroPanel:

```css
/* Allow overflow for HeroPanel to prevent clipping of interactive overlays
   (FreqAxis, DbScale) which are absolutely positioned and need to extend
   beyond the tab container for proper hit testing. */
.flexlayout__tab:has(.hero) {
  overflow: visible;
}
```

This uses the `:has()` CSS pseudo-class (modern browsers) to selectively apply `overflow: visible` only to tabs that contain an element with the `.hero` class, preserving the original `overflow: hidden` behavior for all other flexlayout tabs.

## Why This Works

1. **Preserves Original Intent**: Other tabs in the flex layout still have `overflow: hidden`, preventing unwanted content spillage.

2. **Fixes Hit Testing**: Absolutely positioned overlays in the HeroPanel can now extend beyond the tab boundaries if needed, ensuring their interactive areas are accessible to pointer events.

3. **Minimal Change**: The fix is surgical - it only affects tabs containing the panadapter/waterfall display, not the entire layout system.

## Alternative Approaches Considered

1. **Change all tabs to `overflow: visible`**: Rejected because this could cause layout issues with other panel types that rely on clipping.

2. **Remove absolute positioning from overlays**: Rejected because the current positioning strategy (using percentages relative to the spectrum span) is elegant and performs well.

3. **Add `pointer-events: none` to the flexlayout tab**: Rejected because this would break all interactivity, not just fix the specific issue.

## Browser Compatibility

The `:has()` pseudo-class is supported in:
- Chrome/Edge 105+
- Firefox 121+
- Safari 15.4+

Given Zeus's target audience (radio operators) and modern browser requirements, this is acceptable. If broader compatibility is needed in the future, the rule could be replaced with a more specific class-based approach.

## Related Files

- `zeus-web/src/styles/flex-layout.css` - CSS fix location
- `zeus-web/src/layout/panels/HeroPanel.tsx` - HeroPanel component with `.hero` class
- `zeus-web/src/components/Panadapter.tsx` - Canvas and overlay container
- `zeus-web/src/components/FreqAxis.tsx` - Frequency axis overlay
- `zeus-web/src/components/DbScale.tsx` - Interactive dB scale overlay
- `zeus-web/src/components/PassbandOverlay.tsx` - Filter passband visualization

## Testing

To verify the fix:

1. Start Zeus with `?layout=flex` query parameter
2. Open the panadapter panel
3. Attempt to click-to-tune on the panadapter canvas
4. Try dragging the dB scale on the left edge
5. Test button clicks in the panel header (SP, LP, BEAM controls when QRZ mode active)
6. Verify frequency changes register correctly
7. Confirm other flexlayout tabs still clip content properly (e.g., DSP panel, CW keyer)

## Prevention

When adding new absolutely positioned overlays to the Panadapter or similar components in flex layout:

1. Ensure they have appropriate `pointer-events` settings (`none` for non-interactive, default for interactive)
2. Test in flex layout mode (`?layout=flex`) to verify hit testing works
3. Check that z-index stacking is correct (see `docs/lessons/dev-conventions.md` for z-index hierarchy)
