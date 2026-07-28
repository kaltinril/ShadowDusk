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
| Browser diagnostics squiggle confirmation | [Phase 38](DONE/PHASE-38-wasm-compile-diagnostics.md) | 🟢 Implemented; only the in-browser confirmation rung left |
| DeferredSprite GL MRT render proof (GAP-2) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | Closed at compile + structural-match; render rung left |
| Apos.Shapes render-proof (Option B) | [Phase 49](DONE/PHASE-49-apos-shapes-regression-corpus.md) | Option A shipped; Option B render-proof decision-gated |
| GL macro-defined techniques (GAP-1 / GL) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | DX + FNA closed; GL faithfulness-blocked |
| DX12 / DXIL render-validation (Area C) — ➡️ promoted 2026-07-18 to [Phase 52](PHASE-52-monogame-3.8.5-support.md) Area D, split 2026-07-23 to [Phase 54](DONE/PHASE-54-dx12-dxil-backend.md) | [Phase 35](DONE/PHASE-35-forward-version-support.md) | New-backend build (not a render-validation rung); see B1 |
| Un-park Vulkan trigger (Area D) — ✅ done 2026-07-18 via [Phase 32](DONE/PHASE-32-vulkan-backend.md) | [Phase 35](DONE/PHASE-35-forward-version-support.md) | ext-blocked on MonoGame 3.8.5 stable (since shipped; see B2) |
| DX/FNA/KNI-DX render-in-CI gates | [Phase 44](DONE/PHASE-44-validation-breadth-and-matrix-coverage.md) | Effectively done; ext-blocked on a WARP CI runner |
| `d3dcompiler_47` vs `fxc.exe` DXBC delta study (OQ#2) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | Deferred, low-value |
| ShaderToy sample + runtime-helper migration to `samples/` | [Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md) | Core shipped (NuGet since 0.9.0); the sample-migration appendix stayed Planned (moved 2026-07-18) |
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

### A2 — DeferredSprite GL true 2-attachment MRT render proof (Phase 41 GAP-2)
*From Phase 41.* GAP-2 (Nez DeferredSprite failed on GL with `Semantic COLOR is invalid`)
is **closed at compile + golden-structural-match (2026-06-27)** via the GL-only
`GlStructOutputColorRewriter` (DX byte-identical) plus the true-MRT `gl_FragData[0]` slot-0
fix. One render rung remains.

**Remaining (verbatim from Phase 41):** *"a true MRT render proof (bind 2 render targets,
draw, read back BOTH attachments, compare to mgfxc) needs a NEW render driver — the current
GL render gates are single-target only."*

**Done = ** a new `validation/*` GL driver that binds 2 render targets, draws DeferredSprite,
reads back BOTH attachments, and asserts pixel-equivalence to the `mgfxc` golden (rung-4
pattern). Background: [structural-divergence-matrix.md](PHASE-41-appendix/structural-divergence-matrix.md).

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

### A4 — ShaderToy sample + runtime-helper migration to `samples/` (ex-47)
*From Phase 47 (moved 2026-07-18, at the 0.12.0 release docs audit).* The core promotion shipped
(the `ShadowDusk.ShaderToy` library is in-solution and published as a NuGet since 0.9.0), but the
MonoGame-dependent runtime helper + interactive viewer sample never moved out of
`tools/shadertoy2fx/` — the [sample-migration appendix](DONE/PHASE-47-appendix/sample-migration.md)
stayed **Planned**. Anchor constraint (verbatim from the appendix): *"No code in this appendix may
end up in a shipped `ShadowDusk.*` package."*

**Done = ** the runtime helper + interactive ShaderToy viewer sample live under `samples/` per the
appendix, `tools/shadertoy2fx/` keeps only the out-of-band render-proof driver (or is retired), and
`NoMonoGameInProductLibrariesTests` stays green (no shipped library gains a MonoGame dependency).

### A5 — CLI `.glsl`-route render-gate fixtures (ex-47 CLI appendix)
*From Phase 47 (moved 2026-07-18).* The CLI `.glsl` input is implemented + integration-tested
(`CliShaderToyInputTest`: GL/DX compile, located rejects, CLI ≡ Convert+pipeline byte-identity), but
the **render-gate fixture entries** for the `.glsl` route (Windows DX/FNA + GL gates) were deferred —
recorded in the [CLI appendix](DONE/PHASE-47-appendix/cli-shadertoy-input.md) and
`docs/validation-matrix.md` §8. (`--multipass` batch mode is a separate recorded deferral in the
same appendix, not part of this rung.)

**Done = ** at least one `.glsl`-route fixture renders through the Windows render gates (and the GL
CI gates where applicable), pinning the converted-shader path at rung 4.

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

### A7 — OpenGL sampler records per (texture, sampler) PAIR (from the 2026-07-27 full-project review)

*`mgfxc` compiles this and we do not — a fidelity gap on our side, not a reference-compiler bug.*

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

**Done = ** a `.fx` reading N textures through one shared `SamplerState` compiles for OpenGL,
emits N records naming every `ps_s{k}` the GLSL declares with the right texture parameter and
baked state per pair, is render-proven against the `mgfxc` OpenGL golden, produces identical
bytes on the CLI and in the browser, and `SD0216` is deleted as unnecessary. Note the separate,
pre-existing parameter-naming divergence in the same area (`mgfxc` emits `TextureSampler+DiffuseMap`
where we emit `DiffuseMap`); decide deliberately whether to match it, since changing parameter
names breaks existing consumers' `Parameters[...]` lookups.

---

## B. Externally blocked (gated on an outside event)

### B1 — ➡️ Promoted (2026-07-18) to [Phase 52](PHASE-52-monogame-3.8.5-support.md) Area D — DX12 / DXIL render-validation (Phase 35 Area C)
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
[Phase 52](PHASE-52-monogame-3.8.5-support.md) **Area D**: source-inspect the 3.8.5 WindowsDX12
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
