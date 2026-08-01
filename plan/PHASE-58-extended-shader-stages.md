# Phase 58 — Extended shader stages (geometry, hull, domain, compute): can we support them at all, and failing that, say so honestly

**Track:** Backend breadth (research-gated) / consumer-UX. Additive; no output bytes change.

**Status:** 📋 **Planned / not started** (created 2026-07-31).

**Depends on:** [Phase 48](DONE/PHASE-48-compile-target-profile-validation.md) (`KnownProfiles`, the
profile-recognition surface this phase extends), [Phase 45](DONE/PHASE-45-fx-preparser-robustness.md)
(the `FxPreParser` pass-assignment parsing that currently misdiagnoses these shaders).

**Blocks:** nothing. No open phase waits on this.

**Gated on:** the §5 decision, and only for Area B. **Areas C and D are not gated by it** — Area D
(conversion) is gated on its own D1 hand-probe, and **Area C ships regardless of how anything else
goes** and is the phase's guaranteed deliverable.

> **The question in one sentence:** MonoGame's `Effect` can hold a vertex shader and a pixel shader
> and nothing else, so the honest questions are (1) is there *any* supported runtime where a
> geometry / hull / domain / compute shader could actually run, (2) failing that, can some of these
> workloads be **converted** into vertex/pixel form that stock MonoGame *can* run, the way the
> ShaderToy frontend already converts GLSL into `.fx`, and (3) failing both, does ShadowDusk at
> least tell the truth when it is handed one?

---

## 1. Where this came from

