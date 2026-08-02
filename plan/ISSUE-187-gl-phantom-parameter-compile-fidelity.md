# Issue #187 — OpenGL phantom effect parameter: DXC/fxc compile-fidelity divergence (GradientToy.fx)

**Status: FIXED (2026-08-01) — Option A (synthesized backing, §7.3) implemented, adversarially
reviewed, and validated (full `dotnet test` + the complete Windows render-gate suite, both green);
see §12 for the implementation outcome.** The residual compile-fidelity divergence (§9) is
*documented, not fixed* — it is closable only by the DXC 1.8 pin bump recorded in §7.2. GitHub
issue #187 can be closed when the branch merges.

This doc records the full findings and the decision surface. GitHub issue
[#187](https://github.com/kaltinril/ShadowDusk/issues/187), split out from #185. The issue thread's
earlier investigation (optimization levels, `precise`, `-Oconfig`) is confirmed and extended here:
one previously-untried compiler-side fix was tested and **refuted for the current DXC pin**, and one
genuinely new pipeline-side fix was identified that reproduces the mgfxc golden's structure with a
corpus-proven blast radius of exactly one fixture. All findings below were produced by a
multi-agent investigation on 2026-08-01 (main @ `605b1d6`, 0.17.0) with the load-bearing claims
independently re-verified (re-compiled, re-parsed, re-hashed) by separate verification passes.

## TL;DR

- `GradientToy.fx`'s pixel shader computes `fragCoord = uv * iResolution.xy` then
  `uv2 = fragCoord / iResolution.xy`. DXC's `-spirv` backend cancels the identity; fxc does not.
  Since `iResolution` is the shader's **only** uniform, the whole `$Globals` cbuffer becomes unused
  and DXC removes the binding — the shipped GLSL has **no uniform at all**, while the DXIL
  companion compile that sources desktop-GL reflection (which does not fold) still reports the
  parameter. Result: `Parameters["iResolution"]` exists with **zero cbuffer records** behind it.
- **The phantom is harmless at runtime and precedented in real mgfxc output.** MonoGame's and
  KNI's loaders treat parameters and cbuffers as independent tables by construction, and real
  mgfxc's DirectX_11 profile ships exactly this shape for any declared-but-unused uniform (proven
  empirically against the pinned golden compiler — see §4).
- **Corpus-wide, GradientToy is the sole member of the phantom class** (147 fixtures swept, 104
  compile for GL, structural criterion — see §5).
- The issue thread's "pure algebraic no-op, no observable difference" mitigating claim is
  **true-in-practice but strictly false**: for the parameter's own shipped default (0,0,0) the
  mgfxc build renders NaN-derived (practically black) pixels while ours renders the clean gradient.
  The divergence is inverted in our favor, off-contract only, and invisible to the render gates
  (§6). No reflection- or writer-side fix can close it; only a different DXC could.
- `-fspv-preserve-bindings` — the one compiler-side lever the issue thread never tried — **does not
  exist in the pinned DXC 1.7.2212.40**; it first ships in the DXC 1.8.x line (§7.2).
- **Recommended fix (Option A, §7.3): complete the one-directional reflection→backing join in
  `CompilationPipeline`** — synthesize cbuffer backing for reflected-but-unbacked numeric
  parameters on the GL path. For GradientToy this reproduces the golden's structure exactly
  (`ps_uniforms_vec4` cbuffer, size 16, parameter 0 at offset 0, PS references it, declaration
  emitted), matches the mgfxc-DX11 dead-uniform precedent, changes bytes for exactly one corpus
  fixture, and lets the pinned regression test be rewritten (it must be — §8) and un-skipped.

## 1. The defect, precisely

Desktop OpenGL runs **two independent DXC invocations** per shader:

| Compile | Args (per `DxcFlagBuilder.Build`, `src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs:26-35`) | Folds the identity? | Feeds |
|---|---|---|---|
| Shipped GL compile | `-T ps_5_0 -spirv -fvk-use-dx-layout -auto-binding-space 1 -Zpr` | **Yes** — `$Globals` becomes unused, binding removed from SPIR-V | SPIRV-Cross → GLSL → `MonoGameGlslRewriter` → shipped blobs |
| Reflection-only companion | `Platform=DirectX` (SM6 DXIL, `CompilationPipeline.cs:1984-2013`, `-Vd` per `DxcCompileOptions.SkipValidation`) | **No** — `iResolution` stays live in the DXIL | `DxilReflectionExtractor` → parameter table |

