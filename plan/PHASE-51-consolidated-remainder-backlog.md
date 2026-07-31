# Phase 51 — Consolidated remainder backlog (render-rung tails & ext-blocked validation)

**Status:** 🔵 **Open (collector phase, created 2026-06-28).** This phase exists so that
no *otherwise-finished* phase has to sit open at "95% with 1-2 tail items." The parent
phases below were each substantively complete; their **single remaining work items were
moved here verbatim** and the parents were archived to `plan/DONE/`. This doc is the live
home for those tails. Nothing here blocks v1.0 or the drop-in `mgfxc` promise; each item is
either an *additive* validation rung or is gated on an **external** dependency.

**Why this phase exists (owner directive, 2026-06-28):** *"I don't want to leave a phase doc
95% complete sitting open for 1 or 2 remainder items."* When a phase is done except for a
small tail, the tail moves here and the parent moves to DONE.

---

## Provenance — where each item came from

| Item | Source phase (now archived) | Original status when moved |
|---|---|---|
| OpenGL sampler records per (texture, sampler) PAIR (**A7**) | [2026-07-27 full-project review](../plan/BUG-HUNT-2026-07-27.md) sibling sweep | 🟡 Interim `SD0216` shipped (loud, not silent); the parity fix is open |
| **PENDING MIGRATION — the `BUG-HUNT-2026-07-27.md` DEFERRED residue** | [`BUG-HUNT-2026-07-27.md`](BUG-HUNT-2026-07-27.md) | ⚠️ **Not yet moved in.** That doc's own `DEFERRED, with reasons` block is still the authority on the 11 open items (C2, M2, M4/M13 lowerings, M6, M8, N2, N6, N7, N8, N16, M14's SD0011 span plumbing). Three came **off** that list on 2026-07-31: ~~N5's warning half~~ (closed as `SD0104`, **A11** below), ~~N17's Android include-comparer half~~ (closed, **A12** below), and ~~M12's Linux case-insensitive fallback~~ (closed as *rejected* in the same pass, reasoning recorded in the bug-hunt doc and `project_decisions.md`). **The doc still cannot move to `plan/DONE/` until the rest are migrated here** — filing it as done while it is their sole home would bury them. Migrating them is this phase's stated job ("so no phase sits open at 95% for 1-2 items"); it needs one focused pass to give each item a scope and a done bar, not a bulk paste. |
| Browser diagnostics squiggle confirmation | [Phase 38](DONE/PHASE-38-wasm-compile-diagnostics.md) | 🟢 Implemented; only the in-browser confirmation rung left |
| DeferredSprite GL MRT render proof (GAP-2) — ✅ done 2026-07-29 (A2) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | Closed at compile + structural-match; render rung left |
| Apos.Shapes render-proof (Option B) | [Phase 49](DONE/PHASE-49-apos-shapes-regression-corpus.md) | Option A shipped; Option B render-proof decision-gated |
| GL macro-defined techniques (GAP-1 / GL) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | DX + FNA closed; GL faithfulness-blocked |
| DX12 / DXIL render-validation (Area C) — ➡️ promoted 2026-07-18 to [Phase 52](DONE/PHASE-52-monogame-3.8.5-support.md) Area D, split 2026-07-23 to [Phase 54](DONE/PHASE-54-dx12-dxil-backend.md) | [Phase 35](DONE/PHASE-35-forward-version-support.md) | New-backend build (not a render-validation rung); see B1 |
| Un-park Vulkan trigger (Area D) — ✅ done 2026-07-18 via [Phase 32](DONE/PHASE-32-vulkan-backend.md) | [Phase 35](DONE/PHASE-35-forward-version-support.md) | ext-blocked on MonoGame 3.8.5 stable (since shipped; see B2) |
| DX/FNA/KNI-DX render-in-CI gates | [Phase 44](DONE/PHASE-44-validation-breadth-and-matrix-coverage.md) | Effectively done; ext-blocked on a WARP CI runner |
| `d3dcompiler_47` vs `fxc.exe` DXBC delta study (OQ#2) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | Deferred, low-value |
| ShaderToy sample + runtime-helper migration to `samples/` (**A4** — ✅ done 2026-07-31) | [Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md) | Core shipped (NuGet since 0.9.0); the sample-migration appendix stayed Planned (moved 2026-07-18) |
| CLI `.glsl`-route render-gate fixtures | [Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md) | Deferred in the CLI appendix; tracked in validation-matrix §8 (moved 2026-07-18) |

---

## A. Internally actionable (no external dependency)

### A1 — Browser compile-diagnostics squiggle confirmation rung
*From Phase 38 (WASM compile diagnostics).* The code is done and headless-verified: bad
HLSL in the browser now yields `file:line:col: error: message` (not the opaque
`[object WebAssembly.Exception]`), with **G1 byte-identity 10/10** preserved and the new G2
diagnostics gate green. The C# reformatter is desktop-unit-tested and the format matches.

**Remaining (verbatim from Phase 38):** *"run the sample in a real browser and confirm the
squiggle/gutter lands on the offending line (the C# reformatter is desktop-unit-tested and
the format matches, so this is a confirmation rung, not a risk)."*

**Done = ** the `samples/ShaderFiddle.Web` sample, run in a real KNI/Blazor browser, shows
the diagnostic squiggle/gutter on the offending line for a deliberately-broken `.fx`.

### A2 — ✅ DONE (2026-07-29) — DeferredSprite GL true 2-attachment MRT render proof (Phase 41 GAP-2)
*From Phase 41.* GAP-2 (Nez DeferredSprite failed on GL with `Semantic COLOR is invalid`)
was **closed at compile + golden-structural-match (2026-06-27)** via the GL-only
`GlStructOutputColorRewriter` (DX byte-identical) plus the true-MRT `gl_FragData[0]` slot-0
fix. The render rung is now closed too.

**Was remaining (verbatim from Phase 41):** *"a true MRT render proof (bind 2 render targets,
draw, read back BOTH attachments, compare to mgfxc) needs a NEW render driver — the current
GL render gates are single-target only."*

**Landed.** [`validation/DeferredSpriteMrtGl`](../validation/DeferredSpriteMrtGl/) binds two
render targets on real MonoGame DesktopGL, draws `DeferredSprite.fx`, reads **both**
attachments back, and pixel-diffs each against the real `mgfxc` `OpenGL` golden
(`tests/fixtures/golden/OpenGL/DeferredSprite.mgfx`): **maxd 0 on both attachments**, on the
first run. Wired into `validation-render.yml`, so it runs in CI on Mesa llvmpipe alongside the
other in-process GL gates. Validation-only — no `src/` change, no output-byte churn.

**Why this needed its own driver, restated so it is not re-litigated:** every other GL gate in
the repo binds ONE render target. A single-target gate cannot distinguish "`COLOR1` reached
attachment 1" from "the second output went nowhere", and neither can a structural match,
because the second output lives in the emitted GLSL (`gl_FragData[1]`) and not in the `.mgfx`
record tables. That is exactly why Phase 41 left the rung open rather than calling the
structural match sufficient.

**How the scene was built to discriminate.** The sprite is drawn **1:1 texel-to-pixel**, so no
filter and no build's baked sampler state can shift a boundary; its left half is opaque and its
right half sits below `_alphaCutoff`, so the shader's own `clip()` splits the surface in a
single draw. The normal map is a different colour AND a different alpha, run through the
shader's own arithmetic (`normal.a *= _alphaAsSelfIllumination * _selfIlluminationPower`, 0.25)
to land on exactly 50 — a value neither the clear colour nor a copy of attachment 0 can
produce. Both targets clear to **transparent** black, so "never written" is a nameable outcome
rather than something that blends into a correct result. Arm A reports which of five failure
modes a wrong picture corresponds to (attachment 1 unwritten / attachment 1 == attachment 0 /
slots swapped / `clip()` discarded only attachment 0 / `clip()` never fired) plus a non-vacuity
count, rather than just "wrong".

**The mutation check earned the absolute arm its place.** Binding one render target instead of
two leaves the mgfxc pixel-diff reporting **maxd 0 and OK** — both sides are broken
*identically*, so the diff cannot see it — while arm A turns red and names it. A gate built
only on "same picture as mgfxc" would have passed the mutation. Arm A therefore also runs
against the golden itself, so a future diff failure is attributed to the right side instead of
blaming the candidate by default (Phase 51 A3 already found one case where the `mgfxc` GL
golden, not ShadowDusk, was the wrong one).

**MRT stays a desktop-only rung, by platform limit rather than by omission.** KNI's WebGL
Blazor backend exposes no public multi-render-target binding API to a consumer — the same shape
as the MonoGame `Texture2DArray` limitation — so there is no browser arm to add.

