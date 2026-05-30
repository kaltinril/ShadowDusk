# Phase 19 — WASM Runtime Compilation (In-Browser `.fx` → `.mgfx`)

**Status:** Not started
**Depends on:** Phase 8 (`ShadowDusk.Compiler` / `IShaderCompiler` abstraction — `ShadowDusk.Wasm` is the second implementation), Phase 17 (a browser-produced `.mgfx` must be exactly as MonoGame-loadable, and carry the same MojoShader-dialect GLSL, as a desktop-produced one — same format, same fidelity bar), Phase 25 (security hardening of the untrusted web/input path), Phase 30 (cross-platform CI / native-binary restore).
**Blocks:** ShadowDusk's **Part 1 (reach)** promise for the browser — compiling `.fx` at runtime, in-browser, with **no server roundtrip** (e.g. Vic's XNA Fiddle / KNI web).

> The [plan.md dependency graph](plan.md#dependencies) previously placeholdered this as **"Phase 9W (WASM)"** (now renumbered to Phase 19) but no phase document ever defined it. This is that document. The architecture survey is [`monogame_runtime_mgfx_compiler_research.md`](../monogame_runtime_mgfx_compiler_research.md) §8 (WASM considerations), §11 (package structure), and Task I.

---

## Overview

ShadowDusk earns its existence on **two axes** (CLAUDE.md → *What success actually means*): **reach** (compile where `mgfxc` can't) and **fidelity** (produce what `mgfxc` would). Phase 17 nails fidelity on desktop OpenGL. This phase delivers half of *reach*: compiling `.fx` **inside .NET WASM in the browser** and returning `.mgfx` bytes that load via `new Effect(graphicsDevice, bytes)` in a MonoGame/KNI web runtime — no MGCB, no `fxc.exe`, no server.

`src/ShadowDusk.Wasm/WasmShaderCompiler.cs` exists today as a stub `IShaderCompiler`. The hard part is that DXC and SPIRV-Cross are **native** libraries; in the browser they must be WASM-compiled and called from managed code via `[JSImport]`/`[JSExport]`. The `.mgfx` writer, FX parser, and (post–Phase 17) GLSL MojoShader rewrite are managed C# and run in WASM unchanged — so the format/fidelity work is shared with the desktop path, not duplicated.

### Two delivery modes (research doc §8.2 / §8.3)

1. **Precompiled bytes — ship this first (lowest risk).** `.fx` compiled to `.mgfx` at build time by the Phase 9 CLI, shipped as an asset, loaded in WASM via `new Effect(gd, bytes)`. **No browser-side compiler.** This proves the *runtime-load-in-WASM* half independently of native-WASM interop and is the practical production path for most games.
2. **In-browser compilation — the actual differentiator.** WASM-compiled DXC + SPIRV-Cross invoked from `ShadowDusk.Wasm` via JS interop, so source → `.mgfx` happens entirely client-side at runtime. This is what XNA Fiddle needs. Higher risk (download size, startup, native-WASM interop) — pursue after mode 1 is proven.

---

## Scope and Non-Goals

**In scope:**
- `ShadowDusk.Wasm` as a working `IShaderCompiler` whose output is byte-identical to the CLI/desktop path for the same source + target (constraint 3 — *ShadowDusk's own* determinism; **all hosts produce the same bytes**, the Part-1 cross-host equality requirement).
- **Mode 1** end-to-end: precompiled `.mgfx` loads + renders in a MonoGame/KNI **WebGL** build (browser).
- **Mode 2** spike: WASM-compiled DXC + SPIRV-Cross reachable from `ShadowDusk.Wasm` via `[JSImport]`; compile one minimal shader in-browser.
- OpenGL/WebGL profile only (DesktopGL success ≠ WebGL success — research doc §15.2; validate on the real WebGL target).

**Out of scope:**
- DirectX/DXBC in WASM — no native P/Invoke in the browser, no prebuilt WASM DXBC artifact; the open problem stays in [Phase 4.1 SPIKE](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) / [Phase 18](PHASE-18-directx-dxbc.md).
- Shrinking the in-browser compiler stack (download-size optimization) — a follow-on once mode 2 works at all.
- Full FX feature parity beyond the Phase 15/16/17 corpus.

---

## Tasks

### Mode 1 — precompiled bytes load in WASM (do first)
- [ ] Stand up a minimal MonoGame/KNI **WebGL** sample that calls `new Effect(gd, bytes)` on a Phase-9-compiled OpenGL `.mgfx` (post–Phase 17 format) and draws a known quad.
- [ ] Confirm the `.mgfx` that passes Phase 17 in DesktopGL **also** loads + renders in the WebGL runtime; record any DesktopGL-vs-WebGL divergence (research doc §15.2). Reuse the Phase 17 corpus + by-name parameters.
- [ ] Smoke test: browser console shows **no** shader-compile/link errors for the uniform-free corpus, then the uniform-driven corpus.

### Mode 2 — in-browser compilation (the differentiator)
- [ ] Source/evaluate WASM builds of DXC and SPIRV-Cross (emscripten); define the `[JSImport]`/`[JSExport]` boundary (mirror the desktop native-interop surface).
- [ ] Wire `WasmShaderCompiler` to call WASM-DXC (HLSL → SPIR-V) and WASM-SPIRV-Cross (SPIR-V → GLSL), then reuse the **shared managed** GLSL MojoShader rewrite + `MgfxWriter` to emit bytes.
- [ ] Assert WASM output is **byte-identical** to the CLI output for the same source + target (determinism / cross-host equality).
- [ ] Measure download size, memory, and cold-start; record whether mode 2 is viable for shipping or stays an opt-in.

### Validation / CI
- [ ] Add a headless browser smoke test (or documented manual harness) to [Phase 30 CI](PHASE-30-cross-platform-ci.md) for mode 1; gate mode 2 behind a flag if heavy.
- [ ] Feed untrusted `.fx` source through [Phase 25](PHASE-25-security-hardening.md) input-validation review (the browser path takes arbitrary user shader text).

---

## Acceptance Criteria

- [ ] `ShadowDusk.Wasm` compiles the Phase 17 corpus to `.mgfx` whose bytes are **identical** to the CLI for the same source + OpenGL target.
- [ ] **Mode 1:** a precompiled `.mgfx` loads via `new Effect(gd, bytes)` and renders correctly in a real MonoGame/KNI **WebGL** build — no MGCB, no server.
- [ ] **Mode 2:** at least one shader compiles **fully in-browser** (source → `.mgfx` → `Effect`) with no shader errors in the browser console.
- [ ] DesktopGL-vs-WebGL divergences (if any) are documented, not assumed away.
- [ ] No DirectX/DXBC promised in WASM (explicitly deferred).

---

## Definition of Done

A browser tool (XNA Fiddle–style) takes `.fx` source typed by a user, compiles it with ShadowDusk **entirely client-side in WASM**, and loads the result as a real MonoGame/KNI `Effect` that renders correctly — producing the same bytes ShadowDusk's CLI would on any desktop OS. Combined with Phase 17 (desktop GL fidelity) and Phase 30 (cross-host byte-equality), this delivers the *reach* axis: **the result `mgfxc` gives, produced where `mgfxc` can't run.**