The pipeline then merges them **one-directionally**:

- The effect-level **parameter table comes solely from reflection**: `allParameters` accumulates
  reflected parameters name-deduped across stages (`CompilationPipeline.cs:628-632`), flows through
  `BuildEffectParameterInfoList` (`:751`, `:2340-2364`) into `ShaderIR.Parameters`, and
  `MgfxWriter.WriteParameters` serializes every entry unconditionally with a zeroed default-value
  blob (`src/ShadowDusk.Core/MgfxWriter.cs:223-265`).
- The GL **register backing comes solely from the rewriter**: per-shader `ConstantBufferInfo`
  records are built from the `MonoGameGlslResult.Uniforms` layout (`CompilationPipeline.cs:679-745`),
  and a shader whose layout is empty is skipped entirely (`:684-686`). The join at `:692-728`
  iterates the **GLSL layout** and looks up each uniform in `allParameters` (`IndexOfParam`, `:701`;
  `SD0012` loud failure at `:717-725` for a layout entry with no parameter). **A reflected
  parameter with no GLSL uniform is never visited — silently.**

That silent non-visit is the phantom. The rewriter itself is not at fault: it is a deliberately
pure text transform (`MonoGameGlslRewriter.cs:124`) that packs **every declared member** of every
`std140` block unconditionally — a declared-but-never-read member would get a register, the array
declaration, and zero (harmless) body rewrites (`:528-573`, `:470-475`, `:906-917`). It packs
nothing here because SPIRV-Cross received SPIR-V with no `$Globals` at all.

## 2. Ground truth: our output vs the mgfxc golden

Both files parsed structurally with the repo's own `MgfxBlobReader`; ours freshly compiled at
`605b1d6` (SHA256 `f253af78e899d165ef3464344cb6bc792b652bce554ae6f3334a872c55baf3e6`, 745 bytes),
golden is the committed `tests/fixtures/golden/OpenGL/GradientToy.mgfx` (1200 bytes).

| Link in the chain | ShadowDusk today | mgfxc golden |
|---|---|---|
| Parameter record | `iResolution`, class Vector, float, 1×3, 12-byte zero default | **byte-identical shape** |
| Cbuffer record | **none — cbuffer count is 0** | `cb[0] "ps_uniforms_vec4"`, size 16, paramIndices `[0]`, offsets `[0]` |
| Shader → cbuffer reference | VS `[]`, PS `[]` | PS `[0]` |
| GLSL declaration | none (PS GLSL is the fully-folded `ps_oC0 = vec4(vTexCoord0.x, 1.0 - vTexCoord0.y, 0.0, 1.0);`) | `uniform vec4 ps_uniforms_vec4[1];` + `#define ps_c0 ps_uniforms_vec4[0]` |
| GLSL read | none | `ps_r0.xy = ps_r0.xy * ps_c0.xy;` then `ps_r1.x = 1.0 / ps_c0.x; ps_r1.y = 1.0 / ps_c0.y;` (`ps_c0` read three times; fxc kept the multiply and per-channel reciprocals instead of cancelling) |

So "SetValue silently writes nowhere" means, concretely: MonoGame builds the `EffectParameter`
from the parameter record, `SetValue(Vector3)` writes three floats into that parameter's managed
`Data` array and bumps its `StateKey` — and that is the end of the chain. Upload happens only via
cbuffer records (`ConstantBuffer.Update` iterates its own parameter-index list), and there are
none. It is the earliest possible break (no cbuffer membership), not the subtler
"cbuffer present but array absent/too small" classes.

## 3. Why the phantom is harmless at runtime (MonoGame and KNI)

Verified against MonoGame tag v3.8.2 (= the harness pin 3.8.2.1105), diffed identical to v3.8.5
where it matters, and against KNI v4.2.9001:

- **Parameters and cbuffers are independent tables with one-directional linkage.** `ReadEffect`
  reads each cbuffer's parameter-index+offset list, then reads parameters separately; nothing
  validates that every parameter is referenced by some cbuffer
  (`MonoGame.Framework/Graphics/Effect/Effect.cs:270-321`, `:415-485`). At draw time
  `EffectPass.Apply` updates only the cbuffers the bound shaders list (`EffectPass.cs:98-119`), and
  `ConstantBuffer.Update` reads only its own parameter list (`ConstantBuffer.cs:160-171`). A
  parameter no cbuffer lists is simply never read. Texture/sampler parameters are *always* in this
  state (the reader "skips over all other types as they don't get added to the constant buffer",
  `Effect.cs:472-475`) — e.g. the golden `Grayscale.mgfx` decodes to zero cbuffers plus one object
  parameter.
