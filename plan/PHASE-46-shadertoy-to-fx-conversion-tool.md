# Phase 46 — ShaderToy → FX Conversion Tool (experiment / SPIKE)

**Track:** Reach experiment (adoption/demo). **Not** the product pipeline.

> ## Results so far (2026-06-19) — bet proven; compiles, loads, AND renders
>
> The tool is **built, green, and render-proven**. The central bet holds: ShaderToy/GLSL image
> shader → `.fx` → the **unchanged, real ShadowDusk pipeline** → every XNA-family backend, and a
> converted shader **renders correctly in a real MonoGame GL `Effect`**.
>
> - **`tools/shadertoy2fx/`** — a standalone managed converter (`ShadowDusk.ShaderToy` library +
>   `shadertoy2fx` CLI + a `ShadowDusk.ShaderToy.Runtime` helper), **no native dependency**, **not**
>   wired into the pipeline, **not** in `ShadowDusk.slnx`. Preprocessor → lexer → parser → AST →
>   type-inference → HLSL emitter → harness generator, behind `ShaderToyConverter.Convert(glsl)`.
>   Builds 0-warning under `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`.
> - **Tests: 161/161 green** — unit trap-tests (matrix-order incl. `*=`, `mod`-sign, vector `==`
>   scalarization, intrinsic renames, splat/truncation), preprocessor (28), custom-uniform (10),
>   uniform-detection, options, **golden regression** over 34 goldens, and **loud-reject** coverage.
> - **Language supported (v1+):** the constrained ShaderToy subset PLUS a full C preprocessor
>   (`#if/#ifdef/#elif/#else/#endif` with a const-expr evaluator + `defined()`, object- and
>   function-like macros, `#undef`) AND custom top-level `uniform` declarations (scalar/vector/matrix
>   + `sampler2D`) exposed as consumer-driven effect parameters. Unsupported shapes (structs, arrays,
>   multipass/Buffer, `##`/`#include`, sampler3D/cube, …) stay **loud located rejects**, never silent.
> - **RENDER-PROVEN**: a converted shader loads in a real MonoGame DesktopGL `Effect` and renders
>   with analytic pixel assertions passing — gradient right-side-up vs ShaderToy's bottom-left
>   `fragCoord` convention (Y-flip correct), and a **host-set custom uniform renders exactly
>   through**. The `render-proof/` driver is the gate; PNGs are committed eyeball evidence.
> - **COMPILE SWEEP 102/102**: all 34 emitted `.fx` (incl. the 234-line real CC0 neon shader)
>   compile through the real pipeline → **OpenGL 34/34, DirectX_11 34/34, FNA 34/34**.
> - **Coverage trajectory (160 real third-party shaders, gitignored scratch, none committed),
>   conversion → end-to-end-compile:** v1 baseline 17.5% → +preprocessor 23.4% → +custom uniforms
>   **26.0% convert / 22.1% end-to-end** (compile-of-converted ~85%). The `COVERAGE.md` blast-radius
>   ranking drives what to add next; the remaining ceiling for single-pass image shaders is structs,
>   arrays, and a long tail of exotic GLSL — many real shaders are **multipass** (out of v1 scope).
>
> **Still open (honest):** (a) **multipass** (Buffer A–D / feedback) is the big unbuilt feature and
> the main reason real-world coverage caps in the ~25-50% range for arbitrary shaders; (b) render
> validation is analytic-pixel + eyeball, not yet a diff against ShaderToy's own WebGL reference for
> a broad corpus; (c) productization (NuGet packaging of the runtime helper, a sample app, docs) is
> not started. The matrix/`mod`/Y-flip traps are render-confirmed for the cases tested.

**Status:** Experiment IN PROGRESS (started + compile-proven 2026-06-19). A **standalone, separate tool** that converts a
**ShaderToy GLSL** shader into an **HLSL `.fx`** source file. It is **deliberately NOT part of the
compiler pipeline**: its only output is `.fx` *text*. Once that `.fx` exists, the **existing,
already-proven ShadowDusk pipeline** compiles it to whatever the consumer's game targets
(MonoGame GL/DX, KNI, FNA) with **zero new pipeline code and no new native dependency.**

