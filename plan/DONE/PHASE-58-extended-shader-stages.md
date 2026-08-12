# Phase 58 — Extended shader stages (geometry, hull, domain, compute): can we support them at all, and failing that, say so honestly

**Track:** Backend breadth (research-gated) / consumer-UX. Additive; no output bytes change.

**Status:** ✅ **DONE (2026-08-11).** All four areas are resolved: A and C shipped, B declined by
owner decision, D probed and closed with a recorded no-go. Created 2026-07-31.

- **Area A ✅** — A1–A5 answered from source, with the fork measurement (A4) the decisive one. See
  §4.1. Recommendation into §5: **decline the fork target** (the middle option).
- **Area C ✅** — `FX0014` shipped. The four stage keywords are recognized as pass shader
  assignments and rejected at the stage keyword with the permanent reason, instead of falling
  through to render-state parsing and blaming a missing semicolon. Verified on the three real
  `cpt-max` samples (§7.1), 27 new integration cells + 12 unit cases, **reject set unchanged**.
- **Area B ❌ DECLINED (owner decision, 2026-08-11)** — the fork is **not** an in-scope consumer
  runtime. Closed, not deferred. Recorded with its reasoning in
  [`project_decisions.md`](../../project_decisions.md); see §5.1.
- **Area D ✅ CLOSED with a recorded NO-GO** — D1 **passed** (the hand conversion reproduces the
  original kernel at **maxd 0** in real MonoGame, mutation-checked three ways), and that success is
  exactly what makes the transpiler unwise: the convertible set is a judgement about the algorithm
  rather than a detectable syntactic property, and every converted kernel ships as a shader *plus*
  host C# no converter can write. D2's rule and recipe table, and D3's reasoning, are in §6.6. The
  probe was run against a **CPU reference** rather than the fork's output (owner decision,
  2026-08-11: no third-party toolchain on the dev box, which Area B's decline makes moot anyway —
  there is no fork runtime left to compare against).

**Depends on:** [Phase 48](PHASE-48-compile-target-profile-validation.md) (`KnownProfiles`, the
profile-recognition surface this phase extends), [Phase 45](PHASE-45-fx-preparser-robustness.md)
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
The cause is in [`FxPreParser.cs:1099-1100`](../../src/ShadowDusk.HLSL/FxPreParser.cs#L1099-L1100),
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

### 4.1 Answers (2026-08-05) — the door is shut, and the fork is a second container

Read directly from the repositories named, not from the summaries in §2.

**A1 — MonoGame, at `v3.8.5` AND on `develop`: confirmed, no door.**
`MonoGame.Framework/Graphics/Shader/ShaderStage.cs` declares, on **both** refs, exactly:

```csharp
public enum ShaderStage { Vertex, Pixel }
```

and `Shader.cs`'s binary constructor on both refs decodes it as a single bool:

```csharp
var isVertexShader = reader.ReadBoolean();
Stage = isVertexShader ? ShaderStage.Vertex : ShaderStage.Pixel;
```

That constructor is `internal Shader(GraphicsDevice device, int version, BinaryReader reader)` —
**shared managed code**, so §4's one worry (that the new 3.8.5 `DesktopVK` / `WindowsDX12` backends
might have introduced their own reader and quietly opened a door) is answered: they inherit this
one. A repository-wide code search for the four stage type names returns `ComputeShader` **0**,
`HullShader` **0**, `DomainShader` **0**, and `GeometryShader` **1** — and that single hit is
`native/monogame/directx12/DeviceResources.cpp`, a native D3D12 pipeline-state field with no managed
API behind it. The stage is not merely unimplemented; the container cannot **name** a third stage.

> **Incidental finding, checked because it was adjacent and would have been a real bug:** the same
> `v3.8.5` reader has `if (version > 10) { SourceFile = reader.ReadString(); Entrypoint = reader.ReadString(); }`,
> matched by an *unconditional* pair of writes in `Tools/MonoGame.Effect.Compiler/Effect/ShaderData.writer.cs`.
> ShadowDusk already models this correctly — the strings are v11-only and documented as such on
> `CapabilityProfile` — so the default v10 output is unaffected and nothing needs to change. Noted
> only so the next reader of that writer does not re-raise it.

**A2 — KNI, at `v4.29001-hotfix2`: confirmed, no door, and it is *harder* than MonoGame's.**
`src/Xna.Framework.Graphics/Graphics/Shader/ShaderStage.cs`:

