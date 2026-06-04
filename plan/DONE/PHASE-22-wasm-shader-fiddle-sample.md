# Phase 22 — WASM Shader Fiddle Sample (KNI, paste-a-shader → live cat)

**Status: DONE (2026-06-02).** Both modes ship on the **faithful product pipeline**: Mode 1 loads precompiled `.mgfx`; **Mode 2 compiles in-browser via the faithful DXC→WASM frontend** (`WasmShaderCompiler`, self-registered from the `ShadowDusk.Wasm` package). The Slang substitute it originally used is **superseded by [Phase 23](PHASE-23-in-browser-compilation.md)** and is now dead, sample-only reference (`wwwroot/shadowdusk-dxc.js` + `wwwroot/slang/` — not registered, never runs).

The two caveats that kept this phase open are both resolved:
- **Frontend is now faithful** — [Phase 23](PHASE-23-in-browser-compilation.md) landed the pinned DXC→WASM module; its in-browser SPIR-V is **byte-identical to desktop DXC** (10/10 corpus). No substitute compiler in the product path.
- **"Loads as a real KNI `Effect` + renders live" is proven, not hypothesized** — [Phase 24](PHASE-24-browser-render-validation.md) renders the corpus **10/10** in real headless KNI WebGL (KNI's forked `MGFXReader10` parses ShadowDusk's MGFX v10 — **no KNIFX-v11 writer needed**; Dissolve fixed via the slot-1 sampler pin + the `roundEven`→`floor(x+0.5)` WebGL1 lowering), plus a confirmed **live desktop-browser** compile-and-render run (2026-06-02).