> **The whole bet in one sentence:** if we can faithfully turn a ShaderToy shader into a valid
> `.fx`, then "ShaderToy → MonoGame/KNI/FNA" comes for free, because `.fx` is the **one true
> input** the pipeline already compiles to every backend. This phase tests that bet **cheaply**,
> as a source-to-source transpiler, without touching the faithful pipeline and without vendoring
> `glslang`.

---

## Why this shape (and why it is separate)

We considered three ways to get ShaderToy GLSL into the engine (full discussion lives in this
doc's history; summary here):

1. **GLSL → glslang → SPIR-V → existing GL tail → `.mgfx`.** Reuses the SPIR-V→GL tail but needs
   `glslang` vendored across 4 RIDs + WASM, and reaches **GL only**.
2. **GLSL → glslang → SPIR-V → SPIRV-Cross-HLSL → `.fx` → pipeline.** Same `glslang` native cost,
   but reaches **all** backends.
3. **GLSL → managed source transpiler → `.fx` → pipeline.** **No native dependency at all**, and
   reaches **all** backends. The cost moves from "vendor a binary forever" to "write translation
   code that lives in our normal C# packages."

**This phase is option 3.** For a project whose heaviest recurring burden is native packaging,
deleting the native dependency entirely is the decisive advantage. The price is real compiler
work with sharp correctness edges (see *Translation traps*), but it is **pure managed C#** with
no RID matrix, no WASM build, and no self-contained-packaging story to maintain. If the managed
transpiler's long tail proves too costly, option 2 (glslang + SPIRV-Cross-HLSL) is the documented
fallback — but we do not pay for it unless option 3 fails.

**Separate-tool discipline.** The transpiler must not be wired into `CompilationPipeline` or
`EffectCompiler`. It is its own project (CLI + library), and its contract ends at "emit `.fx`
text." This keeps the faithful pipeline untouched (no risk to the `mgfxc`-equivalence promise)
and keeps the experiment disposable: if it does not pan out, nothing in the product depends on it.

**Depends on:** nothing in the pipeline at build time. It *consumes* the existing pipeline only as
a downstream step a user runs (`shadertoy2fx in.glsl > out.fx`, then `mgfxc`/`EffectCompiler`).

---

## Overview — what the tool does

A ShaderToy shader is **not standalone-compilable** anything. It is a fragment-function body
against an implicit harness:

- entry point is `void mainImage(out vec4 fragColor, in vec2 fragCoord)` — there is no `main()`;
- the uniforms `iResolution`, `iTime`, `iMouse`, `iChannel0..3`, … are **predefined**, never declared;
- there is **no vertex shader** at all;
- optional **"Common"** tab code is shared/prepended.

The tool produces a self-contained HLSL `.fx` that wraps this into a real effect:

```
ShaderToy GLSL (image tab [+ common tab])
        │
        ├─ inject the ShaderToy uniform set as HLSL globals  (iTime float, iResolution float3, …)
        ├─ inject iChannel0..3 as Texture2D + SamplerState
        ├─ translate the GLSL body → HLSL  (the transpiler core)
        ├─ synthesize a fullscreen-triangle vertex shader (HLSL)
        ├─ synthesize a pixel shader that calls the translated mainImage with the right fragCoord
        └─ wrap in `technique { pass { VertexShader = …; PixelShader = …; } }`
        ▼
   self-contained .fx  →  (existing ShadowDusk pipeline)  →  .mgfx / .fxb for GL / DX / FNA
```

The emitted `.fx` is ordinary HLSL/FX9. From that point it is indistinguishable from any other
`.fx` the pipeline already handles.

---

## Scope & Non-Goals

**In scope (v1):**
- **Single-pass "image" shaders only** — one `mainImage`, optionally with a "Common" tab prepended.
- The standard ShaderToy uniform set, mapped to HLSL globals + texture/sampler pairs:
  `iResolution (float3)`, `iTime (float)`, `iTimeDelta (float)`, `iFrame (int)`,
  `iMouse (float4)`, `iDate (float4)`, `iChannelTime[4] (float)`, `iChannelResolution[4] (float3)`,
  `iSampleRate (float)`, `iChannel0..3` → `Texture2D` + `SamplerState`.
- A managed GLSL→HLSL translator covering the **constrained ShaderToy subset** (types,
  operators, the common intrinsic set, control flow, user functions, `texture()` calls).
- A synthesized fullscreen-triangle VS and the `mainImage` wrapper PS.
- A `technique`/`pass` wrapper so the `.fx` is complete.
- **Fail loudly** (clear diagnostic, non-zero exit) on any construct outside the supported subset
  — never emit subtly-wrong HLSL silently (project constraint 5).
- Delivery as a **separate** CLI (`shadertoy2fx`) + a small library, in its own folder, **not** in
  `ShadowDusk.slnx`'s product graph initially (treat like the `validation/*` drivers: real but
  out-of-band).