A user asked whether [`cpt-max/MonoGame-Shader-Samples`](https://github.com/cpt-max/MonoGame-Shader-Samples)
could be compiled with ShadowDusk. Three of its branches were pulled and run through the real CLI
(2026-07-31). Their `compile` profiles:

| Sample branch | Profiles used |
|---|---|
| `tesselation_geometry` | `ds_5_0`, `gs_4_0`, `hs_5_0`, `ps_4_0`, `vs_4_0` |
| `compute_gpu_particles` | `cs_5_0`, `ps_4_0`, `vs_5_0` |
| `edgerounding` | `ds_5_0`, `hs_5_0`, `ps_4_0`, `vs_4_0` |

All three fail, loudly (exit 1, no output written), which is the correct *direction*. But the message
is wrong, and that is defect **C1** below.

---

## 2. What is already established — do not re-derive this

Measured directly from MonoGame's own source at tag `v3.8.5` and from KNI's `HEAD`, 2026-07-31.
This section exists so the spike in §4 starts from evidence rather than from scratch.

### 2.1 Stock MonoGame cannot run these, and it is not a loader gap

Three independent blockers, any one of which is sufficient:

1. **The MGFX container encodes the stage as ONE BIT.**
   `MonoGame.Framework/Graphics/Shader/Shader.cs`:
   ```csharp
   var isVertexShader = reader.ReadBoolean();
   Stage = isVertexShader ? ShaderStage.Vertex : ShaderStage.Pixel;
   ```
   There is no representation for a third stage. A perfect `hs_5_0` blob could be written into a
   `.mgfx`, and the reader has no way to say what it is.
2. **`ShaderStage` has exactly two members** — `Vertex`, `Pixel`. That is the entire public enum.
3. **No runtime type or API exists.** `Shader.DirectX.cs` holds exactly two fields
   (`_vertexShader`, `_pixelShader`) and two creation methods (`CreateVertexShader`,
   `CreatePixelShader`). `ConstantBuffer.DirectX.cs` binds with a bare
   `if (stage == ShaderStage.Vertex) … else …`, not a switch with a default. A repo-wide code
   search for `ComputeShader`, `GeometryShader`, `HullShader`, and `DomainShader` returns **zero**
   hits.

### 2.2 The GPU's capability is not the constraint (the point that keeps being missed)

The GPU runs these stages fine; the hardware has for over a decade. But **the GPU never sees a
`.mgfx`** — that is MonoGame's own container, meaningless to a driver. The chain is:

```
.mgfx  ->  MonoGame parses it  ->  MonoGame calls D3D11/GL/VK  ->  driver  ->  GPU
                                   ^ the only place the API call can originate
```

MonoGame is the sole thing that can turn a bytecode blob into a GPU shader object, and it only ever
calls the vertex and pixel creation entry points. Beyond creation you would additionally need
per-stage binding (`HSSetShader` and friends), pipeline state only some stages use (patch topology
for tessellation), and — for compute — an entirely different **submission path**: `Dispatch(x,y,z)`
rather than a draw call, plus UAV resources MonoGame has no concept of.

There is also an **abstraction mismatch, not just a missing feature**: `Effect` models *techniques
and passes*, which is a drawing concept. A compute shader is not a draw. Even a hypothetical
container extension would not fit the `Effect` shape without inventing a non-`Effect` surface.

**Consequence for scoping:** emitting the bytecode is the small half. Anyone proposing to "just
support compute" is proposing a runtime change, which is why the fork in §2.4 forked the runtime.

### 2.3 KNI is the same answer, with one byte of headroom

`src/Xna.Framework.Graphics/Graphics/Shader/ShaderStage.cs`:

```csharp
public enum ShaderStage : byte { Pixel = 0, Vertex = 1 }
```

A **byte** rather than MonoGame's bool, so KNI's wire format has room to describe more stages one
day. But only two are defined and no runtime support exists, so today the answer is identical.
Recorded because it is the one place in the supported matrix where the *container* is not the
binding constraint.

### 2.4 The fork that does support them, and why it matters here

[`cpt-max/MonoGame`](https://github.com/cpt-max/MonoGame) adds tessellation, geometry, and compute
shaders. It is **not** a source-only experiment, which is what makes it worth a decision rather than
a dismissal:

- It publishes real NuGet packages: `MonoGame.Framework.Compute.WindowsDX`,
  `.Compute.DesktopGL`, `.Compute.Android`, `MonoGame.Content.Builder.Task.Compute`.
- It ships **its own reference compiler**: `dotnet-mgcb-compute` (and
  `dotnet-mgcb-editor-compute`).
- Its own compiler is built on **ShaderConductor**, i.e. DXC + SPIRV-Cross — architecturally the
  *same family as ShadowDusk's own pipeline*, not a MojoShader descendant.
- Platform support: Windows, Linux, Android. **No compute on macOS/iOS** (its DesktopGL Mac path is
  OpenGL 2.1).

So the two things this project normally requires before it will call anything proven — **a consumer
runtime to load into, and a reference compiler to be equivalent to** — both exist for this fork.
That is precisely what stock MonoGame lacks, and it is why §5 is a real decision rather than a
formality.

---

## 3. The defect that exists today (C1)

Independent of everything above, the current diagnostic is **loud but wrong**. Verbatim, from the
real CLI:

```
tesselation_geometry.fx(167,24-24): error FX0008: Expected ';' after render-state 'HullShader = compile'
```

`FX0008` is "a required semicolon is missing after a statement". The file has no missing semicolon.
The cause is in [`FxPreParser.cs:1099-1100`](../src/ShadowDusk.HLSL/FxPreParser.cs#L1099-L1100),
which recognizes exactly two pass assignments:

```csharp
bool isVs = string.Equals(key, "VertexShader", StringComparison.OrdinalIgnoreCase);
bool isPs = string.Equals(key, "PixelShader",  StringComparison.OrdinalIgnoreCase);
```

Anything else falls through to the render-state path, which then chokes on the `compile` expression
and blames punctuation. A user is sent hunting for a syntax error in a file that has none, and is
told nothing about the actual, permanent reason their shader will not work.

By the project's own severity vocabulary this is **minor** (a loud-but-wrong diagnostic, no wrong
output ever ships) — but it is exactly the "fail loudly" rule being satisfied in letter and missed
in spirit: `CLAUDE.md` requires that *"an input shape we don't model gets a registered diagnostic,"*
and `FX0008` is a registered diagnostic for a different condition.

---

## 4. Area A — the spike: is there anything at all we can do? (research, timeboxed)

Answer these against real sources; do not reason from the summaries above where a source can be
read directly.

- **A1.** Confirm §2.1 still holds at MonoGame's current release and on the `develop` branch. The
  3.8.5 native backends (`DesktopVK`, `WindowsDX12`) are new; verify they inherit the same shared
  managed `ShaderStage` and MGFX reader rather than introducing their own path. *(Expected: they
  do inherit it, since `ShaderStage` is shared managed code, but this is the one place a door could
  have opened without anyone noticing.)*
- **A2.** Same for KNI at its current release, given §2.3's byte-width headroom.
- **A3.** Establish whether MonoGame upstream has any accepted design, issue, or PR for additional
  stages. If there is a live upstream effort, the correct ShadowDusk move is to be *ready* for its
  format rather than to invent one.
- **A4.** For the fork: determine **how `dotnet-mgcb-compute` encodes the stage**, since stock's
  one bit cannot. Its docs claim the effect format is not modified, which cannot be literally true
  for extra stages, so read the fork's actual writer and reader. This is the single most
  informative measurement in the phase: it tells us whether a fork target is a small additive
  writer change or a second container.
- **A5.** Record whether the fork is maintained and versioned in a way this project could pin
  against (`project_facts.md` pin discipline), and what its licence permits.

**Deliverable:** a written answer in this doc, with sources, for each of A1-A5, and a recommendation
into §5. If A1-A3 all confirm "no door in any supported runtime", say so plainly — a well-evidenced
"no" is a successful outcome for this area, and it retires a question that will otherwise be asked
again.

---

## 5. Decision gate (Area B is gated on this; Area C is not)

**The question for the owner:** is a *third-party fork* of MonoGame an in-scope consumer runtime for
ShadowDusk?

Arguments recorded so the decision is made on the real trade, not on enthusiasm:

- **For.** The fork has both things the evidence bar requires (a runtime and a reference compiler),
  its compiler is the same DXC + SPIRV-Cross family as ours, and supporting it would be strictly
  additive: a new `PlatformTarget`, no change to any existing output byte. It is the same shape as
  every backend this project has already added.
- **Against.** `CLAUDE.md`'s THE PURPOSE names MonoGame/KNI. A fork multiplies the validation
  matrix (a new runtime, a new reference compiler, a new pin, a new render gate) for a consumer
  base far smaller than stock MonoGame's, and every future release of both stock MonoGame and the
  fork widens the surface to keep green. The project has repeatedly found that an unvalidated cell
  is worse than an absent one.
- **The middle option.** Decline the target, but keep `KnownProfiles` and the pre-parser *aware* of
  the stages so diagnostics stay precise (which is Area C anyway) and so a future decision is cheap.

**Record the outcome in [`project_decisions.md`](../project_decisions.md) before starting Area B.**
Do not start Area B on the strength of the spike alone.

---

## 6. Area B — a fork target (ONLY if §5 says yes)

Not designed here on purpose; A4's finding determines the shape. At minimum it would need: a new
`PlatformTarget` value, stage plumbing through `ShaderStage` / the IR / the writer (today
`ShaderStage` is `{ Vertex = 0, Pixel = 1 }`, deliberately mirroring MonoGame's bit), `KnownProfiles`
entries for `gs_*` / `hs_*` / `ds_*` / `cs_*`, the fork's container encoding, a pinned fork runtime,
and a rung-4 render gate against `dotnet-mgcb-compute` output. Treat it as a phase of its own scale,
i.e. spin it out rather than growing this one.

---

## 6.5. Area D — conversion: re-express the workload in vertex/pixel terms

**This is the avenue §2 does not close, and it is the one with real precedent in this repo.** §2
proves stock MonoGame cannot *run* a compute or geometry shader. It says nothing about whether the
*work* those shaders do can be re-expressed as something MonoGame can run. ShadowDusk already owns
exactly this shape of thing: [`ShadowDusk.ShaderToy`](../src/ShadowDusk.ShaderToy/)
([Phase 46](DONE/PHASE-46-shadertoy-to-fx-conversion-tool.md) /
[47](DONE/PHASE-47-shadertoy-frontend-promotion.md)) takes a shader in a form the pipeline cannot
consume and emits ordinary `.fx` — pure-managed, zero native dependency, additive, changing no
existing output byte. A stage-lowering converter would sit in the same architectural slot.

### 6.5.1 What is plausibly convertible, ranked

- **Compute → pixel shader writing to a render target. Genuinely tractable for a subset.** This is
  simply how GPGPU was done before compute shaders existed: a full-screen quad, the "kernel" as the
  fragment shader, the output buffer as a `RenderTarget2D`. A compute shader whose job is
  *read texture, write texture* maps almost directly. Of the `cpt-max` samples, the pixel-sort and
  3D-texture ones are this shape.
- **Geometry shader → instancing or pre-expansion. Sometimes.** The common GS uses (billboard/quad
  expansion from points, wireframe generation) have well-known instanced-draw or
  expanded-vertex-buffer equivalents. The general case (arbitrary primitive amplification with
  data-dependent output counts) does not.
- **Tessellation (hull/domain) → not meaningfully convertible.** The entire point is data-dependent
  subdivision *on the GPU, inside the draw*. The only equivalents are CPU-side subdivision or
  shipping pre-subdivided meshes, neither of which is a shader transformation. Expect this to come
  out "no."

### 6.5.2 The hard limit, which must be stated before anyone starts

**A converter cannot rewrite the consumer's C#.** This is the decisive difference from ShaderToy and
the reason this area is not simply "do what Phase 46 did":

- A ShaderToy input is *self-contained*: `mainImage` in, a full-screen effect out. The host code is
  "draw a quad", which the sample already does.
- An extended-stage shader is *half of an application*. The compute samples bind structured
  buffers and UAVs, then call `Dispatch(x,y,z)`. MonoGame has **no** structured-buffer or UAV
  concept and no dispatch path. Converting the shader body is the easy part; the consumer must also
  restructure their data as textures/render targets and replace `Dispatch` with a draw. That is
  application surgery, not a compile step.
- Therefore any output here is **at best a shader plus a documented host-code recipe**, and it must
  be honest about that. Silently emitting a `.fx` that only works if the consumer also rewrites
  their rendering code would violate the seamlessness directive far worse than refusing.

### 6.5.3 Evidence bar (borrowed from Phase 47, deliberately)

There is **no `mgfxc` oracle** for a converted compute shader — stock `mgfxc` refuses the input
outright, so there is nothing to be equivalent *to*. The bar is therefore the same honest, weaker
one Phase 47 established for the ShaderToy route: **pixel-fidelity against the original shader's
own output** (here, the `cpt-max` fork running the unconverted shader), *not* mgfxc-equivalence.
Say so explicitly wherever this is documented, exactly as `docs/validation-matrix.md` §8 does for
ShaderToy, so a reader never mistakes it for a rung-4 drop-in claim.

### 6.5.4 Deliverables for this area

- **D1.** A hand-conversion probe on ONE sample before writing any converter — the same Phase-0
  gate Phase 46 used before committing to a parser. Take the pixel-sort or 3D-texture compute
  sample, hand-convert it to a render-target pixel shader, run it in real stock MonoGame, and
  compare against the fork's output. If a human cannot do it convincingly, no converter should be
  written.
- **D2.** From D1, a written statement of exactly which compute shapes are convertible and which
  are not, with the host-side recipe each requires.
- **D3.** A go/no-go recommendation. "No, and here is why" is a fully acceptable outcome; record it
  so this is not re-opened speculatively.

**Do not build a general compute-to-pixel transpiler on the strength of the idea alone.** D1 is the
gate.

---

## 7. Area C — the guaranteed deliverable: tell the truth (ships regardless of §5)

Fix C1. This is small, well-scoped, and valuable even if Areas A and B both end in "no".

- Teach [`FxPreParser`](../src/ShadowDusk.HLSL/FxPreParser.cs#L1099-L1100) to recognize
  `HullShader`, `DomainShader`, `GeometryShader`, and `ComputeShader` as **pass shader assignments**
  rather than letting them fall through to render-state parsing.
- Emit a **new registered diagnostic** naming the unsupported stage and the real reason. `FX0014`
  is free in the pre-parser range (`FX0001`-`FX0099`; `FX0001`-`FX0013` and `FX0099` are taken). If
  the check ends up better placed at the pipeline level rather than the parser, `SD0202` is free in
  the platform/backend-availability range.
- Add the profile tokens to `KnownProfiles` **only if** doing so improves the message without
  implying support. Note the interaction with `SD0013` (unrecognized profile): today `cs_5_0` inside
  a `compile` expression would be *unrecognized*, which is technically true but less useful than
  "MonoGame has no compute stage." Whichever code fires, the message must name the stage and say the
  runtime cannot run it.
- **The message must state the permanent reason**, not just "unsupported", because a user who reads
  "unsupported" will reasonably ask us to add it. Say that MonoGame's `Effect` has only vertex and
  pixel stages, so no compiler can make such a shader run there. Give the user somewhere to go:
  point at the fork if §5 declined it, and at the conversion recipe if Area D produced one for that
  stage.
- Regression fixtures for all four stage keywords, compiled on GL/DX/FNA, asserting the new code and
  that **no output file is written**. Assertions use Shouldly, `Case.Sensitive` on string receivers.

**No output bytes may move.** This is a reject-set *message* change, not a reject-set change: these
inputs already fail today. Verify with the corpus sweep that no fixture changes verdict, only text.

---

## 8. Acceptance

- [ ] A1-A5 answered in this doc with sources; §5 recommendation written.
- [ ] §5 decision recorded in `project_decisions.md` (either way).
- [ ] D1 hand-conversion probe run on one compute sample, with its result written up; D2's
      convertible/not-convertible statement recorded; D3 go/no-go recommendation made. A recorded
      "no" closes the area.
- [ ] Area C shipped: new registered diagnostic, `docs/error-codes.md` row, four regression
      fixtures, full `dotnet test` green.
- [ ] The three `cpt-max` sample shaders produce the new message, captured verbatim in this doc.
- [ ] Corpus sweep shows zero verdict changes and zero output-byte changes.
- [ ] `docs/validation-matrix.md` carries a §7 row stating plainly that geometry / hull / domain /
      compute are **not supportable on stock MonoGame or KNI**, with the §2.1 evidence, so this is
      not re-investigated a third time — and, if Area D lands anything, a §8-style row recording
      that the converted route's bar is source-fidelity, **not** mgfxc-equivalence.
- [ ] If §5 says yes: Area B spun out as its own phase, not grown here.

## 9. Non-goals

- Implementing extended stages for **stock** MonoGame or KNI. §2.1 establishes this is impossible
  without a runtime change we do not own.
- Proposing or authoring an upstream MonoGame runtime change.
- A compute-shader API surface on `ShadowDusk` itself. The library compiles shaders; it does not
  dispatch them.
- Rewriting the consumer's host code. Area D can emit a shader and document a recipe; it cannot
  turn `Dispatch` into a draw or restructure someone's buffers for them (§6.5.2).
- A general compute-to-pixel transpiler built without the D1 probe passing first.
- Metal/MSL compute (see [Phase 31](PHASE-31-metal-msl-backend.md); parked for its own reasons).

## 10. Open questions

- **OQ1.** If §5 declines the fork, should `KnownProfiles` still gain the stage tokens purely for
  message quality? (Area C leaves this deliberately open, since it trades one diagnostic's precision
  against implying a support level we do not offer.)
- **OQ2.** Does the ShaderToy frontend ever emit anything that would trip the new diagnostic? Almost
  certainly not (it synthesizes a VS + PS pair), but confirm rather than assume.
- **OQ3.** A4 may find the fork's container is a superset of stock MGFX that stock MonoGame would
  *reject* rather than ignore. If so, record it: it would mean a fork target can never be a silent
  auto-upgrade and must be an explicit `PlatformTarget`, which is relevant to
  [Phase 57](PHASE-57-universal-compiler-auto-detection.md)'s auto-detection work.