- **A link-stripped uniform is a silent, spec-sanctioned skip.** If the GL driver strips
  `ps_uniforms_vec4` (nothing reads it), `ConstantBuffer.PlatformApply` does
  `if (location == -1) return;` (`ConstantBuffer.OpenGL.cs:42-58`); the -1 is cached
  (`ShaderProgramCache.cs:21-30`) and querying a nonexistent name is not a GL error. Mesa's source
  documents that the -1 tolerance exists precisely so apps survive uniforms "removed by the
  compiler / linker after optimization" (`src/mesa/main/uniform_query.cpp:331-334`).
- **Uploading more array elements than are active is defined-ignored.** MonoGame always uploads the
  full declared buffer (`glUniform4fv(location, len/16, ptr)`, `ConstantBuffer.OpenGL.cs:75-76`).
  OpenGL 2.1 spec §2.15.3 (p.82): *"Values for any array element that exceeds the highest array
  element index used, as reported by GetActiveUniform, will be ignored by the GL."* Mesa literally
  clamps the count (`uniform_query.cpp:1503-1505`). The ES 2.0 / GL4 refpages carry the same rule,
  covering WebGL/ANGLE.
- **KNI is a direct port and behaves identically** (`Platforms/Graphics/.GL/Shader/ConcreteConstantBuffer.cs:49-51`,
  `:72-74`; BlazorGL path same pattern). Its Debug-only `Assert(slot == 0)` (one cbuffer per GL
  stage) is satisfied by ShadowDusk's one-vec4-array-per-stage convention, including under the
  Option A fix.

## 4. Precedent: real mgfxc already ships this shape (DirectX_11 profile)

Empirical probe against the repo's pinned golden compiler (`dotnet-mgcb`'s `mgfxc.dll`), fixture:

```hlsl
float4 LiveColor;      // read by the PS
float3 DeadUniform;    // declared, never referenced anywhere
```

- **`/Profile:DirectX_11`:** parameters = `[LiveColor, DeadUniform]`; `$Globals` cbuffer size 32,
  indices `[0,1]`, offsets `[0,16]`. The never-referenced uniform **is a real effect parameter with
  cbuffer membership whose data no shader ever reads** — because fxc keeps unused variables in the
  RDEF constant buffer (flagged not-used) and mgfxc copies every reflected variable with no
  `D3D_SVF_USED` filter (`Tools/MonoGame.Effect.Compiler/Effect/ConstantBufferData.sharpdx.cs:20-39`,
  identical v3.8.2→v3.8.4).
- **`/Profile:OpenGL`:** parameters = `[LiveColor]` only — GL-profile parameters come from
  MojoShader symbols, i.e. the D3D9 CTAB, which contains only referenced constants
  (`ConstantBufferData.mojo.cs:23-38`, `ShaderProfile.OpenGL.cs:48-58`).

Two consequences: (1) "a parameter whose `SetValue` reaches no live shader code" is a normal,
shipping mgfxc phenomenon that MonoGame's apply path is built to tolerate; (2) mgfxc's GL profile
would not emit a *truly dead* numeric uniform — but that is not GradientToy's situation: fxc does
not fold `iResolution`, so **the golden has it live, and matching the golden's parameter list by
name is the drop-in bar**. The Option A end state (parameter with cbuffer membership whose data the
shader never reads) is exactly the mgfxc-DX11 dead-uniform shape — strictly *more* mgfxc-like than
today's cbuffer-less phantom.

Name presence is consumer-load-bearing, not cosmetic: `samples/ShaderFiddle.Web/ShaderFiddleGame.cs:143`
uses `candidate.Parameters["iResolution"] is not null` as its ShaderToy-detection signal — an
in-repo consumer that breaks if the name disappears, independently corroborating the PR #186
revert (reflecting from shipped SPIR-V removed the parameter entirely).

## 5. Corpus scope: GradientToy is the sole member of the phantom class

The issue's scope note asked for a sweep; it was run (2026-08-01, no code changes): all **147**
`.fx` fixtures under `tests/fixtures/shaders`, compiled for OpenGL via the CLI, outputs parsed
with `MgfxBlobReader`. **104 compiled** (+`MinimalWithInclude.fx` clean with `/I`); every failure
is a documented, expected fixture-level reject (SD0010 macro-technique gap, `ExProfile*` deliberate
errors, per-target-limit rejects, third-party sets marked non-compiling on GL in their NOTICE.md).

