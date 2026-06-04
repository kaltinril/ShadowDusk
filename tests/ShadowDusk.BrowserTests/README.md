# ShadowDusk.BrowserTests — Phase 24 Browser Render Validation

A **headless-browser (Playwright) harness** that renders ShadowDusk `.mgfx` in
the **real KNI WebGL `Effect`** runtime (the `samples/ShaderFiddle.Web` Blazor-
WASM sample) and pixel-compares the canvas against a desktop **DesktopGL** render
of the *same bytes*. This is the first rung-(4) evidence in a real browser
graphics stack — see `plan/PHASE-24-browser-render-validation.md`.

It answers the **KNIFX-v11 question** (Phase 24 Task 1c): does our MGFX v10
load and render in KNI WebGL, or is a `KNIF`-v11 writer needed?

## What it does

1. **Reference (desktop):** `RefRenderer/` is a tiny DesktopGL console app that
   renders each of the 10 precompiled OpenGL corpus `.mgfx`
   (`samples/ShaderFiddle.Web/wwwroot/shaders/OpenGL/*.mgfx`) over the cat, using
   the **exact** draw recipe the browser uses (`ShaderFiddleGame.Draw`: fixed
   square viewport, black clear, fit-centered cat, `BlendState.Opaque` +
   `SamplerState.LinearClamp`, SpriteBatch VS prime, effect in `Immediate`),
   with `WebShaderInputs`-equivalent params, in the **Reach** profile (KNI WebGL
   is Reach). Output → `references/{Name}.png`.
   - Comparing browser-render vs desktop-render of the **same bytes** isolates
     the WebGL-vs-DesktopGL question (Phase 24 risk #2) from the compiler.
2. **Browser (headless):** `run-harness.mjs` publishes & serves the sample,
   launches headless Chromium with deterministic software GL
   (`--use-gl=angle --use-angle=swiftshader`), and per shader:
   - selects it via the `[JSInvokable] TestLoadCorpus` mode-1 path and asserts
     KNI's `new Effect(gd, bytes)` **loads** (returns `null`) — parse risk #1;
   - captures the WebGL canvas pixels and pixel-compares vs the reference at the
     Phase 17 §6.1 tolerance — render risk #2.
   - Then mode-2 (`TestCompileAndApply`, the Slang sample path) on one shader,
     **labelled sample-only — not the faithful-path proof**.
3. Writes `captures/*.png`, `diffs/*_diff.png`, and **`RESULTS.md`** (the Task-1c
   verdict).

### Canvas capture (the de-risk)

KNI's WebGL context does **not** request `preserveDrawingBuffer`, so a naive
screenshot is all-black. The sample's `wwwroot/index.html` adds a hook **gated
behind `?test=<size>`**: it wraps `HTMLCanvasElement.getContext` to force
`preserveDrawingBuffer:true`, pins the canvas to a fixed square, and exposes
`window.__sd_readback()` (a `gl.readPixels` + vertical flip + base64). With no
`?test` query the interactive app is untouched. `probe-readback.mjs` is a
standalone proof that a non-black render is captured.

## Run locally

```bash
cd tests/ShadowDusk.BrowserTests
npm ci                                   # or: npm install
npx playwright install chromium          # CI: --with-deps chromium

# 1. publish the sample + render desktop references (both into this dir):
node publish-sample.mjs

# 2. de-risk probe (optional): prove a non-black capture for one shader
node probe-readback.mjs Grayscale

# 3. the full harness (mode-1 x10 + mode-2):
node run-harness.mjs                     # writes RESULTS.md; exit 0 iff mode-1 passes
```

Requires: .NET SDK with the `net8.0` packs + `wasm-tools` workload (for the
Blazor-WASM publish), Node 18+, Playwright + Chromium.

## KNI HiDef / WebGL2 mode (Phase 33 / issue #7)

`--corpus=sd-hidef` loads ShadowDusk's own `.mgfx` in a KNI **HiDef** context
(WebGL2 / GLSL ES 3.00) instead of the default Reach (WebGL1 / GLSL ES 1.00). It
boots the sample with `?profile=hidef` (a permanent knob in `ShaderFiddleGame.cs`,
read in `Index.razor.cs`), which makes KNI request a WebGL2 context and
runtime-convert the legacy `.mgfx` GLSL to ES 3.00 at load.

This is the issue-#7 regression guard. KNI's converter only rewrites mgfxc's
`#define ps_oC0 gl_FragColor` form; ShadowDusk's *raw* `gl_FragColor` write
(current `MonoGameGlslRewriter`) is left undeclared in ES 3.00, so the shader
fails to compile.

```bash
cd tests/ShadowDusk.BrowserTests
npm install
npx playwright install chromium
node publish-sample-sd-hidef.mjs          # reuses .publish-sd/ + references-sd/ (add --skip-publish if present)
node run-harness.mjs --corpus=sd-hidef
```

- **Before** the `MonoGameGlslRewriter` `#define` fix → **RED**: the corpus fails
  to load (`'gl_FragColor' : undeclared identifier`), written to
  `RESULTS-SD-HIDEF-REPRO.md`. The harness exits 0 because reproducing the bug is
  the success condition for the RED baseline.
- **After** the fix → **GREEN**: all 10 load + render within tolerance vs their
  Reach render, written to `RESULTS-SD-HIDEF.md`.

> Note: two MRT-style corpus shaders (Sepia, Dissolve) fail under HiDef with
> `'gl_FragData' : undeclared identifier` — the same root cause for the
> `gl_FragData[N]` output path; both are addressed by the same `#define` fix
> (`#define ps_oC{N} gl_FragData[N]`).

## CI (Phase 30 §16)

The harness is headless and self-contained. Phase 30 owns the CI wiring; a job is:

```bash
dotnet workload install wasm-tools
cd tests/ShadowDusk.BrowserTests
npm ci
npx playwright install --with-deps chromium
node publish-sample.mjs
node run-harness.mjs
```

Notes for CI:
- Use software GL (`--use-gl=angle --use-angle=swiftshader`, already set in the
  harness) so rendering is deterministic without a GPU.
- Allow generous timeouts: the Blazor-WASM publish + first page boot are slow,
  and AV on-access scanning of fresh native binaries can add minutes (see the
  CLAUDE.md Phase 21 note). The KNI game-boot wait is 120 s.
- **mode-2** needs the ~21 MB `wwwroot/slang/slang-wasm.wasm`, which is
  gitignored/restore-gated (`wwwroot/slang/RESTORE.md`). If absent, mode-2 is
  reported as BLOCKED — it is **out of Phase 24's Definition of Done** (the
  faithful in-browser-compile proof is Phase 23 M3, which reruns this harness).

## Files

| File | Purpose |
|---|---|
| `RefRenderer/` | DesktopGL console app → `references/*.png` (same bytes, same recipe, Reach profile). |
| `publish-sample.mjs` | Publishes the sample into `.publish/` and renders references. |
| `static-server.mjs` | Dependency-free static server for the published `wwwroot`. |
| `probe-readback.mjs` | Standalone de-risk: proves a non-black canvas capture. |
| `run-harness.mjs` | The harness (mode-1 ×10 + mode-2), writes `RESULTS.md`. |
| `image-compare.mjs` | JS mirror of `ShadowDusk.ImageTests/ImageComparer` (RGBA8 max-delta). |
| `references/`, `captures/`, `diffs/` | Reference PNGs, browser captures, magenta diffs. |
| `RESULTS.md` | Generated run results + the KNIFX-v11 verdict (Task 1c). |