Background: [structural-divergence-matrix.md](PHASE-41-appendix/structural-divergence-matrix.md).

### A3 — ✅ CLOSED (2026-07-23) — Apos.Shapes render-proof (Phase 49 Option B, decision-gated stretch)
*From Phase 49.* Option A (the Gum / Apos.Shapes compile-regression corpus) shipped
2026-06-27. Option B was a real but owner-decision-gated render-proof stretch — now **closed
on every target ShadowDusk supports for this shader.**

**DX slice DONE (2026-07-23).** `validation/VsDrivenDx -- apos` renders `apos-shapes-sm6.fx`
(the current-upstream revision, also used by the Vulkan proof — its DirectX macro set
`{MGFX, HLSL, SM4}` takes the fixture's `#else` branch, `vs_4_0`/`ps_4_0` legacy `sampler`/
`tex2D` syntax, so no separate DX fixture variant was needed) through a bespoke 13-element
vertex-buffer harness, pixel-diffed against the real `mgfxc` DirectX_11 golden
(`tests/fixtures/golden/DirectX_11/apos-shapes-sm6.mgfx`) on BOTH ShadowDusk DXBC backends
(`d3dcompiler_47` oracle and `vkd3d-shader`): **maxd 0** on both, wired into
`run-windows-render-gates.ps1`.

**Vulkan slice DONE (2026-07-22, issue #145).** `validation/VsDrivenVulkan -- apos` renders the
same `apos-shapes-sm6.fx` on real MonoGame 3.8.5 DesktopVK, pixel-diffed against the checked-in
`mgfxc 3.8.5` Vulkan golden: **maxd 0**, with a non-vacuity check.

**GL slice DONE (2026-07-23).** `validation/VsDriven -- apos` renders Apos.Shapes on real
MonoGame DesktopGL — but deliberately NOT the same fixture the DX/Vulkan slices use.
`apos-shapes-sm6.fx` compiles fine on GL, but its real `mgfxc /Profile:OpenGL` golden renders
completely wrong (maxd 255, solid black): reverse-engineering the golden's embedded GLSL found
MojoShader's translation of that revision's fxc-optimized shape dispatch hinges on a
`-0.0 >= 0.0` comparison this GPU/driver evaluates false, permanently selecting a hard-zeroed
color branch — a confirmed mgfxc/MojoShader bug (independently verified by recomputing the
shader's OkLab math in double precision, which matches ShadowDusk's candidate, not the golden),
not a ShadowDusk defect. `apos-shapes.fx` (the Phase 49 pin — upstream's older, non-fxc-SM3-
optimizer-mangled revision: plain sequential shape dispatch, Cantor-pair color packing) renders
correctly: pixel-diffed against the real `mgfxc` OpenGL golden
(`tests/fixtures/golden/OpenGL/apos-shapes.mgfx`) through its own 10-element vertex-buffer
harness at **maxd 2/255** (documented transcendental-math GLSL-dialect drift on the shader's
OkLab round-trip, not a structural mismatch), wired into `run-windows-render-gates.ps1`. See
`tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md` for the full trace.

**FNA is permanently excluded, not a remaining rung.** Apos.Shapes' instruction count exceeds
vkd3d-shader's Shader Model 3 ceiling (`fx_2_0`/SM3, a real DirectX-9-era instruction-slot
limit) — a legitimate, documented rejection (`SD0305`), the same honest shader-model ceiling
the other vendored Apos.Shapes revisions hit. There is no further FNA work to schedule here.

**A distinct, real gap surfaced while closing this GL slice — FIXED the same day, tracked in its
own doc, NOT as a Phase 51 item:
[`DONE/ISSUE-149-gl-isnan-versionless-glsl.md`](DONE/ISSUE-149-gl-isnan-versionless-glsl.md).**
The GL candidate GLSL for `apos-shapes.fx` — the exact fixture this render-proof uses — emitted
`isnan()` with no `#version` directive, which Apple's strict GL compiler rejects. This never
invalidated the maxd 2/255 render-proof above (that pixel-diff is real, on this repo's established
Windows/Linux GL evidence ladder), and it was not caused by this work — it was pre-existing in
the GL backend's NaN-lowering and was simply invisible on the lenient desktop drivers every GL
render gate runs on. It affected any GL shader using `min`/`max`/`clamp`, not just Apos.Shapes,
so it got the same standalone-doc treatment issues #70 and #145 did (not a Phase 51 sub-item),
and is now fixed by defaulting SPIRV-Cross's `RELAX_NAN_CHECKS` option on for the OpenGL profile.

**Done = ** `apos-shapes-sm6.fx` rendered pixel-equivalent to `mgfxc` in real MonoGame DX and
Vulkan (maxd 0 on both), and `apos-shapes.fx` rendered pixel-equivalent to `mgfxc` in real
MonoGame GL (maxd 2/255, documented drift) — all three behind the Windows render gate. FNA is
excluded by a real SM3 instruction-count ceiling, not left open-ended.

**Depth follow-on (2026-07-23):** this render-proof exercises exactly one hand-built shape
(a circle) per backend via a hand-rolled vertex struct. Expanding to the full Apos.Shapes
shape/feature surface, using the real `Apos.Shapes` NuGet package's `ShapeBatch` effect-injection
constructor as both harness and golden, is tracked as its own phase:
[Phase 55](DONE/PHASE-55-apos-shapes-shape-gallery-render-proof.md).

### A4 — ✅ DONE (2026-07-31) — ShaderToy sample + runtime-helper migration to `samples/` (ex-47)
*From Phase 47 (moved 2026-07-18, at the 0.12.0 release docs audit).* The core promotion shipped
(the `ShadowDusk.ShaderToy` library is in-solution and published as a NuGet since 0.9.0), but the
MonoGame-dependent runtime helper + interactive viewer sample never moved out of
`tools/shadertoy2fx/` — the [sample-migration appendix](DONE/PHASE-47-appendix/sample-migration.md)
stayed **Planned**. Anchor constraint (verbatim from the appendix): *"No code in this appendix may
end up in a shipped `ShadowDusk.*` package."*

**Was done = ** the runtime helper + interactive ShaderToy viewer sample live under `samples/` per
the appendix, `tools/shadertoy2fx/` keeps only the out-of-band render-proof driver (or is retired),
and `NoMonoGameInProductLibrariesTests` stays green (no shipped library gains a MonoGame
dependency).

**Landed.** [`samples/ShaderToyViewer/`](../samples/ShaderToyViewer/) now holds the interactive
viewer (appendix D1) with the `ShaderToyEffect` helper folded in as `Runtime/ShaderToyEffect.cs`
(D2) — no separate `ShadowDusk.ShaderToy.Runtime` project, so the *only* projects referencing
MonoGame are under `samples/`, `validation/`, and the out-of-band render-proof driver, never under
`src/`. Namespaces moved with the code (`ShadowDusk.ShaderToy.Sample` → `ShadowDusk.ShaderToyViewer`,
`ShadowDusk.ShaderToy.Runtime` → `ShadowDusk.ShaderToyViewer.Runtime`), disambiguating the sample
from the product library that owns the `ShadowDusk.ShaderToy` name. Reference graph is D4's:
`src/ShadowDusk.ShaderToy` + `src/ShadowDusk.Compiler` (natives transitive) +
`MonoGame.Framework.DesktopGL` on the central pin. The sample stays out of `ShadowDusk.slnx` (D5),
its four bundled `.glsl` and the committed eyeball PNGs moved with it, and the `.gitignore` entry
for regenerable `output/*.fx|*.mgfx` was repointed (D7). Everything was `git mv`'d, so history
follows. Verified: full solution + sample + render-proof build 0 warnings;
`dotnet run --project samples/ShaderToyViewer -- --smoke` **4/4 PASS**, regenerating the committed
PNGs **byte-identically**; `NoMonoGameInProductLibrariesTests` green on `net8.0` and `net10.0`.
Zero compiler-output bytes moved (relocation only).