The documented **stretch** (reflect the compiled effect's parameters → auto-generated controls) is **implemented** — the "Live parameters" panel ([commit c84ce3e]) lists the effect's `float` scalar/vector params and drives them live; a **Reset (no shader)** button was added too. `dotnet build` **and** `dotnet publish -c Release` are clean, with the faithful 17 MB `dxcompiler.wasm` bundled as a self-registered static web asset. The **only** carry-forward is untrusted-input hardening — always **[Phase 25](PHASE-25-security-hardening.md)**'s scope, never a Phase 22 acceptance criterion. Doc moved to `plan/DONE/`.

> The body below is the as-built record; where the original prose still says "mode 2 uses the Slang sample-frontend," read it as superseded by the DONE banner above — the shipping sample uses the faithful DXC→WASM path.
**Depends on:**
- **Phase 19** (WASM runtime compilation) — *the* engine prerequisite (injectable backends, `SpirvReflector`, `WasmShaderCompiler`).
- **Phase 17** (MonoGame-loadable `.mgfx` + MojoShader-dialect GLSL) — the browser-produced effect must load and render exactly like a desktop one.
- **Phase 23** (in-browser compilation) — supplies the *faithful* DXC→WASM frontend the **product** path needs; this sample currently uses the Slang sample-frontend instead.
- **Phase 24** (browser render validation) — owns the Playwright run that actually confirms the cat renders + answers the KNI MGFX-v10-load question.
- **Phase 25** (security hardening) — the textarea takes **arbitrary untrusted shader source**; the same input-validation bar as the web path applies.

**Blocks:** A live, demonstrable proof of ShadowDusk's *reach* axis — the XNA-Fiddle-style "type a shader, see it run, no server" experience. This is the showcase deliverable that makes the WASM path tangible.

> Phase 19's Definition of Done describes this tool in prose ("A browser tool (XNA Fiddle–style) takes `.fx` source typed by a user, compiles it entirely client-side in WASM, loads the result as a real MonoGame/KNI `Effect` that renders correctly"). **Phase 22 is that tool, built as a concrete, shippable sample project.** Phase 19 delivers the compiler capability; Phase 22 delivers the app.

---

## Review findings (2026-05-31) — three review agents, before implementation

These supersede the original assumptions where they conflict.

1. **Mode 2 in the sample now runs — via the Slang sample-frontend (UPDATED 2026-06-01).** `src/ShadowDusk.Wasm` is fully built: `WasmShaderCompiler : IShaderCompiler` composes `EffectCompiler` with `JsDxcShaderCompiler` + `JsSpirvToGlslTranspiler` + the pure-managed `SpirvReflector`, and **compiles for `net8.0-browser`**. The original `Phase19.js` stub (which threw by design) is **superseded in the sample** by real modules in `samples/ShaderFiddle.Web/wwwroot/`: `shadowdusk-dxc.js` (backed by **Slang v2026.10 WASM**, ~21 MB, lazy-loaded) and `shadowdusk-spirv-cross.js` (backed by `spirv-cross.wasm`, node-verified byte-identical to desktop). So a real paste→compile **does** run client-side in the sample today. **BUT** Slang is a *substitute compiler* — accepted **sample-only** per THE PURPOSE; it silently drops two DXC flags and cannot be proven faithful on arbitrary shaders. The **product** path (faithful DXC→WASM in the shipping `ShadowDusk.Wasm` package) is [Phase 23](PHASE-23-in-browser-compilation.md) and is **not** what this sample demonstrates.

2. **KNI format risk — load side (highest risk).** KNI v4.2 (`nkast.Kni.Platform.Blazor.GL` 4.2.9001, SDK `Microsoft.NET.Sdk.BlazorWebAssembly`, TFM plain `net8.0`) uses its **own KNIFX v11 format** (`dotnet-knifxc`). KNI's `new Effect(gd, byte[])` *also* accepts legacy **MGFX v10** (signature `MGFX`, version byte `10`, profile byte `ShaderProfileType.OpenGL_Mojo = 0`) — which is exactly what ShadowDusk's GL writer emits — via a backward-compat `MGFXReader10`. **But that reader is a fork**: passing the signature+version gate is necessary, not sufficient. Whether ShadowDusk/`mgfxc` MGFX v10 *renders correctly* (GLSL dialect, reflection, PS-only SpriteBatch link) in KNI WebGL is an **unverified hypothesis**, not a fact. If it diverges, Phase 22 needs a **KNIFX-v11 writer** (study `KNIFXReader11.cs` + `dotnet-knifxc` output). This is the load-side unknown the original doc flagged, now with specifics.

3. **In-session validation is impossible.** The dev environment has no browser and no emscripten toolchain. The project can be authored and (workload permitting) `dotnet build`-ed, but the **rendered-cat acceptance bar must be a documented manual run** (`dotnet run` / `dotnet serve` + open in a browser), not an in-session automated check. Headless-browser CI → Phase 30.

4. **Render path to mirror is fully mapped.** `validation/Shared/EffectImageRenderer.cs` + `validation/Shared/ShaderInputs.cs` give the exact recipe: `BlendState.Opaque` + `SamplerState.LinearClamp`, the **SpriteBatch prime** (`Begin(Deferred,Opaque,LinearClamp)` → `Draw(cat)` → `End()`) so the PS-only effect inherits SpriteBatch's VS, then `Begin(Immediate,Opaque,LinearClamp,null,null,effect)` → `Draw(cat)` → `End()`. Parameters are set **by name** (null-conditional). Cat asset: `samples/ShaderViewer/Content/cat.jpg`. The 10 PS-only SM3 corpus shaders + their default params are enumerated in `ShaderInputs.SetParams`; precompiled mode-1 bytes available at `tests/fixtures/golden/OpenGL/*.mgfx`.

---

## Overview

Build a browser sample — `samples/ShaderFiddle.Web/` — using **KNI** (nkast's cross-platform MonoGame fork, the one with a Blazor WebAssembly + WebGL backend; see `monogame_runtime_mgfx_compiler_research.md` KNI ref). The page presents:

1. A **text area** prefilled with a working `.fx` shader, where the user pastes/edits HLSL effect source.
2. A **"Compile & Apply"** button (plus optional debounced auto-recompile).
3. A **canvas** rendering the standard ShadowDusk **cat image** (`samples/ShaderViewer/Content/cat.jpg`) with the user's shader applied via `SpriteBatch`.
4. An **error panel** that surfaces ShadowDusk's diagnostics verbatim (file/line/column/message — constraint 5, *fail loudly*) when compilation fails, leaving the last good render up.

The end-to-end loop, entirely client-side:

```
paste .fx  ──▶  ShadowDusk.Wasm.WasmShaderCompiler.CompileAsync(src, OpenGL)   // in-memory, in-browser (Phase 19 mode 2)
            ──▶  byte[] .mgfx  (MonoGame/MojoShader format — Phase 17)
            ──▶  new Effect(graphicsDevice, mgfxBytes)                          // KNI WebGL runtime
            ──▶  SpriteBatch.Begin(effect: fx); Draw(cat); End();               // applied on top of the cat
            ──▶  WebGL canvas
```

No MGCB, no `fxc.exe`, no server roundtrip — the differentiator Phase 19 mode 2 unlocks.

---

## Scope and Non-Goals

**In scope:**
- A KNI Blazor-WASM sample project, `samples/ShaderFiddle.Web/`, that builds and runs in a browser.
- Paste-in `.fx` editor → **in-memory** compile via `ShadowDusk.Wasm` → `new Effect(gd, bytes)` → cat with shader applied.
- Diagnostics UI: on compile failure, show ShadowDusk's `ShaderError[]` (line/col/message); on success, render and clear errors.
- A working **default shader** so the page renders something on first load (start from a Phase 17 corpus shader or a fresh example, e.g. Grayscale/Tint).
- Parameter handling for the demo corpus: at minimum a fixed default parameter set for the prefilled shaders; **stretch** — reflect the compiled effect's parameters and auto-generate simple controls (sliders/color pickers) so uniform-driven shaders are interactive.
- OpenGL/**WebGL** profile only.

**Out of scope:**
- The WASM compiler internals themselves (DXC/SPIRV-Cross WASM interop) — the engine is **Phase 19** and the *faithful* frontend is **Phase 23**; this sample consumes them (and uses the Slang sample-frontend in the interim, see *Frontend status* below).
- DirectX/DXBC in the browser (no native P/Invoke; Phase 18 / 4.1 spike).
- A full editor (syntax highlighting, autocomplete, multi-file includes) — a single-buffer textarea is enough; richer editing is a follow-on.
- Hosting/deployment (CDN, custom domain) and download-size optimization — note size/cold-start, don't optimize here.
- Arbitrary user-uploaded textures — the input image is the fixed cat (a texture picker is a stretch/follow-on).

---

## Architecture & key decisions

- **Runtime:** KNI Blazor WebAssembly + WebGL. (MonoGame.Framework's own WASM story is weaker; KNI is the WASM-capable fork ShadowDusk targets for "reach.") Confirm the KNI package/version and that `new Effect(gd, byte[])` is available on its WebGL `GraphicsDevice`.
- **Compiler call:** reference `src/ShadowDusk.Wasm` (`WasmShaderCompiler : IShaderCompiler`); call `CompileAsync(source, options{Target=OpenGL})` and use the returned `.mgfx` bytes directly — no disk, no server.
- **Render path:** mirror the desktop validation harness (`validation/Shared/EffectImageRenderer.cs`) and `samples/ShaderViewer` — same pinned blend/sampler state, same SpriteBatch-prime for the PS-only VS stage (Phase 17 §5), so a shader that matches on desktop behaves the same here.
- **Cat asset:** reuse `samples/ShaderViewer/Content/cat.jpg` (the standard image used across validation). Load it as a `Texture2D` in the WebGL device.
- **Recompile UX:** explicit button first; debounced auto-recompile as a stretch. Keep the last successfully-loaded `Effect` displayed when a new compile fails.
- **Threading/async:** WASM is single-threaded; the compile may be slow on first run (native-WASM module load). Show a "compiling…" state; never block the UI thread badly. No `.Result`/`.Wait()` (constraint).

---

## Frontend status (mode 2 now runs via the Slang sample-frontend)

The user-facing goal is **in-memory, in-browser** compilation, and the sample achieves it today via the Slang-wasm frontend (per *Review finding #1*). Two rules govern this:

1. **Mode 1 (precompiled `.mgfx` loaded in WebGL)** remains the lowest-risk baseline and is what [Phase 24](PHASE-24-browser-render-validation.md) validates first.
2. **A server-side compile relay is explicitly out of bounds** — it is a server roundtrip, which violates THE PURPOSE's "no server" differentiator. Never add one, even behind a flag.

The sample's Slang mode-2 is honest *reach demonstration*, but it is **not** the faithful product path. When [Phase 23](PHASE-23-in-browser-compilation.md) lands the faithful DXC→WASM frontend, the **shipping `ShadowDusk.Wasm` package** swaps to it; the sample may keep Slang as its lightweight demo frontend, clearly labelled.

---

## Tasks

> Legend: **✅** code-complete **and** build/publish-verified in-session · **🖥️** done in code but needs an actual browser run to confirm (no browser in the build session) · **⬜** not started / deferred.

### Project setup
- [x] ✅ Create `samples/ShaderFiddle.Web/` — KNI Blazor WASM project (`nkast.Kni.Platform.Blazor.GL` 4.2.9001, `Microsoft.NET.Sdk.BlazorWebAssembly`, TFM **net8.0-browser**). Kept **out of `ShadowDusk.slnx`** with its own empty `Directory.Build.props` so it doesn't drag browser-only build machinery into core CI (rationale in README). TFM choice proven by build probe: net8.0 → net8.0-browser ref is NU1201; net8.0-browser references both KNI (net8.0) and Wasm (net8.0-browser).
- [x] ✅ Reference `src/ShadowDusk.Wasm`; port the render helper + by-name params from `validation/Shared` (`WebShaderInputs.cs`, near-verbatim — KNI keeps the `Microsoft.Xna.Framework` namespace); copy `cat.jpg`.

### Render path (works against mode 1 first)
- [x] 🖥️ Boot a KNI WebGL `GraphicsDevice` (`ShaderFiddleGame`); load `cat.jpg` via `Texture2D.FromStream`; draw with `SpriteBatch` (prime pass) — code-complete, browser-render pending.
- [x] 🖥️ Load a Phase-17 `.mgfx` (precompiled, bundled in `wwwroot/shaders/OpenGL`) via `new Effect(gd, bytes)` and apply over the cat (dropdown selects 1 of 10) — code-complete; **whether KNI's forked `MGFXReader10` actually renders MonoGame MGFX v10 is the open browser-verified unknown** (see risks).

### Compile loop (mode 2 — the differentiator)
- [x] 🖥️ Wire the textarea → `WasmShaderCompiler.CompileAsync(source, OpenGL)` → bytes → `new Effect` → apply, all in-browser, on button click. **The real compiler is called and runs** via the Slang sample-frontend (`wwwroot/shadowdusk-dxc.js` → Slang-wasm) + `shadowdusk-spirv-cross.js` — node-verified per stage, browser render pending [Phase 24](PHASE-24-browser-render-validation.md). This is the *sample* frontend, not the faithful product path ([Phase 23](PHASE-23-in-browser-compilation.md)).
- [x] ✅ Prefill the textarea with a working default shader (Grayscale `.fx`); render it via its precompiled bytes on first load.
- [x] ✅ Error panel: on `Result` failure, display each `ShaderError` via `FxcFormattedMessage` (file/line/col/message) verbatim; keep the last good render; clear on next success/load.
- [x] ✅ Handle the "compiling…" state (button disabled + label) without blocking; first-run latency note in README.

### Parameters (demo corpus, then stretch)
- [x] ✅ Default parameter set by name for all 10 corpus shaders (`WebShaderInputs.SetParams`, ported from `validation/Shared/ShaderInputs.cs`).
- [ ] ⬜ **Stretch:** reflect the compiled effect's parameters and auto-generate UI controls — not done (documented stretch).

### Validation
- [ ] 🖥️ Manually verify several shaders (Grayscale/Invert; Tint/Sepia) render in-browser equivalently to desktop Phase-17; record WebGL-vs-DesktopGL divergence — **pending a browser run** (manual step documented in README; needed to also settle the MGFX-v10-in-KNI question).
- [x] 🖥️ Feed deliberately-broken `.fx`: the diagnostics path + "keep last good render" is implemented; browser-confirm pending.
- [ ] ⬜ Run untrusted-input cases past [Phase 25](PHASE-25-security-hardening.md) — deferred to Phase 25.
- [x] ✅ Documented manual run/smoke step (`README.md` + `run.ps1`); headless-browser check folded into [Phase 30 CI](PHASE-30-ci-and-nuget-release.md) — Phase 30.

---

## Acceptance Criteria

- [x] 🖥️ `samples/ShaderFiddle.Web/` **builds and publishes** as a KNI Blazor-WASM app (`dotnet build` + `dotnet publish -c Release` clean; all `index.html` JS/asset paths resolve in publish output; `ShadowDusk.Wasm.wasm` bundled). *Running* in a browser is the pending manual step.
- [x] 🖥️ Paste `.fx` → Compile & Apply → cat re-rendered, compiled client-side: **wired and running via the Slang sample-frontend** (node-verified per stage; browser render pending Phase 24). This is the *sample* reach demo, **not** the faithful product proof (Phase 23).
- [x] ✅ A compile/load failure shows ShadowDusk's diagnostics (line/col/message) in the UI, keeps the previous good render, and does not crash the page (implemented; browser-confirm pending).
- [x] 🖥️ Uniform-free + uniform-driven corpus shaders load with the by-name default parameter set (mode 1); interactive controls a documented stretch.
- [x] ✅ WebGL-vs-DesktopGL divergence **and** the KNI MGFX-v10/KNIFX-v11 risk are documented (README + risks below), not assumed away.
- [x] ✅ The in-browser frontend is clearly flagged as the **sample-only Slang substitute**, with the faithful DXC→WASM product path pointed to Phase 23 — never silently passed off as the faithful pipeline.

---

## Definition of Done

A browser page where a user pastes HLSL `.fx` shader source, the sample compiles it to `.mgfx`
**entirely in the browser via WASM** (today via the **sample-only Slang frontend** — *not* the
faithful DXC pipeline; that is [Phase 23](PHASE-23-in-browser-compilation.md)), the bytes load as a
real KNI `Effect`, and the standard cat image is rendered live with that shader applied — with
compile errors surfaced in the UI. This is a visible demonstration of ShadowDusk's *reach* — the
*shape* of "compile where `mgfxc` cannot run, no server."

**Two caveats this DoD does not let drift:**
- The in-browser compile uses **Slang (a substitute compiler)**. Per THE PURPOSE this sample
  proves *reach is reachable*, **not** that the output is faithful to `mgfxc`. The faithful proof
  (byte-identical DXC→WASM) is Phase 23; do not read this sample as discharging it.
- "Loads as a real KNI `Effect` and renders live" is a **hypothesis pending a real browser run** —
  KNI's forked `MGFXReader10` accepting our MGFX v10 is unverified (see risks). **[Phase 24](PHASE-24-browser-render-validation.md)**
  is what turns this DoD clause from hypothesis into fact.

---

## Open questions / risks

- **KNI MGFX-v10 render parity (THE load-side unknown, now with specifics).** KNI v4.2's
  `new Effect(gd, byte[])` accepts legacy MonoGame **MGFX v10** (`MGFX` signature, version byte 10,
  profile `OpenGL_Mojo`) via a backward-compat `MGFXReader10` — so ShadowDusk's existing GL output
  should *load*. But that reader is a **fork** of MonoGame's, and KNI's native format is now
  **KNIFX v11** (`KNIF`, `dotnet-knifxc`). Passing the signature/version gate ≠ rendering parity in
  KNI's GLSL-dialect/reflection path. **Must be confirmed by a browser run.** If it diverges, the
  fix is a ShadowDusk **KNIFX-v11 writer** (study `KNIFXReader11.cs` + `dotnet-knifxc` output) — a
  follow-up task, deliberately *not* pre-built here (untestable in-session; user-confirmed). The
  sample reports the `new Effect` failure verbatim if this is the case.
- **Faithful frontend not yet in the sample — uses Slang (sample-only).** Mode 2 runs via the
  Slang-wasm modules in `wwwroot/`, not the faithful DXC→WASM pipeline. The faithful frontend is
  [Phase 23](PHASE-23-in-browser-compilation.md); when it lands, the **product** `ShadowDusk.Wasm`
  package swaps to it. The original `Phase19.js` throwing stub is superseded in the sample (it
  survives only as the reference module contract in `src/ShadowDusk.Wasm/`).
- **No in-session browser** — the build session has no browser, so the rendered-cat result is owned
  by **[Phase 24](PHASE-24-browser-render-validation.md)** (Playwright headless) + a Phase 30 CI
  item, not verified here.
- **Download size / cold-start** — the WASM compiler stack may be large; measure and report, even
  if optimization is deferred.
- **Untrusted input** — arbitrary shader text is a security surface (Phase 25): bound compile time
  / memory, never leak host paths in diagnostics.
- **WebGL precision drift** — WebGL/ESSL `mediump` vs desktop may legitimately drift a few LSB
  (same class of tolerance noted in Phase 17 §6.1); judge by eye/diff, document.
