# Phase 19 — WASM Runtime Compilation (In-Browser `.fx` → `.mgfx`)

**Status:** ✅ **Done** (2026-05-30, branch `phase19-wasm-runtime`) — **scope narrowed**: this phase now covers the **WASM-portable managed compile engine**, built and desktop-verified. The *browser-runtime* tail (real emscripten DXC/SPIRV-Cross modules + an actual Mode 1/Mode 2 browser run) was carved out to **[Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)** because it is gated on a toolchain/browser not available in-session. This is a deliberate rescope, **not** a claim that in-browser compilation has been validated end-to-end — see *Progress* and the revised *Definition of Done*.
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

### Mode 1 — precompiled bytes load in WASM → **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)**
*(Requires a real browser to validate rendering, unavailable in-session. Tracked in Phase 100.)*
- [→100] Stand up a **minimal, throwaway** MonoGame/KNI **WebGL** harness that calls `new Effect(gd, bytes)` on a Phase-9-compiled OpenGL `.mgfx` and draws a known quad.
- [→100] Confirm the Phase-17 DesktopGL `.mgfx` **also** loads + renders in WebGL; record any DesktopGL-vs-WebGL divergence.
- [→100] Smoke test: browser console shows **no** shader-compile/link errors across the corpus.

### Mode 2 — in-browser compilation (the differentiator)
- [x] Define the `[JSImport]` boundary (mirror the desktop native-interop surface) — **done**: `Phase19.js` contract + `JsDxcShaderCompiler`/`JsSpirvToGlslTranspiler`.
- [x] Wire `WasmShaderCompiler` to call WASM-DXC (HLSL → SPIR-V) and WASM-SPIRV-Cross (SPIR-V → GLSL), then reuse the **shared managed** GLSL MojoShader rewrite + `MgfxWriter` to emit bytes — **done & compiles for `net8.0-browser`**.
- [x] Assert WASM output is **byte-identical** to the CLI output — **proven on desktop**: routing the corpus through `SpirvReflector` (native DXC+SPIRV-Cross otherwise unchanged) yields `.mgfx` byte-identical to the DXIL-reflected default, 10/10. Remaining variable = the DXC/SPIRV-Cross *binary version* used in-browser → closed in Phase 100.
- [→100] Source the emscripten DXC + SPIRV-Cross WASM modules — **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)** (external toolchain).
- [→100] Measure download size, memory, and cold-start — **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)**.

### Validation / CI
- [→100] Headless-browser smoke test in [Phase 30 CI](PHASE-30-cross-platform-ci.md) for mode 1 — **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)**.
- [→100] Feed untrusted `.fx` through [Phase 25](PHASE-25-security-hardening.md) input validation — **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)** (real browser input path).

---

## Acceptance Criteria

- [x] `ShadowDusk.Wasm` produces `.mgfx` bytes **identical** to the CLI for the same source + OpenGL target — proven byte-transparent on desktop via the `SpirvReflector` reflection-source swap (10/10 corpus), modulo the in-browser DXC/SPIRV-Cross binary version.
- [→100] **Mode 1:** a precompiled `.mgfx` loads via `new Effect(gd, bytes)` and renders in a real WebGL build — **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)** (needs a browser).
- [→100] **Mode 2:** at least one shader compiles **fully in-browser** — **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)** (C# wired & browser-compiling; needs the emscripten modules + a browser run).
- [→100] DesktopGL-vs-WebGL divergences documented — **moved to [Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)**.
- [x] No DirectX/DXBC promised in WASM (explicitly deferred) — only the OpenGL/SPIR-V path was built.

---

## Definition of Done (narrowed scope — met)

The **WASM-portable managed compile engine** exists and is desktop-verified: `ShadowDusk.Wasm`'s `WasmShaderCompiler` composes the real pipeline with browser `[JSImport]` backends + the pure-managed `SpirvReflector`, **compiles for `net8.0-browser`**, and the GL pipeline produces `.mgfx` **byte-identical** to the CLI when reflection is sourced from SPIR-V instead of DXIL (10/10 corpus). The DXIL/`ID3D12ShaderReflection` Windows-only blocker is removed from the GL path. This is the engine for the *reach* axis.

> **What this Done does NOT claim:** that in-browser compilation has been validated end-to-end. Proving a shader actually compiles+renders in a real browser — with real emscripten DXC/SPIRV-Cross modules behind the `Phase19.js` contract — is **[Phase 100](../PHASE-100-wasm-browser-runtime-validation.md)** (deferred; needs a browser/toolchain unavailable in-session). The polished user-facing Fiddle app is **[Phase 22](PHASE-22-wasm-shader-fiddle-sample.md)**.
