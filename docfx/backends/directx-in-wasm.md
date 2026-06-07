# DirectX-in-WASM (spike)

> **Status: open problem / spike.** This does not work today; it is a known gap, not a shipping feature.

## The gap

ShadowDusk's in-browser ([WASM](../architecture/wasm-frontend.md)) path compiles for **OpenGL/WebGL** only. The **DirectX DXBC** path — `vkd3d-shader` → DXBC (SM5) — has not been brought to WebAssembly.

That matters because the two backends are different emitted artifacts loaded by different runtime paths: a working in-browser OpenGL compile says nothing about producing a loadable DX `.mgfx` in the browser.

## Why it's hard

- The desktop DirectX backend is `vkd3d-shader` (or the Windows-only `d3dcompiler_47` oracle), a native library not yet compiled to WASM.
- DXC, which *is* compiled to WASM for the OpenGL path, only emits SM6 DXIL — **not** the DXBC (SM ≤ 5) MonoGame's DX11 runtime loads — so it cannot stand in (see [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md)).

## Where it's tracked

This is a planned spike (tracked as Phase 4.1). Until it lands, in-browser compilation should target OpenGL/WebGL; for DirectX `.mgfx`, compile on the desktop (CLI or library) where the `vkd3d-shader` / `d3dcompiler_47` backends run.