**Two deliberate departures from the letter of the "Done" bar, both recorded rather than guessed
at.** (1) **The render-proof driver stayed at `tools/shadertoy2fx/render-proof/`**, taking the
appendix's own R1 deferral rather than D3's relocation to `validation/`; it changes no product
behavior either way, and moving a GPU-only driver that no gate script runs is churn better spent
when someone is actually re-proving it. Its dependency on the helper is now the D3-prescribed
one-file `<Compile Include=…ShaderToyEffect.cs />` source link into the sample. (2) **The standalone
PoC CLI `tools/shadertoy2fx/src/ShadowDusk.ShaderToy.Cli/` stayed too**, so `tools/shadertoy2fx/`
does *not* end up holding "only the render-proof driver". That phrasing turns out to be
unachievable without dropping a real capability: the PoC CLI is the **only** command-line entry
point to the converter's `--multipass` batch mode (the product `ShadowDuskCLI` takes a single
`.glsl` and has no `--multipass` flag), and the appendix explicitly scopes its fate out
("main plan's call"). Retiring it is a separate, still-undecided call. Both `tools/shadertoy2fx/`
READMEs and `docs/repository-layout.md` now say exactly what remains there and why.

### A5 — CLI `.glsl`-route render-gate fixtures (ex-47 CLI appendix)
*From Phase 47 (moved 2026-07-18).* The CLI `.glsl` input is implemented + integration-tested
(`CliShaderToyInputTest`: GL/DX compile, located rejects, CLI ≡ Convert+pipeline byte-identity), but
the **render-gate fixture entries** for the `.glsl` route (Windows DX/FNA + GL gates) were deferred —
recorded in the [CLI appendix](DONE/PHASE-47-appendix/cli-shadertoy-input.md) and
`docs/validation-matrix.md` §8. (`--multipass` batch mode is a separate recorded deferral in the
same appendix, not part of this rung.)

**Done = ** at least one `.glsl`-route fixture renders through the Windows render gates (and the GL
CI gates where applicable), pinning the converted-shader path at rung 4.

#### ✅ CLOSED for OpenGL (2026-07-29)

[`validation/ShaderToyRouteGl`](../validation/ShaderToyRouteGl/) runs the real frontend in process
(`ShaderToyConverter.Convert` on `tests/fixtures/shaders/shadertoy/GradientToy.glsl`), compiles the
converted `.fx` through the real pipeline, and pixel-diffs it against **`mgfxc`'s own build of that
same converted `.fx`** on real MonoGame DesktopGL: **maxd 0**. Wired into `validation-render.yml`, so
it runs in CI on Mesa llvmpipe.

**There IS an mgfxc oracle here, and the distinction matters.** `docs/validation-matrix.md` §8 says
there is no `mgfxc` oracle for the ShaderToy route — that is true of the **input** (a `.glsl` is not
an mgfxc input, so on the input side there is nothing to be equivalent *to*), but the converter's
**output** is ordinary HLSL `.fx`, which mgfxc compiles like any other. So the route's downstream
half can be, and now is, held to the real product bar. The claim is deliberately narrow: *a shader
the converter produced compiles to the same picture as the reference compiler's build of the same
`.fx`.* Whether the converter faithfully reproduces the original ShaderToy is the separate
fidelity axis with its own oracle, and this gate does not touch it.

**The golden is pinned to a specific `.fx`, on purpose.** The golden is mgfxc's build of one file; if
converter output drifted with nothing checking, the golden would quietly stop corresponding to what
the route emits and the diff would be comparing two different shaders while still reporting a number.
So the driver asserts the in-process conversion still equals the committed
`tests/fixtures/shaders/shadertoy/GradientToy.fx` **before** it renders anything, and writes the
current output to the results directory on mismatch, with the regeneration command in the message.

**Two harness facts that are load-bearing:**

- **`SpriteBatch` cannot drive this effect.** The converted effect is VS-driven and its generated
  header states the host contract outright ("the host draws a quad/triangle whose POSITION is already
  in NDC"). `SpriteBatch` feeds screen-space positions expecting the effect to apply its own
  transform, and this VS applies none, so the quad lands outside the frustum and nothing renders. The
  driver draws a real NDC fullscreen quad with a vertex declaration of exactly `POSITION` and nothing
  else — which is also what an actual consumer of this route does.
- **The absolute arm asserts the gradient's SHAPE, not its orientation** (|variation| per axis, plus
  `B==0`/`A==255`). ShaderToy's Y origin is bottom-left and the converter flips it; that is the
  fidelity gate's claim to make, not this one's. Asserting shape still refuses a flat, black, or
  single-axis frame, so "it rendered nothing" cannot pass as agreement. The golden gets the same
  check, so a diff failure is attributed to the right side.

**DirectX: unblocked and wired (2026-07-31, A10).** The blocker was real, not a harness limitation:
the converter's own output was not compilable by mgfxc for `DirectX_11`, so no golden could exist.
A10 fixed the emission, a `DirectX_11` golden is committed for the pinned fixture, and
[`validation/ShaderToyRouteDx`](../validation/ShaderToyRouteDx/) is the DirectX arm of this gate,
default-ON in `validation/run-windows-render-gates.ps1`. **It has not been run yet** — it needs the
Windows GPU box, so the maxd number is still owed. The **OpenGL** arm is unaffected by that change:
ShadowDusk's GL bytes for this fixture are byte-identical before and after, and the mgfxc `OpenGL`
golden regenerated byte-for-byte identical.

**Still open:** the Windows **FNA** render-gate fixture for this route.

**Why this is worth more than it looks (2026-07-28).** Running `tools/shadertoy2fx/render-proof`
during the Phase 52 full-test sweep found it had been **dead since Phase 47** and nobody knew:
first it exited immediately on a corpus path the Phase-47 promotion had moved, and once that was
fixed it **hung forever** on a stdout-before-stderr pipe deadlock in all four of its child-process
helpers — latent until Phase 53 made warnings print by default and a corpus shader started emitting
more `SD0402` text than the pipe buffer holds. Both are fixed, but they are a textbook case of
`CLAUDE.md`'s *"a check nobody remembers to run does not exist"*: this driver is deliberately outside
`ShadowDusk.slnx`, so no suite and no gate touched it for over a month. Wiring the `.glsl` route into
a real gate is what stops the next month of silent rot.

**With both fixed, the gate is green and broader than when it was last run:
`render-proof --fidelity` reports 53/53 shaders MATCH (0 diverged, 0 errored)** — mean abs diff
0.00-0.11/255, >= 99.9% of pixels within tolerance on every shader — against Phase 47's recorded
46/46. The extra seven are corpus growth since, and they pass too; the converter itself never
regressed.

### A6 — `#include` path containment for untrusted-shader hosts (from the 2026-07-23 review)

*Raised by the security review of the Phase 53 error-visibility work.* Two pre-existing pieces
became a chain once diagnostics started printing verbatim by default:

- `FileSystemIncludeResolver.Resolve` canonicalizes an include path
  (`Path.GetFullPath(Path.Combine(dir, includePath))`) but **never bounds-checks it**, so `..`
  traversal and absolute paths both resolve; and
- the preprocessor injects the resolved **absolute** path as `#line`, so DXC attributes and
  **echoes the offending source line**, which the CLI, the validation report, and ShaderFiddle
  now print.

Net effect: a shader can disclose the existence and one-or-two lines of content of any readable
file into whatever consumes the diagnostics. `#include "/any/path"` is additionally a reliable
file-existence oracle (`SD0001` vs a compile error).

**Scope — who this actually affects.** Not the ordinary consumer compiling their own shaders
(reading your own files is a non-event), and not the browser/WASM host (empty VFS, synthetic
source names). It matters for a **trusted build compiling an untrusted shader**: this repo's own
CI compiling PR-supplied fixtures, or any hosted compile service.

**Why it is not simply fixed by restricting includes.** `mgfxc`/`fxc` resolve relative includes
without containment, so `#include "../shared/common.fxh"` is legal, common in real projects, and
part of the drop-in promise. Restricting it by default would break working shaders and diverge
from the reference compiler — rejected under the seamless rule.

**Candidate directions** (either alone breaks the chain; pick one when this is scoped):
1. An **opt-in** containment option (bound includes to the source directory plus the explicit
   `/I` paths) — a non-required hardening escape hatch, never the path to correct output.
2. Filter the raw-diagnostic block so **echoed source lines from files other than the primary
   source** are dropped, keeping the compiler's diagnostic text verbatim but not its file
   contents.

**Done = ** an untrusted `.fx` can no longer surface another file's contents through diagnostics
on a host that opts in, with `mgfxc`-parity include resolution unchanged by default, plus a
regression test using a traversing `#include`.

*Interim mitigation (shipped 2026-07-23):* the exposure is documented on `ShaderError.RawDiagnostics`
— it may carry host absolute paths and echoed source, and should not be forwarded verbatim to
untrusted callers.

---

### A7 — ✅ DONE (2026-07-29) — OpenGL sampler records per (texture, sampler) PAIR