**Out of scope / Non-Goals:**
- **Multipass** ShaderToy shaders (Buffer A–D, feedback/ping-pong, the "Common"+multiple image
  buffers model). That needs render-target orchestration at *runtime*, not just translation — a
  much bigger, v2+ undertaking.
- **Non-texture iChannels**: audio, video, cubemap, keyboard, webcam channels. `iChannelN` is
  modeled as a 2D texture only.
- **Wiring into the compiler pipeline.** The tool emits `.fx`; it never becomes a frontend of
  `EffectCompiler` (that would be option 1/2, explicitly not this phase).
- **The runtime render helper.** Producing a loadable effect is *necessary but not sufficient* to
  see pixels — the consumer's game must set `iTime`/`iResolution`/`iMouse`/`iChannelN` each frame
  and draw a fullscreen triangle. That helper is a **separate deliverable** (see *Runtime helper*),
  not part of this conversion tool.
- Any change to existing GL/DX/FNA output (this phase adds a sibling tool, touches no pipeline code).

---

## Architecture & key decisions

- **Source-to-source, managed, no native dep.** The translator is hand-written C# (a small
  GLSL-subset lexer/parser + an HLSL emitter). It ships as ordinary managed code — no `glslang`,
  no SPIR-V, no RID matrix. This is the entire reason to prefer this over options 1/2.
- **Constrained subset, loud boundary.** We do **not** attempt full GLSL. We support the subset
  ShaderToy image shaders actually use, and we *reject* (with a precise message + the offending
  construct) anything else. A reject is a correct outcome, not a failure of the tool — it protects
  the "never silently wrong" rule.
- **Uniform model.** ShaderToy's fixed uniforms become HLSL globals (the pipeline already packs
  globals into the constant buffer / `*_uniforms_vec4[]` model). `iChannelN` become `Texture2D` +
  `SamplerState` pairs named so a runtime helper can bind them predictably. Only emit the uniforms
  the shader actually references (lean parameter list).
- **Fullscreen pass.** Synthesize a standard fullscreen-triangle VS in HLSL that outputs clip-space
  position and a `fragCoord`-equivalent (pixel coordinates derived from `iResolution`). The PS
  computes `fragCoord` and calls the translated `mainImage`.
- **One `.fx`, every backend.** The emitted `.fx` is backend-neutral HLSL/FX9. The existing
  pipeline decides GL vs DX vs FNA. **Caveat:** FNA's target is fx_2_0 / SM3, which has real
  instruction-count and loop limits — complex ShaderToy shaders may compile fine for GL/DX but
  **legitimately exceed SM3 on FNA**. That is an inherent fx_2_0 limit (the pipeline already fails
  loudly there), not a tool bug; document it.

### Translation traps (the sharp edges — get these right or it renders wrong)

These are the specific GLSL→HLSL semantic differences that must be handled, not just syntax-mapped:

- **Matrix multiply order is reversed.** GLSL is column-major: `M * v`. HLSL row-major: `mul(v, M)`
  (and `mul(M, v)` for the other side). Get this wrong and every rotation/transform is subtly broken.
  This is the single highest-risk trap.
- **`mod` differs for negatives.** GLSL `mod(x,y)` = `x - y*floor(x/y)`; HLSL `fmod` truncates
  toward zero. Emit the GLSL-equivalent expression, not a bare `fmod`, when sign can be negative.
- **`gl_FragCoord` origin.** GL origin is bottom-left; D3D top-left. The Y of `fragCoord` must be
  flipped relative to `iResolution.y` so output matches ShaderToy's reference orientation.
