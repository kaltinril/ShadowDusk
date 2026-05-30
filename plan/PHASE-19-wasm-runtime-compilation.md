# Phase 19 — WASM Runtime Compilation (In-Browser `.fx` → `.mgfx`)

**Status:** In progress — *reach engine* built & verified on desktop (2026-05-30, branch `phase19-wasm-runtime`); browser-run validation (Mode 1) and real emscripten DXC/SPIRV-Cross modules (Mode 2 runtime) remain. See **Progress** below.
**Depends on:** Phase 8 (`ShadowDusk.Compiler` / `IShaderCompiler` abstraction — `ShadowDusk.Wasm` is the second implementation), Phase 17 (a browser-produced `.mgfx` must be exactly as MonoGame-loadable, and carry the same MojoShader-dialect GLSL, as a desktop-produced one — same format, same fidelity bar), Phase 25 (security hardening of the untrusted web/input path), Phase 30 (cross-platform CI / native-binary restore).
**Blocks:** ShadowDusk's **Part 1 (reach)** promise for the browser — compiling `.fx` at runtime, in-browser, with **no server roundtrip** (e.g. Vic's XNA Fiddle / KNI web).

> The [plan.md dependency graph](plan.md#dependencies) previously placeholdered this as **"Phase 9W (WASM)"** (now renumbered to Phase 19) but no phase document ever defined it. This is that document. The architecture survey is [`monogame_runtime_mgfx_compiler_research.md`](../monogame_runtime_mgfx_compiler_research.md) §8 (WASM considerations), §11 (package structure), and Task I.

> **Scope boundary vs [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md) (avoid overlap):** Phase 19 owns the **compiler capability** — `WasmShaderCompiler` working in WASM (modes 1 & 2) plus a **minimal** harness that proves bytes load and a shader compiles in-browser. The **interactive XNA-Fiddle sample app** — paste-in editor, the cat image, parameter controls, error UI — is **[Phase 22](PHASE-22-wasm-shader-fiddle-sample.md)**, which consumes this. Build the throwaway harness here; build the real app there. Don't build the app twice.

---

## Overview

ShadowDusk earns its existence on **two axes** (CLAUDE.md → *What success actually means*): **reach** (compile where `mgfxc` can't) and **fidelity** (produce what `mgfxc` would). Phase 17 nails fidelity on desktop OpenGL. This phase delivers half of *reach*: compiling `.fx` **inside .NET WASM in the browser** and returning `.mgfx` bytes that load via `new Effect(graphicsDevice, bytes)` in a MonoGame/KNI web runtime — no MGCB, no `fxc.exe`, no server.

`src/ShadowDusk.Wasm/WasmShaderCompiler.cs` exists today as a stub `IShaderCompiler`. The hard part is that DXC and SPIRV-Cross are **native** libraries; in the browser they must be WASM-compiled and called from managed code via `[JSImport]`/`[JSExport]`. The `.mgfx` writer, FX parser, and (post–Phase 17) GLSL MojoShader rewrite are managed C# and run in WASM unchanged — so the format/fidelity work is shared with the desktop path, not duplicated.

### Two delivery modes (research doc §8.2 / §8.3)

1. **Precompiled bytes — ship this first (lowest risk).** `.fx` compiled to `.mgfx` at build time by the Phase 9 CLI, shipped as an asset, loaded in WASM via `new Effect(gd, bytes)`. **No browser-side compiler.** This proves the *runtime-load-in-WASM* half independently of native-WASM interop and is the practical production path for most games.
2. **In-browser compilation — the actual differentiator.** WASM-compiled DXC + SPIRV-Cross invoked from `ShadowDusk.Wasm` via JS interop, so source → `.mgfx` happens entirely client-side at runtime. This is what XNA Fiddle needs. Higher risk (download size, startup, native-WASM interop) — pursue after mode 1 is proven.

---

## Progress — 2026-05-30 (branch `phase19-wasm-runtime`)

The **managed "reach engine"** — everything needed to compile `.fx` → `.mgfx` in the browser *except the two native WASM modules and an actual browser run* — is built and verified on desktop:

1. **Injectable backend seams** (`ISpirvToGlslTranspiler`, existing `IDxcShaderCompiler`). `CompilationPipeline`/`EffectCompiler` take optional factory delegates; defaults are the native desktop impls. Behavior-preserving (346 unit + determinism tests green; byte-identical `.mgfx` preserved).
2. **Discovered gap the doc never named — and closed it.** Reflection was *not* a "shared managed" stage: the GL path reflected **DXIL via `ID3D12ShaderReflection`** (Windows/D3D12 native), which cannot run in a browser. Built a **pure-managed, native-free SPIR-V reflector** (`ShadowDusk.Core.Reflection.SpirvReflector` + a minimal SPIR-V binary parser). An equivalence test compiles all **10 Phase 17 corpus PS shaders** to both DXIL and SPIR-V and asserts the `SpirvReflector`'s `ReflectedEffect` matches the DXIL oracle on every `.mgfx`-driving field — **10/10 exact** (the one subtlety — DXC's flat `-auto-binding-space` binding namespace vs per-class `t#/s#/b#` registers — is handled by class-bucketing).
3. **Pipeline routes GL reflection through the reflector seam** (`IShaderReflector` injection). When injected on the OpenGL target it reflects the SPIR-V blob and **skips the DXIL compile + native reflection entirely**; desktop default (no injection) is byte-for-byte unchanged. **Byte-identity proof on desktop:** compiling the corpus with `SpirvReflector` injected (native DXC + SPIRV-Cross otherwise unchanged) yields `.mgfx` **byte-identical** to the default DXIL-reflected output — **10/10**. So the WASM path produces identical bytes to the CLI, *modulo the DXC/SPIRV-Cross binary version* (the only remaining variable).
4. **`WasmShaderCompiler` wired & browser-compiling.** `ShadowDusk.Wasm` retargeted `net8.0-browser`; `WasmShaderCompiler` composes `EffectCompiler` with three browser backends — `JsDxcShaderCompiler` (HLSL→SPIR-V via `[JSImport]`, reusing `DxcFlagBuilder` for identical args), `JsSpirvToGlslTranspiler` (SPIR-V→GLSL via `[JSImport]` with the exact desktop SPIRV-Cross options), and the managed `SpirvReflector`. The `[JSImport]` host contract is documented in `src/ShadowDusk.Wasm/Phase19.js`. **Finding:** the native-entangled `Compiler` chain (Vortice.Dxc, spirv-cross P/Invoke) *does* compile under `net8.0-browser` — the native wrappers only matter at runtime, where the injected JS backends replace them — so **no managed/native assembly split was required**. Both the browser project and the full `ShadowDusk.slnx` build green.

**What remains (genuinely gated on a browser / emscripten toolchain not available in this environment):**
- **Mode 2 runtime:** provide real emscripten-compiled **DXC** and **SPIRV-Cross** WASM modules behind the `Phase19.js` contract, then run a true in-browser source→`.mgfx` compile. (C# side is done & compiles for browser.)
- **Mode 1:** a minimal MonoGame/KNI **WebGL** harness that loads a precompiled `.mgfx` via `new Effect(gd, bytes)` and renders the corpus; document any DesktopGL-vs-WebGL divergence. (No browser available here to validate rendering; the polished interactive app is [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md).)
- **Download size / cold-start** measurement (needs the real modules).

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
- [ ] Stand up a **minimal, throwaway** MonoGame/KNI **WebGL** harness that calls `new Effect(gd, bytes)` on a Phase-9-compiled OpenGL `.mgfx` (post–Phase 17 format) and draws a known quad. *(Just enough to prove load+render in WebGL — the polished interactive app is [Phase 22](PHASE-22-wasm-shader-fiddle-sample.md), not here.)*
- [ ] Confirm the `.mgfx` that passes Phase 17 in DesktopGL **also** loads + renders in the WebGL runtime; record any DesktopGL-vs-WebGL divergence (research doc §15.2). Reuse the Phase 17 corpus + by-name parameters.
- [ ] Smoke test: browser console shows **no** shader-compile/link errors for the uniform-free corpus, then the uniform-driven corpus.

### Mode 2 — in-browser compilation (the differentiator)
- [x] Define the `[JSImport]` boundary (mirror the desktop native-interop surface) — **done**: `Phase19.js` contract + `JsDxcShaderCompiler`/`JsSpirvToGlslTranspiler`. [ ] Source the emscripten DXC + SPIRV-Cross WASM modules — **pending** (external toolchain).
- [x] Wire `WasmShaderCompiler` to call WASM-DXC (HLSL → SPIR-V) and WASM-SPIRV-Cross (SPIR-V → GLSL), then reuse the **shared managed** GLSL MojoShader rewrite + `MgfxWriter` to emit bytes — **done & compiles for `net8.0-browser`** (runtime needs the modules above).
- [x] Assert WASM output is **byte-identical** to the CLI output — **proven on desktop**: routing the corpus through `SpirvReflector` (native DXC+SPIRV-Cross otherwise unchanged) yields `.mgfx` byte-identical to the DXIL-reflected default, 10/10. Remaining variable = the DXC/SPIRV-Cross *binary version* used in-browser.
- [ ] Measure download size, memory, and cold-start — **pending** (needs the real WASM modules).

### Validation / CI
- [ ] Add a headless browser smoke test (or documented manual harness) to [Phase 30 CI](PHASE-30-cross-platform-ci.md) for mode 1; gate mode 2 behind a flag if heavy.
- [ ] Feed untrusted `.fx` source through [Phase 25](PHASE-25-security-hardening.md) input-validation review (the browser path takes arbitrary user shader text).

---

## Acceptance Criteria

- [x] `ShadowDusk.Wasm` produces `.mgfx` bytes **identical** to the CLI for the same source + OpenGL target — proven byte-transparent on desktop via the `SpirvReflector` reflection-source swap (10/10 corpus), modulo the in-browser DXC/SPIRV-Cross binary version.
- [ ] **Mode 1:** a precompiled `.mgfx` loads via `new Effect(gd, bytes)` and renders correctly in a real MonoGame/KNI **WebGL** build — **pending** (needs a browser to validate).
- [ ] **Mode 2:** at least one shader compiles **fully in-browser** (source → `.mgfx` → `Effect`) — **pending** (C# wired & browser-compiling; needs the emscripten DXC + SPIRV-Cross modules + a browser run).
- [ ] DesktopGL-vs-WebGL divergences (if any) are documented, not assumed away — **pending** (Mode 1).
- [x] No DirectX/DXBC promised in WASM (explicitly deferred) — only the OpenGL/SPIR-V path was built.

---

## Definition of Done

`ShadowDusk.Wasm` compiles `.fx` to MonoGame-loadable `.mgfx` **entirely client-side in WASM**, byte-identical to the CLI for the same source + OpenGL target, and a **minimal** WebGL harness proves those bytes load via `new Effect(gd, bytes)` and that at least one shader compiles fully in-browser (source → `.mgfx` → `Effect`, no server). Combined with Phase 17 (desktop GL fidelity) and Phase 30 (cross-host byte-equality), this delivers the engine for the *reach* axis: **the result `mgfxc` gives, produced where `mgfxc` can't run.**

> The polished, user-facing demonstration of this capability — the paste-a-shader-and-see-the-cat XNA-Fiddle tool — is **[Phase 22](PHASE-22-wasm-shader-fiddle-sample.md)**. Phase 19 is done when the *capability* exists and is proven minimally; Phase 22 turns it into the app.
