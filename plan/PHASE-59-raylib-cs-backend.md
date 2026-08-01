# Phase 59 — Raylib-cs backend: the fragment-only slice first

**Track:** Backend breadth (purpose-gated). Additive; no existing output byte changes.

**Status:** 📋 **Planned / not started** (created 2026-07-31).

**Depends on:** **the [Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3 DECISION** (not its
code, see §2), [Phase 46](DONE/PHASE-46-shadertoy-to-fx-conversion-tool.md) (the
`render-proof --fidelity` harness that is this phase's oracle),
[Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md) (the shipped GLSL frontend, relevant to
Area B).

**Blocks:** nothing.

**Gated on:** Phase 57 §3 resolving **yes**. Its "if no" branch closes this work outright, in that
phase's own words: *"§16's Raylib follow-on is **closed, not deferred**."* Do not start Area A
before that decision is recorded in [`project_decisions.md`](../project_decisions.md).

> **The shape in one sentence:** emit a Raylib-cs-loadable **fragment shader string** from the same
> faithful pipeline, covering the fullscreen post-process case that is both ~72% of real raylib
> shaders and the entirety of the request that prompted this, without touching the MGFX writer, the
> MonoGame GLSL rewriter, or any existing output byte.

---

## 1. Where this came from — a real user, with a specific shape

A prospective user (via the maintainer, 2026-07-31) asked, in substance:

> *My game is pure C# and can use MonoGame, Raylib, or other frameworks as a back end for rendering,
> audio, and input. I want to offer users a project-setting enum: no effect / retro Game Boy (green
> tint, pseudo dot-matrix pixel spacing) / GBA (saturation + compression, slight pixel spacing) /
> CRT. **These effects only need two shaders**: a CRT shader, and one doing saturation, tint, and
> dot-matrix spacing. I already have a buffer system, so this is a shader pass on the final buffer;
> **both are full-buffer effects**. Either I write each shader twice because Raylib and MonoGame are
> incompatible, or I use some meta shader language and a transpiler and keep one copy in my pure-C#
> layer with confidence it just works on Raylib, MonoGame, and hopefully KNI for web. **Does
> ShadowDusk solve this problem?***

Two things about that request drive this phase's scope:

1. **It is the cheapest possible Raylib shape.** Single pass, fullscreen, fragment-only, a handful of
   scalar uniforms. Every friction [Phase 57 §16](PHASE-57-universal-compiler-auto-detection.md)
   lists for Raylib (no container, no technique/pass concept, no reflection table, multi-pass and
   in-pass render states having no equivalent) is **absent** from it.
2. **The honest answer today is "half."** MonoGame and KNI including web are solved and
   render-proven. Raylib is not supported at all. This phase is the missing half.

---

## 2. The gate — and why this request is the forcing function

[Phase 57 §3](PHASE-57-universal-compiler-auto-detection.md) requires a recorded decision on whether
THE PURPOSE widens from *"drop-in `mgfxc` replacement"* to *"one faithful pipeline, many runtimes:
reference-compiler equivalence where a reference compiler exists, source fidelity where it does
not."*

**Raylib has no reference compiler.** There is no `mgfxc` for it, so a Raylib target can only ever be
proven under the second model. That makes this phase the concrete instance of the abstract question,
and Phase 57's "if no" branch is explicit that a no closes it.

**What is new since Phase 57 was written** is that the question is no longer hypothetical: a real
user with a real, small, well-shaped need is asking. That does not decide the question, but it is
the input the decision should be made against rather than in the abstract.

**Do not treat this phase's existence as pre-judging the decision.** If the answer is no, close this
doc with the reason recorded and reply to the user honestly that MonoGame plus KNI is what ShadowDusk
covers.

---

## 3. What is already established — do not re-derive this

Measured 2026-07-31 against `ChrisDill/Raylib-cs` at `HEAD` and this repo's own source.

### 3.1 The Raylib-cs surface is a good fit, and fragment-only is supported

From `Raylib-cs/interop/Raylib.cs`:

| API | Signature / behaviour | Why it matters here |
|---|---|---|
| `LoadShaderFromMemory` | `(sbyte* vsCode, sbyte* fsCode)` | Takes **two strings**. No container, no file format to write. |
| `LoadShader(null, fs)` | **null vertex shader uses raylib's built-in one** | Confirmed in `Examples/Shaders/ColorCorrection.cs:61`. **A post-process effect needs no vertex shader at all.** |
| `GetShaderLocation` | `(Shader, sbyte* uniformName)` | Binds **by name**. SPIRV-Cross preserves member names, so custom uniforms need no register arithmetic. This is the inverse of MonoGame Rule 7 and strictly easier. |
| `SetShaderValue` / `V` / `Matrix` / `Texture` | present | The consumer's binding surface; not ours to write. |
| `BeginShaderMode` / `EndShaderMode` | present | Exactly the fullscreen post-process pattern the request describes. |

Raylib-cs binds **raylib 6.0**, targets **`net8.0` and `net10.0`** (identical to ShadowDusk's shipped
multi-target), and supports `linux-x64`, `osx-arm64`, `osx-x64`, `win-x64`, `win-x86`, **plus
`browser-wasm` since 8.1.0**.

> The browser-wasm target is worth noting against the request's *"hopefully KNI for web in the
> future"*: **Raylib-cs already has a web target**, so the web story here may not require KNI at all.

### 3.2 Raylib has fixed interface names, so this is NOT a no-op emitter

From `Examples/resources/shaders/glsl330/alpha_discard.fs`, the shape every raylib fragment shader
must present:

```glsl
#version 330
in vec2 fragTexCoord;          // fixed varying names
in vec4 fragColor;
uniform sampler2D texture0;    // raylib auto-binds these
uniform vec4 colDiffuse;
out vec4 finalColor;           // fixed output name
```

SPIRV-Cross will not produce those names on its own. **A convention mapper is required.** It is a
structural rename, not register math, and it is far smaller than the MonoGame path, but it is not
zero and this doc should not be read as claiming otherwise.

### 3.3 The pipeline seam already exists

[`CompilationPipeline.cs:2035`](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L2035)
already branches on `applyMonoGameGlsl`. At that point `transpileResult.Value.Text` holds
SPIRV-Cross's **modern GLSL** — which is what raylib consumes — and the MonoGame path then spends
~3,515 lines and 15 documented rules dragging it *backwards* into MojoShader's GLSL-110 dialect. A
Raylib target takes the modern GLSL, applies §3.2's rename, and returns a string:
**it skips the rewriter and skips the MGFX writer entirely.**

### 3.4 The multi-version problem is real and is not about portability

Raylib-cs ships **four** GLSL version directories, each a near-complete hand-maintained copy:

| Directory | Files |
|---|---|
| `shaders/glsl100` (GLES2 / web) | 74 |
| `shaders/glsl120` | 71 |
| `shaders/glsl330` (desktop) | 75 |
| `shaders/glsl430` | present |

Every raylib shader is maintained in three or four versions **by hand**. This is a pain a
raylib-**only** developer feels with no MonoGame anywhere in the picture, and it is the broader value
story (§4).

ShadowDusk's ES-1.00 safety lowerings already exist and are reusable verbatim for the `glsl100`
path: rules 8, 9b, 12, 13, 15 (`round`, `trunc`, do-while, WebGL1 loop forms) in
[`MonoGameGlslRewriter`](../src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs) /
[`docs/glsl-uniform-naming.md`](../docs/glsl-uniform-naming.md).

### 3.5 Coverage of a fragment-only slice, measured

In `shaders/glsl330`: **58 fragment shaders, 17 vertex shaders**, and **16** shaders have a matching
`.vs`. So roughly **42 of 58 (~72%) are fragment-only** and would work with raylib's default vertex
shader.

The 16 VS+FS pairs are the 3D/material family: `base`, `cel`, `cubemap`, `deferred_shading`,
`gbuffer`, `lighting`, `lightmap`, `normalmap`, `outline_hull`, `pbr`, `point_particle`, `shadowmap`,
`skinning`, `skybox`, `vertex_displacement`, `voxel_lighting`. Those need raylib's **vertex**
conventions (`vertexPosition`, `vertexTexCoord`, `vertexNormal`, `mvp`, `matModel`) and are Area C.

---

## 4. Two value stories, and the honest adoption caveat

**Story 1, cross-backend portability.** Write once, run on MonoGame, Raylib, and web. Only helps
developers using more than one backend. That is a minority, but it is exactly the request in §1 and
it is the more strategic one.

**Story 2, the multi-version GLSL problem (§3.4).** One source compiling to `glsl100` / `glsl120` /
`glsl330` helps **every** raylib developer, including those who have never touched MonoGame.

**The caveat that must not be buried: a raylib-only developer writes GLSL natively today.** Story 2
asks them to write HLSL `.fx` instead, and that is a real switching cost they may reasonably decline
just to get version generation. Some will take it, many will not. This is an adoption question, not
a technical one, and it is the reason Area B exists rather than being assumed away.

---

## 5. Area A — the fragment-only Raylib-cs target (the slice)

**Scope: single-pass, fullscreen, fragment-only.** Explicitly not general Raylib support.

- **A1.** A new `PlatformTarget` member (name TBD; `Raylib` unless the decision in §2 suggests a
  family-scoped naming). It must be **additive**: full-corpus byte-identity for every existing
  target is an acceptance criterion.
- **A2.** A Raylib GLSL emitter taking `transpileResult.Value.Text` (§3.3) and applying §3.2's
  convention mapping: varyings to `fragTexCoord` / `fragColor`, the primary sampler to `texture0`,
  the fragment output to `finalColor`, and the `#version` header per target profile. Custom user
  uniforms keep their SPIRV-Cross names, since raylib binds by name.
- **A3.** Version targeting: `glsl330` desktop and `glsl100` web at minimum, reusing the existing
  ES-1.00 lowerings (§3.4). `glsl120` if it is free once `100` works; `glsl430` is out of scope.
- **A4.** **Output shape.** `CompiledShader.Data` is a single `byte[]` for `new Effect(gd, bytes)`,
  which does not fit `LoadShaderFromMemory(vs, fs)`. Decide deliberately and record it: a text
  payload on the existing shape, a distinct result type, or a small manifest. **Do not** quietly
  return GLSL bytes in a field whose every other consumer expects an MGFX container.
- **A5.** **Loud rejection for everything out of slice.** Multi-pass techniques, in-pass render
  states (blend/depth/cull), and any shape needing a custom vertex shader must fail with a
  registered diagnostic naming the limitation, never a silently-wrong single-pass emission. This is
  the `CLAUDE.md` fail-loudly rule and it is the main correctness risk in this area.
- **A6.** A `samples/` example matching the request: a fullscreen CRT-style pass driven through
  `BeginShaderMode` on a render target, running on both Raylib-cs and MonoGame from one source.

## 6. Area B — the GLSL import probe (do it EARLY; it sizes Story 2)

Story 2's audience depends entirely on whether a raylib developer must **rewrite their shaders in
HLSL**. If their existing GLSL can be ingested, Story 2 changes from *"switch languages"* to *"keep
your shaders, get the versions free"* — a completely different adoption proposition.

- **B1.** Probe whether [`ShadowDusk.ShaderToy`](../src/ShadowDusk.ShaderToy/) can ingest a real
  raylib `glsl330` fragment shader. They are structurally close (fullscreen, `sampler2D` + uniforms,
  a single entry point), but raylib's entry point is `main()` writing `finalColor`, not ShaderToy's
  `mainImage(out vec4, in vec2)`, and the fixed `fragTexCoord` / `texture0` / `colDiffuse` interface
  has no ShaderToy analogue. **Unverified. Do not promise this to anyone before B1 runs.**
- **B2.** From B1, a written verdict: is a raylib-GLSL frontend a small extension of the existing
  converter, a separate frontend, or not worth it? Record it either way.

**Sequence B1 before or alongside A1**, because a "no" materially weakens Story 2 and should inform
how much is invested in Area A.

## 7. Area C — vertex conventions (deferred; demand-gated)

The remaining ~16 VS+FS shapes from §3.5. Needs raylib's vertex attribute and matrix conventions
mapped. **Not in this phase's first pass**, and should be opened only on real demand rather than for
completeness. Record here if demand appears.

---

## 8. Evidence bar — state it plainly, every time

**There is no reference compiler for Raylib**, so reference-compiler equivalence is unavailable by
construction, not by omission. The bar is **source fidelity**, the model already measured in this
repo at 46/46, mean 0.00/255:

> Render the original GLSL directly in Raylib-cs; render ShadowDusk's build of the same shader in
> Raylib-cs; pixel-diff. **Same runtime, same source, no human translation step.**

[Phase 46](DONE/PHASE-46-shadertoy-to-fx-conversion-tool.md)'s `render-proof --fidelity` harness
already implements exactly this, so retargeting it at Raylib-cs is a retarget rather than an
invention.

Every doc, matrix cell, and user-facing statement about this target must name the model. A Raylib
cell must never be allowed to read as though it carried the `mgfxc`-equivalence claim that OpenGL,
DirectX, DirectX12, Vulkan, and FNA carry.

---

## 9. Acceptance

- [ ] Phase 57 §3 decision recorded in `project_decisions.md`. If **no**, this phase is closed with
      the reason recorded and nothing below applies.
- [ ] B1 probe run and B2 verdict written, before Area A is considered finished.
- [ ] Area A: the two shaders from §1 (a CRT effect and a tint/saturation/dot-matrix effect) compile
      from ONE source and render in real Raylib-cs **and** real MonoGame, with the Raylib arm
      pixel-diffed under the §8 source-fidelity bar.
- [ ] `glsl330` and `glsl100` both emitted and both rendering.
- [ ] Out-of-slice shapes (multi-pass, render states, custom VS) rejected loudly with a registered
      diagnostic; fixtures pin each rejection.
- [ ] **Full-corpus byte-identity for every existing target**, proven, not asserted.
- [ ] `docs/validation-matrix.md` gains Raylib cells that name the **source-fidelity** model
      explicitly, plus a §6 driver row for the new render gate.
- [ ] The support-surface list in `CLAUDE.md` updated: pipeline diagram (+ regenerated SVG),
      `docs/the-purpose.md` backend table, `README.md`, the DocFX pages, `project_facts.md`.

## 10. Non-goals

- General Raylib support. Multi-pass, render states, and 3D/material shaders are Area C or out.
- Raylib **audio/input/windowing**. ShadowDusk compiles shaders; the request's other backend
  concerns are not ours.
- Writing the consumer's uniform-binding code. We emit the shader; `GetShaderLocation` /
  `SetShaderValue` stay theirs.
- Any change to MonoGame, KNI, DirectX, Vulkan, or FNA output. This is strictly additive.
- `glsl430` / compute. See [Phase 58](PHASE-58-extended-shader-stages.md).

## 11. Open questions

- **OQ1.** A4's output shape is the one genuine design decision. `LoadShaderFromMemory` takes two
  strings; our result type carries one `byte[]`. Whatever is chosen must not make a Raylib result
  look like an MGFX container to an existing consumer.
- **OQ2.** Does raylib 6.0 differ from earlier raylib in shader conventions in any way that would
  date the mapping? Pin the raylib-cs version this is proven against, per house pin discipline.
- **OQ3.** If Area B succeeds, does the raylib-GLSL frontend belong in `ShadowDusk.ShaderToy` (whose
  name would then be wrong) or in a new package? Naming decision, not just packaging.
- **OQ4.** Raylib-cs `browser-wasm` versus KNI-for-web: if both work, the request's web need has two
  answers. Worth measuring which is actually seamless before recommending either.
