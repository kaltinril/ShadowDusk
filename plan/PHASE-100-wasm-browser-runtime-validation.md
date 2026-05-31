# Phase 100 — WASM Browser-Runtime Validation (emscripten modules + real in-browser run)

**Status:** Deferred (far-future). Carved out of [Phase 19](DONE/PHASE-19-wasm-runtime-compilation.md) on 2026-05-30 because it is gated on an external toolchain (emscripten) and an actual browser runtime that the development environment could not exercise. Numbered 100 to park it well beyond the active roadmap.

**Depends on:** [Phase 19](DONE/PHASE-19-wasm-runtime-compilation.md) (the managed *reach engine* — injectable backends, the pure-managed `SpirvReflector`, the DXIL-free GL reflection path, and the browser-compiling `WasmShaderCompiler` with its `[JSImport]` contract in `src/ShadowDusk.Wasm/Phase19.js`). Also [Phase 25](PHASE-25-security-hardening.md) (untrusted web input) and [Phase 30](PHASE-30-cross-platform-ci.md) (headless-browser CI).

**Blocks:** The *runtime* half of ShadowDusk's Part-1 (reach) promise for the browser — a shader actually compiling and rendering client-side with no server.

---

## Why this is its own phase

Phase 19 delivered and **desktop-verified** everything needed to compile `.fx` → `.mgfx` in the browser *except* two things that require a toolchain/runtime we did not have in-session:

1. **Real emscripten-compiled native WASM modules** for **DXC** (HLSL → SPIR-V) and **SPIRV-Cross** (SPIR-V → GLSL), exported via the `Phase19.js` contract the C# `[JSImport]` backends already call.
2. **An actual browser** to run Mode 1 (load a precompiled `.mgfx` via `new Effect(gd, bytes)` in a WebGL build) and Mode 2 (source → `.mgfx` → `Effect` fully client-side).

Phase 19's C# side is done and proven byte-transparent on desktop (the `SpirvReflector` reflection-source swap yields `.mgfx` byte-identical to the DXIL path, 10/10). The only unproven variable is whether the *in-browser* DXC/SPIRV-Cross **binary versions** emit the same SPIR-V/GLSL as the desktop natives — which is exactly what this phase pins down.

---

## Tasks

### Native WASM modules
- [ ] Build (or source a prebuilt) **DXC** compiled to WebAssembly via emscripten; export a JS `compileToSpirv(hlslSource: string, args: string[]): Uint8Array` matching the `shadowdusk-dxc` contract in `Phase19.js`. Pin the DXC version to the desktop one (so output matches).
- [ ] Build (or source) **SPIRV-Cross** compiled to WebAssembly; export `transpileToGlsl(spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics): string` matching the `shadowdusk-spirv-cross` contract. Pin the SPIRV-Cross version to the desktop one.
- [ ] Measure download size, memory, and cold-start; decide whether Mode 2 ships by default or stays opt-in.

### Mode 1 — precompiled bytes load in WebGL
- [ ] Minimal MonoGame/KNI **WebGL** harness that calls `new Effect(gd, bytes)` on a Phase-9-compiled OpenGL `.mgfx` and renders a known quad / the corpus.
- [ ] Confirm the `.mgfx` that passes Phase 17 in DesktopGL also loads + renders in WebGL; **document any DesktopGL-vs-WebGL divergence** (research doc §15.2). Reuse the Phase 17 corpus + by-name parameters.

### Mode 2 — in-browser compilation (end-to-end)
- [ ] With the modules above wired, compile at least one corpus shader **fully in-browser** (source → `.mgfx` → `Effect`) with no shader-compile/link errors in the console.
- [ ] Assert the in-browser `.mgfx` bytes are **identical** to the CLI output for the same source + OpenGL target (closes the "modulo binary version" caveat Phase 19 left open).

### Validation / CI
- [ ] Headless-browser smoke test in [Phase 30 CI](PHASE-30-cross-platform-ci.md) for Mode 1; gate Mode 2 behind a flag if heavy.
- [ ] Run untrusted `.fx` through [Phase 25](PHASE-25-security-hardening.md) input validation (browser path takes arbitrary user shader text).

---

## Definition of Done

A shader compiled **entirely in-browser** by `ShadowDusk.Wasm` (source → `.mgfx`) renders correctly in a real MonoGame/KNI **WebGL** build via `new Effect(gd, bytes)`, with no server — and its bytes match the CLI for the same source + OpenGL target. Combined with Phase 19 (engine) and Phase 30 (cross-host equality), this completes the *reach* axis for the browser. The polished, user-facing demo built on top is [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md).