```csharp
public enum ShaderStage : byte { Pixel = 0, Vertex = 1 }
```

§2.3 called the `byte` width "one byte of headroom", and in a narrow sense it is — but the headroom
is unreachable. `MGFXReader10.ReadShader()` reads `(ShaderStage)ReadByte()` and then switches:

```csharp
switch (shaderStage)
{
    case ShaderStage.Vertex: return new VertexShader(...);
    case ShaderStage.Pixel:  return new PixelShader(...);
    default: throw new InvalidOperationException("stage");
}
```

so a third value **throws at load**. And the type system agrees with the switch: `Shader` is
`abstract` with exactly two concrete subclasses (`VertexShader`, `PixelShader`), constructed through
`CreateVertexShaderStrategy` / `CreatePixelShaderStrategy`. Even a hypothetical third byte value has
no class to become.

**A3 — no accepted upstream design to be "ready for".**
Two issues exist and both are open, so the question has been *asked* upstream; neither is a live
effort with a format to target:

| Issue | Opened | Author | State |
|---|---|---|---|
| [#4567 Add Geometry Shader Support](https://github.com/MonoGame/MonoGame/issues/4567) | 2016-02-26 | `tomspilman` (MonoGame lead) | **open**, labelled `status: needs-design` — ten years, still undesigned |
| [#7533 Compute Shader](https://github.com/MonoGame/MonoGame/issues/7533) | 2021-07-15 | `cpt-max` (the fork's author) | **open**, labelled `feature`, no design |

The §4 hypothetical — "if there is a live upstream effort, be ready for its format rather than
inventing one" — does not apply. There is no format to be ready for.

**A4 — the decisive measurement: the fork's effect format IS modified, at the first field of every
shader record.** The fork's docs claim the format is unchanged; §4 predicted that could not be
literally true, and it is not. Both sides of the fork's pipeline were read:

| | Stock MonoGame `v3.8.5` | `cpt-max/MonoGame` @ `compute_shader` |
|---|---|---|
| Writer (`Tools/MonoGame.Effect.Compiler/Effect/ShaderData.writer.cs`) | `writer.Write(IsVertexShader);` then `writer.Write(SourceFile …); writer.Write(Entrypoint …);` | `writer.Write((int)ShaderStage);` — and the two strings are **gone** |
| Reader (`Shader.cs`) | `var isVertexShader = reader.ReadBoolean();` | `Stage = (ShaderStage)reader.ReadInt32();` |
| `ShaderStage` members | `{ Vertex, Pixel }` | `{ Vertex, Pixel, Hull, Domain, Geometry, Compute }` |

A **1-byte bool became a 4-byte int**, and two length-prefixed strings were removed. Every shader
record diverges from its first byte onward, and the fork additionally carries a `ShaderResources[]`
table stock has no concept of. This is **a second container, not a superset** — which answers
**OQ3** directly and with the stronger of its two possible answers: a fork target could never be a
silent auto-upgrade, and stock MonoGame would not ignore fork output, it would misparse it. That is
also relevant to [Phase 57](../PHASE-57-universal-compiler-auto-detection.md): a fork target would have
to be an explicit `PlatformTarget`, never something auto-detection could safely infer.

**A5 — the fork cannot be pinned against under this project's pin discipline.**

- **Staleness.** The fork's last push to any branch is **2024-05-20**, ~2 years and 3 months before
  this measurement. Its NuGet packages top out at **3.8.3** (`dotnet-mgcb-compute` 3.8.3 published
  2024-03-30; `MonoGame.Framework.Compute.DesktopGL` / `.WindowsDX` likewise 3.8.3) against stock
  MonoGame's **3.8.5** stable. It is two minor releases behind the runtime this project already
  render-proves against, with no sign of catching up.
- **Licence.** GitHub resolves the fork's licence to `NOASSERTION` (it inherits MonoGame's mixed
  licensing without a clean SPDX identifier). Not a blocker on its own, but not the clean pin
  `project_facts.md` discipline expects either.

**Recommendation into §5: decline the fork target — take the middle option.** A4 is why. Supporting
it is not "a new `PlatformTarget` and a render gate"; it is **a second output container** with its
own writer, its own reference compiler, its own runtime, and its own pin — against a fork that has
not moved in over two years and trails stock by two releases. The project's own repeated finding
is that an unvalidated cell is worse than an absent one, and this cell would be the hardest in the
matrix to keep green for the smallest consumer base in it. The middle option costs nothing and is
already delivered: Area C makes the diagnostic precise and *names* the fork, so a user who wants it
is pointed at it, and a future reversal stays cheap.

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

**Record the outcome in [`project_decisions.md`](../../project_decisions.md) before starting Area B.**
Do not start Area B on the strength of the spike alone.

### 5.1 Outcome — DECLINED (owner decision, 2026-08-11)

**The fork is not an in-scope consumer runtime. Area B is closed, not deferred.** The middle
option was taken, and it was already delivered by Area C: the pre-parser knows all four stages and
`FX0014` names both the permanent reason and the fork, so a user who wants it is pointed at it and
a future reversal stays cheap.

The decision turned on §4.1's A4 measurement, which came out stronger than this section
anticipated. The trade written above assumed "a new `PlatformTarget`, no change to any existing
output byte… the same shape as every backend this project has already added." That is not the
shape. The fork writes **a second container, not a superset**, so it is a second *writer* as well
as a second runtime, reference compiler, pin, and render-gate family — the largest maintenance
surface in the matrix for its smallest consumer base, against a fork last pushed 2024-05-20 that
trails stock by two releases. The "Against" argument's closing line is the operative one: an
unvalidated cell is worse than an absent one.

Full reasoning, and the revisit condition (the fork resumes active maintenance **and** a real
consumer asks), are in [`project_decisions.md`](../../project_decisions.md).

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
exactly this shape of thing: [`ShadowDusk.ShaderToy`](../../src/ShadowDusk.ShaderToy/)
([Phase 46](PHASE-46-shadertoy-to-fx-conversion-tool.md) /
[47](PHASE-47-shadertoy-frontend-promotion.md)) takes a shader in a form the pipeline cannot
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

## 6.6. Area D results (2026-08-11) — D1 PASSED, D3 says **do not build the transpiler**

### D1 — the hand-conversion probe: PASSED, 5/5

Subject: cpt-max's `compute_write_to_texture` — an odd-even transposition sort that orders each
scanline's pixels by hue — named in §6.5.1 as the most tractable shape. Hand-converted to a
render-target pixel shader (`validation/ComputeConversionProbe/HueSortConverted.fx`), compiled
through the **real, unmodified product pipeline** for OpenGL, and run in **real MonoGame
DesktopGL** (`validation/ComputeConversionProbe`).

**Oracle:** a CPU transcription of cpt-max's original kernel, kept in its **native scatter form**
(writing two pixels per iteration, as the GPU kernel does) rather than restructured into the
gather form the pixel shader uses — deliberately, so the oracle does not share the conversion's
central assumption and can actually falsify it. Per the owner decision of 2026-08-11 the fork
toolchain was **not** installed, so the reference is the kernel's *semantics* rather than the
fork's pixels. That is a weaker oracle than §6.5.3 specifies and is recorded as such; it is still
decisive here, because a conversion that disagrees with the original kernel's own definition has
failed regardless of what any runtime shows.

| Arm | Result |
|---|---|
| Converted effect loads in real MonoGame | PASS |
| The sort is a real transformation (anti-vacuity: input ≠ expected) | PASS — maxd 255 |
| The shader actually rewrote pixels (anti-vacuity: catches pass-through) | PASS — maxd 255 |
| **GPU pixel shader == CPU compute kernel** | **PASS — maxd 0** over 192 px, 0 px beyond tolerance |
| Every row is hue-ordered afterwards (independent property, not an implementation comparison) | PASS — 0/8 rows unordered |

**Mutation-checked, because a green comparison proves nothing until it has been seen to go red**
(the durable lesson from the Phase 51 A2 MRT gate). Three mutations, each turning the probe red on
the arms it should: inverting the pair-parity test → equivalence **and** the independent ordering
arm both fail (84 px wrong, 3/8 rows unordered); a "never swap" no-op → the anti-vacuity arm fires
at maxd 0 exactly as designed, plus both others; a full pass-through → the shader stops compiling
at all. The full multi-pass ping-pong (24 phases, the real workload) is run, not a single phase
that a near-no-op could survive.

**What the conversion actually required, and the finding that generalizes.** The math half — the
`HueFromRGB` function and the comparison — needed **no conversion at all**. The obstacle is
entirely the I/O model: a compute shader **scatters** (this kernel writes *two* pixels per
invocation, `Output[idL]` and `Output[idR]`), while a pixel shader **gathers** (it writes exactly
one location, its own fragment, and cannot choose it). `Output[idL] = …` has no pixel-shader
spelling, so a statement-by-statement port is impossible. It converts anyway because the kernel's
write set is a deterministic function of the output coordinate: each pixel works out which pair it
belongs to, reads *both* members itself, runs the same comparison, and emits only its own half —
paying for it by recomputing the comparison twice per pair. Two smaller adaptations were needed
because the target profile is SM3/GLSL-1.10, not SM5: `Input[uint2]` (a typed load with no
equivalent) became point-sampled `tex2D` at texel centres, and integer `%` became `frac(t * 0.5)`
to stay off the GLSL 1.30+ operators `SD0403` flags.

> **Incidental observation, recorded but not chased:** the pass-through mutation stopped compiling
> with `SD0012` ("GL uniform 'Width' has no matching effect parameter") because the mutation left
> the uniforms live in GLSL but dead in reflection. That is loud, registered behaviour on a
> deliberately-broken shader, not a defect found in the product path, and it is the *opposite* join
> direction from issue #187's phantom parameter. Noted only so a future reader who hits it knows it
> was seen here first.

### D2 — what converts, what does not, and the recipe each needs

Written from D1's actual mechanics, not from the ranking §6.5.1 guessed at.

**The rule.** A compute kernel is convertible **iff each output element is a pure function of its
own coordinate** — i.e. the kernel can be rewritten as a *gather*. Scatter is the whole barrier.

| Shape | Convertible? | Host-side recipe it forces on the consumer |
|---|---|---|
| Read texture → write texture, output depends only on its own coordinate (blurs, tone maps, thresholding) | **Yes**, near-directly | `Dispatch` → `SetRenderTarget` + one full-screen draw. `RWTexture2D` → the bound `RenderTarget2D`. |
| Scatter with a **deterministic, invertible** write set (D1's pairwise sort — a thread writes two known locations) | **Yes**, by re-deriving the gather (redundant recomputation) | As above, plus a **ping-pong pair of render targets** and a host loop over the phases. Read and write must be different targets. |
| Multi-pass / iterative kernels | **Yes**, if each pass is itself gatherable | The host owns the loop and the ping-pong — as it already did with `Dispatch`, so this costs nothing new. |
| Kernels using **groupshared memory / barriers** (tiled reductions, prefix scans) | **No** in general | Would need re-derivation as a multi-pass gather with an intermediate target per level; that is a redesign, not a translation. |
| **Atomics, append/consume buffers, data-dependent output counts** | **No** | The output location is not a function of the coordinate. Nothing to gather from. |
| **Structured buffers / UAVs as the data model** | **No** | MonoGame has no structured-buffer or UAV concept at all; the consumer must re-express their data as textures — application surgery, §6.5.2. |
| **Geometry shaders** | Out of scope for D1; unchanged from §6.5.1's "sometimes" | Billboard/quad expansion maps to instancing; arbitrary amplification does not. |
| **Tessellation (hull/domain)** | **No**, as predicted | Data-dependent subdivision inside the draw has no vertex/pixel equivalent. |

**§6.5.2's hard limit survived D1 fully intact, and is the decisive practical finding.** The
converted shader is useless on its own. Making it run required a host-side recipe — allocate two
render targets, loop 24 times, alternate an `OffsetX` uniform, ping-pong, point-sample — that
**no converter could ever emit**, because it lives in the consumer's C#. D1 needed roughly as much
host code as shader code.

### D3 — recommendation: **NO-GO on building a converter.** Keep the finding, not a feature.

D1 proved a human *can* do this convincingly (maxd 0). It also proved why a **transpiler** should
not be built on that success:

1. **The convertible set is narrow and the user must classify it themselves.** The rule ("is every
   output a pure function of its own coordinate?") is a judgement about the *algorithm*, not a
   syntactic property a converter can reliably detect. Getting it wrong silently produces a shader
   that compiles and renders the wrong thing — the worst failure mode this project recognizes.
2. **The deliverable is unavoidably half a deliverable.** Every converted kernel ships as a shader
   *plus* a host recipe the consumer must implement. That is the direct opposite of the standing
   seamlessness directive ("the consumer adds the package, compiles their `.fx`, and it just
   works"), and it is categorically weaker than the ShaderToy precedent, whose host code is simply
   "draw a quad" and is already written.
3. **There is no oracle, and now there is not even a fork to borrow one from.** §6.5.3 already
   conceded the bar would be source-fidelity rather than `mgfxc`-equivalence; with Area B declined
   (§5.1) there is no fork runtime to diff against either. A whole converter family would be the
   least-provable surface in the project.
4. **The demand is unmeasured.** Phase 58 came from *one* user asking whether some samples compile.
   That question is now answered accurately by `FX0014`, which is what they actually needed.

**What to keep instead:** this probe, the D2 rule, and the recipe table — so that a user who *does*
want to port a compute kernel by hand has the method written down. The probe stays in the tree as
executable evidence for the finding (`dotnet run -c Release --project validation/ComputeConversionProbe`),
**deliberately NOT wired into `run-windows-render-gates.ps1`**: it guards a research conclusion, not
a shipped product guarantee, and adding it to the release gate would be gate-bloat for a feature
that does not exist. **Reopen only if** several users ask for a specific convertible shape, at which
point the honest first step is documentation of the hand method — not a transpiler.

---

## 7. Area C — the guaranteed deliverable: tell the truth (ships regardless of §5)

Fix C1. This is small, well-scoped, and valuable even if Areas A and B both end in "no".

- Teach [`FxPreParser`](../../src/ShadowDusk.HLSL/FxPreParser.cs#L1099-L1100) to recognize
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

### 7.1 As-built (2026-08-05) — `FX0014`

`FxPreParser` gained an `UnloadableShaderStages` map and a guard in `ParsePass`'s key loop, placed
**before the `=` is consumed** so the caret lands on the stage keyword itself. `FX0014`
(`FxParseErrorCode.UnsupportedShaderStage`) is the new registered code; `docs/error-codes.md` has
its row, which `docfx/diagnostics.md` transcludes.

**The reject set was measured against the reference compiler, not assumed.** The pinned `mgfxc`
3.8.2.1105 (`/Profile:DirectX_11`) was run over all eight combinations — four stages × the
`= compile <profile> Entry();` and `= NULL;` forms — and refuses **all eight**, pointing at the
stage keyword with `Unexpected token 'H' found. Expected CloseBracket`. So ShadowDusk refuses the
same eight inputs at the same token and only changes what it *says*. The `NULL` arm was worth
measuring rather than reasoning about: `VertexShader = NULL;` **is** accepted (fxc parity, bug-hunt
2026-07-27 M14), so it is a real branch that could have let these through silently.

**Verbatim, on the three real `cpt-max` samples** (`/Profile:DirectX_11`, exit 1, no output file).
`tesselation_geometry.fx` is the same file and the same line 167 as §3's original `FX0008`, now
reported at column 3 (the `HullShader` keyword) instead of column 24 (the punctuation it blamed):

```
tesselation_geometry.fx(167,3-3): error FX0014: 'HullShader' assigns a hull shader, which the
consumer runtime cannot load: MonoGame's and KNI's Effect has exactly two shader stages, vertex and
pixel, so no compiler can produce an effect containing one. This is a runtime limit, not a
ShadowDusk gap; mgfxc rejects this effect too. Remove the 'HullShader' assignment, or express the
work in the vertex or pixel stage. (MonoGame issues #4567 and #7533 request these stages upstream
and remain open and undesigned; the cpt-max/MonoGame fork does run them, but writes a different
effect container stock MonoGame cannot read.)

compute_gpu_particles_geometry.fx(121,9-9): error FX0014: 'GeometryShader' assigns a geometry shader, …

compute_write_to_texture.fx(56,9-9): error FX0014: 'ComputeShader' assigns a compute shader, …
```

*(The three branches sampled here are `tesselation_geometry`, `compute_gpu_particles_geometry`, and
`compute_write_to_texture`. §1's table listed `compute_gpu_particles` and `edgerounding`; the
substitutions cover the same four stage keywords, and `compute_write_to_texture` additionally
exercises a pass whose **only** shader assignment is the unloadable one.)*

**OQ1 answered: `KnownProfiles` was deliberately NOT extended** with `hs_*`/`ds_*`/`gs_*`/`cs_*`.
The guard fires on the pass **key**, which is strictly earlier and strictly more specific than the
profile token, so `FX0014` always wins and adding the tokens would improve no message while
implying a support level that does not exist. `SD0013`'s interaction is therefore moot: these
shaders never reach profile validation.

**Coverage.** 12 unit cases in `FxPreParserTests` (all eight stage × form combinations asserting the
code, the stage name in the message, and the caret at the keyword; plus a lowercase-spelling case,
a "global variable named `GeometryShader` still compiles" scope guard, and a "real render state
still reaches render-state parsing" insertion-point guard) and 27 cells in the new
`ExtendedShaderStageRejectionTests` (4 stages × 2 forms × OpenGL/DirectX_11/FNA, each asserting
exit 1, `FX0014`, **no `FX0008`**, the MGCB-parseable diagnostic shape, and that **no output file
exists on disk**, plus a three-target control arm proving an ordinary two-stage pass still
compiles). The integration fixtures use the `SM4`-gated shader-model header, because with plain
`vs_3_0`/`ps_3_0` the DirectX_11 cells would have failed on `SD0015`'s profile floor (Phase 51 A10)
and the suite would have passed for the wrong reason.

---

## 8. Acceptance

- [x] A1-A5 answered in this doc with sources; §5 recommendation written. *(§4.1, 2026-08-05.)*
- [x] §5 decision recorded in `project_decisions.md` (either way). *(Owner decision 2026-08-11:
      **DECLINED**. §5.1 here; full reasoning + revisit condition in `project_decisions.md`.)*
- [x] D1 hand-conversion probe run on one compute sample, with its result written up; D2's
      convertible/not-convertible statement recorded; D3 go/no-go recommendation made. A recorded
      "no" closes the area. *(§6.6: D1 PASSED 5/5 at maxd 0, mutation-checked three ways; D2's rule
      + recipe table recorded; **D3 = NO-GO**, area closed.)*
- [x] Area C shipped: new registered diagnostic, `docs/error-codes.md` row, four regression
      fixtures, full `dotnet test` green. *(§7.1; `FX0014`, 12 unit cases + 27 integration cells.)*
- [x] The three `cpt-max` sample shaders produce the new message, captured verbatim in this doc.
      *(§7.1.)*
- [x] Corpus sweep shows zero verdict changes and zero output-byte changes. *(Full `dotnet test`
      green, including the byte-identity manifest and structural-divergence sweeps; the guard is
      keyed on a pass key no compiling fixture uses.)*
- [x] `docs/validation-matrix.md` carries a §7 row stating plainly that geometry / hull / domain /
      compute are **not supportable on stock MonoGame or KNI**, with the §2.1 evidence, so this is
      not re-investigated a third time — and, if Area D lands anything, a §8-style row recording
      that the converted route's bar is source-fidelity, **not** mgfxc-equivalence.
      *(§7 row added 2026-08-05; extended 2026-08-11 with Area D's outcome.)*
- [x] If §5 says yes: Area B spun out as its own phase, not grown here. *(N/A — §5 said no. Area B
      is closed, not deferred, and nothing was spun out.)*

## 9. Non-goals

- Implementing extended stages for **stock** MonoGame or KNI. §2.1 establishes this is impossible
  without a runtime change we do not own.
- Proposing or authoring an upstream MonoGame runtime change.
- A compute-shader API surface on `ShadowDusk` itself. The library compiles shaders; it does not
  dispatch them.
- Rewriting the consumer's host code. Area D can emit a shader and document a recipe; it cannot
  turn `Dispatch` into a draw or restructure someone's buffers for them (§6.5.2).
- A general compute-to-pixel transpiler built without the D1 probe passing first.
- Metal/MSL compute (see [Phase 31](../PHASE-31-metal-msl-backend.md); parked for its own reasons).

## 10. Open questions

- **OQ1.** If §5 declines the fork, should `KnownProfiles` still gain the stage tokens purely for
  message quality? (Area C leaves this deliberately open, since it trades one diagnostic's precision
  against implying a support level we do not offer.)
- **OQ2.** Does the ShaderToy frontend ever emit anything that would trip the new diagnostic? Almost
  certainly not (it synthesizes a VS + PS pair), but confirm rather than assume.
- **OQ3.** A4 may find the fork's container is a superset of stock MGFX that stock MonoGame would
  *reject* rather than ignore. If so, record it: it would mean a fork target can never be a silent
  auto-upgrade and must be an explicit `PlatformTarget`, which is relevant to
  [Phase 57](../PHASE-57-universal-compiler-auto-detection.md)'s auto-detection work.
