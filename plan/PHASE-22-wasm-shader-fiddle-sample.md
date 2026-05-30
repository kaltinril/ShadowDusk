# Phase 22 — WASM Shader Fiddle Sample (KNI, paste-a-shader → live cat)

**Status:** Planned
**Depends on:**
- **Phase 19** (WASM runtime compilation) — *the* prerequisite. This sample is the user-facing realization of Phase 19's **mode 2** (in-browser `.fx` → `.mgfx` via WASM-compiled DXC + SPIRV-Cross from `ShadowDusk.Wasm`). It also exercises mode 1 (loading `.mgfx` bytes via `new Effect` in a WebGL runtime).
- **Phase 17** (MonoGame-loadable `.mgfx` + MojoShader-dialect GLSL) — the browser-produced effect must load and render exactly like a desktop one.
- **Phase 25** (security hardening) — the textarea takes **arbitrary untrusted shader source**; the same input-validation bar as the web path applies.

**Blocks:** A live, demonstrable proof of ShadowDusk's *reach* axis — the XNA-Fiddle-style "type a shader, see it run, no server" experience. This is the showcase deliverable that makes the WASM path tangible.

> Phase 19's Definition of Done describes this tool in prose ("A browser tool (XNA Fiddle–style) takes `.fx` source typed by a user, compiles it entirely client-side in WASM, loads the result as a real MonoGame/KNI `Effect` that renders correctly"). **Phase 22 is that tool, built as a concrete, shippable sample project.** Phase 19 delivers the compiler capability; Phase 22 delivers the app.

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

### Project setup
- [ ] Create `samples/ShaderFiddle.Web/` — KNI Blazor WASM project; confirm KNI WebGL package + version; add to a sample solution filter (keep it out of the core `slnx` build if it complicates cross-platform CI).
- [ ] Reference `src/ShadowDusk.Wasm`; reference/copy the shared render helper from `validation/Shared` and the `cat.jpg` content asset.

### Render path (works against mode 1 first)
- [ ] Boot a KNI WebGL `GraphicsDevice`; load `cat.jpg` as a `Texture2D`; draw it with `SpriteBatch` to the canvas (no shader) as the baseline.
- [ ] Load a Phase-17 `.mgfx` (precompiled) via `new Effect(gd, bytes)` and apply it over the cat; confirm it renders in WebGL (this is Phase 19 mode 1 — reuse its findings).

### Compile loop (mode 2 — the differentiator)
- [ ] Wire the textarea → `WasmShaderCompiler.CompileAsync(source, OpenGL)` → bytes → `new Effect` → apply over the cat, all in-browser, on button click.
- [ ] Prefill the textarea with a working default shader; render it on first load.
- [ ] Error panel: on `Result` failure, display each `ShaderError` (file/line/col/message) verbatim; keep the last good render; clear on next success.
- [ ] Handle the "compiling…" state and first-run module-load latency without freezing the UI.

### Parameters (demo corpus, then stretch)
- [ ] For the prefilled/known shaders, set a sensible default parameter set by name (mirror `MakeSceneFor` values used in validation).
- [ ] **Stretch:** reflect the compiled effect's parameters and auto-generate simple UI controls (float slider, color picker, vector inputs) bound by name.

### Validation
- [ ] Manually verify several shaders (uniform-free: Grayscale/Invert; uniform-driven: Tint/Sepia) render in-browser equivalently to their desktop Phase-17 result; record any WebGL-vs-DesktopGL divergence (research doc §15.2).
- [ ] Feed deliberately-broken `.fx` (syntax error, unknown intrinsic) and confirm the diagnostics surface cleanly and the page stays usable.
- [ ] Run the untrusted-input cases past [Phase 25](PHASE-25-security-hardening.md).
- [ ] Add a documented manual run/smoke step (and, if feasible, a headless-browser check folded into [Phase 30 CI](PHASE-30-cross-platform-ci.md)).

---

## Acceptance Criteria

- [ ] `samples/ShaderFiddle.Web/` builds and runs in a browser as a KNI Blazor-WASM app.
- [ ] A user can paste `.fx` source, click Compile & Apply, and see the **cat** re-rendered with that shader — compiled **in-memory, client-side** (no server roundtrip; via `ShadowDusk.Wasm`).
- [ ] A compile error shows ShadowDusk's diagnostics (line/col/message) in the UI and does not crash the page; the previous good render remains.
- [ ] At least the uniform-free Phase-17 shaders render correctly in-browser; uniform-driven shaders render with a default parameter set (interactive controls a documented stretch).
- [ ] Any WebGL-vs-DesktopGL rendering divergence is documented, not assumed away.
- [ ] If Phase 19 mode 2 is incomplete, the sample ships against mode 1 with the in-browser-compile path clearly stubbed/flagged — never faked.

---

## Definition of Done

A browser page where a user pastes HLSL `.fx` shader source, ShadowDusk compiles it to `.mgfx`
**entirely in the browser via WASM**, the bytes load as a real KNI `Effect`, and the standard cat
image is rendered live with that shader applied — with compile errors surfaced in the UI. This is
the visible, end-to-end demonstration of ShadowDusk's *reach* promise: the result `mgfxc` would
produce, generated where `mgfxc` cannot run — in a browser, with no server.

---

## Open questions / risks

- **KNI WebGL custom-`Effect` support** — confirm KNI's WebGL `GraphicsDevice` accepts a raw
  `byte[]` `.mgfx` via `new Effect(gd, bytes)` and links the SpriteBatch VS to a PS-only custom
  effect the same way DesktopGL does (Phase 17 §5). This is the load-side unknown.
- **Phase 19 mode 2 readiness** — the in-browser compile is the whole point and depends on the
  WASM DXC/SPIRV-Cross interop landing. Sequence accordingly (see *Fallback*).
- **Download size / cold-start** — the WASM compiler stack may be large; measure and report, even
  if optimization is deferred.
- **Untrusted input** — arbitrary shader text is a security surface (Phase 25): bound compile time
  / memory, never leak host paths in diagnostics.
- **WebGL precision drift** — WebGL/ESSL `mediump` vs desktop may legitimately drift a few LSB
  (same class of tolerance noted in Phase 17 §6.1); judge by eye/diff, document.