- **Vector/matrix type spelling.** `vec2/3/4` → `float2/3/4`, `mat2/3/4` → `float2x2/3x3/4x4`,
  `ivec*`/`bvec*` likewise; **component access and constructors** mostly carry over but swizzle
  edge cases need checking.
- **Intrinsic renames.** `mix`→`lerp`, `fract`→`frac`, `atan(y,x)`→`atan2(y,x)` (and `atan(x)`→`atan`),
  `dFdx/dFdy`→`ddx/ddy`, `texture(s,uv)`→`s.Sample(...)` (or `tex2D` for fx_2_0 reach),
  `mix`/`clamp`/`smoothstep`/`step` semantics verified, `inversesqrt`→`rsqrt`, etc. Maintain an
  explicit, tested mapping table; **anything not in the table is a loud reject.**
- **Integer/`%` semantics, `bool` vectors, `discard`** — verify each against HLSL.
- **Precision qualifiers** (`highp`/`mediump`/`lowp`) — strip (HLSL has no direct equivalent in FX9).

A concrete, exhaustive mapping table (type, operator, intrinsic, with the reject-list) is the first
real deliverable — it sizes the whole effort.

---

## Phase 0 — the cheap probe (do this FIRST, before writing the parser)

Prove the bet by hand before building the machine:

1. Pick **one** representative single-pass ShaderToy image shader.
2. **Hand-translate** it into a `.fx` using the harness + mapping above (no tool yet).
3. Compile that `.fx` with **today's** ShadowDusk for **OpenGL and DirectX** (and FNA if it fits SM3).
4. Load + render it (fullscreen triangle, hand-driven uniforms) and eyeball against the ShaderToy page.

If the hand-built `.fx` renders correctly on GL and DX, the bet holds and the transpiler is "just"
automating a proven recipe. If it does not, we learn the real blockers (coordinate convention,
uniform binding, an intrinsic gap) for the price of one afternoon — before committing to the parser.

---

## Tasks

- [ ] **Phase 0 probe**: hand-translate one ShaderToy shader to `.fx`, compile GL+DX with the
      existing pipeline, render and compare to the ShaderToy reference. Record findings.
- [ ] Write the **GLSL→HLSL mapping table** (types, operators, intrinsics) + the explicit
      **reject-list** of unsupported constructs. This sizes the subset.
- [ ] Scaffold a separate project pair: `tools/shadertoy2fx` (library + CLI), **not** added to the
      product graph in `ShadowDusk.slnx` initially (out-of-band like `validation/*`).
- [ ] Implement the **harness generator**: ShaderToy uniform set → HLSL globals + `Texture2D`/
      `SamplerState` pairs (only the referenced ones); the fullscreen-triangle VS; the `mainImage`
      wrapper PS with correct `fragCoord` (incl. Y-flip); the `technique`/`pass` wrapper.
- [ ] Implement the **transpiler core**: a GLSL-subset lexer/parser + HLSL emitter honoring every
      *Translation trap* (matrix order, `mod` sign, intrinsic renames, type spelling).
- [ ] Implement **loud rejection**: any unsupported construct → precise diagnostic (construct +
      location) + non-zero exit; never emit silently-wrong HLSL.
- [ ] Support the optional **"Common" tab** (prepend shared code before translation).
- [ ] **Tests**: a corpus of ~10–15 single-pass ShaderToy shaders → transpile → compile with the
      existing pipeline for GL **and** DX (FNA where it fits SM3); assert compile success; golden
      the emitted `.fx` text for determinism.
- [ ] **Validation/oracle**: capture each corpus shader's ShaderToy WebGL reference image; render the
      transpiled-then-compiled effect (GL) and pixel-diff against the reference with a documented
      tolerance. Record that the oracle here is **ShaderToy WebGL**, not `mgfxc` (a different,
      honestly-weaker bar — state it).
- [ ] Document the FNA/SM3 instruction-limit caveat and the GL-is-most-faithful note.
- [ ] Run `/platform-check` on the new tool (it is build/CLI-time, must stay cross-platform).

## Acceptance Criteria

