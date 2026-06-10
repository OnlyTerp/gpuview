# gpuview

**GPU-native screen vision for AI agents.** Capture frames at the GPU compositor
handoff (DXGI Desktop Duplication) and let the GPU itself tell the agent *which
pixels changed* — a compressed change-feed instead of full-screen screenshots.

```
{"event":"change","n":3,"t":1820,"raw_area":640100,"refined_area":196352,"regions":[{"x":216,"y":344,"w":944,"h":208,"crop":"T:\\gpuview\\...\\chg_3_0.png","crop_wsl":"/mnt/t/gpuview/.../chg_3_0.png"}]}
```

In a live test on a 4K desktop, `watch` collapsed **1.13 billion pixels** of
full-frame-equivalent captures into **42M pixels** of actual changed regions —
a **96.3% reduction** in image data the agent has to look at.

## Why

When an AI agent "watches" your screen the usual way, it re-screenshots the whole
desktop and burns vision tokens re-reading 95% unchanged pixels. But the GPU
compositor already knows exactly what changed each frame — Windows exposes that
via `IDXGIOutputDuplication::GetFrameDirtyRects`. gpuview surfaces that metadata
as a JSON event stream plus tiny crop PNGs of only the changed regions.

- **GPU-first capture path** — `AcquireNextFrame` hands over the exact surface
  scanned out to your monitor. `frame` retries black first-frames and reports
  `source` + `nonblack`; if DXGI only returns black for a static desktop, it
  falls back to `CopyFromScreen` instead of silently handing the agent junk.
- **Dirty rects + refinement** — the OS compositor supplies dirty rect hints;
  `watch` then refines them against the previous frame so coalesced compositor
  rectangles become smaller, readable changed-content crops.
- **HDR aware** — detects RGBA16F scRGB desktops and tonemaps to sRGB.
- **Tiny** — one 15KB C# exe, compiled with the `csc.exe` that ships in every
  Windows install. No SDK, no NuGet, no native build toolchain.
- **WSL-first** — the wrapper script runs from WSL and prints Linux paths.

## Quick start

```bash
git clone https://github.com/OnlyTerp/gpuview ~/.config/devin/skills/gpuview
# (or ~/.claude/skills/gpuview for Claude Code)

GV=~/.config/devin/skills/gpuview/scripts/gpuview.sh
$GV frame              # one full frame -> prints PNG path
$GV watch 10           # 10s of change events as JSON lines
$GV frame 1            # second monitor
$GV rebuild            # recompile the exe from src (~1s)
```

First run auto-compiles `gpuview.exe` to `T:\gpuview\` (edit `scripts/gpuview.sh`
to change the location).

## Modes

| Mode | What it does |
|---|---|
| `frame [mon]` | One full-desktop frame from the compositor. Prints WSL path. |
| `watch <secs> [mon] [minpx]` | Streams JSON events; each contains changed regions + crop PNGs. `minpx` (default 400) filters cursor-blink noise. |
| `rebuild` | Recompile `src/gpuview.cs` with the .NET Framework `csc.exe`. |

## How it compares to prior art

We looked for existing tools before building this. Closest neighbors:

| Project | Approach | Difference |
|---|---|---|
| [screen-capture-mcp](https://github.com/jblanchette/screen-capture-mcp) | PowerShell/GDI screenshots on an interval | Full frames every time; GDI (black frames on GPU apps); no change metadata |
| [claude-screen-mcp](https://github.com/lfzds4399-cpu/claude-screen-mcp) | Screenshots + perceptual-hash change detection | Detects *that* the screen changed (hash distance), not *where*; still re-sends full frames |
| [claude-chrome-stream](https://github.com/joemccann/claude-chrome-stream) | CDP screencast + pixelmatch deltas | Browser-only (Chrome tabs), not the desktop/games |
| [Desktop-capture-js](https://github.com/DeraTechDesign/Desktop-capture-js) | DXGI duplication + dirty rects → WebSocket | Same capture tech, but built for browser-canvas screen sharing, not agent consumption; needs Node + VS build tools |
| [agent-view](https://github.com/PetukhovArt/agent-view) | CDP eval/DOM/JSON-patch for web apps | The "structured state beats pixels" philosophy, but web-only |

To our knowledge gpuview is the first **agent skill** that uses the compositor's
own dirty-rect metadata to give an LLM a region-level change feed of the whole
desktop — including games and GPU-composited apps — with zero runtime deps.

## Architecture

```
┌─ Windows ──────────────────────────────────────────┐
│ gpuview.exe  (C#, raw COM vtables, 15KB)           │
│   DXGI: DuplicateOutput → AcquireNextFrame         │
│   GetFrameDirtyRects → merge+pad regions           │
│   D3D11: CopyResource → Map → PNG (full or crops)  │
│   stdout: JSON lines                               │
└──────────────┬─────────────────────────────────────┘
               │
┌─ WSL ────────▼─────────────────────────────────────┐
│ scripts/gpuview.sh — frame | watch | rebuild       │
│ path translation, auto-build, event passthrough    │
└────────────────────────────────────────────────────┘
```

No P/Invoke wrappers, no SharpDX/Vortice dependency — the COM vtable slots are
called directly via delegates (see `src/gpuview.cs`), which is why it compiles
with the bare .NET Framework 4.x compiler present on every Windows box.

## Limits / roadmap

- Exclusive-fullscreen games can refuse duplication → use borderless windowed.
  (Roadmap: Windows.Graphics.Capture fallback.)
- Captures the visible desktop, not occluded windows. Pair with a
  PrintWindow-based tool for background windows.
- DRM-protected content is blanked by the OS.
- Roadmap: move-rect handling, region-of-interest watch, mouse-pointer overlay,
  optional OCR on changed regions ("the text that changed"), macOS
  (ScreenCaptureKit) and Linux (PipeWire) backends.

## License

MIT
