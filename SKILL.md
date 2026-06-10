---
name: gpuview
description: GPU-native real-time screen capture via DXGI Desktop Duplication. Gets frames from the GPU compositor, uses DXGI dirty-rect metadata as change hints, refines coalesced rectangles into readable crop PNGs, and emits WSL-usable JSON paths. Use to watch the screen in real time, detect what changed where, capture HDR desktops, or grab frames of GPU-composited apps. Complements the screenshot skill (which provides input control).
---

# gpuview — GPU-native screen capture + change-feed

Built from the idea: the GPU already has the screen in perfect form, so take frames
at the compositor handoff (DXGI `IDXGIOutputDuplication`) and let the GPU itself tell
us *which rectangles changed* between frames. Instead of "screenshot the whole 4K
screen every time and make the model stare at it", `watch` emits a JSON event stream
of dirty regions + small crops — typically >90% less image data for UI monitoring.

## How it works

A C# exe (`T:\gpuview\gpuview.exe`, source in `src/gpuview.cs`) talks raw COM
vtables to D3D11/DXGI: `DuplicateOutput` → `AcquireNextFrame` (GPU surface)
→ `CopyResource` to a staging texture → `Map` to CPU → PNG. Dirty rects come from
`GetFrameDirtyRects`; `watch` then refines those hints with previous-frame pixel
diffs so coalesced compositor rectangles become readable changed-content crops.
Handles HDR desktops (RGBA16F scRGB → tonemapped sRGB). Per-monitor (`mon=N` = DXGI
output index). `frame` reports `source` and `nonblack`, and retries/falls back rather
than silently returning an all-black PNG.

## Usage

```bash
GV=/home/terp/.config/devin/skills/gpuview/scripts/gpuview.sh

$GV frame [mon]                # one full frame; prints WSL path to PNG -> read it
$GV watch <secs> [mon] [minpx] # JSON event per change: regions + crop PNGs
$GV rebuild                    # recompile exe from src (csc, ~1s)
```

`watch` output (one JSON line per event):
```json
{"event":"baseline","path":"T:\\gpuview\\watch_...\\baseline.png","w":3840,"h":2160,"path_wsl":"/mnt/t/gpuview/watch_.../baseline.png"}
{"event":"change","n":3,"t":1820,"raw_regions":2,"raw_area":640100,"refined_area":196352,"regions":[{"x":216,"y":344,"w":944,"h":208,"crop":"T:\\gpuview\\watch_...\\chg_3_0.png","crop_wsl":"/mnt/t/gpuview/watch_.../chg_3_0.png"}]}
{"event":"done","changes":42}
```
- `t` = ms since start; `raw_area` is compositor dirty-rect area; `refined_area`
  is the final crop area after previous-frame refinement. `minpx` filters regions
  smaller than that pixel area (default 400 — kills cursor blinks). Read
  `crop_wsl` directly from WSL.

## When to use which eye

- **gpuview `frame`**: full-desktop captures, HDR screens, games/video (compositor
  surface, no GDI black-frame problem for borderless/windowed apps).
- **gpuview `watch`**: "tell me when/where the screen changes" — wait for a build to
  finish, a dialog to pop, a game to reach a menu. Read only the crops.
- **screenshot skill `shot.sh window`**: capturing one specific window (incl.
  occluded/background windows — duplication only sees the visible desktop).
- **screenshot skill `control.sh`**: all mouse/keyboard input (gpuview has no hands).

## Limits

- Exclusive-fullscreen games may block duplication (`DuplicateOutput` error) — switch
  the game to borderless/windowed.
- Captures the *desktop output*, not hidden windows.
- `mon=1` can return black if that output is idle/asleep or on another adapter.
- Protected (DRM) content is blanked by the OS by design.