**Landed.** The GL sampler table is keyed on the (texture, sampler) pairs SPIRV-Cross folds into
combined samplers, in **its** declaration order, derived from the SPIR-V by the pure-managed
`SpirvCombinedSamplerPairs` (host-independent, so CLI and browser bytes agree). `SD0215` and
`SD0216` are retired; `SD0217` covers the unmodelled shapes plus an internal cross-check against
the sampler uniforms the emitted GLSL actually declares. Rung 4 is
[`validation/SamplerPairsGl`](../validation/SamplerPairsGl/) on real MonoGame DesktopGL, wired
into `validation-render.yml`. **The scope grew twice while closing it** — see the two findings
recorded below; both were silent, undiagnosed bugs, and one of them was in DirectX 12.

Kept in full below because the reasoning (why not a native P/Invoke, why the ordering rule is what
it is, why the parameter naming was NOT changed) is the durable part.

*Original framing: `mgfxc` compiles this and we do not — a fidelity gap on our side, not a
reference-compiler bug.*

Several textures read through **one shared `SamplerState`** (the classic diffuse+lightmap shape)
is ordinary HLSL. SPIRV-Cross expands it into one **combined sampler per (texture, sampler)
pair**, so the emitted GLSL declares `ps_s0` AND `ps_s1`, and `MonoGameGlslRewriter` numbers them
in declaration order. ShadowDusk's GL sampler table is keyed on the reflected **samplers**, of
which there is only one, so it emitted a single record: `ps_s1` never received a texture unit and
silently sampled unit 0. `mgfxc`'s own golden for this shape
(`tests/fixtures/golden/OpenGL/PenumbraTexture.mgfx`) carries **two** records, `ps_s0`/`ps_s1`,
with parameters named `TextureSampler+DiffuseMap` / `TextureSampler+Lightmap` — MojoShader's
`<sampler>+<texture>` naming, which is literally the pair identity.

