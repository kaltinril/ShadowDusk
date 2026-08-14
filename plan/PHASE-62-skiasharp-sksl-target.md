# Phase 62 — SkiaSharp: HLSL → SkSL (and why "bytecode" is the wrong target)

**Track:** Backend breadth (purpose-gated). Additive; **no existing output byte changes**.

**Status:** 📋 **Planned / not started** (created 2026-08-11). **The two premise corrections in §2
were put to the requester and both were accepted** (vchelaru, 2026-08-12, on
[PR #201](https://github.com/kaltinril/ShadowDusk/pull/201)) — see §2.5. The phase's *shape* is
therefore settled: **SkSL text, fragment-only.** Its *gate* is not — Phase 57 §3 is unchanged, and
OQ1's demand question is only half answered.

**Depends on:** **the [Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3 PURPOSE
decision** (not its code) — the identical hard gate [Phase 59](PHASE-59-raylib-cs-backend.md)
carries, and for the identical reason: **there is no reference compiler**, so source-fidelity is the
only evidence model available.

**Blocks:** nothing.

**Gated on:** Phase 57 §3 resolving **yes**, plus a demand question in §5 that should be asked
before anything else.

> [issue #197](https://github.com/kaltinril/ShadowDusk/issues/197), vchelaru: *"Add ability to
> convert HLSL -> bytecode for SkiaSharp (Might be same as normal skia?)"* — filed with an empty
> body. The issue's own parenthetical is answered in §2.2 (yes, SkiaSharp is just Skia), and its
> **premise is corrected in §2.1**: SkiaSharp has no bytecode entry point.

---

## 1. Where this came from

The third of Victor Chelaru's 2026-08-09 issues, with
[#199](https://github.com/kaltinril/ShadowDusk/issues/199) → [Phase 60](DONE/PHASE-60-xnb-content-output.md)
and [#198](https://github.com/kaltinril/ShadowDusk/issues/198) → [Phase 61](PHASE-61-slang-support.md).

Context, **now verified rather than assumed** (§2.6, measured 2026-08-13): Victor maintains **Gum**,
whose shaders are already vendored here as a regression corpus
([Phase 49](DONE/PHASE-49-apos-shapes-regression-corpus.md) put three Gum shaders plus Apos.Shapes
under [`tests/fixtures/shaders/third-party/`](../tests/fixtures/shaders/third-party/)), and Gum
ships a real **SkiaSharp** render path (`Runtimes/SkiaGum/`, using `SKRuntimeEffect`) alongside its
MonoGame/FNA ones. So the real request is *"let a Gum author write one shader and have it work on
the SkiaSharp renderer too."*

**This was written as a guess and has since been confirmed** — Gum maintains the *same* grayscale
effect as both a `.fx` and a `.sksl`, and ShadowDusk already has the `.fx`. §2.6 is the detail, and
it is the most useful thing in this doc.

---

## 2. What is already established — do not re-derive this

Measured 2026-08-11 against `mono/SkiaSharp` `main`.

### 2.1 The premise needs correcting: **SkiaSharp takes SkSL SOURCE, not bytecode**

`SKRuntimeEffect`'s entire public creation surface is string-based:

```csharp
public static SKRuntimeEffect CreateShader      (string sksl, out string errors)
public static SKRuntimeEffect CreateColorFilter (string sksl, out string errors)
public static SKRuntimeEffect CreateBlender     (string sksl, out string errors)

public static SKRuntimeShaderBuilder      BuildShader      (string sksl)
public static SKRuntimeColorFilterBuilder BuildColorFilter (string sksl)
public static SKRuntimeBlenderBuilder     BuildBlender     (string sksl)
```

**No public API accepts precompiled bytecode.** Compilation happens inside Skia
(`sk_runtimeeffect_make_for_shader`). Skia does have an internal SkSL IR/VM, but it is not exposed
through SkiaSharp and is not a stable artifact to target.

**So the deliverable, if this phase ever runs, is HLSL → SkSL *text*.** That is a materially
different — and honestly *easier* — target than "bytecode", and it changes the shape of the work
from a binary writer to a source emitter. It also means there is no container, no version byte, and
no loader to be faithful to.

### 2.2 Answering the issue's own parenthetical: yes, it is "the same as normal Skia"

SkiaSharp is a thin P/Invoke binding over Skia's C API. The shading language, its dialect, and its
limits are Skia's, not SkiaSharp's. So anything true of `SkRuntimeEffect` in C++ Skia is true
here, and there is no SkiaSharp-specific shader format to discover.

### 2.3 The pipeline seam already exists, and Phase 59 already found it

This is the one piece of genuinely good news, and it is shared with the Raylib work.
[`CompilationPipeline.cs:2035`](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L2035)
is the point where **SPIRV-Cross's modern GLSL is in hand, before the ~3,515-line MonoGame rewriter
drags it backwards into MojoShader GLSL-110**. An SkSL emitter would branch there and skip both the
rewriter and the MGFX writer entirely.

So the compile route is:

```
HLSL --[DXC]--> SPIR-V --[SPIRV-Cross]--> modern GLSL --[SkSL convention mapper]--> SkSL text
```

Every stage but the last already exists and is proven. **No substitute compiler is involved**, which
keeps this consistent with THE PURPOSE.

### 2.4 It is NOT a no-op emitter — SkSL is GLSL-*like*, not GLSL

The convention mapper is where the real work is, and it must not be hand-waved. SkSL diverges from
GLSL in ways that are shallow individually and add up:

- **Entry point.** SkSL's shader entry is `half4 main(float2 coord)` — it *returns* the colour and
  takes the local coordinate as a parameter. There is no `gl_FragColor` and no `gl_FragCoord`-style
  output variable to assign.
- **Types.** SkSL leans on `half`/`half4` and has its own precision model.
- **Inputs.** No vertex stage and no varyings at all for a runtime effect — everything a GLSL
  fragment shader would receive as an interpolant has to arrive as a uniform or be derived from
  `coord`. **This is the biggest semantic gap** and it bounds what can convert.
- **Samplers.** Child shaders/images come in as `uniform shader` and are sampled with
  `.eval(coord)`, not `texture()`/`texture2D()`.
- **Not a general GPU language.** No arbitrary control flow guarantees, restricted loops, no
  derivatives in the general case.

The honest consequence: **the convertible set is roughly "fragment-only, coordinate-driven effects
with uniform inputs"** — post-process/tint/gradient/SDF work. That is not nothing (it is most of
what a UI library like Gum would want), but it is much narrower than "HLSL support", and §4 A1 is
where it gets measured instead of estimated.

### 2.5 Both corrections were accepted by the requester (2026-08-12)

§2.1 and §2.4 each contradicted something in the issue, so both were put back to vchelaru on
[PR #201](https://github.com/kaltinril/ShadowDusk/pull/201). Verbatim:

> *"I understand that this means that SKSL doesn't support vertex shaders. That's totally okay. For
> my usage at least, I only care about the pixel shader. We can surface this limitation in docs for
> other users."*
>
> *"As far as it being a transpiler to SKSL rather than a true compiler, that's also 100% okay with
> me. No problems here."*

**Settled by this, and not to be relitigated:**

- **The deliverable is SkSL *text*.** §2.1's premise correction stands accepted; "bytecode" is off
  the table by agreement as well as by measurement. §6's non-goal is now uncontested.
- **Fragment-only is acceptable, not a defect.** §6's "no vertex-shader story" non-goal is confirmed
  by the person who filed the issue.
- **A docs obligation now exists** and is the requester's own suggestion: if this phase ever ships,
  the fragment-only limit must be stated where a consumer will hit it, not buried in a phase doc.
  §5 carries it as an acceptance item.

**NOT settled by this — and this is the one place the reply and §2.4 do not quite meet.** The reply
addresses the missing **stage** ("I only care about the pixel shader"). §2.4's binding constraint is
the missing **interface**: a runtime effect has **no varyings at all**, so a pixel shader gets
`coord` plus uniforms and nothing else. A pixel shader that reads an interpolated UV, a vertex
colour, or any other `VS_OUTPUT` member does not convert — *even though it is purely a pixel
shader*, and even though nobody wants to write a vertex shader for it. Not caring about the vertex
stage and not *depending* on it are different properties, and only the second one bounds the
convertible set.

That is not a disagreement with the reply; it is the question the reply leaves open, and it is
already **OQ2**. **A1 must measure it against real Gum pixel shaders rather than infer it from the
acceptance** — the acceptance tells us a fragment-only target is *wanted*, not that his shaders
*fit* one.

### 2.6 §1's guess is confirmed, and **the exact shader pair already exists** (measured 2026-08-13)

§1 reasoned that the real request is *"let a Gum author write one shader and have it work on the
SkiaSharp renderer too"* and flagged it as a guess. **It is not a guess. Measured against
`vchelaru/gum` on 2026-08-13:**

- Gum has a whole **`Runtimes/SkiaGum/`** runtime, and it uses **`SKRuntimeEffect`** directly
  (`Runtimes/SkiaGum/RenderingLibrary/Renderer.cs`), with tests at
  `Tests/SkiaGum.Tests/Renderer/RenderTargetShaderTests.cs`. This is a shipping render path, not a
  side experiment.
- **Gum ships exactly one `.sksl` file today:**
  `Samples/SilkNetGum/SilkNetGumSample/resources/Grayscale.sksl`.
- **And ShadowDusk already has its `.fx` twin vendored** — Phase 49 pulled
  `Samples/MonoGameGumInCode/MonoGameGumInCode/Content/Grayscale.fx` in as
  [`tests/fixtures/shaders/third-party/Gum/MonoGameInCode-Grayscale.fx`](../tests/fixtures/shaders/third-party/Gum/MonoGameInCode-Grayscale.fx),
  where it is already classified **all-runtime (GL + DX + FNA)**.

**The same effect, authored twice by hand, in both languages — this phase's input and its desired
output, sitting in the same project.** Side by side:

| | `.fx` (vendored here, all-runtime) | `.sksl` (Gum, hand-written) |
|---|---|---|
| Entry | `float4 MainPS(VertexShaderOutput input) : COLOR0` | `half4 main(float2 coord)` |
| Sampling | `tex2D(SpriteTextureSampler, input.TextureCoordinates)` | `inputImage.eval(coord)` |
| Types | `float4` / `float3` | `half4` / `half3` |
| Luminance | `dot(color.rgb, float3(0.299, 0.587, 0.114))` | `dot(texel.rgb, half3(0.299, 0.587, 0.114))` — **identical weights** |
| Vertex colour | `… * input.Color` | **absent** |

**Three things follow, and the third is the important one.**

1. **OQ1's "which shader" is answered** — for this shader, with the `.fx` already in the corpus.
   `Grayscale.sksl` is a **hand-authored reference target**: not an oracle in the `mgfxc` sense, but
   the closest this phase can get, and it was written by the requester's project rather than by us.
   A1 becomes *"does our emitter produce something equivalent to this?"* instead of
   *"is this convertible at all?"*, which is a much cheaper and much sharper question.
2. **The convertible set §2.4 predicted is exactly what Gum actually does here.** The `.sksl`'s own
   comment describes a post-process over a baked render-target container — coordinate-driven,
   uniform-fed, no interpolants. §2.4 called that band *"post-process/tint/gradient/SDF"*, and this
   lands squarely in it.
3. **§2.5's varying gap is not hypothetical — it already bit, and it bit silently.** The `.fx`
   multiplies by `input.Color`, the interpolated `COLOR0` varying. **The hand-written SkSL simply
   drops it**, because there is nowhere for it to go. Both files are "the grayscale shader" and they
   are **not** the same function: one tints by vertex colour, the other cannot.

   That is a fine call for a human making a deliberate port. **It is exactly what an automated
   emitter must never do.** `input.TextureCoordinates` maps to `coord` and survives; `input.Color`
   has no destination and vanishes. An emitter that silently followed the same path would be
   shipping a shader that compiles, renders, and is quietly wrong — the precise failure `CLAUDE.md`'s
   *"fail loudly"* rule and Phase 58's D3 no-go exist to prevent. **B2's reject-loudly requirement is
   therefore load-bearing, not defensive**, and this shader is its first test case: the emitter must
   either refuse it or require the consumer to supply the dropped varying as a uniform, and must
   never just emit the lossy version.

---

## 3. The gate — inherited from Phase 59, for the same reason

**Skia has no reference compiler that ShadowDusk can be equivalent to.** There is no `mgfxc` for
SkSL, no golden to diff, and no "the same shader built the official way" to render against. The
only available bar is **source-fidelity**: does the SkSL render the same picture as the original
HLSL does through a proven backend?

That is exactly the question [Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3 decides —
whether THE PURPOSE widens from *"drop-in `mgfxc` replacement"* to two peer evidence models
(reference-compiler equivalence where one exists; source fidelity where it does not, the latter
already measured at 46/46 mean 0.00/255 for the ShaderToy route).

> **HARD GATE: if Phase 57 §3 resolves "no", this phase is CLOSED, not deferred** — the same
> disposition §3's "if no" branch gives Phase 59. There would be no evidence model under which a
> SkSL target could ever be called proven, and this project's repeated finding is that an
> unvalidated cell is worse than an absent one.

---

## 4. Areas (all gated on §3)

### Area A — measure the convertible set before building an emitter

- **A1 (the gate within the gate) — §2.6 has largely pre-supplied the "should work" half.** The
  original plan was to hand-translate **two** shaders to SkSL and run them in real SkiaSharp: one
  that should work and one that should not. **The first now exists upstream**: Gum's
  `Grayscale.sksl` against our vendored `MonoGameInCode-Grayscale.fx`, a hand-authored reference
  pair. So A1 becomes:
  - **A1a.** Render both in their own runtimes and pixel-compare — **expecting a divergence**, since
    the SkSL drops `* input.Color` (§2.6). Quantify it rather than assume it; that number is the
    honest cost of the conversion for this shader, and it is the first real datum this phase has.
  - **A1b.** Still hand-translate a shader that **should not** convert (something genuinely
    varying-dependent — Gum's `KniInCode-Shader.fx` and `apos-shapes.fx` are both vendored and both
    richer). A recorded "the interesting shaders do not convert" closes the area successfully.
- **A2.** From A1, a written statement of which HLSL shapes convert, which do not, and what the
  consumer must supply as uniforms in place of varyings. **§2.6 gives this its first entry
  already:** `TEXCOORD` → `coord` survives; `COLOR` interpolants have no destination.

### Area B — the emitter (only if A1 convinces)

- **B1.** An `SkSlEmitter` at the §2.3 seam, plus the convention mapper for §2.4's entry point,
  types, sampler/`.eval` form, and uniform naming.
- **B2.** Reject loudly, with a registered diagnostic, anything outside A2's set. **Never emit SkSL
  that compiles and renders wrong** — the "fail loudly" rule, and the same reasoning that produced
  Phase 58's D3 no-go.
- **B3.** Keep it additive and dependency-free in the shipped libraries: SkiaSharp must be a *test*
  dependency, never a product one (the `NoMonoGameInProductLibrariesTests` precedent).

### Area C — evidence

- **C1.** Render the emitted SkSL in real SkiaSharp and pixel-compare against the same shader's
  output through a **proven** ShadowDusk backend (OpenGL), which is the closest thing to a reference
  this route can have. Tolerance stated and justified, not assumed to be 0.
- **C2.** A `docs/validation-matrix.md` **§8-style** row — the section for distinct evidence axes —
  never a §1 cell, so no reader mistakes it for an `mgfxc`-equivalence claim.

## 5. Acceptance

- [x] **The premise corrections accepted by the requester** (§2.5, 2026-08-12): SkSL text not
      bytecode, fragment-only not a defect.
- [x] **OQ1 answered** (§2.6, 2026-08-13): the consumer is vchelaru/Gum via its shipping
      `Runtimes/SkiaGum` path, and the shader is `Grayscale.fx` ↔ `Grayscale.sksl`, a hand-authored
      pair with the `.fx` already vendored here.
- [ ] **The §2.6 lossiness finding honoured in the design**: Gum's own hand port silently drops a
      `COLOR` varying. The emitter must **refuse or demand a substitute**, never quietly reproduce
      that loss (B2).
- [ ] Phase 57 §3 resolved. If "no": this phase is closed with that recorded, and no code is written.
- [ ] If "yes": A1's hand-translation probe run in real SkiaSharp and written up, with A2's
      convertible/not-convertible statement. A recorded "no" closes the phase.
- [ ] **The fragment-only limit surfaced in consumer-facing docs**, not only here — the requester's
      own request (§2.5). If this phase ships, that is part of the same PR under `CLAUDE.md`'s
      support-surface rule, and it must state the **no-varyings** consequence (§2.5), not merely
      "no vertex shaders", or it will mislead exactly the way the reply shows is easy to.
- [ ] Any emitter is additive: full-corpus byte-identity across existing targets is an acceptance
      criterion, and SkiaSharp stays out of the shipped libraries' dependencies.
- [ ] The evidence model is stated as **source-fidelity, not `mgfxc`-equivalence**, everywhere it
      appears.

## 6. Non-goals

- **Bytecode.** §2.1: there is no such entry point in SkiaSharp. If Skia ever exposes a stable
  compiled-effect artifact, that is a new question. **Requester-accepted** (§2.5), so this is no
  longer a correction anyone has to be talked into.
- A vertex-shader story. Runtime effects have no vertex stage; this is fragment-only by the
  platform's design, not by our choice. **Requester-accepted** (§2.5) — but read §2.5's last part
  before treating that as permission to skip A1: no vertex *stage* is a smaller claim than no
  varyings, and it is the second one that decides what converts.
- Compute/geometry (see [Phase 58](DONE/PHASE-58-extended-shader-stages.md), and Skia has no such
  surface either).
- Ingesting SkSL as an *input* language.
- Bundling SkiaSharp in any shipped `ShadowDusk.*` package.

## 7. Open questions

- **OQ1 (ask before anything else) — ANSWERED 2026-08-13, both halves.** *Who*: vchelaru, wanting
  pixel shaders only (§2.5) — a real named consumer, not §1's guess, and more demand signal than
  Phase 58's issue ever had. *Which shader*: **`Grayscale.fx` ↔ `Grayscale.sksl`** (§2.6), with the
  `.fx` already vendored here and the SkSL already hand-written upstream. Nothing further needs
  asking before A1 can run. **Still worth confirming with him** whether Grayscale is representative
  of what he wants converted or merely the one that was easy enough to port by hand — those imply
  very different scopes, and the answer feeds OQ2.
- **OQ2 — the load-bearing question. §2.6 gives it a first data point, and it is not reassuring.**
  How much of Gum's real shader set is coordinate-driven and uniform-fed (convertible per §2.4)
  versus **varying**-dependent (not)? §2.5 explains why "I only care about the pixel shader" does not
  answer it: the constraint is the absent interface, not the absent stage. **The one shader Gum has
  actually ported by hand needed a varying dropped to get there** (§2.6). If that is typical, the
  emitter's honest output is a diagnostic more often than a shader, and **that finding closes the
  phase successfully** rather than failing it. Survey the vendored Gum + Apos.Shapes set for
  interpolant dependence before committing to B1 — the fixtures are already in the repo, so this is
  cheap.
- **OQ3.** Does this overlap [Phase 59](PHASE-59-raylib-cs-backend.md) enough to share machinery?
  Both branch at the same §2.3 seam and both are "modern GLSL plus a convention mapper". If both
  clear Phase 57 §3, they may be one phase with two emitters rather than two phases — worth deciding
  before duplicating the seam.
- **OQ4.** SkSL's dialect moves with Skia releases and SkiaSharp's own versioning is fast-moving
  (4.151.x / 4.152.0-preview as of 2026-08-11). What is the pin story, and does SkSL have a
  stability guarantee at all? An unstable target language would make every proof perishable.
