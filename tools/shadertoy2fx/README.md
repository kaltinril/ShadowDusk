# shadertoy2fx — ShaderToy GLSL → HLSL `.fx` converter (Phase 46 experiment)

A **standalone, out-of-band** tool (NOT part of the ShadowDusk compiler pipeline) that converts a
single-pass GLSL **image** shader into a self-contained HLSL **`.fx`** source file. It accepts **both**
entry conventions (G2): a **ShaderToy** `void mainImage(out vec4 fragColor, in vec2 fragCoord)` shader
**and** a **plain-GLSL** `void main()` fragment shader (glslViewer / Bonzomatic / Shadertoy-export
style) that writes `gl_FragColor` or a user-declared `out vec4 <name>;` and reads `gl_FragCoord`. The
convention is auto-detected (no flag, no consumer choice); a shader that defines BOTH a `mainImage` AND
a standalone `void main()` wrapper (the common desktop-runner shape) prefers ShaderToy mode, drops the
`void main()` wrapper with a Warning, and converts. Once you have the `.fx`, the **existing ShadowDusk pipeline** compiles it to MonoGame/KNI
(OpenGL/DirectX) and, within fx_2_0/SM3 limits, FNA — with zero pipeline changes and no native
dependency.

See [`plan/DONE/PHASE-46-shadertoy-to-fx-conversion-tool.md`](../../plan/DONE/PHASE-46-shadertoy-to-fx-conversion-tool.md)
for the full design, scope, traps, and the bet being tested.

## Layout

**Most of this experiment has been promoted or migrated out.** What lives here now:

- `src/ShadowDusk.ShaderToy.Cli/` — the `shadertoy2fx` CLI wrapper. Retained because it is the only
  command-line entry point to the converter's **multipass batch mode** (the product
  `ShadowDuskCLI` takes a single `.glsl` but has no `--multipass`).
- `render-proof/` — the out-of-band fidelity / gallery / multipass render driver. See its own
  [README](render-proof/README.md).

Where the rest went:

- **`src/ShadowDusk.ShaderToy/` → [`../../src/ShadowDusk.ShaderToy/`](../../src/ShadowDusk.ShaderToy/)**
  (Phase 47) — the converter is now a first-class in-solution product library published as a NuGet.
  Single-pass entry point:
  `ShadowDusk.ShaderToy.ShaderToyConverter.Convert(string glsl, ConvertOptions? options = null) → ConvertResult`.
  Multipass batch entry points (in `Multipass/`): `ShaderToyProject.Parse(json) → ShaderToyProject`,
  `MultipassConverter.Convert(project, options) → MultipassResult`, and `MultipassManifest.ToJson(result)` /
  `MultipassManifest.ToWiringMarkdown(result)`.
- **`tests/ShadowDusk.ShaderToy.Tests/` → [`../../tests/ShadowDusk.ShaderToy.Tests/`](../../tests/ShadowDusk.ShaderToy.Tests/)**
  (Phase 47) — unit tests (lex/parse/emit/traps) + golden regression tests over a corpus of
  ShaderToy-dialect shaders; now run by every `dotnet test ShadowDusk.slnx`.
- **`sample/` and `src/ShadowDusk.ShaderToy.Runtime/` → [`../../samples/ShaderToyViewer/`](../../samples/ShaderToyViewer/)**
  (Phase 51 A4) — the interactive viewer and the MonoGame `ShaderToyEffect` runtime helper (folded
  into the sample as `Runtime/ShaderToyEffect.cs`). MonoGame belongs to the sample tier, never to a
  shipped `ShadowDusk.*` package.

## Build & test (out-of-band — not in `ShadowDusk.slnx`)

```bash
dotnet build tools/shadertoy2fx/shadertoy2fx.slnx
dotnet test  tools/shadertoy2fx/shadertoy2fx.slnx
```

## Multipass batch mode (Buffer A–D / feedback)

The converter also has a **BATCH multipass-export** mode that accepts a ShaderToy **multi-tab export**
(the ShaderToy API JSON: `{ "renderpass": [ ... ] }`) and emits **one `.fx` per render tab** plus a
machine-readable **`manifest.json`** and a human **`WIRING.md`**:

```bash
shadertoy2fx --multipass export.json -o outdir/
```

- Each `buffer`/`image` tab is converted with the **exact same single-pass converter** (no behavior
  change); the single `common` tab is prepended to every pass.
- It **resolves the channel wiring**: which pass feeds which `iChannelN`, which channels are
  **feedback** (a buffer reading its own previous frame), and which are external textures you supply.
  Sampler wrap/filter per channel are recorded.
- `sound` / `cubemap` passes are **skipped with a warning** (out of v1 scope); an unsupported channel
  ctype (keyboard/music/mic/webcam/volume/cubemap/video) is **warned and left unbound**.
- The canonical execution order (`Buffer A..D` then `Image`) is recorded; the CLI exits **non-zero** if
  any pass fails to convert (per-pass errors on stderr in MGCB form).

**Explicit scope: we do NOT build a ShaderToy runtime / orchestrator / emulator.** We "accept the
syntax" (convert each tab) and hand you the pieces + a documented ~15-line MonoGame `RenderTarget2D`
wiring example in `WIRING.md`. **The render graph is your job, the way MonoGame already works**:
allocate a target per buffer, bind prior outputs as `iChannelN` via `ShaderToyEffect`, run the passes
in order, ping-pong any feedback buffer, draw the last pass to the screen. The hand-wired `chain2`
example in `render-proof/` is the worked proof of that loop.

## Scope (v1)

Single-pass image shaders (one `mainImage` **or** one plain-GLSL `void main()`, optional Common tab),
**plus** the additive **multipass batch-convert** mode above (one `.fx` per tab + wiring; not an
orchestrator). No audio/video/cubemap channels. Unsupported constructs **fail loudly** (clear
diagnostic + non-zero exit), never a silently-wrong `.fx`. The oracle is ShaderToy's own WebGL output
(an honestly weaker bar than `mgfxc`). A separate `ShaderToyEffect` runtime helper (not in this tool)
is needed to drive the uniforms and draw a fullscreen triangle to actually render in-engine.
