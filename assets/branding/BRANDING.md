# HASM Branding Assets — Raster Extraction v1

These assets were extracted directly from the approved 449×204 HASM composite reference image. The original image is preserved as `hasm-original-reference.png`. No typography, shapes, color palette or logo design were intentionally changed; background separation uses alpha masking.

## Files

- `hasm-mark.png`: original standalone colored symbol, transparent PNG (120×136 source-pixel-scale).
- `hasm-wordmark.png`: original Arabic حسم and English HASM lettering, transparent PNG (174×89).
- `hasm-lockup.png`: original symbol plus original two-line wordmark, without tagline/banner, transparent PNG (323×145).
- `hasm-icon.png`: square 1024×1024 transparent PNG containing the symbol, upscaled from the small source.
- `hasm-icon.ico`: Windows icon generated from the upscaled source, including common sizes.
- `hasm-original-reference.png`: original approved composite, unchanged for provenance. **Not a UI header logo.**

## Usage

- Standard Web/Desktop header: `hasm-mark.png`, with live translated application name/tagline text; alternatively `hasm-lockup.png` when a compact branded lockup is needed.
- Login/About: `hasm-lockup.png` plus any descriptive text rendered in application UI.
- Windows application icon: `hasm-icon.ico` or `hasm-icon.png` depending on Tauri's required asset paths.
- Avoid using `hasm-original-reference.png` as an interface logo.
- Keep aspect ratio. Do not regenerate/recolor/retype.

## Important image quality limitation

The approved source raster is only 449×204 pixels; the small mark and wordmark are extracted at near-native resolution. The 1024×1024 icon is upscaled, *not* a high-resolution master. Edges and tiny desktop tray icons may need cleanup using a true original vector/logo file before a commercial release. Do not call these extracted PNGs the official vector master or mistake the upscale for new detail.