- [ ] **Phase 0 probe passes**: a hand-built `.fx` from a real ShaderToy shader compiles with the
      existing pipeline and renders recognizably like the ShaderToy reference on GL and DX.
- [ ] `shadertoy2fx in.glsl` emits a **self-contained, valid `.fx`** for the supported subset.
- [ ] That `.fx`, fed to the **unchanged** existing pipeline, compiles to **OpenGL and DirectX**
      `.mgfx` (and FNA `.fxb` when within SM3 limits) with **no pipeline code change**.
- [ ] The compiled effect **loads** in a real MonoGame/KNI runtime and renders within the documented
      tolerance of the ShaderToy WebGL reference (GL path is the gold reference).
- [ ] Unsupported constructs produce a **clear, located diagnostic** and a non-zero exit — never a
      silently-wrong `.fx`.
- [ ] The tool adds **zero** native dependencies and makes **zero** changes to existing GL/DX/FNA
      output (a byte-diff of the existing corpus is unchanged).

## Definition of Done

A separate `shadertoy2fx` tool converts a single-pass ShaderToy image shader into a valid HLSL
`.fx`, which the **existing** ShadowDusk pipeline then compiles to MonoGame/KNI (GL/DX) and, within
SM3 limits, FNA — with no pipeline changes and no native dependency. A small corpus is transpiled,
compiled, and render-compared (tolerance) against the ShaderToy WebGL references, with GL as the
gold reference. Unsupported shaders fail loudly. The experiment has answered: **can we get
"ShaderToy → any XNA-family backend" for free by transpiling to `.fx`?** — with evidence either way.

---

## Runtime helper (separate deliverable — flagged, not in this phase)

Producing a loadable effect is **necessary but not sufficient** to see pixels. To actually render a
ShaderToy in-game, the consumer must, each frame: set `iTime`/`iResolution`/`iTimeDelta`/`iFrame`/
`iMouse`/`iChannelN`, bind a fullscreen triangle, and draw with the effect. Without that, the `.fx`
loads but draws nothing — which would violate the project's "it just works" directive if we shipped
the conversion alone.

So a real ShaderToy story needs a tiny **`ShaderToyEffect` runtime helper** (a small sample or
companion library) wrapping the loaded `Effect`:

```csharp
var toy = new ShaderToyEffect(content.Load<Effect>("myShader"));
toy.Update(gameTime, mouse, viewportSize);  // sets iTime/iResolution/iMouse/iFrame/iChannelN
toy.Draw(device);                            // binds fullscreen triangle + applies the pass
```

This is **out of scope for the conversion tool** (which only emits `.fx`) but must exist before the
feature is "usable in MonoGame/KNI" end to end. Tracked here so it is not forgotten when the
experiment succeeds.

---

## Open questions / risks

- **Subset long tail.** Clever ShaderToy shaders reach for intrinsics/constructs outside the common
  subset. Mitigation: loud reject + grow the mapping table from real failures. If the tail proves
  too large to hand-translate, fall back to **option 2** (glslang + SPIRV-Cross-HLSL) for breadth —
  documented but not built unless needed.
- **Matrix-order / `mod` / Y-flip correctness.** The highest-risk traps; a wrong call renders
  plausibly-but-wrong. Mitigation: targeted unit tests per trap + the render-diff corpus.
- **Oracle weakness.** There is no `mgfxc`/`fxc` oracle for "ShaderToy → engine" (it is not an
  `mgfxc` input), so the bar is ShaderToy's own WebGL output, which itself varies by driver. State
  the weaker bar honestly; do not claim `mgfxc`-equivalence here.
- **FNA/SM3 ceiling.** Complex shaders that compile on GL/DX may legitimately exceed fx_2_0 limits.
  Inherent, not a bug — surface the pipeline's existing loud failure and document it.
- **Cross-backend faithfulness.** GL output is closest to the WebGL reference; DX/FNA may differ a
  hair due to D3D conventions. Treat all-backend reach as a bonus; validate GL as gold, DX/FNA as
  "renders correctly," not "pixel-identical to WebGL."
- **Scope creep into multipass.** Buffer A–D is the gateway drug to a much bigger runtime project.
  Keep v1 strictly single-pass; multipass is a deliberate, separately-scoped future phase.
