# Phase 62 — SkiaSharp: HLSL → SkSL (and why "bytecode" is the wrong target)

**Track:** Backend breadth (purpose-gated). Additive; **no existing output byte changes**.

**Status:** 📋 **Planned / not started** (created 2026-08-11).

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
[#199](https://github.com/kaltinril/ShadowDusk/issues/199) → [Phase 60](PHASE-60-xnb-content-output.md)
and [#198](https://github.com/kaltinril/ShadowDusk/issues/198) → [Phase 61](PHASE-61-slang-support.md).

Context worth confirming rather than assuming: Victor maintains **Gum**, whose shaders are already
vendored here as a regression corpus ([Phase 49](DONE/PHASE-49-apos-shapes-regression-corpus.md)),
and Gum ships a **SkiaSharp** rendering path alongside its MonoGame/FNA ones. So the plausible real
request is *"let a Gum author write one shader and have it work on the SkiaSharp renderer too."*
**That is a guess, and §5 OQ1 says to confirm it before building anything** — the exact shape of the
consumer decides whether this is worth doing at all.

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

- **A1 (the gate within the gate).** Hand-translate **two** shaders to SkSL and run them in real
  SkiaSharp: one that should work (a coordinate-driven tint/gradient — ideally a real Gum one) and
  one that should not (something needing varyings or a vertex stage). The Phase-46 / Phase-58-D1
  precedent: **a human does it first**, and a recorded "the interesting shaders do not convert"
  closes the area successfully.
- **A2.** From A1, a written statement of which HLSL shapes convert, which do not, and what the
  consumer must supply as uniforms in place of varyings.

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

- [ ] **OQ1 answered first** (§5 below): who is the consumer and what shader do they want to run?
- [ ] Phase 57 §3 resolved. If "no": this phase is closed with that recorded, and no code is written.
- [ ] If "yes": A1's hand-translation probe run in real SkiaSharp and written up, with A2's
      convertible/not-convertible statement. A recorded "no" closes the phase.
- [ ] Any emitter is additive: full-corpus byte-identity across existing targets is an acceptance
      criterion, and SkiaSharp stays out of the shipped libraries' dependencies.
- [ ] The evidence model is stated as **source-fidelity, not `mgfxc`-equivalence**, everywhere it
      appears.

## 6. Non-goals

- **Bytecode.** §2.1: there is no such entry point in SkiaSharp. If Skia ever exposes a stable
  compiled-effect artifact, that is a new question.
- A vertex-shader story. Runtime effects have no vertex stage; this is fragment-only by the
  platform's design, not by our choice.
- Compute/geometry (see [Phase 58](DONE/PHASE-58-extended-shader-stages.md), and Skia has no such
  surface either).
- Ingesting SkSL as an *input* language.
- Bundling SkiaSharp in any shipped `ShadowDusk.*` package.

## 7. Open questions

- **OQ1 (ask before anything else).** Is the driver Gum's SkiaSharp renderer, and if so which
  shader? §1's reading is a guess. Phase 58's lesson applies directly: one exploratory issue is not
  demand, and building an unvalidated target is worse than not having it.
- **OQ2.** How much of Gum's real shader set is coordinate-driven and uniform-fed (convertible per
  §2.4) versus varying-dependent (not)? If it is mostly the latter, A1 will fail and that is the
  answer.
- **OQ3.** Does this overlap [Phase 59](PHASE-59-raylib-cs-backend.md) enough to share machinery?
  Both branch at the same §2.3 seam and both are "modern GLSL plus a convention mapper". If both
  clear Phase 57 §3, they may be one phase with two emitters rather than two phases — worth deciding
  before duplicating the seam.
- **OQ4.** SkSL's dialect moves with Skia releases and SkiaSharp's own versioning is fast-moving
  (4.151.x / 4.152.0-preview as of 2026-08-11). What is the pin story, and does SkSL have a
  stability guarantee at all? An unstable target language would make every proof perishable.
