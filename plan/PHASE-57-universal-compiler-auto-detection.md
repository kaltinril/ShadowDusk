# Phase 57 — The universal-compiler shape: input auto-detection and runtime target auto-detection

**Track:** Product surface / seamlessness. Additive; no output bytes change.

**Status:** 📋 **Planned / not started** (created 2026-07-30).

**Depends on:** [Phase 46](DONE/PHASE-46-shadertoy-to-fx-conversion-tool.md) (the ShaderToy/GLSL
converter and its frozen contract), [Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md) (the
CLI's `.glsl` route and `InputFormatDetector`),
[Phase 35](DONE/PHASE-35-forward-version-support.md) (`CapabilityProfile`, `RuntimeProfileDetector`,
"auto-select seam 6").

**Blocks:** a future Raylib (or any non-XNA) backend, which needs the target-resolution seam this
phase builds. It does **not** implement one — see §16.

**Gated on:** the §3 decision. Do not start §5/§6 until that is recorded in
[`project_decisions.md`](../project_decisions.md).

> **The shape in one sentence:** hand `CompileAsync` any shader source and it works out what the
> source *is*; hand it the consumer's live runtime handle and it works out what the target *is* —
> with both detections failing loudly rather than guessing, and neither changing what an existing
> caller gets today.

---

## 1. The problem, stated exactly

The library's entry point is:

```csharp
Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
    string hlslSource, CompilerOptions options, CancellationToken ct = default);
```

Two things are hard-coded into that signature that no longer need to be:

1. **The input is assumed to be HLSL.** The parameter is literally named `hlslSource`
   ([`EffectCompiler.cs:91`](../src/ShadowDusk.Compiler/EffectCompiler.cs#L91)). GLSL/ShaderToy input
   is handled by a **separate optional package** the consumer must know about, call first, and wire
   up themselves.
2. **The target must be chosen by the consumer.** `CompilerOptions.Target` defaults to
   `PlatformTarget.OpenGL` ([`CompilerOptions.cs:18`](../src/ShadowDusk.Core/CompilerOptions.cs#L18)).
   A consumer running WindowsDX who does not set it gets OpenGL bytes their runtime cannot load,
   with a successful compile and no diagnostic.

### 1.1 The inversion

The **CLI** — a secondary delivery shape — already has the input half. The **library** — the product
— does not:

| | Input auto-detection | Target auto-detection |
|---|---|---|
| **CLI** (`ShadowDusk.Cli`) | ✅ `InputFormatDetector` + `PipelineRunner` route `.glsl` through the converter, byte-identical to Convert+pipeline (`CliShaderToyInputTest`) | ❌ explicit `/Profile:`, default `DirectX_11` — and structurally must stay explicit (§6.5) |
| **Library** (`ShadowDusk.Compiler`) | ❌ assumes HLSL; converter is a separate optional package | ⚠️ half — `RuntimeProfileDetector` resolves the runtime *family*, not the graphics *backend* |

### 1.2 Why "default to MonoGame" is not an answer

**MonoGame is not a target.** It is four artifacts — DesktopGL (`PlatformTarget.OpenGL`), WindowsDX
(`DirectX`), WindowsDX12 (`DirectX12`), DesktopVK (`Vulkan`) — each a different byte stream with a
different profile byte, none loadable by the other three. "Assume MonoGame" does not resolve to
bytes. Runtime detection is the only thing that makes a default meaningful, which is why §6 is not
optional garnish on §5.

### 1.3 Why this is in scope at all

Standing owner directive (`CLAUDE.md` → *Standing owner directives*): *"The consumer adds the
package, compiles their `.fx`, and it **just works** — they never choose a version/target/format,
flip a flag, or take a manual step to get correct output."* Today a consumer must choose a target,
and choosing wrong compiles successfully and fails at load. This phase closes that specific gap.

---

## 2. What already exists (do not re-derive this)

### 2.1 `InputFormatDetector` — the input sniff, already written and shipped

[`src/ShadowDusk.Cli/InputFormatDetector.cs`](../src/ShadowDusk.Cli/InputFormatDetector.cs) — currently
`internal` to the CLI. Its rules, verbatim from the source:

| Step | Rule |
|---|---|
| 1 | Explicit `--input-format fx\|glsl` override honored verbatim (escape hatch, never required). |
| 2 | `.fx` extension ⇒ **always** `Fx`, never sniffed. Stated in-source as the backwards-compat guarantee: no existing `.fx` invocation can regress. |
| 2 | `.glsl`, `.frag`, `.fs`, `.glslf` ⇒ `Glsl` (de-facto ShaderToy / glslViewer / KodeLife export extensions). |
| 3 | Unknown/absent extension ⇒ content sniff on comment- and string-stripped text: whole-word `technique` ⇒ `Fx`; `mainImage` or `void main` ⇒ `Glsl`. |
| 3 | **Both** signals present ⇒ **loud `SD0005`**, never a silent route pick. |
| 3 | **Neither** signal ⇒ **loud `SD0005`**. |

The sniff is explicitly *not* a parse — the converter does the real validation and fails loudly if
the sniff guessed wrong.

### 2.2 `ShaderToyConverter` — the GLSL frontend, pure-managed and zero-native

[`src/ShadowDusk.ShaderToy/`](../src/ShadowDusk.ShaderToy/). Single entry point, marked in-source as a
**FROZEN CROSS-AGENT CONTRACT (Phase 46)** — additive changes only, never breaking:

```csharp
ShaderToyConverter.Convert(string glsl, ConvertOptions? options = null) → ConvertResult
```

`ConvertResult` carries `Success`, `Fx` (the emitted HLSL `.fx` text), `Diagnostics` (located,
line/column), and `UsedUniforms`. Unsupported constructs **fail loudly** with a located diagnostic
rather than emitting a silently-wrong `.fx`.

### 2.3 `RuntimeProfileDetector` — half the target axis, already written

[`src/ShadowDusk.Core/RuntimeProfileDetector.cs`](../src/ShadowDusk.Core/RuntimeProfileDetector.cs),
labelled in-source *"Phase 35 auto-select seam 6"*.

- `Classify(string? xnaAssemblySimpleName) → DetectedRuntime` — name-based, reflection-only,
  **XNA-free** (ShadowDusk keeps no MonoGame/KNI/FNA reference). Discriminators:
  `nkast.Xna.Framework*` ⇒ `Kni` (**checked first**), `MonoGame.Framework*` ⇒ `MonoGame`,
  `FNA` / `FNA.*` ⇒ `Fna`, else `Unknown`.
- `Recommend(DetectedRuntime, PlatformTarget) → CapabilityProfile` — **still requires the caller to
  pass the graphics target.** It selects the container/profile (MGFX v10, or fx_2_0 for FNA), not
  GL-vs-DX-vs-DX12-vs-Vulkan. **This is the gap §6 closes.**
- It is **conservative by design**: it only ever returns a profile already render-proven for the
  detected runtime, and never silently upgrades a consumer to an unproven format. Promoting a newer
  container to auto-selection is documented in-source as *"a deliberate version event"*. §7 reuses
  that exact policy.

### 2.4 The loud-failure precedent, already paid for

[`RuntimeProfileDetector.cs:68-88`](../src/ShadowDusk.Core/RuntimeProfileDetector.cs#L68-L88) records a
fixed bug that is the direct ancestor of this phase's main hazard: a fall-through `else` handed back
the OpenGL profile for `Vulkan`/`DirectX12`/`Metal`, and because the pipeline applies a set
`Profile`'s `GraphicsTarget` **over** `options.Target`, it *silently rewrote the requested backend* —
a DesktopVK or WindowsDX12 game got a MojoShader-GLSL `.mgfx` its runtime cannot load, **compiled
with exit 0 and no diagnostic**. The fix throws instead. The in-source conclusion is the rule §6.4
inherits: **"Refusing loudly is the only honest answer."**

### 2.5 The evidence already standing behind the GLSL route

From [`docs/validation-matrix.md`](../docs/validation-matrix.md) §8 and §6:

- **Fidelity vs the original GLSL** (Phase 46, out-of-band `render-proof --fidelity`): **46/46
  deterministic shaders match the original at mean 0.00/255**; gallery 72/72.
- **Downstream half vs the reference compiler** (Phase 51 A5,
  [`validation/ShaderToyRouteGl`](../validation/ShaderToyRouteGl/), in CI on Mesa llvmpipe):
  `GradientToy.glsl` converted in process, then ShadowDusk's OpenGL build pixel-diffed against
  **`mgfxc`'s build of the same converted `.fx`** on real MonoGame DesktopGL: **maxd 0**.
- **CLI ≡ library**: `CliShaderToyInputTest` proves byte-identity between the CLI `.glsl` route and
  Convert+pipeline.

**This phase adds no new evidence obligation for the GLSL route itself.** It relocates where the
route is invoked from. §12 is about proving the relocation is behavior-preserving.

---

## 3. The gating decision — settle this before writing code

`CLAUDE.md` and [`plan.md`](plan.md) both open with: *"The product is a drop-in `mgfxc` replacement."*
Every target to date is an XNA-family runtime with a reference compiler (`mgfxc` / `fxc /T fx_2_0`)
to be proven against. This phase does not itself break that, but it is the enabling step for a
reading of the product as *"give it any shader, name any runtime"* — which does.

**Two evidence models exist in this repo, and they must stay named as distinct:**

| Model | Bar | Where it applies | Measured |
|---|---|---|---|
| **Reference-compiler equivalence** | ShadowDusk's output vs `mgfxc`/`fxc`'s output, same source, real engine, pixel diff | Every target with a reference compiler: OpenGL, DirectX, DirectX12, Vulkan, FNA | maxd 0 across the corpora |
| **Source fidelity** | ShadowDusk's output vs the **original shader source** rendered directly, same runtime, pixel diff | Inputs with no reference compiler (ShaderToy/GLSL — matrix §8) | mean 0.00/255, 46/46 |

The second model is **not weaker in measurement**, but it is a **different claim**, and §8 of the
matrix exists precisely to stop the two being conflated.

**Required decision, recorded in [`project_decisions.md`](../project_decisions.md) in the house
`decided X (not Y) because Z` form, before §5/§6 begin:**

> Does ShadowDusk's stated purpose widen from *"drop-in `mgfxc` replacement"* to *"one faithful
> pipeline, many runtimes — reference-compiler equivalence where a reference compiler exists, source
> fidelity where it does not"*, with the two evidence models named as **peers** rather than §8 being
> the exception?

- **If yes:** `CLAUDE.md` → THE PURPOSE, [`docs/the-purpose.md`](../docs/the-purpose.md), and
  [`plan.md`](plan.md) → THE PURPOSE are rewritten **in this phase's PR** (they are three copies of
  one sentence and must not drift), and matrix §8 is promoted from "a distinct axis" to "the second
  of two models".
- **If no:** this phase still ships in full — both detections are seamlessness work squarely inside
  the current purpose — but §16's Raylib follow-on is **closed, not deferred**, and that is recorded
  too.

**Do not let the answer be decided implicitly by writing the code.**

---

## 4. Scope

### In

- **A.** Input auto-detection promoted from the CLI into the library, behind a seam that keeps
  `ShadowDusk.Compiler` free of a hard dependency on `ShadowDusk.ShaderToy`.
- **B.** Target auto-detection extended from runtime *family* to graphics *backend*, plus a
  `CompilerOptions` seam so the compiler can resolve the target itself.
- **C.** The diagnostics, tests, validation, and doc updates those two require.

### Out

- **Any new backend.** No Raylib, no Metal, no new `PlatformTarget` member that emits bytes. §16.
- **Changing the default `Target`.** Explicitly deferred to a named version event — §7.
- **Any change to emitted bytes.** Byte-identity across the whole corpus is an **acceptance
  criterion** (§12.1), not a hoped-for outcome.
- **Any change to `ShaderToyConverter`'s contract.** It is frozen; additive only.
- **New input languages** (SPIR-V in, WGSL, MSL). The seam §5 builds is what makes them cheap later;
  none are built here.

---

## 5. Feature A — input auto-detection in the library

### 5.1 Requirement

`IShaderCompiler.CompileAsync` accepts HLSL `.fx` **or** ShaderToy/GLSL source and routes correctly
without the consumer selecting a route, while `ShadowDusk.Compiler` remains installable and fully
functional **without** `ShadowDusk.ShaderToy` present.

### 5.2 Rules

| # | Rule | Rationale |
|---|---|---|
| A1 | `.fx` extension (via `SourceFileName`) ⇒ **always** the HLSL path, never sniffed. | The existing in-source backwards-compat guarantee. No existing call can regress. |
| A2 | When no frontend is registered, **every** input takes the HLSL path — detection does not run at all. | `ShadowDusk.Compiler` alone must behave byte-identically to today. |
| A3 | Detection order is: explicit override → extension → content sniff. Identical to the CLI's, because it is the same code (§5.4). | One implementation, one behavior. Two sniffs would drift. |
| A4 | Ambiguous or unclassifiable input ⇒ **`SD0005`, loud**, never a silent route pick. | Existing rule; unchanged. |
| A5 | A frontend's own diagnostics are surfaced **verbatim**, located in the **original** source, never reworded. | `CLAUDE.md` → Coding Conventions: *"Never swallow or reformat a compiler's own message."* |
| A6 | On the converted path, pipeline (DXC) errors are attributed to a **synthetic generated-source name**, not the user's `.glsl`, because generated-HLSL line numbers do not correspond to the original. | Already solved in [`PipelineRunner.cs:58-63`](../src/ShadowDusk.Cli/PipelineRunner.cs#L58-L63); the library must reproduce it, not reinvent it. |
| A7 | The library route must be **byte-identical** to `Convert` + `Compile` called separately. | Mirrors the existing `CliShaderToyInputTest` guarantee. |

### 5.3 Packaging constraint (hard)

[`src/ShadowDusk.ShaderToy/README.md`](../src/ShadowDusk.ShaderToy/README.md) states the contract:
*"This package is standalone and optional — it is not part of ShadowDusk's faithful `mgfxc`-replacement
pipeline, and `ShadowDusk.Compiler` does not depend on it."*

**`ShadowDusk.Compiler` must not gain a `ProjectReference`/`PackageReference` to
`ShadowDusk.ShaderToy`.** The dependency direction is inverted with an interface in Core plus
registration.

### 5.4 Implementation

**A-1. Move `InputFormatDetector` to `ShadowDusk.Core`, make it public.**
Move the file to `src/ShadowDusk.Core/Frontend/InputFormatDetector.cs`; promote `InputFormat`,
`InputKind`, `InputFormatDetector` to `public`. **Move, do not copy** — a second sniff is a drift
source. The CLI keeps calling it; its behavior and its `SD0005` message text are unchanged.

**A-2. Define the frontend seam in Core.**

```csharp
namespace ShadowDusk.Core.Frontend;

/// A source-language frontend: non-HLSL source in, HLSL .fx text out.
public interface IShaderFrontend
{
    InputKind Handles { get; }
    Result<string, ShaderError[]> ToFx(string source, string? sourceFileName);
}
```

`ToFx` returns `.fx` **text**, not bytes — the downstream pipeline is unchanged and receives ordinary
HLSL. This is the same shape the CLI already uses and is what makes §2.5's `mgfxc` oracle applicable
to the converted output.

**A-3. Registration, without a hard dependency.**
`ShadowDusk.ShaderToy` gains a small `ShaderToyFrontend : IShaderFrontend` adapter over the frozen
`ShaderToyConverter.Convert`. Registration options, in preference order:

1. **Module initializer** in `ShadowDusk.ShaderToy` self-registering into a Core registry — matches
   the precedent already used for `ShadowDusk.Wasm` self-registration (Phase 23: *"a scratch consumer
   compiles with only a `PackageReference`, zero wiring"*). **Preferred**: zero consumer wiring,
   which is the whole point.
2. **Explicit** `CompilerOptions.Frontends` collection — always available as the testable,
   deterministic path regardless of which is chosen as the default.

Decide in task A-3 and record why. A module initializer is load-order-sensitive; the test suite must
cover "package present" and "package absent" as distinct assemblies (§11.1).

**A-4. Wire into `CompilationPipeline`.**
A single stage ahead of the existing preprocessing, mirroring
[`PipelineRunner.cs:34-63`](../src/ShadowDusk.Cli/PipelineRunner.cs#L34-L63): detect → if `Glsl` and a
frontend is registered, `ToFx` → continue on the **exact unchanged** `.fx` path. Carry the synthetic
source name for A6.

**A-5. Rename the parameter, keep the ABI.**
`hlslSource` → `source` on `IShaderCompiler` and `EffectCompiler`. This is source-compatible for
positional callers and a **named-argument break** for `CompileAsync(hlslSource: x, ...)`. Grep the
solution, the samples, and the docs for named usage; record it in `CHANGELOG.md` under
`### Changed`. Update the XML doc-comments — they render into the published API reference.

**A-6. Re-point the CLI at the library route.**
Once A-4 lands, `PipelineRunner`'s convert stage is redundant. Delete it and let the CLI call through,
**keeping the CLI's existing diagnostic formatting and `SD09xx` convert-code mapping intact** —
`CliShaderToyInputTest` and the CLI's MGCB-parseable output format must not move. If preserving the
CLI's exact stderr text requires keeping the CLI stage, **keep it and delete nothing**; the byte-identity
of compiled output is the requirement, not code-sharing aesthetics.

---

## 6. Feature B — target auto-detection from the live runtime

### 6.1 Requirement

Given the consumer's live runtime handle, resolve the correct `PlatformTarget` **and**
`CapabilityProfile` with no consumer input — or fail loudly.

### 6.2 The two sub-axes

| Axis | State | Work |
|---|---|---|
| Runtime **family** — MonoGame / KNI / FNA | ✅ done (`RuntimeProfileDetector.Classify`) | Extend the enum only if §16 lands. |
| Graphics **backend** — GL / DX11 / DX12 / Vulkan | ❌ **absent** — the caller supplies it | **B-1, the spike.** |

### 6.3 B-1 — the backend-discriminator spike (do this first; everything else depends on the result)

**This is the one piece that cannot be specified from reading ShadowDusk's source — it depends on
what MonoGame and KNI actually ship.** Modern MonoGame ships the same `MonoGame.Framework.dll` simple
name across backends, so `Classify`'s technique does not extend by itself.

**Constraint that bounds every candidate: the detector must stay XNA-free.** No
MonoGame/KNI/FNA reference may enter `ShadowDusk.Core` — reflection over names only, exactly as
`Classify` does today. A candidate that requires referencing `GraphicsDevice` is disqualified
regardless of how well it works.

Candidates to evaluate, in decreasing preference:

1. **Loaded/referenced assembly probe** — `SDL2`/`OpenTK` ⇒ DesktopGL; `SharpDX.*` ⇒ WindowsDX;
   Vulkan-loader assemblies ⇒ DesktopVK; DX12-specific ⇒ WindowsDX12.
2. **Backend-only type-name probe** by reflection over `typeof(GraphicsDevice).Assembly` handed in as
   `object`/`Assembly`, checking for types that exist in exactly one backend build.
3. **The runtime's own shader-profile value** read reflectively (MonoGame's internal `Shader.Profile`
   is precisely the byte the `.mgfx` must match). **Most semantically correct, most brittle** — it is
   an internal, and the doc-comments on `PlatformTarget.Vulkan`/`DirectX12` already warn that profile
   bytes are the runtime's, *"never this enum ordinal"*.

**Spike deliverable** (a section appended to this doc, not a separate file):

- For each of MonoGame **3.8.1.263 · 3.8.2.1105 · 3.8.5** × {DesktopGL, WindowsDX, WindowsDX12,
  DesktopVK} and KNI × {DesktopGL, WinForms-DX, WebGL}: does the chosen discriminator resolve
  correctly? A **table of measured results**, not a prediction.
- The MonoGame version span is not arbitrary — `project_facts.md` records **3.8.1.263 as the measured
  floor** and `validation/ForwardCompat` sweeps 7 releases. A discriminator that works only on one
  version is not shippable.
- **An explicit statement of which cells return `Unknown`.** Unknown is an acceptable, honest result;
  a wrong guess is not.

**If no reliable discriminator exists for a cell, that cell returns `Unknown` and B-3 fails loudly.**
That is a shippable outcome. Guessing is not.

### 6.4 Rules

| # | Rule | Rationale |
|---|---|---|
| B1 | An explicitly set `CompilerOptions.Target` is **always** honored verbatim. Auto-detection never overrides it. | Backwards compatibility, and detection must never be a trap. |
| B2 | Auto-detection is **opt-in in this phase** (§7). Not opting in reproduces today's behavior byte-for-byte. | Zero-risk landing. |
| B3 | Detection failure (`Unknown` runtime **or** `Unknown` backend) ⇒ **loud diagnostic, no output**. Never a fallback to OpenGL. | §2.4. The exact bug already paid for once: silently substituting a backend ships unloadable bytes at exit 0. |
| B4 | Detection returns only **render-proven** (runtime, target, container) combinations — it inherits `RuntimeProfileDetector`'s conservatism verbatim. | In-source: *"Auto-detect only ever returns a profile already proven for the detected runtime; it never silently upgrades a consumer to an unproven format."* |
| B5 | A detected FNA runtime ⇒ `PlatformTarget.Fna` regardless of graphics backend. | Already true (`Recommend` short-circuits on `DetectedRuntime.Fna`). FNA loads one artifact for every backend. |
| B6 | Detection is **pure and side-effect-free**: same inputs ⇒ same result, no I/O, no process launch. | It runs inside `Compile`; `CLAUDE.md` bars hidden I/O in that path. |
| B7 | The detector must not throw across the API boundary — failures travel as `Result` / `ShaderError`. | `CLAUDE.md`: *"Errors use a `Result<T, TError>` union, never exception-as-control-flow."* Note `Recommend` currently **throws** `ArgumentOutOfRangeException`; the new surface wraps it, and the existing method's behavior is left alone. |

### 6.5 Where auto-detection cannot apply

The **CLI** and the **MGCB plugin** compile ahead of time on a build machine, frequently
cross-compiling for a runtime that is not present. **There is no runtime to detect.** They keep an
explicit target permanently. This is not a limitation to fix later — it is inherent, and the docs
must say so plainly (§13) rather than leaving consumers to infer that `/Profile:` is optional.

### 6.6 Implementation

**B-2. `RuntimeTargetDetector` in `ShadowDusk.Core`** — a new type, sibling to `RuntimeProfileDetector`,
which is left untouched (it is public API with existing consumers):

```csharp
public sealed record DetectedTarget(
    DetectedRuntime Runtime, PlatformTarget Target, CapabilityProfile Profile);

public static class RuntimeTargetDetector
{
    // Overloads mirroring RuntimeProfileDetector's: by assembly, by name, and by a
    // live graphics-device handle passed as `object` (kept XNA-free by reflection).
    public static Result<DetectedTarget, ShaderError> Detect(object graphicsDevice);
    public static Result<DetectedTarget, ShaderError> Detect(Assembly xnaAssembly);
}
```

**B-3. The `CompilerOptions` seam.**

```csharp
/// When set, the target and profile are resolved from the consumer's live runtime and
/// CompilerOptions.Target is ignored. Detection failure fails the compile loudly (SD0030).
public object? DetectTargetFrom { get; init; }
```

Additive, opt-in, no default change, no `PlatformTarget` enum change.

**Conflict rule (document it in one sentence, and test it):** when `DetectTargetFrom` is non-null it
**wins** over `Target`; we do not attempt to distinguish "`Target` explicitly set to `OpenGL`" from
"`Target` left at its default", because the property is a non-nullable enum and that distinction is
not observable.

**B-4. `WithGraphicsTarget` must copy the new property.**
[`CompilerOptions.cs:105-123`](../src/ShadowDusk.Core/CompilerOptions.cs#L105-L123) carries an in-source
warning that every property **must** be copied, because `Defines` was missed once and
`--target-runtime monogame-gl /Defines:X` silently compiled with `X` undefined, making `ValidateAsync`
report on a different source than `CompileAsync` produced. **Add `DetectTargetFrom` to the copy list
and extend the existing round-trip test that pins this.**

---

## 7. Default-change policy — explicitly deferred

Making auto-detection the **default** (`Target` unset ⇒ detect) would change what an existing caller
receives: a WindowsDX consumer relying on the default currently gets OpenGL bytes and would begin
getting DirectX bytes. That is a **fix**, but it is still a behavior change.

**Decision for this phase: auto-detection ships opt-in. The default stays
`PlatformTarget.OpenGL`.** This is the exact policy `RuntimeProfileDetector` already documents for
KNIFX/MGFX-v11 auto-selection — proven first, opt-in second, promoted third, and the promotion is
*"a deliberate version event"*.

Promotion criteria, to be written into this doc when B-1 reports and re-read at the version event:

1. B-1's measured table shows the discriminator correct on **every** MonoGame release in
   `validation/ForwardCompat`'s sweep (currently 7, floor 3.8.1.263) and on KNI.
2. Zero `Unknown` results for any runtime/backend combination the matrix marks render-proven.
3. A rung-4 gate exists proving a default-compiled effect loads and renders on each detected backend
   (§12.2).
4. A `CHANGELOG.md` `### Changed` entry and a `project_decisions.md` entry, both written before
   the flip, not after.

---

## 8. Diagnostics

Registered in [`docs/error-codes.md`](../docs/error-codes.md). **House rule, recorded at
[`InputFormatDetector.cs:97-100`](../src/ShadowDusk.Cli/InputFormatDetector.cs#L97-L100) from bug-hunt
N13: one code = one condition.** Reusing a code for a second condition makes it read as the first.

| Code | Condition | Status |
|---|---|---|
| `SD0005` | Input format ambiguous or unclassifiable | **Reuse.** Same condition, new surface. Re-word the registry entry so it is not CLI-flavored. Do **not** mint a second code. |
| `SD0030` | Target auto-detection requested but the runtime could not be classified | **New.** Verified free against §8's allocated list. |
| `SD0031` | Runtime classified, but its graphics backend has no modelled `PlatformTarget` | **New.** Distinct condition from `SD0030` — "I don't know what you are" vs "I know what you are and can't serve it". This is the code a future Raylib consumer hits before §16 lands. |

Both new codes must state, in the message, the concrete remedy: **set `CompilerOptions.Target`
explicitly**. A loud failure that does not say what to do next is only half the rule.

---

## 9. Interactions and known hazards

### 9.1 ✅ RESOLVED (2026-07-31) — the Phase 51 A10 divergence is fixed, so this phase no longer inherits it

**This section used to be a required work item for this phase**, on the grounds that auto-detection
would make the divergence reachable by default. It was closed independently in Phase 51 A10, by
option (a) below, so nothing here blocks this phase any more. The reasoning is kept because it is
the standing rule for any *future* auto-detection change.

The finding was: `ShaderToyConverter` emitted `vs_3_0`/`ps_3_0` in **both** branches of its
`#if OPENGL` header. Real `mgfxc /Profile:DirectX_11` **rejects** that (*"Invalid profile 'vs_3_0'.
Vertex shader 'VSMain' must be SM 4.0 level 9.1 or higher!"*) while **ShadowDusk compiled it
successfully** — a Phase 48-class drop-in accept/reject divergence, and the reason
`ShaderToyRouteGl` had no DirectX arm. Today it required deliberately running
`ShadowDuskCLI shader.glsl /Profile:DirectX_11`; after §5 + §6 a WindowsDX consumer who passes a
`.glsl` would have reached it by doing nothing unusual at all.

**What landed (option (a), the preferred one):** the converter now emits an **`SM4`-gated** header
whose DirectX arm names `vs_4_0_level_9_1`/`ps_4_0_level_9_1` (OpenGL and FNA keep SM3), verified
compilable by the pinned `mgfxc` for `DirectX_11`; a DirectX golden and the
`validation/ShaderToyRouteDx` arm exist; and the accept side was closed too — the DirectX target
now rejects sub-floor profiles itself as **`SD0015`**. No output bytes moved.

**The standing rule this leaves behind:** *silently compiling something `mgfxc` rejects, on a path
consumers reach by default, is not acceptable.* If auto-detection later routes a new input/target
combination, re-check it against the reference compiler before shipping the default.

### 9.2 Profile-over-target precedence

`CompilationPipeline` applies a set `Profile`'s `GraphicsTarget` **over** `options.Target` (§2.4).
Adding a third source of truth (`DetectTargetFrom`) makes the precedence chain three-deep. **Write
the precedence down as an ordered list in the XML doc-comments and pin it with a test per pair** —
this is exactly the mechanism that produced the silent-backend-rewrite bug.

Required order: `DetectTargetFrom` (when set) → `Profile` (when set) → `Target`.

### 9.3 Module-initializer load order

If A-3 chooses self-registration, a consumer who references `ShadowDusk.ShaderToy` but whose linker
trims it, or who calls `Compile` before the initializer runs, silently gets the HLSL path and a
confusing `.fx`-parse error on GLSL source. **Mitigation:** the explicit `Frontends` collection is
always available, and §11.1 tests the absent-package case as a separate assembly rather than assuming.

---

## 10. What does NOT change

State these as acceptance criteria, because "additive" has to be measured:

- **No emitted byte moves.** The golden corpus and the byte-identity manifest are untouched.
- **No `.fx` input changes route.** Rule A1 makes this structural, not incidental.
- **The MGFX v10 default, the MonoGame pin, and every `PlatformTarget` member's output** are
  untouched.
- **`ShaderToyConverter`'s frozen contract** is unchanged (§9.1 option (a) would be additive to the
  emitted `.fx` text, not to the API).
- **`RuntimeProfileDetector`'s existing public surface** is unchanged; `RuntimeTargetDetector` is a
  new sibling.

---

## 11. Testing (`dotnet test` half)

### 11.1 Unit — pure, no disk, no process

- `InputFormatDetector` after the move: every row of §2.2's table, plus the ambiguity and
  unclassifiable cases. Port the existing CLI tests; **do not rewrite them**.
- Frontend seam: registered vs not-registered; `Handles` mismatch; frontend returning failure.
- **Package-absent behavior in a separate test assembly** that does *not* reference
  `ShadowDusk.ShaderToy`, asserting GLSL source takes the HLSL path and fails as ordinary `.fx`
  (rule A2). A same-assembly test cannot prove this.
- `RuntimeTargetDetector`: one case per B-1 table row, plus `Unknown` runtime and `Unknown` backend
  ⇒ `SD0030`/`SD0031`.
- Precedence: `DetectTargetFrom` > `Profile` > `Target`, one test per pair (§9.2).
- `WithGraphicsTarget` round-trip including `DetectTargetFrom` (§6.6 B-4).

### 11.2 Integration — `[Trait("Category","Integration")]`

- **Byte-identity, the headline test:** for every `.glsl` fixture, `CompileAsync(glsl)` via the
  library route == `Convert` + `CompileAsync(fx)` separately, **byte for byte** (rule A7). Mirrors
  `CliShaderToyInputTest`'s existing guarantee at the library level.
- **Corpus byte-identity:** every existing `.fx` fixture × every target compiles to bytes identical
  to pre-change output. This is §10's first bullet made executable and is the phase's primary
  no-regression proof.
- CLI ≡ library after A-6, if A-6 lands.
- Diagnostic attribution on the converted path: a deliberate error in `.glsl` reports the original
  `.glsl` with real line/column; a deliberate error in generated HLSL reports the synthetic name
  (rule A6).

### 11.3 Full suite

`dotnet test ShadowDusk.slnx` — **the full suite, never a filtered subset.** `CLAUDE.md` records that
a filtered subset stayed green while a whole class of valid HLSL silently failed to compile
(issue #106).

---

## 12. Validation (the render half — `validation/*`)

### 12.1 Required for this phase as scoped

Because §10 asserts no byte moves, the render gates are a **regression check, not new evidence**:

```powershell
dotnet test ShadowDusk.slnx
./validation/run-windows-render-gates.ps1
```

Green here means the detection work did not perturb output. **A byte-identical corpus plus green
gates is the complete render obligation for this phase** — no new driver is required, because no new
bytes exist to prove.

### 12.2 Required *before* the §7 default flip (not in this phase)

A rung-4 gate proving a **default-compiled** (no explicit `Target`) effect loads and renders on each
auto-detected backend — the point being that the *detection*, not just the compilation, produced the
right artifact. Scope it when B-1 reports. Register it as a
[`docs/validation-matrix.md`](../docs/validation-matrix.md) §6 row **with its exact run command**, and
wire it into `run-windows-render-gates.ps1`. `CLAUDE.md`: **a check nobody remembers to run does not
exist.**

---

## 13. Documentation surfaces (same PR, per `CLAUDE.md`)

This phase changes what ShadowDusk supports and how it is used, so the same-PR rule fires:

| Surface | Change |
|---|---|
| `docfx/getting-started/in-memory-quickstart.md` | The `hlslSource` → `source` rename; the GLSL-input route; the `DetectTargetFrom` opt-in. **The "Library vs CLI defaults" table stays true** (§7 changes no default) — say so explicitly rather than deleting it. |
| `docfx/guides/choosing-a-target.md` | Add auto-detection as a path, and §6.5 (why CLI/MGCB stay explicit). |
| `docfx/index.md`, `docfx/getting-started/overview.md` | Headline tables if §3 answers yes. |
| `README.md` | The pipeline block gains the frontend stage. |
| `docs/pipeline-overview.puml` | Add the frontend-detection stage — **and regenerate `docfx/images/pipeline-overview.svg`** via `tools/render-diagrams.{ps1,sh}`. The site embeds the SVG; an un-regenerated SVG ships the old diagram. |
| `docs/validation-matrix.md` | §8 promoted to a peer model if §3 answers yes; a §7 gap row for the deferred default flip; a §6 row **only** if 12.2 lands. |
| `docs/error-codes.md` | `SD0030`, `SD0031`; re-word `SD0005`. |
| `docs/the-purpose.md`, `CLAUDE.md`, `plan.md` | THE PURPOSE, **all three copies**, if §3 answers yes. |
| `project_facts.md` | The detection seams and their limits; B-1's measured discriminator table. |
| `project_decisions.md` | The §3 decision; the §7 deferral; the A-3 registration choice. |
| XML doc-comments | `IShaderCompiler`, `CompilerOptions`, `RuntimeTargetDetector`, `IShaderFrontend` — these render into the published API reference. |
| `CHANGELOG.md` | `### Added` for both detections; `### Changed` for the parameter rename. |
| `plan/plan.md` | This phase's index row; move this doc + any appendix to `plan/DONE/` on completion, fixing relative links here and in every referrer. |

---

## 14. Task checklist

**Ordered. B-1 gates all of §6; §3 gates everything.**

### Gate

- [ ] **G-1** Record the §3 purpose decision in `project_decisions.md`. Do not start §5/§6 first.

### Spike (do before designing §6's surface)

- [ ] **B-1** Backend-discriminator spike. Deliverable: a measured table over MonoGame
      3.8.1.263 / 3.8.2.1105 / 3.8.5 × {DesktopGL, WindowsDX, WindowsDX12, DesktopVK} and KNI ×
      {DesktopGL, WinForms-DX, WebGL}, appended to this doc, naming every `Unknown` cell. Must stay
      XNA-free.

### Feature A — input

- [ ] **A-1** Move `InputFormatDetector` to `ShadowDusk.Core`, make public. Port its tests. No
      behavior or message-text change.
- [ ] **A-2** Add `IShaderFrontend` + registry to Core.
- [ ] **A-3** `ShaderToyFrontend` adapter in `ShadowDusk.ShaderToy`; choose and record the
      registration mechanism. **No `ShadowDusk.Compiler` → `ShadowDusk.ShaderToy` reference.**
- [ ] **A-4** Wire the detect→convert stage into `CompilationPipeline`, including A6 synthetic source
      naming.
- [ ] **A-5** Rename `hlslSource` → `source`; sweep named-argument callers; update XML docs and
      `CHANGELOG.md`.
- [ ] **A-6** Re-point the CLI, **only if** its exact stderr text and `SD09xx` mapping survive
      unchanged. Otherwise keep the CLI stage and say so here.
- [ ] **A-7** §9.1: fix the converter's non-OPENGL profile branch (preferred), or add the located
      divergence diagnostic. Not optional.

### Feature B — target

- [ ] **B-2** `RuntimeTargetDetector` + `DetectedTarget`, per B-1's findings. `Result`-returning, never
      throwing (rule B7).
- [ ] **B-3** `CompilerOptions.DetectTargetFrom` + the §9.2 precedence chain, documented and pinned.
- [ ] **B-4** Add `DetectTargetFrom` to `WithGraphicsTarget`'s copy list; extend the round-trip test.
- [ ] **B-5** Register `SD0030`/`SD0031`; re-word `SD0005`. Messages must name the remedy.

### Proof

- [ ] **T-1** Unit tests per §11.1, including the **separate package-absent assembly**.
- [ ] **T-2** Integration tests per §11.2, including library-route byte-identity and full-corpus
      byte-identity.
- [ ] **T-3** Full `dotnet test ShadowDusk.slnx` green (never filtered).
- [ ] **T-4** `./validation/run-windows-render-gates.ps1` green, confirming no byte moved.

### Close

- [ ] **D-1** Every §13 surface updated **in the same PR**, including the regenerated
      `pipeline-overview.svg`.
- [ ] **D-2** Write §7's promotion criteria back into this doc with B-1's real numbers.
- [ ] **D-3** Move this doc to `plan/DONE/`, fix relative links here and in referrers, update
      `plan.md`'s row.

---

## 15. Risks and open questions

| # | Risk | Mitigation |
|---|---|---|
| R1 | **B-1 finds no reliable backend discriminator** on some runtime/version. | Shippable: that cell returns `Unknown` → `SD0031` → consumer sets `Target`. Feature A is independent and lands regardless. The phase does not fail; its scope narrows honestly. |
| R2 | The parameter rename breaks named-argument callers. | Source-level, compile-time, warnings-as-errors. Sweep + `### Changed` entry. Low severity, zero silence. |
| R3 | Module-initializer registration is trim/load-order fragile. | §9.3; explicit `Frontends` always available; absent-package tested in its own assembly. |
| R4 | Three-deep target precedence reintroduces the silent-backend-rewrite class of bug. | §9.2: ordered list in the doc-comments, one test per pair. This is the highest-severity risk in the phase — it is the exact bug already paid for once. |
| R5 | §3 is answered implicitly by shipping code before deciding. | G-1 is a hard gate ahead of all implementation tasks. |
| R6 | §9.1's divergence reaches consumers by default. | A-7 is mandatory, not best-effort. |

**Open questions for B-1 to answer, not to guess now:**

1. Does MonoGame 3.8.5 change assembly identity per backend, or is it still one
   `MonoGame.Framework.dll`?
2. Is KNI's WebGL platform distinguishable from its DesktopGL platform by assembly name alone? Both
   are `PlatformTarget.OpenGL`, so this may not matter for target selection — confirm it does not.
3. Does any candidate discriminator survive single-file publish, trimming, and NativeAOT? A detector
   that works in `dotnet run` and fails in a shipped game is worse than none.

---

## 16. Out of scope — the Raylib follow-on

The conversation that produced this phase started at *"could ShadowDusk target Raylib (Raylib-cs)?"*.
**No Raylib work is in this phase**, deliberately: a new backend and a new compiler shape are separate
changes, and mixing them makes both harder to prove.

Recorded so the analysis is not redone:

- **The codegen is the cheap part.** SPIRV-Cross already emits modern GLSL 330 / ES 300, which is what
  Raylib consumes. The MonoGame path spends 3,515 lines and 15 documented rules
  ([`MonoGameGlslRewriter.cs`](../src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs),
  [`docs/glsl-uniform-naming.md`](../docs/glsl-uniform-naming.md)) dragging that GLSL *backwards* into
  MojoShader's GLSL-110 dialect. A Raylib emitter skips most of it and skips the MGFX writer entirely.
  Its uniform handling is the **inverse** of Rule 7 and strictly easier: Raylib binds by name
  (`GetShaderLocation`), and SPIRV-Cross preserves the original member names, so
  `_Globals.DiffuseColor` → `uniform vec4 DiffuseColor;` needs none of Rule 7's register-offset and
  swizzle arithmetic.
- **The ES-1.00 safety lowering already exists and is reusable verbatim** — rules 8, 9b, 12, 13, 15
  (`round`, `trunc`, do-while, WebGL1 loop forms) for Raylib's `#version 100` path.
- **The output shape is the friction.** `CompiledShader.Data` is a single `byte[]` for
  `new Effect(gd, bytes)`. Raylib's `LoadShaderFromMemory(vs, fs)` takes **two strings**, with no
  container, no technique/pass concept, and no reflection table. Multi-pass techniques and in-pass
  render states (blend/depth/cull) have no Raylib equivalent and would need a manifest or a loud
  rejection.
- **The oracle is the real cost, and it is already half-built.** Raylib has no reference compiler.
  The honest bar is §3's **source-fidelity** model, and Phase 46's `render-proof --fidelity` harness
  already implements exactly it (46/46 at mean 0.00/255): render the original GLSL directly, render
  ShadowDusk's build of the mechanically-converted `.fx`, diff — **same runtime, same source, no human
  translation step.** Retargeting that harness at Raylib-cs is a retarget, not an invention.
- **The residual gap that harness cannot close:** ShaderToy-shaped shaders are `gl_FragCoord`-driven
  fullscreen and never exercise vertex attributes, `mvp`, `matModel`, or `texture0` binding — i.e.
  precisely Raylib's *conventions*. That needs a small hand-paired corpus, scoped to conventions only,
  not to math.
- **What this phase leaves in place for it:** `IShaderFrontend` (§5), `RuntimeTargetDetector` +
  `SD0031` (§6) — a Raylib-cs consumer already gets a correct, located "I know what you are and cannot
  serve it" failure before any backend exists.

**A Raylib backend is opened as its own phase, or not at all, on the strength of §3's decision.**