**Criterion (structural, never name-substring):** a reflected non-Object parameter is phantom when
(A) no cbuffer record lists its parameter index, or (B) a listing cbuffer is referenced by no
shader, or (C) a referencing shader's GLSL declares no uniform named after the cbuffer (MonoGame
keys `glGetUniformLocation` on the cbuffer name), or (D) its vec4 register span falls outside the
declared `{vs,ps}_uniforms_vec4[N]`. Object-class parameters checked via the sampler table instead.

**Result: exactly one non-Object phantom corpus-wide — `shadertoy/GradientToy.fx` / `iResolution`
(class A), with the golden live.** Every other compiled fixture's value-class parameter sits at a
cbuffer offset inside a declared, shader-referenced array span. (The criterion also surfaced 110
Object-class rows across 85 fixtures; all map to the two **pinned, deliberate, render-proven**
sampler divergences recorded in `MgfxParameterMatchTests.cs:30-46` — additively-exposed sampler
params and the legacy `*_SDTexture` companion naming — excluded from the phantom class by prior
project decision.)

## 6. The "algebraic no-op" claim, corrected

The issue thread's mitigating claim — *"no consumer can get an observable rendering difference out
of this parameter on either compiler's output"* — is **true for every value the gates and samples
actually set, and strictly false in general**:

- The golden computes `(u·w)·(1/w)` per axis (multiply + per-channel reciprocal — fxc's D3D9 `rcp`
  via MojoShader); ours emits `u` directly.
- **Power-of-two resolutions: bit-for-bit identical** (exact exponent shifts; measured 0 ulp over
  100k samples). The GL ShaderToy render gate uses size **64** with tolerance 4
  (`validation/ShaderToyRouteGl/Program.cs:68,199,350`) — **the existing gate is structurally
  incapable of seeing this fold.**
- **Other positive normal resolutions (1280/720/1920/1080):** ≤1 ulp; ~2 per 100k pixels flip by
  ±1 8-bit level at quantization boundaries. Imperceptible, inside every gate tolerance — but not
  literally zero.
- **`iResolution = (0,0,0)` — which is the parameter's shipped default (12 zero bytes,
  byte-identical default blobs in ours and the golden):** the golden computes `u·0 = 0`,
  `1/0 = +Inf`, `0·Inf = NaN` and writes NaN-derived pixels (practically black on
  llvmpipe/D3D-style hardware; formally NaN→unorm8 is undefined in GL), while our folded build
  renders the clean gradient. **A consumer who loads the effect and never calls SetValue gets
  visibly different pictures from the two compilers, out of the box.** The fixture header declares
  the host "MUST drive" `iResolution`, so this sits outside the stated contract — but nothing
  enforces the contract at runtime.
- Subnormal / huge-finite / ±Inf / NaN components: divergent, partly hardware-dependent
  (flush-to-zero vs gradual underflow). Negative normals: identical (signs cancel exactly).
  True-fp16 `mediump` ES devices: visible ±1-level flips become common; components > 65504
  overflow to Inf→NaN.

**Risk inversion — worth stating plainly:** the phantom parameter itself (the thing the issue is
about) is the *harmless* half; the *actual* behavioral divergence vs the reference compiler is the
fold, where **ShadowDusk's build is the more forgiving one** on degenerate input. No
reflection-side or writer-side fix (including both options below) changes the folded arithmetic;
that residual is closable only by a DXC whose codegen keeps the reads, which none of the available
levers achieves (§7).

## 7. Fix directions

### 7.1 Exhausted (issue-thread findings, confirmed)