**Interim (shipped 2026-07-27):** `SD0216` turns that silent mis-bind into a loud compile error
naming the one-line workaround (give each texture its own `SamplerState`, which is
behavior-identical and produces exactly `mgfxc`'s structure). DirectX and DirectX 12 are already
correct — they key on the reflected textures, as `mgfxc` does.

**Why it was not fixed in the same change.** The pair list exists but is discarded: the pipeline
calls `spvc_compiler_build_combined_image_samplers` for its side effect and never reads
`spvc_compiler_get_combined_image_samplers`. Adding that P/Invoke would fix the desktop path
only — **`src/ShadowDusk.Wasm/wwwroot/spirv-cross/spirv-cross.wasm` exports exactly 11 functions
and that is not one of them**, and it is an out-of-band emscripten build. A desktop-only fix
would therefore break the CLI-vs-WASM byte-identity promise (`project_facts.md`) for this shape,
which is why this needs its own scoped effort rather than a same-PR add-on.

**Candidate directions:**
1. **Pure-managed pair extraction** (preferred, and the precedent this project has already set
   twice with `RdefReader` and `SpirvReflector`): derive the (image, sampler) pairs from the
   SPIR-V ourselves by walking `OpSampledImage` back through `OpLoad` to the variables. Host-
   independent by construction, so byte-identity holds. **The hard part is not extraction, it is
   reproducing SPIRV-Cross's combined-sampler DECLARATION ORDER exactly** — `ps_s{k}` must name
   the uniform the GLSL actually declares, and getting the order subtly wrong silently binds the
   wrong texture, which is the very bug class this item exists to close. Pin it against real
   SPIRV-Cross output before trusting it.
2. Rebuild `spirv-cross.wasm` with the pairs API exported and use the native call on both hosts.
   Removes the ordering guesswork entirely, at the cost of an emscripten rebuild and a new pinned
   artifact.

#### Ground truth established 2026-07-29 (direction 1 confirmed viable; scope grew)

Read from the pinned SPIRV-Cross source (`.wasm-build/spirv-cross-src`, tag
`vulkan-sdk-1.4.335.0`, the same tree the WASM module is built from) and then confirmed against
real transpiler output. **The ordering rule is not a guess:**

- **`Compiler::build_combined_image_samplers`** (`spirv_cross.cpp:3261`) runs
  `traverse_all_reachable_opcodes` from the single entry-point function: **blocks in binary
  order, ops in binary order, recursing into each `OpFunctionCall` target** and pushing a
  parameter→argument remapping for the callee's scope.
- The only trigger is **`OpSampledImage`** (`spirv_cross.cpp:3124`). Its image/sampler operands
  are resolved to global variables through `remap_parameter` → `maybe_get_backing_variable`
  (i.e. back through `OpLoad`/`OpAccessChain`), then the `(image, sampler)` pair is appended to
  `combined_image_samplers` **if not already present**. So the order is **first-use order,
  deduplicated** — nothing to do with declaration order, bind slots, or binding numbers.
- The synthesized variable ids come from `ir.increase_bound_by(2)`, so they are monotonic in
  first-use order and sort **after** every original module id; `CompilerGLSL::emit_resources`
  walks variables in id order and **skips every separate image/sampler** when
  `vulkan_semantics` is off (`spirv_glsl.cpp:3893`). Hence **emitted declaration order ==
  first-use pair order**, exactly.
- **Naming is a red herring.** SPIRV-Cross's `SPIRV_Cross_Combined<Image><Sampler>` name is
  applied by its **CLI** (`main.cpp`), not by `build_combined_image_samplers`, so through the C
  API the combined uniforms come out as bare `_<id>` (`uniform sampler2D _40;`) and carry **no
  pair identity at all**. The emitted GLSL therefore cannot be used to recover which pair each
  declaration is — the extraction must be done from the SPIR-V.

Empirically confirmed with a three-texture/two-sampler probe whose textures have **different
dimensions**, so the emitted decl kinds (`samplerCube` / `sampler2D` / `sampler3D`) identify each
pair unambiguously: the cube texture, declared *second* in HLSL but sampled *first*, is emitted
first. First-use order, confirmed.

**Two findings that grow the scope beyond the shared-sampler shape:**

1. **A worse, silent sibling bug that `SD0216` cannot see.** Two textures + two samplers sampled
   in **reverse declaration order** produces matching counts (2 GLSL uniforms, 2 records), so the
   `SD0216` count check passes — but the slot-keyed table assigns `ps_s0` the `t0`/`s0` texture
   while the GLSL's `ps_s0` is the *first-sampled* pair. Both the **texture parameter** and the
   **sampler-type byte** come out swapped, with **no diagnostic**. Probed with a
   `Texture2D` + `TextureCube` pair: the GLSL declares `ps_s0` as `samplerCube` while the record
   claims `ps_s0` is `Type=0` (2D) pointing at the 2D texture. A 2D texture would be bound to a
   cube sampler unit. The same mis-numbering hits any shader mixing legacy `sampler2D` and modern
   `Texture2D`+`SamplerState` declarations. **This is the real reason the table must be keyed on
   the pair list rather than on either reflected list.**
2. **`DirectX12` is NOT already correct** — the claim above (and in `docs/validation-matrix.md`)
   is wrong. `bool directX` at `CompilationPipeline.cs:316` is `Target == PlatformTarget.DirectX`
   only, so **DX12 falls through to the sampler-keyed branch**, and the shared-sampler shape
   emits **one** record on DX12: `Lightmap` never binds, silently, with no diagnostic (DX11 emits
   two and is correct). Vulkan is fine (loud `SD0028` for the shared shape, correct records
   otherwise).

**No legacy/pre-combined case exists on the GL path.** The pre-parser's `RewriteToSm4` mode
(every target except FNA) rewrites `sampler2D`/`tex2D` into `SamplerState` +
`<texture>.Sample(...)` before DXC ever sees the source, and SM6 HLSL has no combined sampler
type, so **every** GLSL sampler uniform on the GL path is a synthesized pair. A module-level
`OpTypeSampledImage` variable is therefore an input shape we do not model and must fail loudly on
rather than mis-number.

**`SD0215` also becomes unnecessary.** It rejects sampler registers that are not contiguous from
`s0`, purely because the old table named `ps_s{samp.BindSlot}`. Once the record index is the
pair's declaration position, bind slots are never consulted for GL naming and a
`register(s3)`-only shader numbers correctly.

**Done = ** a `.fx` reading N textures through one shared `SamplerState` compiles for OpenGL,
emits N records naming every `ps_s{k}` the GLSL declares with the right texture parameter and
baked state per pair, is render-proven against the `mgfxc` OpenGL golden, produces identical
bytes on the CLI and in the browser, and `SD0216` is deleted as unnecessary. Note the separate,
pre-existing parameter-naming divergence in the same area (`mgfxc` emits `TextureSampler+DiffuseMap`
where we emit `DiffuseMap`); decide deliberately whether to match it, since changing parameter
names breaks existing consumers' `Parameters[...]` lookups.

#### How it was closed (2026-07-29)

**Direction 1 (pure-managed extraction), as preferred.**
[`SpirvCombinedSamplerPairs`](../src/ShadowDusk.Core/Reflection/SpirvCombinedSamplerPairs.cs)
reproduces the traversal transcribed above and returns the pairs keyed by HLSL **name** (the key
both reflection paths agree on — the DXIL oracle and `SpirvReflector` assign different raw binding
numbers, so a binding-keyed join would not be host-independent). Direction 2 (rebuilding
`spirv-cross.wasm` with the pairs API exported) was **not needed**, so the emscripten artifact is
untouched.

The GL branch of `CompilationPipeline` now emits one record per pair: `Name = ps_s{k}`,
`TextureSlot = SamplerSlot = k` (the record index **is** the GL texture unit — each pair needs its
own unit even when several pairs share a texture or a sampler), `Type` from the reflected texture's
dimension (one source, so it stays byte-transparent across hosts), `Parameter` = the pair's texture,
`State` = the pair's **sampler** half.

**Diagnostics.** `SD0215` and `SD0216` are retired with their numbers marked do-not-reuse in
`docs/error-codes.md`; both of their tests were rewritten as positive assertions. `SD0217` covers
the shapes the model does not cover, plus an internal cross-check of the derived pair count against
the sampler uniforms the emitted GLSL actually declares — that cross-check is the thing that would
catch a drift from the pinned SPIRV-Cross instead of shipping a mis-bound table.

**Evidence.** Full suite green on `net8.0` and `net10.0` with **no corpus shader's bytes moved**
(the 1:1 texture/sampler case is byte-identical under the old and new rules, which is why the whole
golden corpus is unchanged). Seven new regression tests in `ReviewRegressionTests` pin: the
shared-sampler shape against `mgfxc`'s own record structure; reverse-use-order with a
`Texture2D`+`TextureCube` pair (asserting the record type byte against the kind the GLSL declares);
the one-texture-two-samplers baked-state order; mixed legacy/modern declarations; sampling inside
called functions (the `OpFunctionCall` remapping branch); a four-pair 2D/Cube/3D/2D ordering
discriminator checked position-by-position against the emitted GLSL; explicit/sparse sampler
registers now compiling; and the DX12 shared-sampler record count.

**Rung 4:** [`validation/SamplerPairsGl`](../validation/SamplerPairsGl/) — both arms pass on real
MonoGame DesktopGL at **maxd 0 against the real `mgfxc` `OpenGL` goldens**, wired into
`validation-render.yml` and `docs/validation-matrix.md` §6. See that §6 row for what each arm
discriminates and the two measured harness details that are load-bearing.

**A third finding, from generating those goldens: `mgfxc` numbers `ps_s{k}` by SAMPLER REGISTER,
SPIRV-Cross by FIRST USE.** They coincide whenever use order matches register order — which is the
entire existing corpus, hence zero golden churn — but `SamplerPairMirror.fx` is built to make them
disagree, and there `mgfxc`'s golden makes `ps_s0` the Linear pair while ours makes it the Point
pair. **ShadowDusk must follow SPIRV-Cross**, because the record has to name the uniform *our own*
GLSL declares; each build is internally consistent, so the renumbering is invisible in the picture,
and arm B exists to hold exactly that claim (maxd 0). Worth noting what this says about the old
code: it numbered by register *like `mgfxc`* while its GLSL was SPIRV-Cross-ordered — internally
inconsistent, which is the bug. Matching `mgfxc`'s numbering is not even possible without matching
its GLSL generator.

**Two structural-matrix cells are now expected-divergent and render-proven benign** (so a future
reader triaging `plan/PHASE-41-appendix/structural-divergence-matrix.md` does not re-open them):

| Cell | Divergence | Why it is fine |
|---|---|---|
| `SharedSamplerPair [OpenGL]` | object-class param shape: `mgfxc` names the texture params `TextureSampler+DiffuseMap` / `+Lightmap`, we name them `DiffuseMap` / `Lightmap` and add the `TextureSampler` sampler param | The deliberate parameter-naming decision above. Sampler *records* match `mgfxc` exactly; maxd 0. |
| `SamplerPairMirror [OpenGL]` | sampler slot 0/1 baked-state differs | The register-order vs first-use renumbering above. Each build self-consistent; maxd 0. |

Both DirectX_11 cells for these fixtures are structurally **clean**.

**The parameter-naming question was decided: do NOT adopt `<sampler>+<texture>`.** Recorded with
its reasoning in [`project_decisions.md`](../project_decisions.md). Short version: MonoGame resolves
a sampler's texture through the record's `Parameter` **index**, never the name, so the two spellings
are behaviorally identical; renaming would break every existing consumer's `Parameters["DiffuseMap"]`
lookup; and ours is the same name the DX/DX12/Vulkan/FNA targets use, whereas `mgfxc`'s spelling is
GL-only and makes parameter names backend-dependent. Note this divergence is **not** specific to the
shared-sampler shape — `mgfxc` uses `<sampler>+<texture>` for *every* modern-syntax GL shader (e.g.
its `PenumbraLight.mgfx` golden says `TextureSampler+Texture`), so it was never A7-shaped.

#### Two things found here and deliberately NOT fixed (each needs its own scope)

- **DX12's sampler-record NAME.** Real `mgfxc`'s `DirectX_12` goldens put the HLSL sampler name
  there (`SpriteTextureSampler`); we write the GL-style positional `ps_s{k}`. Harmless (DX12 binds
  through the resource table) and rung-4 proven in that form by Phase 54, but a real divergence
  from the golden. Changing it moves DX12 bytes and needs its own DX12 render re-proof.
- **One texture, two `SamplerState`s on DirectX/DirectX 12** emits one record, so only the slot-0
  sampler's baked state is applied and the second sampler's state is silently dropped. Pre-existing
  and unchanged here. The DX table is texture-keyed for good reason (it matches `mgfxc` and the
  DXBC resource table), so fixing this is not "key it on pairs too" — it needs a decision about
  what `mgfxc` itself does with that shape on DX first.

---

### A8 — Dependency currency: the two items with a real deadline (audited 2026-07-28)

*Not a phase tail — filed here because this is the de-facto backlog and these otherwise have no
home. Full audit recorded in [`project_facts.md`](../project_facts.md).*

The 2026-07-28 audit found **zero vulnerable and zero security-deprecated packages** anywhere, so
nothing is urgent. Two items nonetheless have a clock on them:

1. ~~**.NET 8 end of support, November 2026.**~~ ✅ **RESOLVED 2026-07-28** — the shipped libraries
   and all test projects now multi-target `net8.0;net10.0`, suite green on both (4762 tests),
   output byte-identical. Multi-targeting rather than a bump, because a `net10.0`-only package
   cannot be referenced from the `net8.0` projects most MonoGame/KNI games are. Remaining tail
   (small, not urgent): `ShadowDusk.Wasm` is still `net8.0-browser` only — adding
   `net10.0-browser` needs the .NET 10 wasm workload; `ShadowDusk.Cli` stays net8.0 as a dotnet
   tool (rolls forward onto newer runtimes).
2. ~~**vkd3d-shader is pinned at 1.17; upstream is at 2.0.**~~ ➡️ **PROMOTED 2026-07-28 to its own
   scoped phase: [Phase 56](PHASE-56-vkd3d-shader-2.0-upgrade.md).** The compatibility research is
   done and lives there (short version: "2.0" is a project-version bump, **not** an ABI break — the
   soname stays `1` and our interop needs no change; the real cost is that every DirectX and FNA
   byte moves, and the real upside is that the new register allocator may lift the `SD0305`
   rejections blocking `BasicEffect`/`SkinnedEffect` on FNA). Nothing left to track here.

**Deliberate non-bumps, recorded so they are not "fixed" by a well-meaning sweep:** `Vortice.*`
(3.3.4 *is* the DXC pin — same commit as our macOS/Android/WASM builds) and `Apos.Shapes` 0.7.7 (a
Phase 55 evidence pin). **`FluentAssertions` is no longer on this list — it is gone entirely**: the
"stay on the Apache-2.0 7.x line" holding pattern was resolved on 2026-07-30 by migrating the whole
suite to `Shouldly` (issue #171), because a frozen line only defers the problem. It is now BANNED
outright; see `project_facts.md`.

**Done = ** a decision recorded for the `net8.0` floor before November 2026, and vkd3d either
bumped-and-re-proven or explicitly deferred with a reason.

---

### A9 — Stop the `Integration Tests (ubuntu-latest)` test-host crash costing reruns (filed 2026-07-29)

*Not a phase tail — filed here for the same reason A8 was: this is the de-facto backlog and the
item otherwise has no home.*

The ubuntu integration lane intermittently aborts with *"Test host process crashed"* and passes on
rerun every time. Registered here so the *mitigation* is a scheduled decision instead of a note that
gets re-derived every time. The full observation lives in [`project_facts.md`](../project_facts.md).

#### Evidence re-read from the actual job logs (2026-07-29) — the recorded signature was wrong

Both occurrences whose logs are still retrievable (PR #170 run `30422038170`, PR #173 run
`30490728997`, attempt 1 of each) abort on **`ShadowDusk.Compiler.Tests`, the `net10.0` build
specifically** — the same assembly and the same TFM, twice, while its own `net8.0` build passes in
the same run.

Attribution is by elimination, not by eye: of the 14 assemblies, the **8** that carry no
`Category=Integration` tests each log *"No test matches the given testcase filter"* and are fully
accounted for; **5** of the 6 that do carry them log a `Passed!` summary; `Compiler.Tests (net10.0)`
logs **neither**. Identical in both runs.

**The previous reading — "the abort lands on an assembly with zero integration tests" — came from
log adjacency and does not survive checking.** The abort line happens to sit next to a *"Test run
for … GLSL.Tests/net8.0"* line, but 14 assemblies run concurrently and VSTest interleaves their
output; that same GLSL.Tests assembly logs its own no-match line a second later, so it plainly did
not crash.

**This invalidates both candidate mitigations as written:**

1. **Bound VSTest parallelism.** Still possible, but it was justified as a shot at generic resource
   exhaustion. Two runs failing on the *same assembly and TFM* is not that shape, so applying it now
   would be treating a symptom whose description has changed.
2. **Scope the integration filter to assemblies that carry `Category=Integration`.** Its entire
   rationale was *"the crash always lands on one of the wasted hosts"*, which is false.
   `Compiler.Tests` carries integration tests, so it would remain in any scoped list and **this
   change would not have prevented either observed crash.** It would still remove ~8 wasted hosts,
   but that is now a tidiness argument, not a fix.

#### What was fixed here (2026-07-29): the evidence was being destroyed

The reason this kept being re-derived from log-reading is that **the crashed assembly's `.trx` was
overwritten every time.** Every `dotnet test` invocation in `ci.yml` and `release.yml` passed a fixed
`--logger "trx;LogFileName=…"` while running 14 assemblies concurrently, so all of them wrote **one**
file — the logs are full of *"WARNING: Overwriting results file"* — and the uploaded artifact held
whichever assembly finished last. The one artifact anyone would want after a crash is the one
guaranteed to be clobbered.

All five invocations now use **`LogFilePrefix`**, which emits `<prefix>_<tfm>_<timestamp>.trx` per
assembly. Measured locally on the real solution: **14 files, zero overwrite warnings**, against 1
file and 13 overwrite warnings before. A guard step fails the integration job if only one `.trx`
lands, so a revert to `LogFileName` turns the lane red instead of silently losing 13 results again.

This is deliberately *not* claimed as a fix for the crash. It is what makes the next occurrence
diagnosable, and it repairs a real evidence defect that was costing every test-results artifact in
the repo, not just this lane's.

**Done = ** the ubuntu lane stops needing reruns, from a mitigation justified against the *measured*
signature above rather than the disproven one. **Next step:** on the next occurrence, read the
per-assembly `.trx` artifact (now preserved) for `Compiler.Tests (net10.0)` before choosing a
mitigation — the concentration on one assembly + TFM is the lead worth pulling, and a `net10.0`-only
failure of an assembly whose `net8.0` twin passes in the same run is not obviously environmental.

---

### A10 — ✅ DONE (2026-07-31) — A converted ShaderToy `.fx` compiled on DirectX for us and was REJECTED by `mgfxc` (found 2026-07-29)

*Surfaced by A5, and only because the new fixture joined the auto-globbed corpus. Filed rather than
fixed, because each half moves something with its own blast radius.*

Trying to generate a `DirectX_11` golden for the converted `GradientToy.fx` — so the fixture would be
golden-backed on both profiles like the rest of the corpus — failed on the reference compiler:

```
mgfxc /Profile:DirectX_11 GradientToy.fx
  Invalid profile 'vs_3_0'. Vertex shader 'VSMain' must be SM 4.0 level 9.1 or higher!
```

**ShadowDusk compiles the identical file for `DirectX_11` successfully** — measured through the CLI,
exit 0, 2033 bytes. The structural-divergence census records the same split: `shadertoy/GradientToy.fx
| DirectX_11 | PASS` on our side, no golden on mgfxc's.

**Two separable defects:**

1. **The converter's emitted profile header.** `ShaderToyConverter` writes `vs_3_0`/`ps_3_0` in *both*
   arms of its `#if OPENGL … #else … #endif`, so the DirectX arm asks for a profile below MonoGame's
   DirectX floor. The stock MonoGame convention — and what mgfxc requires — is
   `vs_4_0_level_9_1`/`ps_4_0_level_9_1` in the `#else` arm. As emitted, a converted shader is not
   `mgfxc`-compilable for DirectX at all, which also means the DX slice of A5 has no available oracle.
   The generated file's own header comment says the legacy style is "so the existing ShadowDusk
   pipeline can target OpenGL, DirectX, and FNA fx_2_0" — true of *our* pipeline, misleading about
   portability. **Blast radius:** changing the emission moves every ShaderToy converter golden and the
   A5 fixture + golden with them, so it wants its own change with a full converter-corpus regen.
2. **The reject-fidelity gap.** ShadowDusk accepts a vertex profile for the DirectX target that
   `mgfxc` refuses. This is exactly the class [Phase 48](DONE/PHASE-48-compile-target-profile-validation.md)
   exists for, but it is a different check from the two that phase shipped: `SD0013` catches an
   *unrecognized* profile and `SD0014` a cross-stage mismatch, whereas `vs_3_0` is a perfectly
   recognized profile that is simply **below the target's floor**. **Blast radius:** this turns a
   currently-succeeding compile into a loud rejection, so it needs the Phase 48 treatment — establish
   the floor per target from mgfxc's actual behavior, sweep the corpus for what would start failing,
   and land it as a deliberate reject-set change.

**Which is the real bug is itself a decision.** If (2) is fixed alone, the converter's own output stops
compiling for DirectX and the `.glsl` route loses a target — so (1) should almost certainly land first
or alongside. Do not fix (2) in isolation.

**Done = ** the converter emits a DirectX-valid profile header (its output compilable by real `mgfxc`
for `DirectX_11`), a DirectX golden exists for the A5 fixture and the DX arm of `ShaderToyRouteGl` is
wired, and ShadowDusk's DirectX target rejects sub-floor vertex profiles the way `mgfxc` does, with
the corpus sweep showing exactly what changed.

#### ✅ Landed 2026-07-31 — both halves, in one change, in the required order

**The oracle.** Everything below was measured against the **pinned** golden `mgfxc`:
`~/.nuget/packages/dotnet-mgcb/3.8.4.1/tools/net8.0/any/mgfxc.dll`, invoked through the `dotnet`
host exactly as `tools/compile-fixtures.ps1` resolves it (§6.1 of the validation matrix explains
why "the newest mgfxc on the machine" is the wrong answer in two different ways at once).

**(1) The converter's header — fixed, and NOT with the stock two-arm split.** `HarnessGenerator`
now emits:

```hlsl
#if SM4
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#else
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#endif
```

This item originally called for the stock `#if OPENGL … #else …` shape. **Gating on `SM4` instead
is deliberate**: the `#else` arm of the stock header also catches ShadowDusk's **FNA** target
(`PlatformMacros.For(Fna)` = `{FNA, HLSL, SM3}`, no `OPENGL`), whose `fx_2_0` output is capped at
Shader Model 3, so the stock split would have put a `*_4_0_level_9_1` profile in front of the FNA
path and made the converter's own tri-target claim false. `SM4` is the macro **MonoGame's own
DirectX_11 profile defines** (verified empirically: a probe with this exact header compiles under
mgfxc for `DirectX_11` *and* `OpenGL`), so DirectX gets the profile it requires while OpenGL, FNA,
Vulkan, and DX12 keep the legacy pair they had.

**(2) The reject-fidelity gap — fixed as `SD0015`, DirectX only.** The floor is
**empirical, not inferred**: every `KnownProfiles` entry was swept through the pinned mgfxc for
each of its three profiles. Two results make a `major >= 4` rule wrong in *both* directions and are
regression-locked in `ProfileRecognitionTests` and `examples/ExProfileSm6OnDirectX.fx`:

| mgfxc profile | Accepted set | Enforced here? |
|---|---|---|
| `DirectX_11` | `{vs,ps}_4_0_level_9_1`, `_4_0_level_9_3`, `_4_0`, `_4_1`, `_5_0` — **`_level_9_0` is refused, and so is every SM6 profile** | ✅ `SD0015` |
| `OpenGL` | SM ≤ 3 only (*"must be SM 3.0 or lower!"*) | ❌ left open, on purpose |
| `Vulkan` | `vs_6_0`/`ps_6_0` only (*"Requires vs_6_0"*) | ❌ left open, on purpose |
| `DirectX_12` | **unmeasured** — its reference compiler is mgfxc **3.8.5**, outside the golden oracle pin and not installed | ❌ deliberately not guessed |

The two open ones are the same class with their own reject-set blast radius (the OpenGL ceiling
alone would newly reject 7 root fixtures, every one of which real mgfxc already refuses). They are
recorded with their measurements in `docs/validation-matrix.md` §8.1 and a §7 gap row, so the next
person does not have to redo the probe.

**The corpus sweep — what actually changed.** Every fixture was compiled through the real CLI for
all five targets before and after. **20 cells flipped, all `DirectX_11`, all ACCEPT → REJECT
`SD0015`; nothing else moved, including every output byte:**

- 14 vendored Nez shaders (`Bevels`, `BloomCombine`, `BloomExtract`, `Crosshatch`, `GaussianBlur`,
  `HeatDistortion`, `Letterbox`, `Noise`, `PixelGlitch`, `Reflection`, `SpriteBlinkEffect`,
  `SpriteLines`, `Twist`, `Vignette`) — each verified rejected by real mgfxc first,
- `FnaMultiPassStates.fx`, `examples/ExIntUniformMember.fx`, `examples/ExMat3UniformMember.fx`,
- the 3 new fixtures (`ExProfileSm3OnDirectX`, `ExProfileSm3BothArms`, `ExProfileSm6OnDirectX`).

Two more cells changed only their *code*, not their verdict: `Gum/FnaSample-Shader.fx` and
`Gum/KniInCode-Shader.fx` on DirectX went from an unlabelled `X0000`/`E5005` further down the
pipeline to the precise `SD0015`.

**Goldens that moved, and the one that did not.** 81 ShaderToy converter goldens + 2 multipass
goldens moved (header text only). **No `.mgfx`/`.fxb` byte moved anywhere**: ShadowDusk's OpenGL
build of the pinned `GradientToy.fx` is byte-identical before and after (md5-checked against the
pre-change fixture), and mgfxc's own `OpenGL` golden regenerated byte-for-byte identical, so the
CI GL gate is untouched. A new `tests/fixtures/golden/DirectX_11/GradientToy.mgfx` was added;
`tools/compile-fixtures.ps1` now sweeps `shadertoy/` by default so it cannot be silently skipped.

**Fallout handled rather than papered over.** `FnaMultiPassStates.fx` was pinning **DirectX** bytes
in the cross-host byte-identity manifest for a shader mgfxc cannot build (`compile vs_2_0`); it was
dropped from that arm only, with the reason recorded at both corpus definitions. Four tests carried
inline `compile ps_3_0` sources that were being validated on DirectX for an unrelated subject; each
got the `SM4`-gated header with a note saying why it is load-bearing, so the test still tests what
it claims to.

**The render arm is WIRED BUT NOT YET RUN.** `validation/ShaderToyRouteDx` exists, builds, and is
default-ON in `validation/run-windows-render-gates.ps1` with a `docs/validation-matrix.md` §6 row.
Its first green run is still owed — it needs the Windows GPU box.

---

### A11 — ✅ DONE (2026-07-31) — the unknown-vertex-semantic warning (bug-hunt 2026-07-27 N5, warning half)

*From the [2026-07-27 bug hunt](BUG-HUNT-2026-07-27.md) N5.* `VertexSemanticMapper` maps an HLSL
vertex-input semantic to MonoGame's `VertexElementUsage` byte, and an unrecognized semantic falls
back to `TextureCoordinate`. **That fallback value is correct and did not change** — real `mgfxc`
defaults exactly the same way. What was missing is the other half of `mgfxc`'s behaviour: it
**prints a warning when it defaults**, and ShadowDusk did not. A typo (`TEXCORD0` for `TEXCOORD0`)
therefore silently minted a phantom TextureCoordinate attribute that MonoGame's
`VertexInputLayout` then demanded from the consumer's vertex declaration, with a failed draw far
from the shader as the only symptom. The mapper's own doc-comment admitted the gap and pointed
back at N5.

**Was deferred because** the mapper is pure and "has no warning channel to thread through". That
stopped being true when **Phase 53** added `CompiledShader.Warnings`.

**Landed as `SD0104`** (`docs/error-codes.md`; the `SD0100`–`SD0199` reflection/transpilation-backend
range, deliberately *not* `SD0400`–`SD0499`, which `GlslPortabilityAnalyzer` owns):

- `VertexSemanticMapper.Map(string semantic, out bool recognized)` reports whether the value came
  from the table or the fallback; the original `Map(string)` delegates to it, so every existing
  caller and value is untouched. `VertexSemanticMapper.UnrecognizedSemanticWarning` builds the
  diagnostic in one place for both backends.
- `SpirvVertexInputReflector.Read` (Vulkan) and `DxilVertexInputReflector.Read` (DirectX12) each
  gained an `out IReadOnlyList<ShaderError> warnings` overload; the old signatures remain and
  delegate.
- `CompilationPipeline.CompileEntryPoint` appends them to the `Warnings` element of its returned
  tuple (stamping the source path the compile was given), so they reach `CompiledShader.Warnings`,
  CLI stderr, MGCB, and `ValidateAsync` like every other warning.

**It is a WARNING, never an error** — `mgfxc` accepts and defaults, so drop-in parity forbids
rejecting. **No emitted byte moves:** the fallback usage/index values are unchanged, only the
Vulkan and DirectX12 attribute-table paths are touched, and warnings never gate output.
Pinned by `VertexSemanticMapperTests` (the recognised/unrecognised report, both overloads agreeing
on every value, the warning's code/severity/text), `SpirvVertexInputReflectorTests` (a hand-built
minimal SPIR-V module: the fallback attribute is still emitted, and both `Read` overloads produce
the identical table), and end-to-end `SD0104` tests in `VulkanEffectCompilerTests` /
`DirectX12EffectCompilerTests` (including the no-false-positive direction on a clean shader).

---

### A12 — ✅ DONE (2026-07-31) — the `#include` comparer stopped guessing case sensitivity from the OS (bug-hunt N17; M12's case half rejected)

*From the [2026-07-27 bug hunt](BUG-HUNT-2026-07-27.md), N17's second half — the last piece of
that item, and the disposal of M12's residue with it.*

**What the code actually did.** `Preprocessor.PreprocessorContext` keyed its cycle-detection
stack and its `#pragma once` set on `OperatingSystem.IsLinux() ? StringComparer.Ordinal :
StringComparer.OrdinalIgnoreCase`. That is wrong on two hosts ShadowDusk really ships to:
**Android's file system is case-sensitive** and `OperatingSystem.IsLinux()` is **false** there,
and **APFS can be formatted case-sensitive**. On both, two genuinely distinct headers whose
names differ only by case were folded into one file: a `#pragma once` in the first silently
suppressed the second (missing declarations, at best a confusing later error), and a legal
`a → Helper.fxh → helper.fxh` chain was rejected as a false `SD0002`. Going the other way, the
rule is also unsound for a case-insensitive volume mounted on Linux and for a per-directory
case-sensitive NTFS directory on Windows.

**The fix: ask, do not infer.** The comparer now canonicalizes through the injectable
[`IIncludePathCanonicalizer`](../src/ShadowDusk.Core/Preprocessor/IIncludePathCanonicalizer.cs).
Two paths that differ only by case are the same file **only** when the storage says both
spellings canonicalize to one name; anything else stays ordinal. On a case-insensitive volume
both spellings collapse onto the real name (today's Windows/macOS behaviour, unchanged); on a
case-sensitive volume each spelling canonicalizes to itself. **No OS check is involved, so the
answer is right on a host nobody has tested on** — which is the whole point, because nobody
here can run Android.

**Ordinal is the default and case-insensitivity is the exception**, deliberately. Wrongly
*merging* two paths is the damaging direction (a suppressed header, a false cycle); wrongly
*separating* two spellings of one file only costs a duplicated expansion, which the existing
cycle check still terminates. So an "I cannot tell" answer (a virtual path, an I/O failure)
falls back to ordinal rather than to the permissive rule.

**Plus a diagnostic, because the comparer alone does not close the user-visible failure.** The
shape that actually breaks a player is an `#include` whose spelling differs from the file's real
name by case: it resolves on the author's Windows box and fails with `SD0001` on Android. That
was a silent pass-through, so it is now the **`SD0008`** warning, naming the on-disk spelling.
A warning and not an error: the include *did* resolve, `mgfxc` on Windows accepts it, and
rejecting it would be a reject-set change that breaks working shaders. Only the segments the
directive itself spells are checked — the absolute prefix above them is the author's own machine
layout and never ships, so warning about it would be noise.

**M12's "Linux case-insensitive fallback" is closed as REJECTED, not deferred.** It is the same
subject seen from the opposite direction, and it is the wrong direction: it would have the
compiler open a file the host's file system says does not exist, ambiguously so wherever two
real case twins exist, and it would hide the author's mistake at the one moment they could still
fix it. `SD0008` gives the author the same information without lying about the file system.

**No output bytes moved.** The resolved path string is unchanged, so the `#line` directives and
therefore every emitted artifact are untouched; the change is confined to how two already-
resolved paths are compared and to a new non-fatal warning. Full `ShadowDusk.Core.Tests` (591 × 2)
and `ShadowDusk.Integration.Tests` (720 × 2, including the golden and byte-identity fixtures)
green on `net8.0` and `net10.0`.

**Testable without Android hardware, by construction.** The canonicalizer is an interface, so
`IncludePathCaseSensitivityTests` (pure, no disk) drives *both* file-system behaviours plus the
"cannot tell" fallback on any host. `IncludePathCanonicalizerTests` (integration) then measures
what the running volume actually does and asserts the real canonicalizer agrees — a test body
that is correct on Windows, Linux, either APFS flavour, and Android alike.

**What is still unproven, and honestly so:** nobody has executed this on an Android device. The
*logic* is now OS-independent and both branches are exercised, so the class of bug is closed on
evidence rather than on an assumption — but the on-device rung stays exactly where
`docs/validation-matrix.md` already puts Android.

---

## B. Externally blocked (gated on an outside event)

### B1 — ➡️ Promoted (2026-07-18) to [Phase 52](DONE/PHASE-52-monogame-3.8.5-support.md) Area D — DX12 / DXIL render-validation (Phase 35 Area C)
*From Phase 35.* The DXIL path is **already built**; what is missing is render-validation in
a real MonoGame **DX12** runtime, which only MonoGame 3.8.5 provides — currently preview only.

**Remaining (verbatim from Phase 35):** *"Render-validate the **already-built** DXIL path in
a real MonoGame **DX12** runtime. Seamless means ShadowDusk emits whatever the consumer's
DirectX runtime loads (DXBC for DX11, DXIL for DX12) **automatically** — the consumer never
picks DXBC vs DXIL or SM5 vs SM6."* Open design Q (same shape as Phase 33's one-blob problem):
*"can one artifact serve both, or must it be auto-detected from the target? Resolve
reproduce-first."* DX11 DXBC (vkd3d) stays the default.

**Blocked on:** MonoGame 3.8.5 going **stable** (do not target a preview as the product baseline).

**Update (2026-07-18): gate cleared — MonoGame 3.8.5 shipped stable 2026-07-15.** Per this
phase's definition of done ("promoted to its own scoped phase"), B1 was absorbed into
[Phase 52](DONE/PHASE-52-monogame-3.8.5-support.md) **Area D**: source-inspect the 3.8.5 WindowsDX12
effect load path first (the Phase-32 playbook — the Vulkan container assumptions were wrong on
inspection), then build the rung-4 DX12 render driver, with an explicit decision gate to split
into its own phase if the container research turns out Phase-32-sized.

**Update (2026-07-23): decision gate tripped — re-pointed to [Phase 54](DONE/PHASE-54-dx12-dxil-backend.md).**
Source inspection found no `PlatformTarget.DirectX12` anywhere in the codebase — genuine
new-backend work on Phase-32's scale, not a render-validation rung. B1 closes here; tracked at
Phase 54 from now on.

### B2 — ✅ DONE (2026-07-18) — Un-park Vulkan (Phase 35 Area D → trigger for Phase 32)
*From Phase 35.* **Landed:** MonoGame 3.8.5 shipped `DesktopVK` stable and
[Phase 32](DONE/PHASE-32-vulkan-backend.md) implemented + render-proved the Vulkan target
(real profile-80 container, 10/10 on real DesktopVK; PR #126, vchelaru). The one surviving
tail is **externally blocked and tracked in the Phase 32 doc**: a literal pixel-diff vs
`mgfxc`'s own Vulkan output (that output crashes in real DesktopVK — a confirmed upstream
MonoGame `SlotOffset` bug; `compare_vulkan.py` auto-upgrades to a real pixel-diff once
MonoGame fixes it). The half of the original mgfxc-oracle hope that DID materialize is the
render target; the oracle half is what the MonoGame bug still withholds.

### B3 — GL macro-defined techniques (Phase 41 GAP-1 / GL) — faithfulness-blocked
*From Phase 41.* GAP-1 (the `TECHNIQUE()` macro idiom invisible to the pre-preprocess
technique count) is **closed on DX and FNA**; GL remains and is **faithfulness-blocked, not
merely hard**. The stock effects expand to the legacy DX9/SM2 branch on GL (because `mgfxc`'s
own OpenGL target defines only `{MGFX, GLSL, OPENGL}` — no SM4 — and ShadowDusk faithfully
mirrors that), and DXC's native SPIR-V codegen crashes (uncatchable AV) on that legacy SM2
HLSL. Defining SM4 in the GL recovery path is **rejected** — it would diverge from `mgfxc`
(a different effect on GL), breaking THE PURPOSE.

**Remaining:** a faithful GL fix needs **either (a)** DXC not crashing on legacy SM2 SPIR-V
codegen, **or (b)** a provably behavior-preserving managed legacy→modern HLSL transcription —
both real projects, not a pipeline tweak. **Not a blocker for the motivating use case:** Gum
targets MonoGame **DX** and **FNA**, where macro-technique recovery is already proven. Pinned
by `OpenGl_MacroTechniqueEffect_KeepsLoudSd0010_NoCrash` and
`GumFnaSampleShader_MacroTechnique_OpenGl_KeepsSd0010_GlMacroModelGap`.

**Blocked on:** an upstream DXC fix, or a dedicated managed-transcription project.

### B4 — DX / FNA / KNI-DX render-in-CI gates (Phase 44)
*From Phase 44.* Desktop + browser **GL** render-in-CI is done (`validation-render.yml`,
ubuntu/llvmpipe). The DirectX-family gates can't run in CI: there is no verified headless
D3D path on the runners (Mesa is GL-only).

**Remaining (verbatim from Phase 44):** *"the **DX / FNA / KNI-DX** render gates
(`validation/CandidateDx`, `VsDrivenDx`, `DxModernFeatures`, `FnaValidation`,
`KniWinFormsDX`) need a **Windows runner with a software D3D driver (WARP)** — unverified, so
deliberately not wired yet."*

**Mitigation already in place:** these are a baked-in **local pre-release gate**
(`validation/run-windows-render-gates.ps1`, required by the `/release` skill), so the product
bar is enforced outside CI. **Blocked on:** a verified WARP-on-GitHub-Actions story.

---

## C. Deferred / low-value (record only, no action unless triggered)

### C1 — `d3dcompiler_47` vs `fxc.exe` DXBC delta study (Phase 41 OQ#2)
*From Phase 41.* **Deferred as low-value.** The structural matrix already shows ShadowDusk's
DX output matches the `mgfxc` (fxc-derived) goldens wherever it compiles, so the Phase 18
oracle choice is evidenced indirectly. **Pick this up only if** a specific DX divergence ever
needs the `fxc`-vs-`d3dcompiler` distinction.

---

## Definition of done for this phase

This collector phase closes when every **A** item is render-proven (or explicitly retired by
the owner) and every **B** item has either landed (its external gate cleared) or been promoted
to its own scoped phase. The **C** item needs no action; it is recorded so the question is not
re-asked from scratch. Until then, this is the single open home for these tails instead of
keeping five 95%-complete phases on the board.
