# Phase 24 — Browser Render Validation (Playwright headless)

**Status:** ✅ **DONE (2026-06-01).** All four DoD items met by a real headless-browser run (see `tests/ShadowDusk.BrowserTests/RESULTS.md`): the harness renders the `.mgfx` corpus in the real KNI WebGL `Effect`, mode-1 is now **10/10 load + 10/10 render-equivalent** (after the Dissolve fix below), the KNIFX-v11 question is answered with evidence, and the harness is handed to Phase 30 §16 for CI.

**KNI render-parity carry-forward — CLOSED (2026-06-01).** MGFX **v10 renders 10/10 in real KNI WebGL**, so **no KNIFX-v11 writer is needed for render parity.** The lone Dissolve failure (Δ198 / 1.68% px) was root-caused (`tests/ShadowDusk.BrowserTests/DISSOLVE-INVESTIGATION.md`) to the **sample/harness leaving texture slot 1 (Dissolve's `_dissolveTex`) at the undefined runtime default** — `SpriteBatch.Begin(...LinearClamp)` only sets slot 0, and KNI WebGL vs DesktopGL resolve the slot-1 default differently for the NPOT cat, shifting the threshold-band tint. **Not a compiler bug** — ShadowDusk's emit path (`SpirvCrossGlslTranspiler`/`MonoGameGlslRewriter`/`MgfxWriter`) is unchanged (byte-identical output preserved). Fix = pin `SamplerStates[1] = LinearClamp` in `ShaderFiddleGame.cs` + the harness `RefRenderer`; verified by a real headless re-run (Dissolve → Δ128 / 0.145% px, PASS; other 9 unchanged; desktop tests green). Precision (`mediump`) was tested and **refuted** as the cause (SwiftShader evaluates `mediump`≡`highp`); kept `mediump` to stay faithful to mgfxc.

**Carry-forwards:**
- **`roundEven` WebGL1 incompatibility — ✅ FIXED (2026-06-01).** The harness validated the **golden (mgfxc) MGFX bytes**, which hid that **ShadowDusk's own** `Pixelated.mgfx` *failed to load* in KNI WebGL — ShadowDusk emitted `roundEven()`, which **GLSL ES 1.00 / WebGL1 lacks**. Fixed in `MonoGameGlslRewriter` (Rule 8: lower `roundEven(x)`/`round(x)` → `floor((x)+0.5)`, byte-faithful to mgfxc), and the harness now validates **ShadowDusk's own** output via `--corpus=sd`. ShadowDusk's own corpus now loads+renders 10/10 in real KNI WebGL (Pixelated Δ1 LSB); desktop 498/498 incl. mgfxc cross-validation. See `tests/ShadowDusk.BrowserTests/ROUNDEVEN-FIX.md`.
- **Mode-2 sample verification → carried to Phase 23 Gate 3 (M3).** Wired and reached, but blocked here on the restore-gated `slang-wasm.wasm`; it is *sample-only* (Slang) and explicitly out of this phase's DoD. Phase 23 reruns this same harness against the faithful DXC→WASM frontend.

This was the cheapest, highest-information step on the WASM-KNI path: the first thing to actually run ShadowDusk output in a *real* browser graphics stack.

**Depends on:** Phase 22 (the `samples/ShaderFiddle.Web/` KNI Blazor-WASM app — the page under test), Phase 17 (the 10-shader OpenGL corpus + the reference PNGs and the **pixel-equivalence tolerance standard, §6.1**, reused verbatim here). Tooling: **Playwright** (approved) driving headless Chromium with real WebGL.

**Blocks:** Phase 23 Gate 3 (the render proof) and any confidence that "a user uses our library inside WASM KNI" actually holds. **Sequenced before Phase 23's DXC→WASM build effort on purpose** — there is no point building the faithful frontend until we know the `.mgfx` it would produce can even *load and render* in KNI's browser runtime.

---

## Why this phase exists (the evidence-ladder gap)

Per CLAUDE.md's evidence ladder, only rung **(4)** — "ShadowDusk's `.mgfx` loads in a real `Effect` and renders like `mgfxc`'s" — proves the promise. For desktop OpenGL (Phase 17) and WindowsDX (Phase 18) rung (4) is met. **For the browser, nothing above rung (1) has ever run:** every WASM stage is node-verified per-stage only (Phase 19/22/23), and **no real browser has rendered a single ShadowDusk effect.** This phase builds the harness that climbs to rung (4) in the browser.

It also retires the single biggest unknown on the whole WASM-KNI path:

### The KNI MGFXReader10 load risk (the thing this phase answers)

- `MgfxWriter` writes the **`MGFX`** signature at **version 10** (MonoGame 3.8.x); `CompilerOptions.MgfxVersion = 10` by default.
- **KNI's native format is KNIFX v11 (`KNIF`)**, produced by `dotnet-knifxc`. KNI's browser `new Effect(gd, byte[])` *also* accepts legacy MGFX v10 — but **via a forked `MGFXReader10`**, and **whether our MGFX v10 GLSL dialect actually *renders* in KNI WebGL is unverified.** Two ways it could fail even after a clean compile:
  1. KNI's reader fork parses the v10 header/section layout differently than desktop MonoGame's `Effect` does → load-time failure.
  2. The MojoShader GLSL dialect KNI's **WebGL** runtime expects differs from desktop **DesktopGL** (e.g. GLSL ES precision qualifiers, attribute binding) → loads but renders wrong.

If either bites, ShadowDusk needs a **KNIFX-v11 (`KNIF`) writer** behind the existing `MgfxVersion` seam. **This phase decides that**, with evidence, before more compiler work is spent on a format that may not load.

---

## Scope: mode-1 first (precompiled), then mode-2 (in-browser compile)

**Mode 1 (precompiled bytes load + render) is the lower-risk, higher-information half and runs first.** It isolates the *KNI load/render* question from the *in-browser compile* question — if a CLI-produced `.mgfx` (already render-validated on desktop in Phase 17) fails in KNI WebGL, the problem is the format/dialect, not the WASM compile path.

Mode 2 (compile in-browser, then load + render) runs **on the existing Slang sample path** for now, purely to exercise the end-to-end browser chain — it is explicitly **not** the faithful-frontend proof (that is Phase 23 M3, which reruns this same harness against the DXC→WASM output once built). Any mode-2 result here is a *sample* result and must be labelled as such per THE PURPOSE.

---

## Tasks

> Legend: **◻ harness/CI** · **🖥️ browser-run** · **🎯 decision-producing**

> **Status update (2026-06-01):** harness BUILT and RUN headless end-to-end.
> Mode-1: **10/10 LOAD, 9/10 render-equivalent, 1/10 (Dissolve) render-diverges.**
> Full results + verdict in `tests/ShadowDusk.BrowserTests/RESULTS.md`.

### Track H — the harness
- [x] ◻ H1. Publish `samples/ShaderFiddle.Web` (`dotnet publish -c Release`, `net8.0-browser`) and serve `wwwroot` over a static HTTP server the test controls. — `publish-sample.mjs` + `static-server.mjs` (dependency-free node server).
- [x] ◻ H2. Playwright harness under `tests/ShadowDusk.BrowserTests/` (node `playwright`, matching the `.wasm-build/*.mjs` pattern) that launches **headless Chromium with software GL** (`--use-gl=angle --use-angle=swiftshader --enable-unsafe-swiftshader`), navigates to the served page, and drives the shader-select + compile UI via two `[JSInvokable]` test entry points (`TestLoadCorpus`, `TestCompileAndApply`). — `run-harness.mjs`.
- [x] ◻ H3. Canvas capture: read back the KNI WebGL canvas pixels. KNI's context does NOT set `preserveDrawingBuffer`, so a naive screenshot is all-black; the sample's `index.html` adds a **`?test=<size>`-gated** hook that forces `preserveDrawingBuffer:true` (wraps `getContext`), pins a fixed square canvas, and exposes `window.__sd_readback()` (`gl.readPixels` + vertical flip + base64). De-risked first by `probe-readback.mjs` (proven non-black). Zero effect on the normal interactive app.
- [x] ◻ H4. Pixel comparison reuses **Phase 17 §6.1** (`image-compare.mjs` mirrors `ShadowDusk.ImageTests/ImageComparer` — RGBA8 max-channel-delta). Reference = the SAME corpus bytes rendered on desktop **DesktopGL** (`RefRenderer/`, Reach profile) — comparing browser-vs-desktop of the same bytes isolates risk #2. Tolerance: start exact; documented per-shader headroom only (Dots transcendental, max-delta 12, justified).

### Track 1 — Mode 1: precompiled `.mgfx` loads + renders in KNI WebGL — 🖥️🎯 (run first)
- [x] 🖥️ 1a. **10/10 corpus shaders LOAD** — `new Effect(gd, bytes)` returns success in KNI's forked `MGFXReader10`. **Parse risk #1 RESOLVED.**
- [x] 🖥️ 1b. Rendered + pixel-compared each vs the desktop reference. **9/10 render-equivalent** (7 within 1 LSB, Saturate within 3 LSB on 10 px, Dots within a documented transcendental tolerance of 12). **Dissolve diverges** (max-delta 198 over 1.68% of pixels) — a real WebGL-vs-DesktopGL render difference at its `discard`/threshold-band boundary (confirmed NOT a profile artifact: HiDef and Reach references are byte-identical).
- [x] 🎯 1c. **KNIFX-v11 decision recorded** in `RESULTS.md`: MGFX v10 **parses** correctly in KNI WebGL (no v11 *container* blocker — all 10 loaded), but render parity is **not complete** (9/10). The single failure is a GLSL **dialect/runtime** divergence, which a v11 *format* writer would not by itself fix; scoped follow-up = investigate the Dissolve `discard`/threshold render path as a Phase-23 prerequisite. Carry-forward is **NOT closeable as-is**.

### Track 2 — Mode 2: in-browser compile + render (sample path, Slang) — 🖥️
- [~] 🖥️ 2a. Mode-2 path is **wired and reached** (`TestCompileAndApply` → `WasmShaderCompiler` → Slang JS backend), but **BLOCKED in this environment**: the ~21 MB `wwwroot/slang/slang-wasm.wasm` is gitignored/restore-gated and absent, so `WebAssembly.instantiate` fails on the 404 HTML. Out of this phase's DoD; rerun with the wasm restored.
- [ ] 🖥️ 2b. Render+compare deferred with 2a (blocked on the restore artifact). Harness already labels any mode-2 result **"sample-only (Slang frontend), not the faithful-path proof."**

### Track 3 — handoff to CI
- [x] ◻ 3. Harness is headless + self-contained for **[Phase 30](PHASE-30-cross-platform-ci.md) §16**: `npm ci && npx playwright install --with-deps chromium && node publish-sample.mjs && node run-harness.mjs`. Deterministic software GL is baked in; 120 s game-boot wait; AV-scan slowness allowance noted in `README.md`. Phase 30 owns the CI wiring.

---

## Definition of Done

1. A headless-browser harness (Playwright) renders ShadowDusk `.mgfx` in the **real KNI WebGL `Effect`** runtime and pixel-compares against the Phase-17 references at the §6.1 tolerance.
2. **Mode 1: all 10 corpus shaders load and render pixel-equivalent** in headless Chromium — OR a precise, reproduced failure list exists.
3. **The KNIFX-v11 question is answered with evidence** (Task 1c): either "MGFX v10 is sufficient for KNI WebGL" is recorded and the carry-forward closed, or a scoped `KNIF`-v11-writer task is opened as a Phase-23 blocker.
4. The harness is runnable unattended and handed to Phase 30 for CI.

> Mode-2's faithful proof is **not** part of this phase's DoD — it is Phase 23 Gate 3 (M3), which reruns this harness against the DXC→WASM frontend. This phase proves the *load/render half*; Phase 23 proves the *faithful-compile half*.

---

## Key files

- `samples/ShaderFiddle.Web/{Pages/Index.razor.cs, ShaderFiddleGame.cs}` (the mode-1 `ApplyEffect` path + mode-2 compile UI), `wwwroot/shaders/OpenGL/*.mgfx` (precompiled corpus)
- `src/ShadowDusk.Core/MgfxWriter.cs`, `CompilerOptions.cs` (the `MgfxVersion` seam where a `KNIF`-v11 writer would land if needed)
- Phase 17 reference PNGs + §6.1 tolerance (`plan/DONE/PHASE-17-monogame-runtime-validation.md`)
- `.wasm-build/publish-check/` (proves the app publishes to wasm)

## Sources
- KNI `MGFXReader10` fork + KNIFX v11 (`dotnet-knifxc`) — see `samples/ShaderFiddle.Web/README.md`
- [Playwright .NET](https://playwright.dev/dotnet/) · headless WebGL via ANGLE/SwiftShader for deterministic CI rendering