| Lever | Outcome |
|---|---|
| `-O0` | Stops the fold but regresses 7 other GL fixtures + a new `SD0217`; corpus-hostile |
| `-O1`/`-O2` | Byte-identical to `-O3` corpus-wide — no middle ground |
| HLSL `precise` | No-op on this pinned DXC's SPIR-V backend (confirmed byte-identical) |
| `-Oconfig` pass-picking | Barred: "never fork or own compiler internals" (`project_rules.md`) |
| Reflect from shipped SPIR-V (`SpirvReflector`) | Tried and reverted (PR #186): removes the phantom but removes the **name**, breaking the drop-in bar (and a real in-repo consumer, §4) |

### 7.2 `-fspv-preserve-bindings` — refuted for the current pin, viable after a DXC bump

The one compiler-side lever the issue thread never evaluated. Hypothesis: preserve the unused
`$Globals` binding in the SPIR-V → SPIRV-Cross emits the (unread) block → the rewriter packs it
unconditionally (§1) → real backing with name parity, via a documented public flag.

**Empirically refuted at the first link — the flag does not exist in the pinned DXC** (Vortice.Dxc
3.3.4 → `dxcompiler.dll` 1.7.2212.40 `(e043f4a12)`). Proven four independent ways, then
re-proven by an adversarial verifier with its own IDxcCompiler3 probe:

1. Runtime: the patched compile fails with DXC's own `error X0000: Unknown argument:
   '-fspv-preserve-bindings'` (string absent from ShadowDusk sources).
2. Binary: the shipped `dxcompiler.dll`'s ASCII option table contains all seven sibling `fspv-*`
   options but neither `fspv-preserve-bindings` nor `fspv-preserve-interface`.
3. Upstream: `HLSLOptions.td` at tag `release-1.7.2212` defines neither; at `release-1.8.2403` and
   `main` it defines both ("Preserves all bindings declared within the module, even when those
   bindings are unused").
4. History: the 2019 PR microsoft/DirectXShaderCompiler#2435 proposing it was closed unmerged; the
   shipping flag is a later re-addition in the 1.8.x line.

**If/when the DXC pin moves to 1.8.x, this becomes the strategically right fix** — and the pin
move is *already independently motivated*: the DX12 Apos.Shapes `maxd 1` gap is root-caused to
this exact pin, and a DXC 1.8 build reproduces that golden at maxd 0 (see `project_facts.md`,
Known gaps). One future pin bump can close both. Where the flag would go: the two
`(PlatformTarget.OpenGL, Vertex/Pixel)` arms of `DxcFlagBuilder.cs` (`:30`, `:35`) — structurally
scoped away from Vulkan/DirectX/DirectX12/Metal, **but shared with the WASM host by design**
(`JsShaderBackends.cs:68-73` calls the same builder), which is desirable: with the binding
preserved, the WASM path's `SpirvReflector` would report `iResolution` too, closing the WASM-host
parameter-list divergence (§9) and re-opening the door to retiring the companion DXIL compile
(the #186 reconvergence). Residual unknowns to verify then: whether SPIRV-Cross emits the unused
block in the `layout(binding = N, std140) uniform` shape the rewriter's regex requires (the
rewriter side is code-verified, §1), and the flag's interaction with the fold (declarations
surviving is sufficient; the reads staying folded is expected and fine). A pin bump is its own
project: "never bump a pinned native version casually" — full goldens + render-gate re-proof
across GL, Vulkan, DX12, and WASM (with a `dxcompiler.wasm` rebuild for one-pipeline parity).

### 7.3 Option A (recommended, available now): complete the join — synthesize backing on the GL path

The defect in §1 is a one-directional join: GLSL-layout → parameter, never reflected-parameter →
backing. Complete it: after the per-shader join (`CompilationPipeline.cs:692-728`), for each stage
whose companion reflection lists a **non-Object parameter absent from that stage's rewriter
layout**, append it a register after the live ones, create/extend the stage's
`{vs,ps}_uniforms_vec4` cbuffer record, and inject/extend the
`uniform vec4 {vs,ps}_uniforms_vec4[N];` declaration in the GLSL text. (Pipeline-side, keeping
`MonoGameGlslRewriter` pure; per-stage placement mirrors mgfxc's per-stage CTAB behavior; a
parameter reflected by both stages gets backed in both, as mgfxc would.)

- **For GradientToy this reproduces the golden's structure exactly** — cb `ps_uniforms_vec4`
  size 16, parameter 0 at offset 0, PS references cbuffer 0, declaration present — differing only
  in the (unfixable) folded body. Every link SetValue→cbuffer→upload exists; the driver either
  link-strips (silent, spec-sanctioned skip, §3) or uploads bytes nothing reads. Render-identical
  either way.
- **Blast radius is corpus-proven: exactly the phantom class, i.e. one fixture** (§5). No live
  parameter's register moves (synthetic slots append after live ones; the container is
  self-describing, so even hypothetical ordering divergence from mgfxc would be benign).
- **It is the blessed pattern**: minimal and reversible on our side of the compiler boundary
  (`project_rules.md`'s `D3d9BytecodePatcher` precedent), with the upstream follow-up (§7.2)
  recorded. Design it so synthesis is a **no-op whenever the layout already covers the parameter**
  — then a future DXC-1.8 `-fspv-preserve-bindings` bump makes it naturally inert rather than
  conflicting.
- Scope boundary: synthesis backs wholly-missing parameters. A partially-folded *array* uniform
  (some elements live) does not occur in the corpus and stays out of scope; the sweep criterion
  (§5) would flag one loudly if it ever appeared.
- **The bar for shipping it** (this touches the MGFX writer's GL output — both halves of the
  pre-merge bar apply): full `dotnet test ShadowDusk.slnx` **and**
  `./validation/run-windows-render-gates.ps1`; the rewritten §8 test un-skipped; the §5 sweep
  criterion promoted to a permanent corpus-wide integration test (every fixed bug earns a
  regression test); the support-surface docs checklist walked for the rewriter/writer behavior
  change.

### 7.4 Option C: won't-fix (the issue thread's suggestion)

Defensible on the evidence (harmless at runtime, precedented in mgfxc DX11 output, sole corpus
member, name parity already intact). But it leaves `GlPhantomParameterTests` permanently skipped —
or deleted with a pinned GradientToy exclusion in any corpus guard, since the corrected assertion
(§8) still fails without backing — and it leaves ShadowDusk's own output structurally
self-inconsistent in a way Option A removes for one small, corpus-bounded change. Given Option A
exists, won't-fix is the fallback, not the recommendation.

## 8. The pinned test cannot gate any fix as written

`GlPhantomParameterTests.cs:85` asserts `allGlsl.ShouldContain(p.Name, Case.Sensitive)` — a literal
substring check. **The target end state itself cannot satisfy it**: the golden's own PS GLSL
contains `ps_uniforms_vec4`/`ps_c0` and no literal `iResolution` anywhere — GL packs every
non-sampler uniform into the register arrays, so the parameter name never appears in healthy
output (corroborated on Sepia: `_sepiaTone` is correctly backed and absent from its GLSL). This
confirms and sharpens the issue thread's observation. The rewrite: assert **structural backing** —
every non-Object parameter's index is listed by a cbuffer record, some shader references that
cbuffer, the GLSL declares the cbuffer-named array, and the parameter's register span fits the
declared size (criterion §5); Object-class parameters via the sampler table with the two pinned
deliberate divergences excluded. Un-skip only together with Option A (without it, the corrected
assertion still correctly fails on GradientToy).

## 9. Residual limitations — true under every option

1. **The folded arithmetic stays folded.** Backing (A) or preserved bindings (7.2) restore the
   declaration/upload chain, not the reads. At degenerate `iResolution` values — including the
   shipped default (0,0,0) — mgfxc's build renders NaN-derived pixels where ours renders the clean
   gradient (§6). Unfixable on our side of the DXC seam; divergence favors us; off-contract;
   invisible to current gates.
2. **Every SPIR-V-reflecting path's parameter list still diverges by name for this fixture** —
   the WASM host (`SpirvReflector`, `WasmShaderCompiler.cs:114`) **and desktop Vulkan**, which
   reflects the shipped SPIR-V unconditionally (`CompilationPipeline.cs` `reflectFromSpirv`), so
   `iResolution` is missing from the parameter table entirely in-browser and on DesktopVK
   (verified empirically on the Vulkan output during the final audit) — the #186 divergence
   persists there, pinned by the GradientToy exclusion in
   `SpirvReflectionByteIdentityTests.cs` (the exclusion comment block). Option A synthesizes from
   reflection and therefore cannot help where reflection itself lacks the parameter; only the
   §7.2 pin-bump direction closes this (and would let the exclusion be removed).
3. **The class is open-ended in principle.** DXC-folds-what-fxc-keeps is a family; the corpus
   guard (§5 criterion as a permanent test) is the tripwire that turns any future member from a
   silent divergence into a red test.

## 10. Reproduction

```powershell
dotnet build ShadowDusk.slnx
# Today's phantom (parameter present, zero cbuffers):
src/ShadowDusk.Cli/bin/Debug/net8.0/ShadowDuskCLI.exe `
  tests/fixtures/shaders/shadertoy/GradientToy.fx out.mgfx /Profile:OpenGL
# Structure: parse out.mgfx and tests/fixtures/golden/OpenGL/GradientToy.mgfx with the
# repo's MgfxBlobReader (tests/ShadowDusk.Integration.Tests/MgfxBlobReader.cs); the GLSL
# blobs are UTF-8 text inside the container.
# Flag rejection (current pin): add "-fspv-preserve-bindings" to the OpenGL arms of
# src/ShadowDusk.HLSL/Dxc/DxcFlagBuilder.cs (:30, :35), rebuild, recompile → DXC's
# "error X0000: Unknown argument". Revert after.
# mgfxc dead-uniform precedent (§4): compile the two-line DeadUniform.fx above with the
# pinned dotnet-mgcb mgfxc.dll for /Profile:DirectX_11 vs /Profile:OpenGL and compare
# parameter tables.
```

## 11. Cross-references

- CHANGELOG 0.17.0 → "Known issue" block (first public record of #187).
- `tests/ShadowDusk.Integration.Tests/Reflection/GlPhantomParameterTests.cs` (rewritten
  structural regression + sub-shape theories + corpus sweep; was Skip-pinned before the fix, §8).
- `tests/ShadowDusk.Integration.Tests/WasmPath/SpirvReflectionByteIdentityTests.cs` — the
  GradientToy exclusion comment block (§9.2).
- `project_facts.md` → Known gaps (the #187 summary line; the DX12 maxd-1 DXC-pin gap that makes
  the §7.2 pin bump doubly motivated).
- [`DONE/ISSUE-70`](DONE/ISSUE-70-gl-vertex-fidelity.md), [`DONE/ISSUE-145`](DONE/ISSUE-145-vulkan-vs-driven-and-legacy-sampler.md),
  [`DONE/ISSUE-149`](DONE/ISSUE-149-gl-isnan-versionless-glsl.md) — the sibling issue-record docs
  this one joins (move to `DONE/` when #187 is resolved or formally closed won't-fix).

## 12. Implementation outcome (2026-08-01, same day)

Option A shipped on branch `fix/issue-187-gl-phantom-parameter-backing`, hardened by an
adversarial three-agent review (correctness, mgfxc-faithfulness, test-strength via mutation
testing) that found two real defects in the first cut — both fixed and fixture-pinned:

- **The synthesis** lives in `CompilationPipeline`: `shaderNumericParams` captures each shader's
  reflected Scalar/Vector/Matrix parameters, and the GL cbuffer-record loop, after the existing
  layout→parameter join, appends a register slot + cbuffer membership for each reflected numeric
  parameter the layout missed, then `PatchGlUniformArrayDeclaration` resizes — or inserts, past
  the full `#extension` / `#ifdef GL_ES` prologue — the `uniform vec4 {vs,ps}_uniforms_vec4[N];`
  declaration. `GradientToy`'s GL output now carries the golden's exact structure
  (`ps_uniforms_vec4`, size 16, parameter 0 at offset 0, PS references it).
- **Review finding 1 (blocker, fixed):** the first cut sized a Matrix phantom by `Rows`, but
  MonoGame/KNI upload matrices **transposed** (`ConstantBuffer.SetParameter` writes `ColumnCount`
  16-byte rows), so a non-square phantom (`float2x4`) under-allocated and threw
  `ArgumentException` on the **first `EffectPass.Apply`**, no SetValue needed. Fixed: Matrix
  parameters are sized by `Columns` (`float4x4` unchanged, corpus byte-stable). Pinned by
  `examples/ExPhantomNonSquareMatrix.fx` (cb size 64 for a `float2x4`).
- **Review finding 2 (major, fixed):** the insertion anchor assumed the GLSL starts with
  `#ifdef GL_ES`, but derivative-using shaders lead with `#extension GL_OES_standard_derivatives`
  (issue #139's mgfxc-faithful placement), so the declaration was prepended above the
  `#extension` line — rejected by strict ESSL front ends (ANGLE/WebGL, Android GLES), invisible
  to lenient desktop GL gates. Fixed: `GlPrologueEnd` skips the entire leading prologue. Pinned by
  `examples/ExPhantomDerivativeUniform.fx`.
- **Review finding 3 (minor, fixed):** the declaration-**resize** branch was unreachable from any
  fully-folded fixture (mutation testing proved it dead-but-shipped). `examples/ExPhantomSecondCbufferFold.fx`
  (one live cbuffer + one fully-folded cbuffer) now executes it (`[1]` → `[2]`, offsets `[0,16]`).
- **Tests:** `GlPhantomParameterTests` rewritten per §8 and un-skipped — the GradientToy
  structural regression (golden-parity shape pinned), the sub-shape theories (three in this
  round; a fourth added in §12.1), and the corpus-wide backing sweep (criterion §5 plus the
  runtime-write-footprint clause from finding 1; every referencing shader must declare coverage;
  compiled-count floor pinned — final value 108, see §12.1). Mutation
  testing confirmed both core tests fail against the unfixed pipeline and against a neutered
  declaration patch.
- **Faithfulness evidence:** full-corpus compile with the fix vs the 0.17.0 baseline — 103 of 104
  outputs byte-identical, `GradientToy` the only change; `DeadUniform`-style dead-in-live-cbuffer
  shapes byte-identical (already backed pre-fix by the full-layout model); DirectX/FNA/Vulkan
  paths untouched (`monoGameGl`-scoped). mgfxc-precedent (§4) makes the synthesized shape the
  reference compiler's own dead-uniform shape.
- **Validation:** full `dotnet test ShadowDusk.slnx` green (both TFMs, zero skips — the #187 skip
  is retired), and `./validation/run-windows-render-gates.ps1` exit 0 with every gate PASS
  (the `[baseline-vulkan] 0/10` arm is the documented upstream MonoGame Vulkan `SlotOffset` crash
  on mgfxc's own output — `project_facts.md`, pre-existing, tolerated by the script by design).
  The Phase 41 structural-divergence report regenerated: GradientToy [OpenGL] is now **clean**
  (71 clean / 17 divergent, was 70/18).
- **Deliberately not addressed here** (recorded, not forgotten): the §9 residuals — the
  SPIR-V-reflector parameter-list divergence (WASM host + desktop Vulkan) and the fold's
  degenerate-value behavior — plus the review's optional suggestion of an Object-class
  (sampler-table) analogue of the backing sweep; the reflector residual waits on the §7.2
  DXC 1.8 follow-up, the fold residual is closable by no available lever.

### 12.1 Final-audit round (same day, second adversarial pass)

A five-auditor final audit (fresh-eyes code review, a 6-mutation battery, full-corpus
byte-stability rebuilt from a clean 0.17.0 clone across all four targets, docs-consistency +
support-surface audit, and a real-MonoGame load test) ran after §12 and found one more code
defect plus doc drift — all fixed the same day:

- **TexLod prologue (major, confirmed + reproduced):** the rewriter's THIRD leading header —
  the balanced `#if __VERSION__ >= 300 … #elif … #extension GL_ARB_shader_texture_lod … #endif`
  TexLod block (explicit-LOD shaders) — was not consumed by `GlPrologueEnd`, so a phantom in a
  `SampleLevel` shader got its declaration inserted above the block's `#extension` directives
  (a hard error on Mesa desktop GL, which takes that branch). Fixed: `GlPrologueEnd` now
  consumes balanced `#if/#ifdef/#ifndef … #endif` blocks depth-tracked, in any order with
  `#extension` lines. Pinned by `examples/ExPhantomTexLodUniform.fx` (SampleLevel — the
  cross-platform syntax; `tex2Dlod` is Windows-DXC-only). All previously-validated outputs
  byte-identical under the generalized skip.
- **Surviving mutation killed:** an in-bounds mis-offset (a synthesized slot overlapping a live
  parameter) survived every assertion; the sweep now enforces per-cbuffer member-span
  non-overlap (mutation re-run: killed via `ExPhantomSecondCbufferFold`).
- **Criterion hardening:** the sweep's backing verdict now requires every listing cbuffer's
  referencing shaders to declare coverage (first-backing-wins removed); the sub-shape theories
  assert their prologue markers unconditionally (no vacuous pass if a future pin stops folding
  or stops emitting the header) and pin the synthesized offsets; the synthesis loop's
  impossible parameter-table miss now fails loudly (SD0012) instead of silently skipping.
- **Docs corrected:** the "both residuals close via the flag" overstatement (only the reflector
  residual does), the residual scope widened to desktop Vulkan (verified: Vulkan GradientToy
  output carries no `iResolution`), corpus census 147→151 + provenance rows for the four
  `ExPhantom*` fixtures (`docs/test-shader-corpus.md`, `docs/repository-layout.md`), the
  transcluded parameter-binding design note (`docs/references/compilation-pipeline.md`), and a
  `project_decisions.md` entry for the Option A choice.

Clean bills from the same audit: full-corpus byte-stability (103/104 pre-existing GL outputs
identical to a clean-rebuilt 0.17.0, GradientToy the only change; DirectX 115, Vulkan 129, FNA
outputs all byte-identical), and all four phantom fixtures' GL outputs **load in a real
MonoGame.Framework.DesktopGL 3.8.2.1105 `Effect` on real hardware** — construct, first
`Apply` (the pre-review crash point), `SetValue` on the phantom, re-`Apply`, draw — with no
exception, no GL errors, and fully non-empty frames.
