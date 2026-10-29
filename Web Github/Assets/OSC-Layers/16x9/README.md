# NVIDIA ShadowPlay NEXT — OSC 16:9 ARGB Layers

Actual independent rendered layers from **NVIDIA OSC.exe** via CEF GPU OSR surface isolation; not AI-generated or cropped from a combined Desktop screenshot.

| Download | Size | Description |
| --- | --- | --- |
| [osc-topbar.png](osc-topbar.png) | 1920×1080 | NVIDIA OSC Top Bar, header, logo & close |
| [bg.png](bg.png) | 1920×1080 | NVIDIA Classic background, 50% opacity at export time |
| [main-menu.png](main-menu.png) | 1920×1080 | NVIDIA Main Menu icons, text, status & focus border |
| [layer-manifest.json](layer-manifest.json) | — | Layer dimensions, offsets and hashes |
| [OSC-Layers-16x9.zip](OSC-Layers-16x9.zip) | — | All three RGBA PNGs + manifest |

**Pixel format:** lossless PNG RGBA (8 bits per channel, 32-bit), straight/unassociated alpha.

**Canvas:** 1920×1080 (16:9); original OSR buffer was 1680×1050, placed at (120, 15) with transparent padding **without scaling or cropping**.

**Layer order:** bg → main-menu → osc-topbar.

## NVIDIA colors

- **NVIDIA Green:** `#76B900` (RGB 118, 185, 0)
- **Black:** `#000000`
- **White:** `#FFFFFF`

NVIDIA Green and Black are the standard logo colors in NVIDIA Partner Co-Branding Guidelines (Pantone 376 C for green):
https://uk.pforce.nvidia.com/Brand/Brand_Guidelines/NV_Partner_Co-brand_Guidelines.pdf

## Source and verification

All three source images were isolated in the renderer DOM before capturing CEF's own compositor buffer. No Windows desktop/game pixel content was read. Alpha calibration with 50% transparent red returned RGBA (255,0,0,128), proving straight alpha. Re-compositing source-size layers had mean channel error 0.0113/255.

Test-only extraction/verification tooling is developed separately at `Project/Overlay On-Screen Control/NVIDIA OSC/tools/argb-layer-export` and was not included in this asset-only publication. These images capture the selected **Classic background** rather than an alternate website particle mode.

NVIDIA marks and source visual assets belong to their respective owners; this is an unofficial project resource.
