# Phase 65 — Full, slangc-backed Slang input: is it worth building? (spike)

**Track:** Investigation only. **No shipped code, test, or output byte changes in this phase.**
`src/ShadowDusk.Compiler/Slang/SlangFrontend.cs` is untouched; no Slang toolchain becomes a
runtime dependency of any `ShadowDusk.*` package.

**Status:** 🔬 Open (investigation). Delivered: measurements + a recommendation. Not delivered:
any implementation — that is exactly the question this spike answers.

**Opened 2026-09-11.** The repo owner has been publicly describing ShadowDusk as supporting
Slang without the scope limit in view: the shipped [Phase 61](DONE/PHASE-61-slang-support.md)
frontend accepts only the **HLSL-compatible subset of Slang**, as a pure managed text
transform — real Slang-only features (`import`, generics, `extension`, `associatedtype`) are
rejected by name with `SD0600`, and no Slang toolchain is shipped, downloaded, or invoked by the
product, ever. This phase asks the question directly: **is building real, full, slangc-backed
Slang input support worth doing — as a general, product-wide capability for every consumer
(arbitrary end-user `.slang` at runtime, the same way `.fx` input works today for anyone), not
a fixed small template set one downstream team (Gum) authors once?**

Phase 61 §6/§7 and its Open Questions (OQ2, OQ3) already scoped exactly this work and recorded
real measurements from 2026-08-13. This spike **re-ran those measurements today** against the
still-currently-pinned `slangc v2026.14.1` (unchanged since Phase 61 — confirmed in
`validation/SlangCorpus/Program.cs`), broadened the corpus per an explicit scope correction
mid-task (see below), and added two measurements Phase 61 did not run (the vkd3d-shader/FNA
route, and the shipped frontend's actual behavior on a genuinely-Slang-only file).

**Scope correction mid-task, recorded because it changes how §4 (OQ3) reads:** the investigation
was initially briefed around Gum's narrow ask (grayscale/tint/blur templates authored once). The
owner corrected this before the doc was written: the real question is whether ShadowDusk should
support full Slang input as a **general product capability** for **any** consumer, not whether a
cheap Gum-specific answer exists. §4 below evaluates both, explicitly labeled, and does not let
the cheap answer stand in for the general one.

**Depends on:** [Phase 61](DONE/PHASE-61-slang-support.md) (all definitions, prior measurements,
`SlangEntryScanner`, `SlangFrontend`). **Blocks:** nothing — this is read-only investigation.

---

## 0. Method

A throwaway console harness, `plan/PHASE-65-appendix/slang-probe/` (not in `ShadowDusk.slnx`,
never shipped, deliberately outside `tests/fixtures/`), references `ShadowDusk.Compiler` and runs
the **real, pinned `slangc.exe`** already cached by `validation/SlangCorpus`
(`v2026.14.1`, SHA-256-verified on first fetch) against:

- **Part A** — the shipped **17-shader `.slang` corpus** (`tests/fixtures/shaders/slang/`), 20
  entries across VS+PS pairs.
- **Part B** — **3 new Gum-shaped fragment shaders** written for this spike (grayscale, tint,
  fixed-radius box blur — single-texture post-process effects, the shape Gum's own SkiaGum
  `Grayscale.sksl`/`.fx` pair and `TintShader.fx` already represent in the corpus).
- **Part C/E** — **1 hand-written genuinely-Slang-only shader** (`GenericsProbe.slang`): a
  `IBlendMode` interface, a conforming `MultiplyBlend` struct, and a generic free function
  `applyBlend<T : IBlendMode>(...)` — none of which have an HLSL spelling.
- **Part D** — **7 general, non-Gum-shaped `.fx` fixtures** (`MultiTexture.fx`,
  `ForwardLighting.fx`, `ArrayUniform.fx`, `SimpleLightShader.fx`, `MultiCbuffer.fx`,
  `SamplerStatesFull.fx`, plus the vendored third-party `Apos.Shapes/apos-shapes.fx` gallery
  shader — real production HLSL, not authored for this spike), each run through ShadowDusk's own
  `FxPreParser` + `Preprocessor.Flatten` (OpenGL/SM4 macros) to strip the FX9 technique block and
  resolve `#if` ladders to one concrete body — the same "strip FX9, define macros" methodology
  Phase 61 A5 used — before handing the flattened HLSL to `slangc` as if it were Slang input.

For every accepted `slangc -target hlsl` emission, the harness then compiles that HLSL through
**the exact backends ShadowDusk actually invokes**: `DxcShaderCompiler` (the real OpenGL/Vulkan/
DirectX SPIR-V/DXIL route) and `Vkd3dShaderCompiler` at a literal `ps_3_0`/`vs_3_0` profile
override (the real FNA SM≤3 route, `Vkd3dCompileContract`'s D3D_BYTECODE target). Raw slangc
emissions for two representative files are kept in the appendix directory
(`GumGrayscale.slangc-hlsl.txt`, `GenericsProbe.slangc-hlsl.txt`) as quotable evidence.

**One correction to this phase's own brief, found while building the harness:** the brief asked
to test DXC "at the SM3 (`ps_3_0`) and SM4-level-9.1 (`ps_4_0_level_9_1`) profiles ShadowDusk
actually uses." Reading `DxcFlagBuilder.cs` shows this profile pair is not literally what DXC
compiles at — DXC has never supported SM3 and always compiles ShadowDusk's OpenGL/Vulkan/DirectX
route at `ps_6_0`/`vs_6_0` (SM6, the SPIR-V/DXIL route); `ps_4_0_level_9_1`/`ps_3_0` are **FX9
profile tokens** that select which `#define ..._SHADERMODEL` macro branch a `.fx` body takes
(the same convention `SlangFrontend.cs` itself synthesizes, §3 below) — a source-dialect
selector, not DXC's own compile target. The **real** SM≤3 floor is `vkd3d-shader`'s
`D3D_BYTECODE` target (`Vkd3dCompileContract.IsSm3OrBelow`), invoked directly on HLSL text,
**bypassing DXC entirely** — that is what the FNA route actually is. This spike tests both real
routes (DXC at its real SM6 target; vkd3d-shader at a real `ps_3_0`/`vs_3_0` profile), which is a
stronger and more accurate test than the brief's literal framing asked for.

---

## 1. OQ2 — SM dialect reachability

**MEASURED TODAY (2026-09-11), against the still-pinned slangc v2026.14.1.**

| Route | Result |
|---|---|
| `slangc -target hlsl` acceptance | **33/33 entries accepted**, 0 rejections, across all four parts |
| DXC (ShadowDusk's real OpenGL/Vulkan/DirectX SPIR-V/DXIL route, `ps_6_0`/`vs_6_0`) | **33/33 OK** |
| vkd3d-shader at `ps_3_0`/`vs_3_0` (ShadowDusk's real FNA SM≤3 route) | **32/33 OK** |

The one vkd3d-shader failure is `apos-shapes.fx`'s pixel shader (a real third-party, non-Gum
shader — Apostolique's Apos.Shapes gallery, already vendored for the DX/Vulkan render gates,
28,548 characters of emitted HLSL): `E5017: Aborting due to not yet implemented feature:
Instruction type HLSL_IR_LOOP`. **This is not a Slang-dialect problem.** vkd3d-shader's own SM≤3
backend does not support loop constructs at all — the same loop, hand-written directly in HLSL
with no Slang anywhere in the chain, would hit the identical wall. It is a **pre-existing
vkd3d-shader ceiling**, unrelated to whether the HLSL came from Slang or from a human.

**Conclusion: OQ2's original worry — that slangc's HLSL emission assumes SM6-era HLSL and
silently closes off OpenGL/FNA even where DirectX/Vulkan remain reachable — is NOT borne out by
this corpus.** Every one of 33 entries, spanning procedural/uniform-free shaders, cbuffer- and
texture-driven shaders, VS+PS pairs, and one real 28KB third-party production shader, reached
**both** real ShadowDusk routes (the DXC SPIR-V/DXIL route and the vkd3d-shader legacy-bytecode
route) via slangc's plain HLSL emission. The one failure traces to a pre-existing backend
capability ceiling, not to Slang.

**Caveat, stated plainly:** 33 shaders is a real, broadened sample — not exhaustive. No
construct in this corpus exercised an SM6-only Slang builtin (wave intrinsics, ray tracing,
mesh-shader features); such a construct could still produce the OQ2 failure mode on a shader this
sample does not contain. This finding is **measured-today for the tested corpus**, not a proof
that arbitrary Slang always reaches every target.

---

## 2. Residue/mangling — still there, confirmed today

**MEASURED TODAY**, quoting the real `slangc -target hlsl` emission for `GumGrayscale.slang`
(full text in `plan/PHASE-65-appendix/slang-probe/GumGrayscale.slangc-hlsl.txt`):

```hlsl
Texture2D<float4 > SpriteTexture_0 : register(t0);
SamplerState SpriteTextureSampler_0 : register(s0);

struct SLANG_ParameterGroup_Params_0
{
    float Strength_0;
};

cbuffer Params_0 : register(b0)
{
    SLANG_ParameterGroup_Params_0 Params_0;
}

float4 MainPS(PsInput_0 input_0) : SV_TARGET
{
    float4 tex_0 = SpriteTexture_0.Sample(SpriteTextureSampler_0, input_0.TexCoord_0) * input_0.Color_0;
    float3 _S1 = tex_0.xyz;
    ...
}
```

Both findings from Phase 61's one-day slangc-invoking route **still hold, unchanged, on the
current pin**:

- **Every symbol is `_N`-suffixed** (`SpriteTexture_0`, `Strength_0`, `input_0`, …), plus
  entirely synthetic compiler temporaries (`_S1`) with no source-name relationship at all.
- **Every cbuffer is wrapped** in a single-field `SLANG_ParameterGroup_*` struct — across the
  full 33-entry corpus, this fires on **12/33 entries** (every one that declares a `cbuffer`;
  0/33 false positives on uniform-free shaders).

**New observation this spike adds:** the wrapper struct and the outer cbuffer instance share the
**identical name** (`Params_0` is both the `cbuffer` block's name and its single field's name).
This is legal HLSL — DXC accepted it in all 12 cases — but it means a demangling shim cannot be a
blind global find/replace; it needs to track the two-level scope (`Params_0.Strength_0` →
`Strength`) the way a real HLSL-aware rewrite pass would.

**Assessment (engineering estimate, not validated by building it):** the pattern is completely
regular — every identifier gets a numeric suffix in a predictable, source-order-correlated way,
and every cbuffer wrap is "one struct, one field, same base name." A managed post-compile HLSL
rewrite pass (strip `_N` suffixes, unwrap single-field parameter groups, using slangc's own
emitted `#line` directives to keep the mapping honest) looks **shim-fixable in principle** — the
"demangle groundwork" Phase 61 flagged. This is a judgment call from the observed shape, not a
built-and-measured result; the real cost would only be known by building it.

---

## 3. FX9 wall — confirmed, and it is already solved

**Confirmed by reading the current shipped source, not by new measurement** (this is not
Phase 61's slangc-rejects-technique-blocks finding re-derived — it is a direct check of whether
today's shipped code already contains the reusable piece):

- `SlangEntryScanner.cs` already discovers entry points from `[shader("vertex")]`/
  `[shader("fragment")]` attributes — the only authoritative statement of intent in a language
  with no `technique`/`pass` concept — exactly the convention a slangc-backed route would need.
- `SlangFrontend.cs:139-158` already synthesizes the technique block from those entries, using
  the **same `#if SM4` → `vs/ps_4_0_level_9_1` else `vs/ps_3_0` convention** the ShaderToy
  frontend established, with the `[shader(...)]` attributes stripped before compile.

A slangc-backed route needs **no new design here**. The only thing that changes is the
"compile body" step: today the stripped body goes **straight to DXC**; a slangc-backed route
would run `slangc -target hlsl` on the body **first**, then feed slangc's HLSL emission to the
existing DXC/vkd3d-shader backends — exactly what Part D of this spike's harness does today. Entry
discovery, Slang-only-construct pre-checks, and technique synthesis are all unchanged.

---

## 4. OQ3 — packaging, evaluated as a product-wide question

The owner's correction (mid-task) is decisive here: **the question is not "does Gum's fixed
template set need a runtime Slang toolchain" (it clearly does not — see (b) below) but "should
ShadowDusk support arbitrary end-user `.slang` at runtime, for any consumer, the way `.fx` input
already works for anyone."** Both are evaluated, explicitly separated.

### (a) Keep the shipped subset; fix the public messaging

Cost: **~zero** — a docs/README correction (the `.slang` support description already exists in
`README.md`, `docfx/`, and `.claude/skills/local-test/SKILL.md`; the gap is that "we support
Slang" was said without the subset qualifier attached). No code change. This spike's own findings
in §1/§3 say the shipped subset is not *failing to reach targets* — every shape it accepts reaches
every route the product supports. What it does not do is accept Slang-only syntax, which is a
deliberate, documented, by-name-rejected trade (`SD0600`), not a silent gap.

### (b) An author-time-only `slang2fx` CLI tool — cheap, but answers the NARROW case only

**Explicitly labeled: this solves Gum's specific ask, not the general product question.** Shape:
same as `tools/shadertoy2fx` — a developer runs `slang2fx` once at development time, commits the
resulting plain `.fx`, and ShadowDusk's runtime library never touches Slang or ships the slangc
native at all. Cost: reuses `validation/SlangCorpus`'s existing pin/download/SHA-256-verify
pattern almost unchanged; ships nothing to consumers; zero per-RID native vendoring; zero NuGet
package growth; zero release-gate burden. This is the cheap, safe answer **for a team authoring a
small, fixed set of templates once** (Gum's actual stated need). It does **not** let an arbitrary
end user write `.slang` and have it compile at runtime the way `.fx`/ShaderToy GLSL do today —
that capability is what (c) would need to deliver, and (b) cannot stand in for it.

### (c) Full packaged-native runtime `.slang` ingestion — the general product capability

This is what "does ShadowDusk support Slang" would need to mean if answered at product scope,
under CLAUDE.md's **self-contained** requirement in full force (native rides inside the NuGet
package, transitively, no separate install) — there is no way around shipping the slangc native
per-RID for this case.

**Cost, measured today from the actual cached oracle** (`validation/SlangCorpus/.slang-oracle/
bin/`, windows-x64):

| Component | Size |
|---|---|
| `slang-llvm.dll` | 84.4 MB |
| `slang-compiler.dll` | 25.3 MB |
| `slang-glslang.dll` | 6.2 MB |
| `slang-glsl-module.dll` | 1.5 MB |
| `gfx.dll` | 1.1 MB |
| `slang-rt.dll` | 0.3 MB |
| `slang.dll` | 0.2 MB |
| **Total, one RID** | **~121 MB** |

Compare: `dxcompiler.dll` (win-x64), the native the DXC route already ships, is **17.1 MB**;
`libvkd3d-shader-1.dll` is **6.5 MB**. The Slang redistributable is **roughly 7x DXC's size**, per
RID, before counting the other RIDs Slang publishes (linux-x64/aarch64, macos-x64/arm64, wasm —
per Phase 61 §2.2, broadly matching DXC's own RID coverage, so coverage is not the blocker; size
and engineering are). **Caveat:** 121 MB is the whole redistributable; whether a `-target hlsl`
-only embed needs `slang-llvm.dll` (LLVM-backed codegen, plausibly only for CPU/native targets
this product would never ask for) is **unmeasured** — a real packaging effort would need to check
whether that 84 MB is avoidable, which could change this number substantially.

Beyond the native itself, (c) needs: a pin + SHA-256-verified `tools/restore.*` entry (the
Phase 37/40 playbook, "several days of work before any shader compiles" per Phase 61 §2.2); a
release-gate line (the missing-native-fails-red rule already in `project_decisions.md` would need
a Slang row); and **productionizing** the §2 demangling shim (today an estimate, not a build) plus
a genuinely broad A5-style residue sweep (this spike's 33-shader corpus is a solid start, not the
full sweep Phase 61 envisioned) before the `SD0600` boundary could safely widen from "the
HLSL-compatible subset" to "whatever real slangc accepts."

**What it would buy, weighed against that cost:** §5 below shows real Slang-only expressive power
(generics/interfaces) does compile through real slangc and does reach every route ShadowDusk
uses. But the same measurement shows that power **monomorphizes into ordinary, hand-writable
HLSL** — the shipped subset's author could write the monomorphized call site directly today and
get the same compiled result through the existing, already-shipped, zero-native-cost frontend.
Full ingestion buys **syntactic convenience at the source level** (write the generic once, let
slangc specialize it) rather than a shader shape the subset cannot reach at all.

---

## 5. Expressiveness — real Slang-only feature, real slangc, real result

**MEASURED TODAY.** `GenericsProbe.slang` declares an `IBlendMode` interface, a conforming
`MultiplyBlend` struct, and a generic free function `applyBlend<T : IBlendMode>(mode, base,
blend)` — none of which HLSL can spell. Real `slangc -target hlsl` **accepted it** and
**monomorphized the generic completely**, emitting ordinary, straight-line HLSL with no vtables,
no dynamic dispatch:

```hlsl
float3 MultiplyBlend_blend_0(float3 baseColor_0, float3 blendColor_0)
{
    return baseColor_0 * blendColor_0;
}

float3 applyBlend_0(float3 baseColor_1, float3 blendColor_1)
{
    return MultiplyBlend_blend_0(baseColor_1, blendColor_1);
}
```

That emission compiled through **both** real routes (DXC SM6 and vkd3d-shader `ps_3_0`) with no
new failure mode — it is ordinary HLSL by the time DXC or vkd3d-shader ever sees it.

**A second, unplanned measurement this spike ran (Part E): what does the SHIPPED frontend do with
this exact file today?** `SD0600`'s five-pattern reject list (`import`, `module`, `extension`,
`associatedtype`, `__generic`) does **not** name the `interface` keyword or the bare
angle-bracket generic-constraint syntax this file uses — so `SlangFrontend.ConvertToFx` does
**not** reject it. It falls through to `.fx` assembly and, in the real product, straight to DXC —
which has no interface/generic support and rejects it with its own native diagnostic:

```
X0000: expected ';' after __interface
```

**This is not a bug.** It is exactly the fallback `SlangFrontend.cs`'s own doc comment describes:
*"subtler Slang-isms fall through to DXC, whose own verbatim diagnostics remain the authority."*
The practical outcome — loud rejection, never a silent miscompile — holds either way; `SD0600`
just isn't the mechanism for every Slang-only construct, only the five it names by pattern.

**Conclusion for item 5:** full Slang input buys real, working expressive power (generics and
interfaces genuinely compile and monomorphize to portable HLSL) — but for a Gum-shaped
consumer, that power expresses as **syntactic sugar over what the shipped subset can already
produce by hand**, not a shader shape otherwise unreachable. The messaging gap (§4a) and the
narrow author-time tool (§4b) both remain live, independently of whether (c) is ever built.

---

## 6. Recommendation

**(b) — ship an author-time-only `slang2fx` CLI tool for the narrow, real, already-named
consumer (Gum-shaped fixed-template authoring), and pair it with (a) — fix the public messaging
— but do NOT build (c) now.**

One-paragraph reason: this spike's own measurements are the case against (c) at this time — the
shipped subset already reaches every route ShadowDusk supports for every HLSL-compatible Slang
shape tested (§1), the FX9 synthesis work a full route would need is already built and reusable
(§3), and the one thing full ingestion adds — real generics/interfaces — monomorphizes into
ordinary HLSL a subset author can already hand-write (§5), so the expressive gap it closes is
real but narrow. Against that, (c)'s cost is concrete and large: a ~7x-DXC-sized native per RID
(§4c, with the 84 MB LLVM component's necessity itself unmeasured), a multi-day packaging and
release-gate project, and a demangling shim that is today an estimate, not a build. **Nothing in
this spike found demand that would justify that cost** — the demand named so far (Gum's fixed
template set) is fully satisfied by the much cheaper (b), and the owner's own broader "should this
be a general capability" framing has not yet surfaced a consumer who needs *arbitrary end-user*
`.slang` at runtime rather than a small set of shaders a developer authors once. Revisit (c) if
such a consumer appears, using this spike's harness (`plan/PHASE-65-appendix/slang-probe/`) as the
starting point for a real A5-style broad sweep rather than re-deriving the measurements above.

---

## 7. What could not be verified

- **Whether `slang-llvm.dll`'s 84 MB is avoidable** in a `-target hlsl`-only embed (§4c) — would
  need an actual minimal-build experiment against Slang's own build system, out of scope for a
  read-only spike.
- **Linux/macOS RID parity for the same measurement** — this spike ran on the cached
  windows-x64 oracle only (the same platform constraint `validation/SlangCorpus`'s own oracle
  restore already documents: *"the pinned slangc oracle download is windows-x86_64"*). Phase 61
  §2.2 records that Slang itself publishes linux-x64/aarch64 and macos-x64/arm64 builds, but this
  spike did not fetch or measure them.
- **Whether the residue shim (§2) is actually buildable at the estimated cost** — the "shim-fixable
  in principle" assessment is a judgment call from the observed pattern, not a built-and-measured
  result.

## 8. Non-goals

- Modifying `SlangFrontend.cs`'s shipped behavior, or any other shipped code/test/output byte.
- Adding slangc as a runtime dependency of any `ShadowDusk.*` package.
- A decision on (b) vs (c) beyond the recommendation in §6 — that is the owner's call to make
  from this evidence, not this spike's to implement.

## Appendix

Throwaway measurement harness and fixtures: `plan/PHASE-65-appendix/slang-probe/` (`Program.cs`,
`SlangProbe.csproj`, `shaders/GumGrayscale.slang`, `shaders/GumTint.slang`,
`shaders/GumBlur.slang`, `shaders/GenericsProbe.slang`, plus the two raw-HLSL captures quoted
above). Not in `ShadowDusk.slnx`; not a validation gate; not referenced by any product code.
