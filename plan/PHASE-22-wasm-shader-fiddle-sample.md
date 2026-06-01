# Phase 22 — WASM Shader Fiddle Sample (KNI, paste-a-shader → live cat)

**Status:** In progress (2026-05-31) — building on the **Fallback (mode 1)** path. Mode 2 (in-browser compile) is **blocked on Phase 100** (the emscripten DXC/SPIRV-Cross WASM modules do not exist yet; `ShadowDusk.Wasm/Phase19.js` throws by design). In-browser *visual* validation is **not possible in the current dev session** (no browser, no emscripten) — the rendered-cat acceptance bar is a documented **manual run** step, not an in-session check.
**Depends on:**
- **Phase 19** (WASM runtime compilation) — *the* prerequisite. This sample is the user-facing realization of Phase 19's **mode 2** (in-browser `.fx` → `.mgfx` via WASM-compiled DXC + SPIRV-Cross from `ShadowDusk.Wasm`). It also exercises mode 1 (loading `.mgfx` bytes via `new Effect` in a WebGL runtime).
- **Phase 17** (MonoGame-loadable `.mgfx` + MojoShader-dialect GLSL) — the browser-produced effect must load and render exactly like a desktop one.
- **Phase 25** (security hardening) — the textarea takes **arbitrary untrusted shader source**; the same input-validation bar as the web path applies.

**Blocks:** A live, demonstrable proof of ShadowDusk's *reach* axis — the XNA-Fiddle-style "type a shader, see it run, no server" experience. This is the showcase deliverable that makes the WASM path tangible.

> Phase 19's Definition of Done describes this tool in prose ("A browser tool (XNA Fiddle–style) takes `.fx` source typed by a user, compiles it entirely client-side in WASM, loads the result as a real MonoGame/KNI `Effect` that renders correctly"). **Phase 22 is that tool, built as a concrete, shippable sample project.** Phase 19 delivers the compiler capability; Phase 22 delivers the app.

---

## Review findings (2026-05-31) — three review agents, before implementation

These supersede the original assumptions where they conflict.

1. **Mode 2 is not functional yet (blocked on Phase 100).** `src/ShadowDusk.Wasm` is fully built: `WasmShaderCompiler : IShaderCompiler` composes `EffectCompiler` with `JsDxcShaderCompiler` + `JsSpirvToGlslTranspiler` + the pure-managed `SpirvReflector`, and **compiles for `net8.0-browser`**. But [`Phase19.js`](../src/ShadowDusk.Wasm/Phase19.js) stubs both JS imports to `throw` — the emscripten **DXC** and **SPIRV-Cross** WASM modules behind the `shadowdusk-dxc` / `shadowdusk-spirv-cross` contracts **do not exist in the repo** (Phase 100 tail). So a real paste→compile cannot run client-side today. **→ Build against the Fallback (mode 1); wire the mode-2 call but let it surface the stub error honestly.**

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
- The WASM compiler internals themselves (DXC/SPIRV-Cross WASM interop) — that is **Phase 19**; this sample consumes it. If Phase 19 mode 2 is not yet functional, see *Fallback* below.
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

## Fallback if Phase 19 mode 2 isn't ready

The user-facing goal is **in-memory, in-browser** compilation. If Phase 19 mode 2 (WASM DXC + SPIRV-Cross) is not yet functional when this sample is built, do **not** silently fake it. Options, in order:
1. Build the full UI + render path against **mode 1** (precompiled `.mgfx` bytes loaded in WebGL) using the Phase 17 corpus, so the load+render+cat half is proven and the page works — with the editor disabled/labelled "compile coming with Phase 19 mode 2."
2. Optionally wire a **temporary server-side compile endpoint** (calls the Phase 9 CLI / `ShadowDusk.Compiler`) behind a clearly-labelled flag, purely to demo the UX — but mark it explicitly as *not* the differentiator (it's a server roundtrip).
Then swap in `WasmShaderCompiler` the moment mode 2 lands. The sample's value is the in-browser compile; ship that as soon as Phase 19 allows.

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
- [x] 🖥️ Wire the textarea → `WasmShaderCompiler.CompileAsync(source, OpenGL)` → bytes → `new Effect` → apply, all in-browser, on button click. **The real compiler is called**; today it surfaces the honest Phase19.js stub error (mode 2 → Phase 100), never faked.
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
- [x] ✅ Documented manual run/smoke step (`README.md` + `run.ps1`); headless-browser check folded into [Phase 30 CI](PHASE-30-cross-platform-ci.md) — Phase 30.

---

## Acceptance Criteria

- [x] 🖥️ `samples/ShaderFiddle.Web/` **builds and publishes** as a KNI Blazor-WASM app (`dotnet build` + `dotnet publish -c Release` clean; all `index.html` JS/asset paths resolve in publish output; `ShadowDusk.Wasm.wasm` bundled). *Running* in a browser is the pending manual step.
- [ ] 🖥️/⬜ Paste `.fx` → Compile & Apply → cat re-rendered, compiled client-side: **path wired to the real `WasmShaderCompiler`, but blocked on Phase 100** (WASM DXC/SPIRV-Cross modules). Surfaces the stub error today; not faked.
- [x] ✅ A compile/load failure shows ShadowDusk's diagnostics (line/col/message) in the UI, keeps the previous good render, and does not crash the page (implemented; browser-confirm pending).
- [x] 🖥️ Uniform-free + uniform-driven corpus shaders load with the by-name default parameter set (mode 1); interactive controls a documented stretch.
- [x] ✅ WebGL-vs-DesktopGL divergence **and** the KNI MGFX-v10/KNIFX-v11 risk are documented (README + risks below), not assumed away.
- [x] ✅ Phase 19 mode 2 is incomplete, so the sample ships against **mode 1** with the in-browser-compile path clearly flagged and surfacing the real stub error — never faked.

---

## Definition of Done

A browser page where a user pastes HLSL `.fx` shader source, ShadowDusk compiles it to `.mgfx`
**entirely in the browser via WASM**, the bytes load as a real KNI `Effect`, and the standard cat
image is rendered live with that shader applied — with compile errors surfaced in the UI. This is
the visible, end-to-end demonstration of ShadowDusk's *reach* promise: the result `mgfxc` would
produce, generated where `mgfxc` cannot run — in a browser, with no server.

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
- **Phase 19 mode 2 readiness — CONFIRMED BLOCKED (Phase 100).** `Phase19.js` throws by design; the
  emscripten DXC/SPIRV-Cross WASM modules don't exist yet. The sample is built on the Fallback
  (mode 1) and wires the real `WasmShaderCompiler` for mode 2 so the swap is a `Phase19.js` change,
  not a C# one.
- **No in-session browser** — the build session has no browser, so the rendered-cat result is a
  documented manual run (`README.md`) + a Phase 30 headless-browser CI item, not verified here.
- **Download size / cold-start** — the WASM compiler stack may be large; measure and report, even
  if optimization is deferred.
- **Untrusted input** — arbitrary shader text is a security surface (Phase 25): bound compile time
  / memory, never leak host paths in diagnostics.
- **WebGL precision drift** — WebGL/ESSL `mediump` vs desktop may legitimately drift a few LSB
  (same class of tolerance noted in Phase 17 §6.1); judge by eye/diff, document.
