# Changelog

All notable changes to ShadowDusk are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

ShadowDusk is a cross-platform, in-memory drop-in `mgfxc` replacement: a self-contained
library that compiles `.fx` → `.mgfx` at runtime on Linux, macOS, and Windows, with output
that loads and renders identically to `mgfxc`'s in the real MonoGame/KNI runtime. All eight
`ShadowDusk.*` packages share a single version (see `Directory.Build.props` `<Version>`).

## [Unreleased]

### Added

### Changed

### Fixed

- **OpenGL: a numeric parameter that reflection reports but the shipped GLSL never declares now
  gets synthesized register backing instead of shipping as a phantom** (issue #187, found on
  `GradientToy.fx`'s `iResolution`). DXC's `-spirv` backend folds the shader's
  `(uv * iResolution.xy) / iResolution.xy` identity and drops the then-unused `$Globals` cbuffer
  from the SPIR-V, while the DXIL companion compile that sources desktop-GL reflection — like real
  fxc/mgfxc — keeps it; the .mgfx therefore carried `Parameters["iResolution"]` with **zero**
  cbuffer records behind it, so `SetValue` wrote into CPU-side parameter data nothing ever
  consumed. The pipeline's reflection→backing join was one-directional (GLSL layout → parameter,
  never the reverse); it now appends a register slot, cbuffer membership, and a covering
  `uniform vec4 {vs,ps}_uniforms_vec4[N];` declaration for each reflected Scalar/Vector/Matrix
  parameter the rewriter's layout missed. For `GradientToy` the output now carries exactly the
  mgfxc golden's structure (one `ps_uniforms_vec4` cbuffer, 16 bytes, parameter 0 at offset 0,
  referenced by the pixel shader); rendering is unchanged — the GL driver either link-strips the
  unread array (a silent, spec-sanctioned skip) or uploads data nothing reads, the same shape real
  mgfxc's DirectX profile ships for any declared-but-unused uniform. A 147-fixture corpus sweep
  confirmed `GradientToy` is the sole pre-existing affected output. Two rounds of adversarial
  review hardened four synthesis sub-shapes before shipping, each pinned by a purpose-built
  fixture: a **non-square matrix** phantom must be sized by the runtime's transposed write model
  (Columns registers — MonoGame uploads `ColumnCount` 16-byte rows, so sizing by Rows crashes the
  first `EffectPass.Apply`; `ExPhantomNonSquareMatrix.fx`); the synthesized declaration must be
  inserted after the `#extension` derivatives header + `#ifdef GL_ES` precision block
  (`ExPhantomDerivativeUniform.fx`) **and** after the balanced `#if __VERSION__ >= 300 … #endif`
  TexLod header, whose `#extension` directives live inside branches — Mesa hard-errors on a
  mid-shader `#extension`, so `GlPrologueEnd` consumes balanced preprocessor blocks
  (`ExPhantomTexLodUniform.fx`); and a stage with live uniforms plus a phantom must resize its
  existing declaration rather than insert a second one (`ExPhantomSecondCbufferFold.fx`).
  `GlPhantomParameterTests` was rewritten to
  assert **structural backing** and un-skipped — its original `ShouldContain(name)` assertion
  could never pass, even against the golden's own GLSL (GL packs uniforms into register arrays, so
  parameter names never appear literally) — and a new corpus-wide backing sweep guards the whole
  class. Residual divergences (SPIR-V-reflecting paths — the WASM host and desktop Vulkan —
  still cannot see the folded-away parameter, so their parameter lists lack it; the fold itself
  diverges from mgfxc only at degenerate values like an unset `iResolution`, where ShadowDusk's
  build is the more forgiving one, and no lever closes that half) and the DXC 1.8
  `-fspv-preserve-bindings` follow-up for the reflector half are recorded in
  `plan/DONE/ISSUE-187-gl-phantom-parameter-compile-fidelity.md`.

## [0.17.0] - 2026-08-01

### Added

- **`validation/DumpPreprocessedHlsl`, a no-GPU diagnostic** that dumps the exact HLSL text the
  compilation pipeline hands to DXC for a given `.fx` + target, plus the `-D` macro flags. It exists
  so a divergence on any DXC-fed target can be *attributed*: replay the identical input through a
  different `dxc.exe` and diff the disassembly. An empty instruction diff means our source and flags
  are right and only the pinned DXC build differs.
- **`SD0104`, the mgfxc-parity warning for an unrecognized vertex-input semantic** (closes the
  remaining half of bug-hunt 2026-07-27 N5). An HLSL vertex semantic ShadowDusk does not model
  has always defaulted to `VertexElementUsage.TextureCoordinate`, which is correct — real `mgfxc`
  defaults the same way — but `mgfxc` also *prints a warning when it defaults* and ShadowDusk did
  not, so a typo such as `TEXCORD0` for `TEXCOORD0` silently minted a phantom TextureCoordinate
  attribute that MonoGame's `VertexInputLayout` then demanded from the vertex declaration, with a
  failed draw as the only symptom. The Vulkan (SPIR-V) and DirectX12 (DXIL) attribute-table paths
  now surface it through `CompiledShader.Warnings`, so it reaches CLI stderr, MGCB, and
  `ValidateAsync` like every other warning. It is a **warning, never an error** (`mgfxc` accepts
  and defaults, so a drop-in replacement must too), and **no emitted byte moves**: the fallback
  usage/index values are unchanged and warnings never gate output.
- **`SD0008`, a warning for an `#include` that only resolves because your file system ignores
  case.** `#include "shared/macros.fxh"` against a file really named `Shared/Macros.fxh`
  compiles on Windows and on a default macOS volume, and then fails with `SD0001` on Android,
  on Linux, and on a case-sensitive APFS volume — a break the author cannot see locally. It is
  a warning rather than an error because the include genuinely did resolve and `mgfxc` accepts
  it too; the message names the on-disk spelling so the fix is one edit. Only the path segments
  the directive itself spells are checked, since the absolute prefix above them is your own
  machine layout and never ships.
- **`ShaderToyRouteDx`, the DirectX arm of the ShaderToy / `.glsl` route render gate.** It converts
  `GradientToy.glsl` in process with the real converter and pixel-diffs ShadowDusk's `DirectX_11`
  build against **`mgfxc`'s `DirectX_11` build of the same converted `.fx`** on real MonoGame
  `WindowsDX`. This arm was previously impossible, not merely missing: mgfxc refused the converter's
  own output on DirectX (see below), so no golden could exist. Default-ON in
  `validation/run-windows-render-gates.ps1`; there is no CI lane, because no GitHub runner has a
  headless D3D driver (the same bucket as every other DX gate).
- **A `DirectX_11` golden for the pinned ShaderToy fixture**, so `tests/fixtures/shaders/shadertoy/GradientToy.fx`
  is golden-backed on both profiles like the rest of the corpus. `tools/compile-fixtures.ps1` now
  includes the `shadertoy/` subdirectory by default, so a future regeneration cannot silently skip it.
- **`ShadowDusk.MgcbPlugin` — MonoGame Content Builder integration, for real** (Phase 29). The
  project went from a `.csproj` with zero `.cs` files to a shipping content-processor plugin:
  `/reference:` it in a `.mgcb`, select `ShadowDuskEffectImporter` / `ShadowDuskEffectProcessor`,
  and MGCB compiles `.fx → .xnb` through ShadowDusk **in its own process** — no `mgfxc`, no
  `fxc.exe`, no Wine, no PATH plumbing. This is the native MGCB route, and the only one: MGCB
  compiles effects in-process and launches no external effect compiler, so the previously
  documented "put ShadowDusk on PATH as `mgfxc`" override never fired.
  - **The target comes from the content project's own `/platform:` line** (`Windows` → DirectX 11,
    the GL-family platforms → OpenGL, consoles → a loud `SD0501`). No ShadowDusk-specific flag is
    ever required for correct output. Optional processor parameters: `DebugMode`, `Defines`,
    `IncludeDirs`, and the escape hatches `ShaderProfile` (reaches DirectX 12 / Vulkan, which
    MGCB's platform list cannot name), `MgfxVersion`, `DxbcBackend`.
  - **The `.mgfx` inside the `.xnb` is byte-for-byte the ShadowDusk CLI's** output for the same
    source and target, because the plugin is an adapter onto the same `EffectCompiler` and adds no
    compilation logic. Proven, not asserted: `MgcbPluginByteIdentityTests` (14/14, under
    `dotnet test`, compared against the real CLI binary as a separate process) and the new
    `validation/MgcbPlugin` driver (7/7 through a real `dotnet mgcb`, which additionally checks the
    `.xnb` envelope equals MGCB's own stock output and the payload differs from it). Same payload
    out of `dotnet mgcb` 3.8.2.1105, 3.8.3, 3.8.4, 3.8.4.1 and 3.8.5, and out of the packed
    `.nupkg` extracted into a bare directory.
  - Shader errors surface through MGCB in the canonical `file(line,col-col): error CODE: message`
    form, from the CLI's own formatter (source-linked, so the two cannot drift), with the
    underlying compiler's words verbatim beneath. `#include`d files are registered as build
    dependencies.
  - The package is **tools-only** (no `lib/`; everything under `tools/net8.0/any/`), because MGCB
    resolves a referenced plugin's dependencies — managed and native — from the plugin's own
    directory. It is a `DevelopmentDependency` and contributes nothing to a consumer's shipped game
    assembly. `release.yml` fails the release red if any native is missing from it, or if it ships
    MonoGame's content-pipeline assembly.
  - `samples/mgcb` gained `Content/Content.ShadowDusk.mgcb`, the same corpus built through the
    plugin alongside the stock one.
- **`ShadowDusk.MgcbPlugin` is now a published package**, making it the **eighth** `ShadowDusk.*`
  NuGet. `release.yml`, `RELEASING.md`, the `/release` skill, and the package-count mentions in
  `CLAUDE.md` / `Brand/README.md` were updated together.

### Changed

- An opt-in third arm on the DirectX 12 Apos.Shapes gate (`SHADOWDUSK_DX12_PROBE_MGFX`) that renders
  an arbitrary supplied `.mgfx` alongside the golden and candidate. Off unless the variable is set,
  and its result is reported, never asserted.
- **Root-caused the DirectX 12 Apos.Shapes gallery's `maxd 1`: it is the pinned DXC build, not a
  ShadowDusk defect.** ShadowDusk compiles DXIL with `dxcoob 1.7.2212.40` (the `Vortice.Dxc` 3.3.4
  pin); the `mgfxc` `DirectX_12` golden was built with MonoGame 3.8.5's bundled `dxcoob 1.8.2505.32`.
  Feeding ShadowDusk's own pre-parsed HLSL and own DXC flags to a DXC 1.8 build reproduces the
  golden's DXIL instruction-for-instruction, and rendering that payload in ShadowDusk's own container
  gives maxd 0 with zero differing pixels of 402,984. The delta reaches a pixel at all only because
  the shader adds half an 8-bit LSB of dither immediately before quantization. `maxd 1` stays the
  honest DX12 tolerance until the DXC pin moves. No compiler behavior changed.
- **The interactive ShaderToy viewer and its MonoGame runtime helper moved out of the Phase-46
  experiment tree into `samples/ShaderToyViewer/`** (Phase 51 A4, closing the Phase 47
  sample-migration appendix that had stayed *Planned*). The `ShaderToyEffect` helper is folded into
  the sample as `Runtime/ShaderToyEffect.cs` rather than kept as a separate
  `ShadowDusk.ShaderToy.Runtime` project: it is one file with one public type, and folding it in
  makes the MonoGame boundary structural, since the only projects that reference MonoGame are now
  under `samples/`, `validation/`, and the out-of-band render-proof driver, never under `src/`.
  Namespaces moved with it (`ShadowDusk.ShaderToy.Sample` and `ShadowDusk.ShaderToy.Runtime` became
  `ShadowDusk.ShaderToyViewer` and `ShadowDusk.ShaderToyViewer.Runtime`), which disambiguates the
  sample from the `ShadowDusk.ShaderToy` product library that now owns that name. Like every other
  sample it stays out of `ShadowDusk.slnx`; run it with
  `dotnet run --project samples/ShaderToyViewer` (add `-- --smoke` for the headless self-test, which
  is green 4/4 and regenerates the committed eyeball PNGs byte-identically). **Relocation only: no
  compiler code, no shipped package, and no output byte changed**, and
  `NoMonoGameInProductLibrariesTests` stays green on both TFMs. `tools/shadertoy2fx/` keeps the
  standalone PoC CLI (still the only entry point to the converter's `--multipass` batch mode) and
  the out-of-band fidelity/gallery render-proof driver, which now source-links the single helper
  file from the sample.
- **The ShaderToy converter emits a DirectX-valid compile-profile header.** It used to write
  `vs_3_0`/`ps_3_0` in *both* arms of its `#if OPENGL … #else … #endif`, so the DirectX arm asked for
  a profile MonoGame's `DirectX_11` shader profile refuses — real `mgfxc /Profile:DirectX_11` failed
  every converted shader with *"Invalid profile 'vs_3_0'. Vertex shader 'VSMain' must be SM 4.0 level
  9.1 or higher!"*. The header is now gated on **`SM4`** (the macro MonoGame's own DirectX_11 profile
  defines): DirectX gets `vs_4_0_level_9_1`/`ps_4_0_level_9_1`, while OpenGL **and FNA** keep
  `vs_3_0`/`ps_3_0`. `SM4` rather than the stock `#if OPENGL … #else` split precisely because that
  `#else` arm also catches the FNA target, whose `fx_2_0` output is capped at Shader Model 3.
  **If you regenerate a `.fx` from a `.glsl`, its header text changes** — 81 converter goldens and 2
  multipass goldens moved with it. **No compiled output moved:** ShadowDusk's OpenGL `.mgfx` for the
  pinned fixture is byte-identical before and after, and mgfxc's OpenGL golden regenerated
  byte-for-byte identical.
- **The DirectX target now rejects compile profiles below MonoGame's floor, matching `mgfxc`
  (new diagnostic `SD0015`).** A profile can be perfectly recognized — so the Phase 48 `SD0013`
  check passes — and still be one the reference compiler refuses for this target. `mgfxc`'s
  `DirectX_11` profile accepts **only** `{vs,ps}_4_0_level_9_1`, `_4_0_level_9_3`, `_4_0`, `_4_1`,
  and `_5_0`; the accepted set was established by sweeping every recognized profile through the
  pinned `mgfxc` rather than inferred from the names, which matters because `_4_0_level_9_0` **and
  every SM6 profile** are refused too. **This turns previously-succeeding DirectX compiles into
  loud rejections**, for shaders real `mgfxc` was already refusing: 20 corpus fixtures flipped,
  including 14 vendored Nez post-process shaders (which name `ps_2_0`/`ps_3_0` outright — Nez
  targets DesktopGL), `FnaMultiPassStates.fx`, and `examples/Ex{Int,Mat3}UniformMember.fx`. The fix
  a consumer applies is the standard MonoGame `#if OPENGL … #else …` header, which the diagnostic
  names. OpenGL, Vulkan, DirectX 12, and FNA are **unaffected**: their floors are different
  (measured and recorded in `docs/validation-matrix.md` §8.1) and enforcing them is separate work.
  No output bytes changed on any target.
- `FnaMultiPassStates.fx` was dropped from the **DirectX** arm of the cross-host byte-identity
  manifest **and from the DirectX arm of `Vkd3dCorpusProbe`**, which captures the desktop ground
  truth for the WASM vkd3d byte-identity gate (it stays in the OpenGL and FNA arms of both). It
  compiles `vs_2_0`/`ps_2_0`, so its DirectX row was pinning bytes the reference compiler cannot
  produce. The probe keeps its own corpus list in step with `CrossHostByteIdentityTests`, and
  missing it there turned the WASM gate red on the first CI run that exercised it, which is the
  reason that gate is not skipped on a PR that changes the reject set. The node gate's corpus is
  now **94** stage compiles rather than 98, the four removed being this fixture's one vertex and
  three pixel entries on the DirectX arm.
- **`NoMonoGameInProductLibrariesTests` gained a narrow, named exemption** for
  `ShadowDusk.MgcbPlugin` — an MGCB plugin cannot exist without the
  `ContentImporter`/`ContentProcessor` contract — plus a second test pinning that the reference
  stays `IncludeAssets="compile" PrivateAssets="all"`, which is what keeps it harmless. No other
  `src/` project may name MonoGame, and none does.
- The MGCB documentation across the site (`guides/mgcb-content-pipeline.md`,
  `samples/mgcb.md`, `index.md`, `getting-started/overview.md`, `contributing/index.md`,
  `api/index.md`, `README.md`, `docs/the-purpose.md`) now documents the plugin as the MGCB route
  instead of describing it as an unimplemented scaffold.

### Fixed

- **The Apos.Shapes gallery harness named the wrong shape in every divergence it reported.** Its
  per-cell rectangles came from the untransformed layout while the scene renders through a 1.15x
  scale plus a (6,4) translate, so a shape drawn in layout cell (3,3) lands in screen cell (4,4).
  That is why the DX12 delta above was recorded first against `DrawCircle`/`FillArc` and later
  against `FillRing`; the pixels are `DrawEllipse`'s. Cell rectangles now go through the view matrix.
- **A third of the Apos.Shapes gallery was being drawn but never compared.** The render target was
  sized to the untransformed 600x500 layout, so the same 1.15x scale pushed the entire last column
  off the right edge and the last row down to a ten-pixel sliver: 10 of the 30 cells contributed no
  pixels to any comparison, while the OpenGL visibility check still reported 30/30 because it was
  measuring those same untransformed rectangles. The target is now sized to the transformed extent.
  The gallery has no stored reference images, so no goldens needed regenerating; re-verified after
  the change at DX11 maxd 0 (both arms), Vulkan maxd 0, OpenGL 30/30 genuinely visible, DX12 maxd 1.
- **`#include` de-duplication and cycle detection no longer guess whether the file system is
  case-sensitive from the operating system** (bug-hunt 2026-07-27 N17). The rule was
  "Linux is case-sensitive, everything else is not", which is wrong on two hosts ShadowDusk
  ships to: **Android's file system is case-sensitive** (and .NET's `OperatingSystem.IsLinux()`
  is false there), and **APFS can be formatted case-sensitive**. On those hosts two genuinely
  distinct headers whose names differed only by case were treated as one file, so a
  `#pragma once` in the first silently suppressed the second, and a legal include chain through
  a case twin was rejected as a false circular-include error. Resolved paths are now compared
  the way the storage they came from spells them: ordinal by default, with two case-only
  variants merged only when the file system confirms they are one file. Output bytes are
  unchanged for every input that resolved the same way before.
- **OpenGL-target compiles could crash on hosted CI with a DXIL validation error** (e.g.
  GitHub Actions `windows-latest`, `dotnet test`): `error: DXIL container mismatch for
  'PSVRuntimeInfoSize' ... Validation failed`, while the identical source compiled fine for
  DirectX on the same runner and on a real dev machine (issue #185). The OpenGL path compiles
  the shader twice: once targeting OpenGL (SPIR-V, shipped) and once targeting DirectX (SM6
  DXIL) solely to reflect parameters from the native DXIL oracle. Hosted runners can carry
  their own preinstalled Windows SDK `dxil.dll`/`dxcompiler.dll` on PATH, version-skewed
  against the ones this library is pinned to; when the mismatched `dxil.dll` wins native
  resolution, DXC's validator rejects an otherwise-correct module. The reflection-only
  companion compile now passes DXC's `-Vd` (skip validation) — its bytes are discarded after
  reflection and never shipped, so skipping validation there cannot ship an invalid module.
  Every shipped compile (OpenGL SPIR-V, DirectX DXBC, DirectX 12 DXIL) still validates fully.

### Known issue (found, filed, deliberately not fixed here)

- **A pixel shader whose DXC compile cancels an algebraic identity that mgfxc's fxc compile
  does not can carry a phantom effect parameter** (issue #187, split out from #185 while
  investigating the DXIL-validation fix above). `GradientToy.fx` computes `fragCoord = uv * iResolution.xy`
  then `uv2 = fragCoord / iResolution.xy`; DXC's `-spirv` backend cancels the identity
  entirely, so the shipped GLSL never references `iResolution`, while `fxc`/`mgfxc` do not
  perform the same cancellation and the committed `mgfxc` golden's GLSL still reads it.
  ShadowDusk's OpenGL reflection sources from a separate DXIL companion compile that also
  does not cancel it, so `Parameters["iResolution"]` exists but is inert (`SetValue` writes
  nowhere). Reflecting from the SPIR-V that actually ships instead was tried and reverted: it
  removes the phantom but makes the parameter list diverge from the mgfxc golden **by name**,
  which is the project's primary compatibility bar — trading one divergence for a different
  one, not a fix. The real root cause is upstream of reflection (ShadowDusk's DXC compile and
  mgfxc's fxc compile produce non-equivalent GLSL for this shader) and needs its own scoped
  fix. Pinned by `GlPhantomParameterTests` (`Skip`-marked pending that fix, not deleted).

## [0.16.0] - 2026-07-30

> The MonoGame pin stays 3.8.2.1105 and the default output stays MGFX v10. The golden corpus and
> the byte-identity manifest are **untouched**: the OpenGL and DirectX 12 sampler-table fix below
> changes output only for shapes that previously failed to compile or were silently mis-bound, and
> every 1:1 texture/sampler shader (which is the whole golden corpus) is byte-identical.

### Added

- **`DeferredSpriteMrtGl`, the first render gate in the repo that binds more than one render
  target**, closing the multiple-render-target render rung that had been open since the
  `DeferredSprite.fx` compile fix in June. It draws the shader on real MonoGame DesktopGL with
  two targets bound, reads **both** attachments back, and pixel-diffs each against the real
  `mgfxc` OpenGL golden: maxd 0 on both. Every other GL gate binds one target, so none of them
  could tell "the second output reached attachment 1" from "the second output went nowhere",
  and a structural match cannot either — that output lives in the emitted GLSL, not in the
  `.mgfx` record tables. Wired into `validation-render.yml` so it runs in CI on Mesa llvmpipe.
  Alongside the mgfxc diff it asserts the exact values the HLSL implies and names which failure
  mode a wrong picture is, because the mutation check (binding one target instead of two) leaves
  the diff arm reporting maxd 0 — both sides broken identically — and only the absolute arm
  catches it.
- **`ShaderToyRouteGl`, a render gate for the ShaderToy / `.glsl` frontend route**, which had been
  compile-proven and fidelity-proven but never held to the reference compiler. It converts
  `GradientToy.glsl` in process with the real converter and pixel-diffs ShadowDusk's OpenGL build
  against **`mgfxc`'s build of the same converted `.fx`** on real MonoGame DesktopGL: maxd 0, in CI.
  An `mgfxc` oracle exists here despite the docs saying otherwise, because "no oracle" is true of
  ShaderToy *input* — the converter's *output* is ordinary HLSL that `mgfxc` compiles like any other,
  so the downstream half of the route can be held to the real product bar. The gate asserts the
  converter still emits the committed `.fx` the golden was built from before it renders, so converter
  drift turns it red instead of leaving the golden describing a different shader.
- **`SamplerPairsGl`, a new OpenGL rung-4 render gate** for per-(texture, sampler)-pair sampler
  records, wired into `validation-render.yml` so it runs in CI on Mesa llvmpipe. Both of its arms
  render an *asymmetric* function of two samplers so that a mis-binding changes the picture (a
  symmetric `diffuse * light` would render identically under a swap and prove nothing), and arm A
  reports *which* failure mode a wrong colour corresponds to rather than just "wrong".

### Known issue (found, filed, deliberately not fixed here)

- **A converted ShaderToy `.fx` compiles for DirectX in ShadowDusk and is rejected by `mgfxc`.** The
  converter emits `vs_3_0`/`ps_3_0` in *both* arms of its `#if OPENGL` header, so the DirectX arm asks
  for a profile below MonoGame's DirectX floor; real `mgfxc /Profile:DirectX_11` refuses it (*"must be
  SM 4.0 level 9.1 or higher"*) while ShadowDusk compiles the identical file successfully. Surfaced
  while adding the gate above, and only because the new fixture joined the auto-globbed corpus. Two
  separable defects (the converter's emitted profile header, and a reject-fidelity gap of the Phase 48
  class that `SD0013`/`SD0014` do not cover), each with its own blast radius: one moves every converter
  golden, the other turns a currently-succeeding compile into a rejection. Filed as Phase 51 A10 with
  the ordering constraint that fixing the reject side alone would break the route's own output.
  **Both halves are fixed in `[Unreleased]`** — see the entries there; this note stays as the record
  of when the divergence shipped.

### Fixed (compiler)

- **OpenGL now compiles several textures read through one shared `SamplerState`** — the classic
  diffuse+lightmap shape, ordinary HLSL that `mgfxc` has always compiled (its own golden for the
  shape carries two sampler records). ShadowDusk rejected it outright with `SD0216`. The GL sampler
  table is now keyed on the **(texture, sampler) pairs** SPIRV-Cross folds into combined samplers
  rather than on the reflected samplers, which is the only list that can be right: the GL runtime
  looks each record up by GLSL uniform name, and there is one uniform per pair.
- **A second, silent OpenGL mis-binding that the `SD0216` guard could not see.** SPIRV-Cross
  declares combined samplers in **first-use** order, not declaration order, so a shader sampling
  two textures through two samplers in reverse order produced matching counts — two uniforms, two
  records, guard satisfied — while both the texture parameter and the sampler-type byte came out
  **swapped**. Probed with a `Texture2D` + `TextureCube` pair: the emitted GLSL declared `ps_s0` as
  `samplerCube` while the record claimed 2D, so MonoGame bound a 2D texture to a cube sampler unit.
  It compiled cleanly with no diagnostic. The same mis-numbering affected any shader mixing legacy
  `sampler2D` with modern `Texture2D` + `SamplerState` declarations.
- **DirectX 12 had the shared-`SamplerState` bug too**, and silently: it was never included in the
  texture-keyed branch, so the diffuse+lightmap shape emitted one record and the second texture was
  never bound, with no diagnostic. DirectX 11 was always correct. Found while closing the OpenGL
  work.
- The pair list is derived in **pure managed code** from the SPIR-V (`SpirvCombinedSamplerPairs`),
  not by calling SPIRV-Cross's own `spvc_compiler_get_combined_image_samplers`, because the browser
  host's `spirv-cross.wasm` does not export that function — a native call would have fixed desktop
  only and broken the guarantee that the CLI and the browser emit identical bytes.

### Fixed (CI / evidence)

- **Every workflow now installs the .NET 10 SDK alongside .NET 8.** All seven pinned
  `dotnet-version: '8.0.x'` while six `src/` libraries multi-target `net8.0;net10.0`; the
  `net10.0` leg was being satisfied only by whatever the runner image happened to preinstall.
  That is an undeclared dependency in `release.yml` — the workflow that publishes — so a runner
  image change could have broken a release with no prior signal. `pack-consume.yml` also gained a
  `tfm` matrix dimension, so the scratch consumer now restores and runs against **both** shipped
  TFMs rather than `net8.0` alone; a broken `net10.0` asset previously had no end-to-end gate.
- **The retracted MGCB "expose ShadowDusk as `mgfxc` on `PATH`" claim is now corrected
  everywhere it appeared**, not only on the four docfx pages fixed earlier. It survived in
  `src/ShadowDusk.Cli/README.md` — which ships *inside the NuGet package* — and in the site's
  Overview delivery-shapes table, `README.md`, and `docfx/index.md`. All now point at the routes
  that work: invoke the CLI directly and `/copy:` the `.mgfx`, or compile at runtime.
- **The GLSL rewriter-rule docs no longer describe the retired sampler-slot model.**
  `docs/glsl-uniform-naming.md` and `docs/references/compilation-pipeline.md` (both transcluded
  into the published site) still said samplers were `ps_s{slot}` "looked up by slot"; they now
  document the per-(texture, sampler)-pair, first-use-order model this release shipped, including
  what `SD0217` cross-checks and why the pair list is derived in managed code.
- Assorted support-surface drift corrected in the same pass: the rung-4 list gained the three new
  render gates; the CI GL-gate count went from three to **seven** in both `docs/validation-matrix.md`
  and the gate script's own help text; the MGFX v10 floor reads **3.8.1.263** (the measured floor)
  rather than 3.8.2 in nine places; fixture counts in `docs/repository-layout.md` (144 `.fx`);
  `docs/test-shader-corpus.md` gained the sampler-pair and ShaderToy-route fixtures plus a
  last-updated line; `project_facts.md` no longer records the *reverted* `Apos.Shapes` 0.7.12 bump
  as shipped; and two stale references to the retired `SD0215`/`SD0216` are gone.

- **Test-results artifacts stopped discarding 13 of every 14 assemblies' results.** Every
  `dotnet test` invocation in `ci.yml` and `release.yml` passed a fixed
  `--logger "trx;LogFileName=…"` while running 14 assemblies (7 projects across `net8.0` and
  `net10.0`) concurrently, so they all wrote the same file — the logs were full of
  `WARNING: Overwriting results file` — and the uploaded artifact held whichever assembly
  happened to finish last. All five invocations now use `LogFilePrefix`, which emits one
  `<prefix>_<tfm>_<timestamp>.trx` per assembly (measured on the real solution: 14 files, zero
  overwrite warnings). A guard step fails the integration job if only one `.trx` lands, so a
  revert cannot silently re-lose the results. This matters beyond tidiness: the ubuntu
  integration lane intermittently crashes a test host, and the crashed assembly's `.trx` was
  precisely the one guaranteed to be overwritten, which is why that crash had been re-derived
  from log adjacency three times — and mis-attributed each time. No product code is affected.

### Changed

- **The test suite moved off `FluentAssertions` onto `Shouldly` 4.3.0, and FluentAssertions is now
  banned** (issue #171). This is a licence obligation, not a preference: FluentAssertions 8.x
  relicensed to the Xceed "Community License Agreement (for Non-Commercial Use)", which requires a
  paid commercial licence for any organisation that earns revenue. We had been capped at 7.2.2 (the
  last Apache-2.0 release), but that line receives no further fixes, so the cap only deferred the
  work onto a frozen dependency. Shouldly is BSD-3-Clause at every version with no commercial gate.
  All 7 test projects and ~4,000 assertion sites across 132 files were converted; the suite is green
  at the same 2,394 tests per target framework as before the migration. Nothing shipped changes:
  no `ShadowDusk.*` package ever referenced FluentAssertions, so consumer output and dependency
  graphs are untouched.
  - Two Shouldly differences were **not** mechanical and are worth knowing when writing new tests.
    String `ShouldContain`/`ShouldNotContain` default to **case-insensitive** where FA's
    `Contain`/`NotContain` were case-sensitive, so every string-receiver site now passes
    `Case.Sensitive` explicitly — without it roughly 900 assertions over generated GLSL/HLSL would
    have silently weakened, and no test failure would have revealed it. And FA's `BeEquivalentTo`
    compared structurally where Shouldly's `ShouldBe(…, ignoreOrder: true)` compares with `Equals`,
    so collections of reference types without value equality use `ShouldBeEquivalentTo`.
  - FluentAssertions is now **banned** by standing rule, recorded in `project_facts.md`,
    `project_rules.md`, `CLAUDE.md`, and the `Directory.Packages.props` comment. Deliberately a
    written rule rather than a repo-scanning test: the thing being prevented is an author
    reaching for the familiar `.Should()` API, which the rule addresses where authors read.
- **`SD0215` and `SD0216` are retired**, and their numbers are marked do-not-reuse in
  `docs/error-codes.md`. Both existed only because of the old sampler-keyed GL table: `SD0216`
  rejected the shared-`SamplerState` shape, and `SD0215` rejected sampler registers that were not
  contiguous from `s0` (the record used to be named after the sampler's bind slot). Neither
  restriction applies now — a `register(s3)`-only shader compiles correctly. The new **`SD0217`**
  covers input shapes the declaration-order model does not cover, plus an internal cross-check of
  the derived pair count against the sampler uniforms the emitted GLSL actually declares; ordinary
  HLSL never raises it.
- OpenGL texture parameters **keep the plain texture name** (`DiffuseMap`) rather than adopting
  `mgfxc`'s MojoShader `<sampler>+<texture>` spelling (`TextureSampler+DiffuseMap`), which its
  OpenGL goldens use for every modern-syntax shader. This is a deliberate, recorded decision:
  MonoGame resolves a sampler's texture through the record's parameter *index* and never its name,
  so the two spellings behave identically; renaming would break every existing consumer's
  `Parameters["DiffuseMap"]` lookup; and ours is the same name the DirectX, DirectX 12, Vulkan, and
  FNA targets use, whereas `mgfxc`'s is OpenGL-only and makes an effect's parameter names depend on
  the backend.

- **The shipped libraries now multi-target `net8.0` and `net10.0`.** `ShadowDusk.Core`, `.HLSL`,
  `.GLSL`, `.Compiler`, `.Metal`, and `.ShaderToy` build for both, and all seven test projects run
  against both — **4762 tests green (2381 on each)** — with the compiler's output verified
  byte-identical across them. This is deliberately multi-targeting rather than a move to .NET 10:
  a `net10.0`-only package cannot be referenced from a `net8.0` project, which is what most
  MonoGame/KNI games still target, so bumping would have broken existing consumers. .NET 8 reaching
  end of support in November 2026 no longer strands the packages. `ShadowDusk.Cli` (a dotnet tool,
  which rolls forward onto newer runtimes), `ShadowDusk.MgcbPlugin` (a stub), and
  `ShadowDusk.Wasm` (`net8.0-browser`) stay single-TFM for now.
- The forward-compatibility matrix now covers **every MonoGame release that can load ShadowDusk's
  output, not one anchor version**. One unchanged **v10** build renders **pixel-identically (max
  delta 0) across seven consecutive releases — 3.8.1.263, 3.8.1.303, 3.8.2.1105, 3.8.3, 3.8.4,
  3.8.4.1, and 3.8.5 stable** — 70 renders in total, all within tolerance of the mgfxc goldens
  (`validation/ForwardCompat`). The **floor is now measured rather than assumed**: every stable
  `MonoGame.Framework.DesktopGL` release was probed, and 3.8.0.1641 is the one that rejects our
  output (*"This MGFX effect seems to be for a newer release of MonoGame"* — its loader predates
  MGFX v10), which makes **3.8.1.263** the true floor. Nothing about the product changed to earn
  this; it is the same compiler, the same default options, and the same bytes.
- The opt-in **MGFX v11** output is re-proven against **3.8.5 stable** instead of
  `3.8.5-preview.6`, with the result table unchanged cell for cell (`validation/MonoGameV11`).
- `validation/AndroidGl` moved to `MonoGame.Framework.Android` 3.8.5. Build-verified only: the
  on-device proof was taken on 3.8.4.1 and has not been repeated, which the csproj, the Phase 50
  notes, and the validation matrix all state so the pin is not misread as render evidence.

### Changed

- Dependency currency pass. **Nothing a consumer downloads changed**: the only packages the shipped
  `ShadowDusk.*` libraries reference are `Silk.NET.SPIRV.Cross.Native` (already latest) and
  `Vortice.*` (the DXC pin, deliberately held — see below). Everything updated here is
  test/validation/build-only: `xunit` 2.9.2 → 2.9.3, `xunit.runner.visualstudio` 2.8.2 → 3.1.5,
  `Microsoft.NET.Test.Sdk` 17.11.1 → 18.8.1, `coverlet.collector` 6.0.3 → 10.0.1,
  `FluentAssertions` 6.12.2 → 7.2.2, `docfx` 2.78.3 → 2.78.5.
  No vulnerable or security-deprecated package was found anywhere before or after.
- **Two dependencies are now explicitly capped for licensing reasons, with the reason recorded at
  the pin** so a future currency sweep does not undo it. `FluentAssertions` stays on the **7.x**
  line because 7.2.2 is the last Apache-2.0 release and **8.x** relicensed to an Xceed
  non-commercial community licence that requires payment for commercial use.
  `SixLabors.ImageSharp` stays on **3.1.12** because **4.0.0 refuses to build at all** without a
  paid Six Labors licence key. `Vortice.*` stays at 3.3.4/3.5.0 because that version *is* the
  pinned DXC commit (`e043f4a1`) our macOS/Android/WASM natives are built from; moving it is an
  output-affecting change, not a routine bump.
- **`Apos.Shapes` stays at 0.7.7 — it is an evidence pin, and the reason is now recorded at the
  pin.** A bump to 0.7.12 was attempted and reverted: the Phase 55 shape-gallery proof uses the
  package's own embedded effect as its baseline arm, which is only a valid comparison because
  0.7.7's shader *is* the vendored `apos-shapes-sm6.fx` (upstream `a85a31c`). 0.7.12 pins a
  different upstream commit (`b69bd73`) whose shader differs by ~1150 lines and adds a **fourth
  sampler** (`ArcTex` at `t2`, displacing `BlueNoiseTex` to `t3`) — so the bump would have
  silently pointed the baseline arm at a different shader than the candidate. Re-pinning it means
  vendoring the new upstream revision and re-running that phase's proof.
- The `mgfxc` used to generate the golden corpus is now **pinned and version-checked** rather than
  discovered as "the newest `mgfxc.exe` in the NuGet cache". `tools/compile-fixtures.ps1` resolves
  it from the `dotnet-mgcb` version in `.config/dotnet-tools.json`, invokes it through the `dotnet`
  host so it behaves the same on every OS, and **asserts the MGFX version byte of every file it
  writes**, refusing to overwrite the v10 corpus with a different container version.
  `validation/ReservedWordGl` uses the same pin for its reference-compiler arm. Verified: mgfxc
  3.8.2.1105 and 3.8.4.1 each reproduce all 46 committed goldens byte-for-byte on both OpenGL and
  DirectX_11.
- Documentation correction across `docfx/guides/mgcb-content-pipeline.md`, `docfx/samples/mgcb.md`,
  `docfx/guides/dropin-mgfxc.md`, and `docfx/cli/index.md`: the **"expose ShadowDusk as `mgfxc` on
  `PATH` and MGCB will use it" integration does not work**, and these pages had documented it as the
  shipping path. Measured against `dotnet mgcb` 3.8.2.1105, 3.8.4.1, and 3.8.5 with a real logging
  `mgfxc.exe` first on `PATH`: zero invocations in all three, and a valid `.xnb` produced each time.
  MGCB compiles `.fx` in-process and launches no external effect compiler; MonoGame 3.8.5's new
  code-centric Content Builder has no external-tool seam either. The pages now document the routes
  that do work: invoke the CLI directly and `/copy:` the resulting `.mgfx`, or compile at runtime and
  hand the bytes to `Effect`. Compiling with the ShadowDusk CLI or library is unaffected.

### Fixed

- The out-of-band ShaderToy render-proof driver (`tools/shadertoy2fx/render-proof`) had been dead
  since Phase 47, in two stacked ways. **(1)** That phase promoted the converter and its corpus
  in-solution to `tests/ShadowDusk.ShaderToy.Tests/`, but the driver kept looking under
  `tools/shadertoy2fx/tests/…` and exited with "authored corpus not found" before rendering
  anything; it now probes the current location first and falls back to the legacy one.
  **(2)** With that fixed, it then hung indefinitely. All four of its child-process helpers drained
  the CLI's stdout to EOF *before* reading stderr, which deadlocks as soon as the child writes more
  to stderr than the pipe buffer holds: the child blocks writing, so it never exits, so stdout never
  reaches EOF. Latent until Phase 53 made warnings print by default — a corpus shader that trips
  `SD0402` on a dozen loops now emits well past the buffer. **The compiler was never implicated**
  (the shader that hung it compiles in 0.38 s when run directly); all four call sites now drain both
  pipes concurrently through a shared `ProcessCapture` helper. Nothing caught either failure because
  the driver is deliberately not in `ShadowDusk.slnx`. With both fixed the gate is green again and
  broader than when it last ran: **53/53 shaders match the original GLSL within tolerance, 0
  diverged, 0 errored** (Phase 47 recorded 46/46; the extra seven are corpus growth and they pass
  too, so the converter never regressed).
- `tools/compile-fixtures.ps1` could not regenerate the golden corpus at all, in two independent
  ways. Its mgfxc probe selected the highest version in the NuGet cache, which resolved to
  `dotnet-mgcb-editor-windows` 3.8.4.1's `mgfxc.exe` - a binary that throws
  `Could not load file or assembly 'SharpDX.D3DCompiler'` on every shader because that package ships
  it without its dependency, so a regeneration would have compiled 0 of 46 and reported them all as
  failures. Separately, a bare no-argument run globbed **0 shader files**: a `[string]` parameter
  defaulted to `$null` arrives as an empty string, so the `$ShaderDir ?? (default)` fallback never
  fired and the script only worked when every path was passed explicitly.

## [0.15.1] - 2026-07-28

### Added

- New `SD0216` diagnostic: on the OpenGL target, the emitted GLSL declares a different number of
  sampler uniforms than the effect's sampler table has records, so some `ps_s{k}` would never be
  assigned a texture unit and would silently sample unit 0. It fires for several textures read
  through one shared `SamplerState`, which SPIRV-Cross expands into a combined sampler per
  (texture, sampler) pair while the GL table is keyed on samplers. Previously that shipped a
  table that could not bind; now it is a compile error naming the fix.
- New `SD0006` / `SD0007` diagnostics for the ShaderToy/GLSL front end. Its convert errors and
  warnings were emitted under `SD0010` and `SD0001`, which are already allocated to "effect
  source contains no techniques" and "`#include` file not found" — so a converter failure
  printed a code whose published meaning was unrelated and unactionable. Registered in
  `docs/error-codes.md`.
- `SD0403` now also flags the integer bitwise and modulo operators (`&`, `|`, `^`, `~`, `%`).
  They are reserved below GLSL 1.30 / ES 3.00 by the same specification sentence as the shifts
  it already flagged, and SPIRV-Cross emits them verbatim for signed-`int` operands, where no
  `uint` token appears for the existing unsigned check to catch — so an ordinary
  checkerboard/hash/mask shader shipped with no signal and failed `Effect`-load on Mesa, macOS
  OpenGL, and WebGL1.

### Changed

- Security: the vkd3d-shader loader's dev-convenience `tools/vkd3d/` probe now runs **after**
  the packaged-native probe and is bounded to a directory that actually looks like a ShadowDusk
  checkout. It previously walked to the filesystem root ahead of the NuGet
  `runtimes/<rid>/native` lookup, so on Windows — where the volume root is add-subdirectory
  writable by ordinary users — a planted `C:\tools\vkd3d\libvkd3d-shader-1.dll` would have been
  loaded and executed inside a framework-dependent consumer's process, and any unrelated
  `tools/vkd3d` on the path could silently displace the pinned, hash-verified native.
- `RuntimeProfileDetector.Recommend` now refuses a target no `CapabilityProfile` models instead
  of falling through to the OpenGL profile. Because a set `Profile` overrides
  `CompilerOptions.Target`, `Vulkan` and `DirectX12` silently compiled to a MojoShader-GLSL
  `.mgfx` the consumer's runtime cannot load, and `Metal` bypassed the pipeline's own `SD0200`
  rejection. Setting `Target` directly with `Profile` left null is unaffected and remains the
  supported path for both.
- The release workflow's published-CLI smoke now also compiles `/Profile:DirectX_11` (and runs
  from outside the checkout on Windows too, as it already did elsewhere). It only ever compiled
  `/Profile:OpenGL`, which drives DXC and SPIRV-Cross but never vkd3d-shader — so half the
  single-file bundle's native surface had no guard on any platform.
- `wasm.yml` now fails hard when the DXC→WASM module is missing, instead of warning and silently
  skipping the entire `ShadowDusk.Wasm` build and pack. The module is force-committed, so its
  absence is a repo regression; the old guard let a green run cover nothing.

### Fixed

- **DirectX / DirectX 12: two textures sharing one `SamplerState` now emit one `.mgfx` sampler
  record per texture.** The table was built from the reflected samplers, so the classic
  diffuse+lightmap shape got a single record: MonoGame's `ApplySamplers` only binds the slots it
  is handed, so every texture after the first was never bound and
  `Parameters["Lightmap"].SetValue(tex)` silently did nothing, with exit 0. `mgfxc` keys its own
  DX table on the reflected textures and its golden carries one record per texture; this closes
  the last "sampler slot / baked-state" divergence in the Phase-41 structural matrix.
  The OpenGL table stays keyed on samplers, because there a record must NAME a `ps_s{k}` uniform
  the emitted GLSL actually declares, and SPIRV-Cross declares one combined sampler per
  (texture, sampler) **pair** — a texture-keyed GL table would drop the mirror shape (one texture
  read through two `SamplerState`s, the linear+point idiom), leaving `ps_s1` on texture unit 0.
  The new `SD0216` makes the residual GL case loud instead of silent (see Added).
- `--target-runtime=<name>` (the `=` form) is now parsed. It fell through to the silent
  unknown-flag branch, so `--target-runtime=monogame-gl` compiled with the default profile and
  exit 0: the wrong artifact, with no diagnostic. The space and `:` forms were unaffected, and
  every other long option already accepted all three spellings.
- OpenGL: `trunc()` lowering is now fully parenthesized. `trunc(x)` is a primary expression, so
  splicing the bare product `sign(x) * floor(abs(x))` over it re-associated wherever the
  surrounding operator bound at least as tightly — `1.0 / trunc(x)` became
  `(1.0 / sign(x)) * floor(abs(x))`, valid GLSL with a silently wrong value.
- OpenGL: an omitted HLSL semantic index is now correctly treated as index 0 when naming
  varyings (`: COLOR` ≡ `COLOR0`, `: TEXCOORD` ≡ `TEXCOORD0`, as fxc/mgfxc treat them). DXC
  passes the author's spelling through verbatim, so a SpriteBatch-style pixel-only pass
  declaring `: COLOR` emitted `var_COLOR` instead of `vFrontColor` — a hard link failure against
  MonoGame's built-in SpriteEffect on strict drivers, and garbage on lenient ones.
- OpenGL: the Rule 13 bounded-loop rewrite now also proves the loop bound is **invariant**. It
  derived the ceiling from the bound's initializer without checking that the bound never
  changes, so a body that raised it made the synthesized header exit before the terminal `else`
  finalizer ever ran, leaving its output undefined — issue #160's failure mode, reachable
  through the one property the rewrite's correctness rested on. Those shapes now decline to the
  honest `SD0402` warning.
- `ColorWriteEnable = None;` (and `true` / `false`) now compiles instead of failing `SD0011`.
  `None` is the idiomatic depth-only or stencil-only pass and `mgfxc` accepts all three.
- Render-state and FNA sampler-state values now accept the HLSL float suffix and float-spelled
  integers, via one shared mgfxc-parity numeric parse. `DepthBias = 0.0001f;` hard-failed the
  compile on every target, and `MipMapLodBias = -2.0f;` / `MaxAnisotropy = 4.0;` compiled for
  OpenGL and DirectX but failed for FNA — source `fxc /T fx_2_0` itself accepts.
- `CompilerOptions.WithGraphicsTarget` no longer drops `Defines`, despite documenting that it
  preserves every other setting. The pipeline calls it whenever a `Profile` implies a different
  backend and `Validate`/`ValidateAsync` call it once per target, so
  `--target-runtime monogame-gl /Defines:HIGH_QUALITY=1` compiled with the macro undefined and
  wrote the wrong artifact with exit 0.
- An `#include` resolver's own diagnostic is no longer overwritten with a synthesized `SD0001`
  "cannot find include". A present-but-unreadable header (locked, ACL-denied, or deleted mid-read)
  reported a missing file, making the registered `SD0004` unreachable, and any diagnostic from a
  consumer-supplied `IIncludeResolver` was silently discarded.
- A dropped `#pragma once` line, and a skipped duplicate `#include`, are now blanked rather than
  deleted, so every later line of the enclosing file keeps its `#line`-relative number. DXC
  reported the whole file's diagnostics one line too low, and the CLI, MGCB, and IDE
  jump-to-line all trust that location verbatim.
- DXIL reflection no longer converts a cancellation into an `SD0102` "Reflection failed" error.
  The CLI's watchdog never reported `X0007` "Compilation timed out", and a library consumer's own
  `CancellationToken` stopped behaving per the .NET contract.
- ShaderToy: a parenthesized assignment or comma sequence used as a sub-expression keeps its
  parentheses. The parser drops the source's grouping parens, so the common raymarching idiom
  `if ((d = map(p)) < 0.001)` emitted `if (d = map(p) < 0.001)` — HLSL binds `<` tighter than
  `=`, so `d` received a bool 0/1 instead of the distance and the shader rendered a different
  image with no diagnostic.
- ShaderToy: a `#if` / `#ifdef` / `#ifndef` inside a **skipped** conditional group is no longer
  evaluated, per C11 6.10.1p6 (which the GLSL preprocessor inherits). An expression the evaluator
  could not handle inside a dead `#if 0` branch aborted conversion of a shader every real GLSL
  compiler accepts.
- `samples/mgcb` and `samples/ShaderViewer` can be restored and built again. Both declared
  `Version` on `PackageReference` while inheriting Central Package Management, so `dotnet
  restore` failed `NU1008` and neither sample — including the repo's only demonstration of the
  Tier-1 drop-in delivery shape — could run at all, contrary to their published instructions.
  `ShaderViewer`'s floating `3.8.*` MonoGame reference is also now pinned to 3.8.2.1105.

## [0.15.0] - 2026-07-27

### Added

- CLI: mgfxc's `/Defines:<name=value;...>` flag is now implemented (previously the flag was
  silently ignored and `#ifdef` branches compiled out with exit 0). Library consumers get the
  same via the new `CompilerOptions.Defines` property; the macros ride through both the
  `#define` prepend and the DXC `-D` flags on every backend, including FNA.
- New `SD0403` portability warning: a GLSL-1.30+/ES-3.00-only construct that survived into the
  versionless emitted GL source (`transpose`, `sinh`-family, `isnan`/`isinf`, `texelFetch`,
  bit-casts, `switch`, `uint`, non-square matrices, integer shifts) is now flagged at compile
  time instead of failing at Effect-load only on strict drivers (macOS/Mesa/WebGL1) — the
  class behind issues #149 and #163, made loud up front. Also backstops the round/trunc
  lowerings by flagging any call a rewrite missed.
- FX parser accepts more real-world fxc/mgfxc syntax: `VertexShader = NULL;` /
  `PixelShader = NULL;`, `Texture = NULL;` in `sampler_state` blocks (binds the synthesized
  runtime texture instead of emitting `NULL.Sample(...)`), `technique10` blocks, numeric
  booleans (`AlphaBlendEnable = 1;`), and hex stencil values (`StencilMask = 0xFF;`).
- New diagnostics, all registered in `docs/error-codes.md`: `SD0004` (unreadable include),
  `SD0005` (undetectable input format — was mis-filed under `SD0002`), `SD0028` (Vulkan
  shared-sampler co-location, located at the offending `Sample` call) with `SD0213` as its
  post-compile reflection backstop, `SD0215` (OpenGL sparse sampler registers), `X0009` (CLI
  flag missing its required value — previously silently ignored, compiling with the default),
  and the `SD0214` warning: DirectX12 DXIL compiled on a non-Windows host is unsigned
  (`dxil.dll` signing is Windows-only) and retail D3D12 rejects it at pipeline-state
  creation — previously shipped silently as a per-host output divergence.
- `BlobKind.Dxil`: DXC's SM6 output blobs were mislabeled `Dxbc`/`Spirv` (harmless today,
  a trap for any future kind-keyed dispatch).

### Changed

- CLI diagnostics print the file path exactly as given instead of stripping to the basename,
  so two same-named includes stay distinguishable and IDE/MSBuild jump-to-file works.
- The CLI's `X0099` internal-error catch-all now prints the full exception (type and stack) in
  release builds — it marks a ShadowDusk bug, and the detail is what a bug report needs.
- `.mgfx`/KNIFX annotation counts are now always written as 0, matching mgfxc (MonoGame
  materializes `count` null `EffectAnnotation` slots, so a real count could NRE consumer code,
  and KNI's writer asserts count == 0). Parsed annotations stay in the IR as metadata.

### Fixed

- OpenGL: the issue-#138/#160 bounded-loop rewrite (Rule 13) is now provable for exactly the
  shapes it accepts. An inclusive (`<=`) inner comparison gets a `provenMax + 1` header cap so
  the loop's else-finalizer stays reachable (the #160 dropped-finalizer failure re-created one
  operator over); descending walks, non-unit steps, bounds below the init, and loops whose
  index is read afterward now decline to the honest `SD0402` warning instead of rewriting
  wrong.
- macOS: the released single-file CLI archives could not load their own bundled DXC/vkd3d
  dylibs outside a repo checkout (the per-arch bundle subdirs were never probed in the
  extraction directory). Both loaders now probe `osx-<arch>/` inside every host native-search
  directory, and the release smoke test runs from outside the checkout so CI can catch this
  class.
- Browser/WASM: the SPIRV-Cross shim now sets `RelaxNanChecks` like the desktop transpiler
  (issue #149) — in-browser output for min/max/clamp shaders re-converges with desktop bytes
  and no longer carries the `isnan()` lowering that strict GL front ends reject.
- Vulkan/DirectX 12: a vertex-attribute reflection failure is now a compile-time `SD0101`
  error instead of a silent empty attribute table that crashed at the consumer's first Draw
  with an unattributed `E_INVALIDARG`.
- DirectX 12: `SV_VertexID`/`SV_InstanceID` are no longer minted as phantom TEXCOORD vertex
  attributes (the SPIR-V path already skipped builtins; the DXIL path now matches).
- Vulkan: two textures sampled through one shared `SamplerState` in the same code path now
  fail loudly with a located `SD0028` (the rewriter tracks `#if` branches, so the legal
  cross-branch re-pairing shape still compiles) plus an `SD0213` reflection backstop —
  instead of silently co-locating onto one descriptor and sampling the wrong texture.
- Test infrastructure: the macOS test gates pick the dylib arch by `ProcessArchitecture`
  (not `OSArchitecture`), fixing silently mis-targeted gating under Rosetta 2.
- OpenGL: sparse explicit sampler registers (`register(s3)` with no `s0`) now fail loudly
  (`SD0215`) instead of silently binding the wrong texture units (the `.mgfx` record and the
  emitted GLSL numbered samplers from different sources).
- OpenGL: HLSL semantics are matched case-insensitively in the GL rewriter (`: Position`,
  `: TexCoord0`), matching HLSL's own rules — mixed-case position semantics no longer render
  garbage and mixed-case varyings link correctly; `POSITIONT`-style non-numeric semantic tails
  are a located unsupported-semantic error instead of an unhandled `FormatException`; any
  stage-interface identifier that survives the rewrite is now a loud error instead of invalid
  GLSL that failed only at Effect-load.
- `#include`: Windows-style backslash paths now resolve on Linux/macOS hosts, and an include
  that exists but cannot be read (locked/ACL-denied) returns a located `SD0004` error instead
  of throwing a raw `IOException` through `CompileAsync`.
- Vertex semantics: `PSIZE` (the real D3D9 point-size semantic) now maps to PointSize instead
  of falling through to the TEXCOORD default and colliding with real texture coordinates;
  absurd numeric semantic suffixes no longer throw `OverflowException`.
- DX11/FNA diagnostics: vkd3d-shader's colon-style messages (`file:line:col: E5005: ...`) are
  now parsed into real file/line/column diagnostics instead of collapsing into a single
  line-less `X0000`.
- CLI: a compile wedged inside a native compiler is now hard-terminated by the watchdog with a
  proper `X0007` (previously the documented timeout could never fire on a hung native call and
  MGCB waited forever). A failed sampler-to-texture parameter join now fails the compile via
  the writer's range guard instead of silently pointing the sampler at parameter 0.
- Native loading: the SPIRV-Cross fallback RID map now distinguishes `win-arm64` and
  `linux-arm64` instead of collapsing them to x64; four macOS test gates now key on
  `ProcessArchitecture` instead of the Rosetta-2 `OSArchitecture` trap the production
  loaders already avoid (they silently skipped or mis-targeted coverage on Apple Silicon).
- Determinism: injected `#line` directives and the platform-macro prepend now use `\n` like
  the body they join, so the flattened compiler input no longer differs by build OS
  (previously CRLF-mixed on Windows, visible in debug-mode artifacts via embedded source).
- ShaderToy front-end: `uint`/`uvecN` now map to real HLSL `uint` types with faithful
  unsigned semantics (`>>` zero-fills, `float(x)` is unsigned) and `u`-suffix literals are
  accepted — hash/PRNG shaders no longer silently produce different noise; vector `==`/`!=`
  scalarizes with `all()`/`any()` in every context (not just `if` conditions);
  `for`-conditions get the same paren/vector handling as other conditions; function-like
  macro calls may span lines; `mainSound`/`mainVR` in comments no longer false-reject;
  non-zero `textureLod` is a located convert-time reject instead of doomed generated HLSL;
  nested-block shadowing no longer poisons type inference; locals shadowing emitter intrinsics
  (`frac`, `lerp`, ...) are renamed like reserved words.
- Docs: the validation matrix, gate-script header, `RELEASING.md` gate list, and contributor
  validation page no longer describe the pre-0.14.0 Apos.Shapes golden-arm setup or the
  deleted 13-element harness; `DirectX_12` is listed in the CLI usage/help and error text;
  Android's status wording matches the validation matrix; the 0.14.0 changelog's #149 fix is
  filed under Fixed.
- Docs: `/Defines` and `CompilerOptions.Defines` are documented on the published site (the CLI
  option table previously listed every flag *except* this one and closed with "unknown flags
  are silently ignored", so a consumer would conclude it was unsupported). The `SD0214`
  DirectX 12 constraint is now stated wherever it matters: the DX12 backend page, the
  per-OS caveats guide, the host x target matrix, and a new validation-matrix gap row. Three
  blanket "output bytes are OS-independent" claims (`the-purpose`, `validation-matrix`,
  contributor validation page) gained the DX12 carve-out they now need, since the byte-identity
  manifest covers `DirectX_Vkd3d`/`FNA`/`OpenGL` only. Also: the README pipeline block and CLI
  README list DirectX 12, the `SD0400`-`SD0403` gap row covers the new code, and package tags
  mention `dx12`/`vulkan`.

## [0.14.2] - 2026-07-25

### Fixed

- OpenGL: `trunc()` (which SPIRV-Cross emits when lowering HLSL's truncating `%`/`fmod`) is now
  lowered to `sign(x)*floor(abs(x))`, a GLSL ES 1.00-safe expression. `trunc()` is a GLSL ES 3.00 /
  GL 1.30+ builtin, absent from the versionless legacy dialect ShadowDusk targets; strict GLSL ES
  1.00 front ends (ANGLE on macOS DesktopGL) rejected it as an undeclared identifier where lenient
  desktop drivers did not (Apos.Shapes issue #34).

## [0.14.1] - 2026-07-25

### Added

- Thin-ellipse slice in the OpenGL Apos.Shapes render gate (`validation/VsDriven -- apos`),
  supplementing the existing circle with a needle-thin ellipse compared same-backend against the
  mgfxc GL golden. Supplementary coverage for the issue #160 shape; the authoritative guard is a
  rewriter unit test.

### Fixed

- **OpenGL regression from 0.14.0: thin/eccentric ellipses in iterative SDF shaders rendered from
  garbage distances (issue #160).** The issue #138 GL loop rewrite (`LowerBoundedHeaderlessForLoop`)
  bounded the hoisted `for` header with `< provenMax` instead of `<= provenMax`, so when the
  runtime trip count equalled the loop's ceiling the rewritten loop exited one iteration early and
  skipped the `else` branch that finalizes the solver's result, leaving it read from an
  uninitialized variable. Affected only OpenGL, only shaders whose SPIRV-Cross output takes this
  header-less-loop shape (e.g. Apos.Shapes' `EllipseSDF`); DirectX/DX12/Vulkan were never affected.

## [0.14.0] - 2026-07-24

### Added

- **New target: DirectX 12 (MonoGame `WindowsDX12`), rung-4 proven (Phase 54).**
  `PlatformTarget.DirectX12` compiles to plain SM6 DXIL via DXC and is auto-selected for
  consumers targeting MonoGame's `WindowsDX12` runtime (3.8.5+) — seamless, no flag to pick.
  Render-proven maxd 0 against a real `mgfxc` `DirectX_12` golden, for both the 10-shader
  PS/SpriteBatch corpus and Apos.Shapes/VS-driven custom-vertex-shader effects
  (`validation/BaselineDx12`/`CandidateDx12`/`compare_dx12.py`, `validation/VsDrivenDx12`).
- **Apos.Shapes (Gum's SDF shape renderer) render-proof — DX and GL (Phase 51 A3), then
  expanded to the full shape gallery (Phase 55).** Vulkan already shipped in 0.13.0
  (`validation/VsDrivenVulkan -- apos`, maxd 0). This release closes the remaining single-shape
  DX/GL slices, then supersedes all of them with a 30-cell gallery driven through the REAL
  `Apos.Shapes` NuGet package's `ShapeBatch(GraphicsDevice, Effect?)` effect-injection
  constructor — every `Draw*`/`Fill*`/`Border*` shape kind, not one hand-built circle:
  - **DirectX 11 and DirectX 12.** Both pixel-diffed against a real, locally-generated `mgfxc`
    golden at **maxd 0** across all 30 cells for DX11 (`d3dcompiler_47` oracle and
    `vkd3d-shader`), and 28/30 at maxd 0 for DX12 (2 cells at 1/255, an open, unexplained
    finding — see `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md`). Along the way,
    found that Apos.Shapes' own embedded DX11 effect is compiled by `vkd3d-shader`, not
    `mgfxc` — comparing the `d3dcompiler_47` oracle against it was comparing two independent
    compilers, not a ShadowDusk fidelity gap; fixed by comparing against the real local
    `mgfxc` golden instead.
  - **Vulkan.** Full 30-cell gallery at **maxd 0** against Apos.Shapes' own (DXC-family)
    embedded golden.
  - **OpenGL.** No trustworthy `mgfxc` oracle exists for this shader revision on GL (a
    confirmed MojoShader codegen bug renders every non-textured shape solid black, unrelated
    to ShadowDusk) — the gallery renders through ShadowDusk's compile only, confirming all 30
    shapes produce visible output. The original single-shape GL proof (against an older,
    MojoShader-safe fixture revision) stays in place: **max Δ 2/255** (documented
    transcendental-math GLSL-dialect drift on the shader's OkLab round-trip).

  Wired into `run-windows-render-gates.ps1`. FNA stays permanently excluded (SM3
  instruction-slot ceiling).

### Fixed

- **GL profile emitted `isnan()` into versionless GLSL; rejected on macOS (issue #149).**
  Found while closing the GL slice above: ShadowDusk's own GL candidate for
  `apos-shapes.fx` contained 28 `isnan(` occurrences and no `#version` directive (the real
  mgfxc golden has zero of either). Desktop NVIDIA/AMD/Intel drivers tolerated it; Apple's
  strict GL compiler did not, breaking any GL shader using `min`/`max`/`clamp` on macOS — real
  downstream breakage (Apos.Shapes 0.7.6). Fixed by defaulting SPIRV-Cross's
  `RELAX_NAN_CHECKS` compiler option on for the whole OpenGL profile: zero `isnan(` now, zero
  byte changes anywhere else in the corpus. See `plan/DONE/ISSUE-149-gl-isnan-versionless-glsl.md`.

- **GL loop shapes outside GLSL ES 1.00 Appendix A: both shapes SD0402 covers are now
  auto-fixed where provably safe, not just warned about (issue #138).** GLSL ES 1.00
  (WebGL1 / KNI Reach) requires a loop's increment to live in the for-header and its bound
  to be a compile-time constant; SPIRV-Cross emits two shapes that violate this, and both
  used to fail to *load* there (desktop GL, WebGL2, and KNI HiDef were unaffected) while
  Phase 53 only added a compile-time warning (`SD0402`) for them.
  - **Constant-bounded, empty increment** (`for (int i = 0; i < N; ) { …; i++; continue;
    }`, the index advanced in the body). `MonoGameGlslRewriter` now hoists the increment
    into the header whenever it can prove the rewrite safe (no other write to the index, no
    other `continue` in the body). Confirmed end-to-end on the real vendored
    `Nez/GaussianBlur.fx`: compiling it through the CLI no longer emits `SD0402` at all.
  - **Header-less, runtime-looking trip count** (`for (;;) { if (i < bound) {…} else
    break; }`). Turns out "runtime" doesn't always mean unprovable: when `bound`'s own
    value is traceable to a compile-time-constant expression (a literal, or a ternary
    between two literals), the shader's real ceiling is still knowable even though
    SPIRV-Cross renamed it into a runtime-looking temporary. `MonoGameGlslRewriter` now
    gives the header that real, exact bound and hoists the increment the same way — not an
    approximation, since the derived bound IS the shader's true maximum. Confirmed on the
    real vendored `Apos.Shapes/apos-shapes.fx` (its Newton-iteration SDF): compiling it
    through the CLI no longer emits `SD0402` either, and the existing GL render-proof
    (`validation/VsDriven -- apos`) still matches the `mgfxc` golden at the same max Δ
    2/255 — pixels unchanged, as expected from an exact rewrite.

  A genuinely unfixable case remains: a loop bounded by a plain runtime uniform with no
  compile-time ceiling anywhere in the shader. There's no safe constant to derive there, so
  it keeps warning via `SD0402` — pinned by a fresh regression fixture,
  `examples/Sd0402UniformBoundedLoop.fx`, compiling clean while still warning through the
  real CLI.

## [0.13.0] - 2026-07-23

### Added

- **One-call shader validation: `Validate()` / `ValidateAsync()`.**
  `Console.WriteLine(await compiler.ValidateAsync(fx))` prints everything wrong with a
  shader: every error and every warning, per target, with source locations and the
  underlying compiler's complete verbatim text. Defaults to OpenGL + DirectX, putting the
  classic "compiles for DirectX, fails for OpenGL" report on one screen; an overload takes
  explicit targets (FNA, Vulkan). It runs the real compile pipeline per target, so what
  validates is exactly what compiles, and works on desktop and in the browser alike.
- **`CompiledShader.Warnings`** — successful compiles now carry their non-fatal diagnostics
  instead of discarding them: the underlying compiler's own warnings (DXC, d3dcompiler, and
  vkd3d's message buffer, previously thrown away on success) plus the new GL portability
  findings. The CLI prints them as MGCB-parseable `warning` lines on stderr with exit 0, and
  the ShaderFiddle sample lists them in its diagnostics panel. Warnings raised by an earlier
  technique also survive a later hard failure in the same effect instead of being dropped.
- **GL portability lint (`SD0400`–`SD0402`)**: compile-time warnings for constructs that
  compile fine but are known to fail or misbehave at *runtime* on narrower GL stacks, where
  the only previous signal was the engine's generic draw-time "Shader Compilation Failed"
  with the real driver log hidden in `Debug.WriteLine`.
  `SD0400` ([#141](https://github.com/kaltinril/ShadowDusk/issues/141)): a gradient op inside
  a divergent loop, silently 0.0 on ANGLE Direct3D11 (WebGL in every Windows browser).
  `SD0401`: a pass with no vertex shader whose pixel shader reads interpolants SpriteBatch's
  built-in vertex shader never writes, a strict-driver link failure at the first draw.
  `SD0402` ([#138](https://github.com/kaltinril/ShadowDusk/issues/138)): loop shapes outside
  GLSL ES 1.00 Appendix A that may fail to load on WebGL1 / KNI Reach.
  Warnings only; the lint never rejects a shader.
- **The published documentation now includes the [Diagnostic Codes](https://kaltinril.github.io/ShadowDusk/diagnostics.html)
  registry**, so any code seen in build output can be looked up.
- **A VS-driven Vulkan render gate with a real reference-compiler oracle**
  (`validation/VsDrivenVulkan`, issue #145): pixel-diffs ShadowDusk against the `mgfxc 3.8.5`
  golden on a real MonoGame DesktopVK device at **max Δ 0**, both for a non-identity
  asymmetric transform and for the upstream `Apos.Shapes` reproducer through its own
  13-element vertex layout. This is the first Vulkan comparison against the reference
  compiler; it works because explicit registers keep mgfxc's slot arithmetic in range. Both
  Vulkan gates are now default-ON in `run-windows-render-gates.ps1`.
- **A corpus-wide, device-free Vulkan structural gate** plus **MonoGame's own 17 test effects
  vendored into the corpus** (Ms-PL, tag `v3.8.5`), which carry the reference compiler's
  acceptance set. Every fixture must now either produce a structurally valid Vulkan container
  or fail with a real diagnostic, never an exception. They found two real defects on the day
  they landed.

### Changed

- **Compile errors are verbatim, everywhere, by default.** Diagnostic text that could not be
  parsed into file:line:col entries (DXC SPIR-V codegen failures, disproportionately the
  OpenGL leg) used to collapse into a fixed `X0000: "Shader compilation failed"`, with the
  real text hidden in a field no surface printed. The compiler's own words are now the
  message; the primary error prefers the first error-severity diagnostic, so a leading
  warning can no longer masquerade as the failure, and always carries the complete raw
  output, which the CLI and the ShaderFiddle sample print by default. There is no verbosity
  flag to find, deliberately.
- **The OpenGL/Vulkan leg no longer forces `-WX` (warnings-as-errors).** mgfxc's fxc front
  end never passed `/WX`, so ShadowDusk's GL leg was *stricter than the reference compiler*:
  warning-grade HLSL such as an implicit truncation compiled for DirectX but hard-failed for
  OpenGL, a confirmed "DX works, GL doesn't" divergence class. Those warnings now surface
  through `CompiledShader.Warnings` instead of failing the compile. Output bytes for
  previously-compiling shaders are unchanged.

### Fixed

- **Vulkan: matrices were packed row-major, so every VS-driven effect rendered nothing**
  (issue [#145](https://github.com/kaltinril/ShadowDusk/issues/145)). `-Zpr` was applied to
  every DXC compile, Vulkan included, but MonoGame uploads a `Matrix` parameter for HLSL's
  column-major default, so a Vulkan vertex shader read `mul(pos, worldViewProj)` transposed
  and threw its geometry out of clip space: loads fine, draws without error, renders nothing.
  mgfxc's Vulkan command line carries no `-Zpr`; now neither does ShadowDusk's. OpenGL keeps
  the flag (its rewriter compensates) and DirectX never used DXC.
- **Vulkan: legacy `tex2D` shaders access-violated inside `GraphicsDevice_DrawIndexed`**
  (issue #145). The `Texture2D` synthesized for a legacy `sampler` was excluded from the
  register-pairing rewrite, so the image auto-numbered to binding 0/1 while its sampler
  shifted to 32/33; MonoGame recovers the texture slot as `binding - 32` and indexed its
  texture array at -32. Pairs are now always co-located, explicit `register` indices are
  reserved so an auto-assigned pair cannot collide with them, and every texture
  dimensionality is covered (`TextureCube`/`Texture3D` had the same crash shape).
- **Vertex shaders now get the stage-agnostic GL body lowerings** (issue
  [#137](https://github.com/kaltinril/ShadowDusk/issues/137)). The rewriter returned early
  for the vertex stage, so a VS using `round()` shipped `roundEven()` (absent from GLSL ES
  1.00) and a VS with an inlined early-return helper shipped the raw `do { … } while(false)`
  Appendix A forbids, both silent Effect-load failures on Mesa/WebGL1 with compile exit 0.
- **Derivative-using fragment shaders ship `#extension GL_OES_standard_derivatives : enable`**
  as the first line of the emitted GL source, where mgfxc puts it (issue
  [#139](https://github.com/kaltinril/ShadowDusk/issues/139)). Strict ESSL 1.00 compilers
  reject derivative builtins without it. The scan covers `fwidth` too.
- **A `round()` nested inside another `round()`'s argument is now fully lowered** (issue
  [#140](https://github.com/kaltinril/ShadowDusk/issues/140)); the inner call previously
  survived as `roundEven()`, the exact load failure the lowering exists to prevent.
- **Vulkan: a texture/sampler pair could still be handed a binding another texture already
  held.** Explicit `register` indices were only honoured by the auto-assign path, so a pair
  that *inherited* its index from an explicitly-registered half could collide with a
  different pair — two textures on one binding, the invalid descriptor layout that
  access-violates in MonoGame's descriptor writer. Pair co-location now outranks a
  disagreeing explicit register (the runtime binds by slot index, not by the source's
  register number), and explicitly-registered textures are assigned first so the guard cannot
  be defeated by declaration order. Two textures still share an index when they name the same
  sampler, which is the same-sampler-in-two-`#if`-branches shape where only one branch
  survives the compile. No shader in the test corpus changes output: every fixture is
  byte-identical through the rewrite on all four targets.
- **Vulkan: `Gather*` calls, `Texture2DArray`/`TextureCubeArray`/`Texture2DMS` declarations,
  and `SamplerComparisonState` are now seen by the pairing pass.** Being invisible to the
  scan meant those pairs were left at separate bindings and their explicit registers were
  never reserved. `Texture2DArray` and `SamplerComparisonState` appear in MonoGame's own
  vendored test effects (both already carry agreeing registers, so no corpus output changes);
  the rest are covered defensively.
- **A `while` loop following any block no longer loses its `SD0402` warning.** The do-while
  tail check accepted *any* `while` preceded by `}`, so `if (…) { … } while (…) { … }` was
  misread as a do-while's trailing clause and the finding silently dropped.
- **The compiler's diagnostic text no longer prints twice.** The "the message already says
  this" check compared the raw blob and the message without normalizing line endings or
  blank lines, so it never matched on multi-line diagnostics — exactly the unparseable-text
  path the verbatim work was built for. Multi-line messages are also indented under their
  parseable first line now, so compiler-controlled text can no longer start a stderr line and
  be misread by a build-log parser as a separate diagnostic.
- **`Validate` reports name the file for line-less findings**, matching the CLI.
- **Warnings without a line number now name their source file.** The GL portability warnings
  are derived from emitted GLSL and carry no line mapping, so the CLI printed a bare
  `warning SD0401: …` with no way to tell which effect produced it in a build compiling many.
- **Anonymous techniques (`technique { pass { … } }`) are accepted** — legal FX that mgfxc
  compiles, and what 8 of MonoGame's 17 own test effects use, previously rejected with
  `FX0001: Expected technique name`. Relatedly, the FNA writer no longer rejects an empty
  technique name, which `d3dcompiler_47` at `fx_2_0` compiles cleanly.
- **A native process crash on the FNA path is now a diagnostic.** `SamplerComparisonState`
  made vkd3d's SM1 lowering hit "Unreachable code reached" and took the whole process down
  with an access violation; it is rejected up front with the new `FX0013` instead.
- **Vulkan container faithfulness:** vertex shaders now carry the attribute table mgfxc emits
  (recovered from the SPIR-V input semantics), a combined image-sampler sets both the texture
  and sampler slot masks, and the shipped SPIR-V no longer carries `-fspv-reflect`'s
  `SPV_GOOGLE_*` extensions.

## [0.12.1] - 2026-07-21

### Added

- Vendored the derivative-based-antialiasing revision of Apos.Shapes' shader
  (`apos-shapes-aa.fx`, upstream `d507a73`) into the third-party corpus (GL + DX compile
  pins), plus a structural pin that fails if any gradient op ever again lands inside a
  loop with a divergent exit in emitted GL GLSL (issue #136).
- `validation/AngleDerivativeProbe`: a self-asserting headless-browser gate that renders
  the emitted fragment control-flow shapes on real ANGLE Direct3D11 (the WebGL backend of
  every Windows browser) and fails if ShadowDusk's shape loses derivatives — the first
  gate that sees the browser backend the issue-#136 bug lives on. Wired into
  `run-windows-render-gates.ps1` as default-ON, alongside the real-KNI OpenGL desktop and
  VS-driven drivers (previously manual-only, now impossible to forget for GL-affecting
  changes).

### Changed

### Fixed

- **`dFdx`/`dFdy` no longer return 0 in Windows browsers (ANGLE D3D11)** — issue #136,
  reported by Jean-David Moisan (Apostolique). ANGLE's D3D11 backend silently zeroes every
  gradient op inside a loop with a divergent exit (a conditional `break` or `discard`), and
  the issue-#107 for-loop lowering of SPIRV-Cross's entry-point `do { … } while(false);`
  wrapper put the whole fragment body inside exactly such a loop, disabling derivative-based
  antialiasing (Apos.Shapes SDF shapes) on KNI BlazorGL with no compile or link error. The
  GLSL rewriter now **unwraps** the wrapper when it can prove it safe: plain brace block,
  each loop-level `break` → duplicated tail + `return;` — straight-line `main` with real
  early exits, valid in every GLSL profile including ESSL 1.00, and the same shape
  mgfxc/MojoShader emits. The unwrap recurses through the plain blocks it creates, so an
  **inlined helper that both early-returns and takes a derivative** (its wrapper nests
  inside the entry wrapper) unwraps too — the shape the fix's adversarial review found
  still poisoned. A tail whose duplication would move a gradient op or implicit-LOD
  sample into divergent flow (undefined per GLSL §8.13.1) is never unwrapped; those and
  all other unprovable shapes keep the WebGL1-safe for-loop fallback. Desktop GL/KNI
  output remains render-equivalent (Windows render gates + KNI desktop GL/VS-driven
  drivers + Linux CI GL gates).

## [0.12.0] - 2026-07-18

The headline: **a Vulkan backend**. MonoGame 3.8.5 (stable 2026-07-15) ships `DesktopVK`, a
native Vulkan desktop platform — and ShadowDusk now compiles for it: the same faithful
pipeline, one DXC compile straight to SPIR-V, wrapped in the real MonoGame Vulkan `.mgfx`
container. Additive and seamless like every backend before it: existing OpenGL / DirectX /
FNA / WASM output is byte-identical, and the MGFX v10 default is unchanged.

### Added

- **Vulkan output target** (`PlatformTarget.Vulkan` / CLI `/Profile:Vulkan`) — Phase 32,
  contributed via PR #126 (Victor Chelaru / vchelaru). HLSL compiles through the pinned DXC
  frontend directly to SPIR-V (`vs_6_0`/`ps_6_0`) and is emitted in MonoGame 3.8.5's own
  Vulkan `.mgfx` container (profile byte 80, v11-shaped shader records, the SPIR-V wrapped in
  the descriptor-layout header MonoGame's native Vulkan pipeline reads), with reflection from
  the SPIR-V itself. Texture+sampler pairs bind as single combined image-sampler descriptors,
  matching MonoGame's runtime. **Render-proven on real hardware:** the shader corpus loads and
  renders correctly (10/10) in real MonoGame 3.8.5 `DesktopVK`
  (`validation/CandidateVulkan`; local gate `./validation/run-windows-render-gates.ps1
  -IncludeVulkan`). A pixel-diff against `mgfxc`'s own Vulkan output is blocked upstream —
  that output currently crashes in real DesktopVK due to a confirmed MonoGame `SlotOffset`
  bug. MonoGame-only: KNI ships no Vulkan platform (a KNI+Vulkan request fails loudly with
  `SD0025`).

### Changed

- Documentation: **DirectX 12 (MonoGame 3.8.5 `WindowsDX12`) is now recorded as an explicit
  not-yet-supported target** across the support matrix and site pages (planned,
  research-first — see `plan/PHASE-52-monogame-3.8.5-support.md`). The validation matrix's
  accumulated update trail moved to a compact bottom-of-page history, consumer-facing pages
  were trimmed of internal status noise, and the `ShadowDusk.Compiler` / `ShadowDusk.Cli`
  package READMEs now list the Vulkan target.

### Fixed

- **OpenGL codegen fidelity: `pow(x, 2.0)` strength-reduced to a multiply** (issue
  [#127](https://github.com/kaltinril/ShadowDusk/issues/127)). GLSL leaves `pow` undefined for a
  negative base (drivers lowering it to `exp2(y*log2(x))` return NaN), while fxc constant-folds
  `pow(x, 2)` into a multiply — so HLSL that squares a possibly-negative value via `pow` (e.g.
  Apos.Shapes' `LinearGradient` squaring normalized-direction components) was well-defined through
  mgfxc but a latent driver-dependent hazard through ShadowDusk's GL output. `MonoGameGlslRewriter`
  Rule 10 now emits the multiply (exact, and the reference compiler's semantics); simple-operand
  bases only, so no expression is ever duplicated unsafely.
- **OpenGL codegen fidelity: `1.0 / (a / b)` folded to `b / a`** (issue
  [#127](https://github.com/kaltinril/ShadowDusk/issues/127)). SPIRV-Cross preserves the HLSL
  reciprocal-of-quotient shape literally (fxc folds it), costing an extra rounding step at every
  such site (all 8 `SmoothDiscontinuity` call sites in apos-shapes.fx). Rule 11 emits the single
  correctly-rounded division; value-equivalent across the zero/infinity edge cases, applied only
  when the division is provably the group's root operator. Both rules are pinned by rewriter unit
  tests and an end-to-end regression test compiling the vendored `apos-shapes.fx` on GL
  (`AposShapes_OpenGl_EmitsNoPowSquare_NoReciprocalOfQuotient_Issue127`); the full suite, the
  Windows DX render gates, and every GL render gate (corpus vs mgfxc, VS-driven at 1/255, KNI
  desktop GL, state/cbuffer/texture/reserved-word) stayed green.

## [0.11.0] - 2026-06-28

Android joins the supported runtime-compile platforms: ShadowDusk now compiles `.fx` -> `.mgfx`
**in memory, at runtime, on an Android device** (the "shader fiddle on a phone" shape), through the
same faithful HLSL -> DXC -> SPIR-V -> SPIRV-Cross -> GLSL -> MGFX pipeline used everywhere else, via
the seamless `new EffectCompiler()`. This is additive and seamless: all existing OpenGL / DirectX /
FNA / WASM output is byte-identical, the MGFX v10 default is unchanged, and desktop/WASM consumers are
unaffected (the Android natives are RID-scoped, so they are never deployed into a non-Android app).

### Added

- **On-device runtime shader compilation on Android (arm64-v8a)** (Phase 50). A .NET-for-Android
  MonoGame app can take a user's shader **text**, compile it to a MonoGame `.mgfx` on the device, and
  load it into a live `Effect`, with no host precompile and no content pipeline. The two native pieces
  the OpenGL pipeline needs (`libdxcompiler.so` and `libspirv-cross.so`, built for `android-arm64`)
  now ship inside `ShadowDusk.HLSL` and `ShadowDusk.GLSL` under `runtimes/android-arm64/native/`, so
  "add the package, call the API" is the entire setup, exactly as on desktop. Proven on-device by
  compiling and rendering a pixel shader on an Android emulator.
- A consumer guide, **"On-Device (Android Runtime Compile)"**, in the published documentation site,
  with the integration recipe (`new EffectCompiler().CompileAsync(fx, new CompilerOptions { Target =
  PlatformTarget.OpenGL })` -> `result.Value.Data` -> `new Effect(GraphicsDevice, mgfx)`) and notes for
  embedding the compiler in an Android shader fiddle.

### Changed

- `EffectCompiler` **auto-selects the pure-managed `SpirvReflector` on Android** (the native
  DXIL-oracle reflection path is unavailable there). The selection is automatic and produces the same
  reflection result, so consumers do nothing and desktop behavior is unchanged.

### Fixed

## [0.10.0] - 2026-06-28

Fidelity fixes driven by real shipping shaders from the Gum / Apos.Shapes ecosystem (requested by
vchelaru, Gum's author): several real-world effects that previously failed now compile and render on
the targets they ship for, across FNA, OpenGL, and KNI WebGL. Every change is additive: the seamless
MGFX v10 default and all existing OpenGL / DirectX / FNA output stay byte-identical (pinned by the
cross-host byte-identity gate), and the new behavior only enables previously-failing shaders.

### Added

- Real, MIT-licensed **Apos.Shapes and Gum** `.fx` shaders vendored under
  `tests/fixtures/shaders/third-party/` as **compile-level** regression inputs (Phase 49), each
  classified by an actual GL / DX / FNA compile probe. These guard the compiler against the exact
  shaders the Gum / Apos.Shapes ecosystem ships; provenance and per-shader target classification are
  in `docs/test-shader-corpus.md`.

### Changed

- **`__KNIFX__` is now defined for a KNIFX-targeted compile** (`--target-runtime kni-knifx` /
  `CapabilityProfile.KniGL_4_02`), matching KNI's own effect compiler, so a shader that branches on
  `#ifdef __KNIFX__` (e.g. Apos.Shapes selecting its SM4 profile) takes the correct branch. The
  seamless universal MGFX default deliberately does **not** define it, so default output is unchanged.

### Fixed

- **FNA: macro-defined techniques are now recovered** (Phase 41 GAP-1). An effect whose techniques
  come only from a `TECHNIQUE(...)` `#define` (the stock-MonoGame / Gum idiom) previously failed
  `SD0010` on FNA because techniques were counted before macro expansion. The zero-technique recovery
  (preprocess then re-parse) now extends to the FNA path, so **7 MonoGame stock effects compile on
  FNA** (SpriteEffect, AlphaTestEffect, DualTextureEffect, and the Penumbra hull/light/shadow/texture
  effects). Effects that still fail now do so for honest shader-model reasons (register pressure /
  sub-SM2 profiles), not technique-blindness. Only effects that returned zero bytes before are
  affected, so existing FNA output is byte-identical.
- **OpenGL: multi-render-target pixel shaders now compile** (Phase 41 GAP-2). A deferred-rendering
  effect whose pixel shader returns a struct with `COLOR0` / `COLOR1` output semantics (e.g. Nez
  `DeferredSprite.fx`) failed on the GL target with `Semantic COLOR is invalid`. A GL-only struct
  output `COLOR` to `SV_Target` rewrite (applied only to the OpenGL compile, so DirectX bytes stay
  identical) fixes it, and true multi-target slot 0 now emits `gl_FragData[0]` instead of
  `gl_FragColor` so writing one target no longer corrupts the others.
- **KNI WebGL: a one-shot `do { ... } while(false)` loop no longer breaks loading**
  ([#107](https://github.com/kaltinril/ShadowDusk/issues/107)). A helper with a nested `if` that early
  returns made SPIRV-Cross emit a `do/while(false)`, which compiles and loads on desktop GL but is not
  guaranteed by GLSL ES 1.00, so the effect **failed to load in KNI WebGL / Reach**. The GL rewriter
  now lowers it to an equivalent WebGL-safe bounded `for` loop (pixels unchanged); render-proven in
  real KNI WebGL on both Reach (WebGL1) and HiDef (WebGL2). DirectX / FNA bytecode is unchanged.
- **KNIFX now loads and renders on KNI WebGL and mobile GLES** (the opt-in KNIFX container). A
  KNIFX-targeted compile previously advertised only the desktop OpenGL backend, so KNI WebGL rejected
  it ("profile is not compatible with the graphics backend 'WebGL'"). The KNIFX writer now emits a
  multi-backend GL-family directory (desktop OpenGL keeps its faithful body byte-for-byte; GLES and
  WebGL share a body KNI's runtime converts to GL ES at load, the same proven path that already loads
  MGFX v10 in KNI WebGL). One `.knifx` now loads on every KNI GL host; desktop KNI render is unchanged.

## [0.9.0] - 2026-06-22

Robustness pass on the **ShaderToy GLSL -> `.fx` converter** (`ShadowDusk.ShaderToy`): several real
ShadowToy shaders that previously failed to convert now compile, and the cases that genuinely cannot be
faithfully translated reject with a clear, located message instead of an opaque downstream parser error.
The converter is a pure-managed front end that emits `.fx`; the core compile pipeline and all existing
`.mgfx` / `.fxb` output are unchanged (zero golden churn).

### Added

- **`ShadowDusk.ShaderToy` is now a published NuGet package** (the seventh `ShadowDusk.*` package).
  It is the standalone, **pure-managed, zero-native** ShaderToy/GLSL → `.fx` converter
  (`ShaderToyConverter.Convert`), so anyone can convert ShaderToy shaders **in-process** (e.g. an
  XNA/KNI web shader fiddle or an in-app importer) without the CLI. It is **optional and separate**:
  `ShadowDusk.Compiler` does not depend on it, so existing consumers are unaffected. (The converter
  also continues to ship embedded in the `ShadowDuskCLI` tool's `.glsl` input.)
- Regression fixtures and unit tests for every case below: authored corpus shaders
  (`matrix_from_vector`, `mirror_happy_accident`, `infinite_cube_starfield`, `abstract_waterfall`,
  `chimera_final_pass`) auto-converted and golden-compared, plus reject fixtures
  (`unsigned_int_literal`, `texture_cubemap_coord`) and targeted unit suites
  (`MatrixConstructorTests`, `ForLoopScopingTests`, `ConstGlobalTests`, `TextureLodTests`).

### Changed

- **Clearer, located rejects for constructs outside the float-based subset.** An unsigned-integer
  literal (`123U`, which drives uint/uvec bit-hash arithmetic) now rejects **at the literal** instead of
  the stray `U` surfacing later as a confusing "expected `)`" parse error, and `texture(sampler, vec3)`
  (a cubemap sample) rejects with a message naming the cubemap rather than truncating silently.

### Fixed

- **`mat2` constructed from a `vec4` now converts** (e.g. `mat2(someVec4)`): the four-component vector is
  flattened into the `float2x2` instead of failing to emit.
- **Reused `for`-loop induction variables now convert** under HLSL's legacy for-scope rule. GLSL scopes a
  `for`-init declaration to its loop; legacy HLSL leaks it to the enclosing scope, so a second
  `for (int i = ...)` in the same function tripped `-Wfor-redefinition` under `-WX`. The converter now
  scope-renames reused induction variables per loop (first occurrence keeps its name), so multi-loop
  raymarchers convert.
- **Multi-declarator `const` globals now parse** (e.g. `const float A = 1., B = 2.;`). The additional
  declarators were previously swallowed by the comma operator, surfacing as a misleading "Undeclared
  identifier" error on later use.
- **A base-level `textureLod(s, uv, 0.)` lowers to a plain `tex2D`.** The legacy `tex2Dlod` intrinsic
  does not rewrite to a modern Texture method on the OpenGL/DirectX targets (`FX0012`); since the
  single-pass harness binds each iChannelN without mipmaps, mip 0 is the only level, so the two are
  equivalent and the shader now compiles on every backend. A non-zero LOD keeps the explicit `tex2Dlod`.

## [0.8.0] - 2026-06-18

### Added
- `SECURITY.md` at the repo root: the project's trust model (compiling a `.fx` runs code; the
  shader author and the compiler-runner are the same developer, so the library is a build-time/in-app
  tool, not a sandbox), the consumer's isolation responsibility for any service that compiles
  third-party `.fx`, the supply-chain integrity model for the natives we ship, and how to report a
  vulnerability.
- KNI **DirectX** render validation (`validation/KniWinFormsDX`): ShadowDusk's DX output renders
  pixel-equivalent to the `mgfxc` DirectX golden in real KNI v4.02 WinForms.DX11.
- `validation/run-windows-render-gates.ps1`: one command that runs the Windows-GPU render proofs CI
  cannot (DirectX corpus, vertex-texture-fetch, KNI-DX, and FNA under `-IncludeFna`), required before a
  release.
- CI render gates for the in-process OpenGL validation drivers
  (`.github/workflows/validation-render.yml`, Mesa llvmpipe under xvfb).
- 15 real, MIT-licensed **Nez** `.fx` shaders vendored under
  `tests/fixtures/shaders/third-party/Nez/` as **compile-level** regression inputs (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) / Phase 45), plus author-original
  regression fixtures for the pre-parser fixes below. These guard the FX9 pre-parser against
  real-world shaders; they are not new render-equivalence proofs (provenance + per-shader target
  classification in `docs/test-shader-corpus.md`).
- `validation/ReservedWordGl`: a GL render driver that render-proves the reserved-word uniform
  binding fix below pixel-identical to `mgfxc`.
- `tests/fixtures/shaders/examples/Issue106Repro.fx`: the verbatim shader from the issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) report (a helper using `==`, `<=`, a
  nested `if`, and an early `return`), pinned as a permanent regression fixture and compile-asserted
  on OpenGL, DirectX, and FNA.

### Changed
- The in-process MGFX/KNIFX/FNA golden-comparison and cross-host byte-identity tests now run on the
  fast PR lane (previously only on the heavier integration lane), so a writer/transpiler/render-state
  regression that still compiles is caught on every pull request, not just at release time.
- The KNI WebGL render proof was refreshed on the current KNI v4.02 runtime, and the browser harness now
  stamps the KNI runtime version into every generated results file.
- Documentation accuracy pass: corrected Vulkan's status (it compiles to a SPIR-V `.mgfx` but has no
  shipping runtime to render-validate against, i.e. experimental/unvalidated, not "future"), the
  fixture-corpus count, and assorted status/cross-reference drift surfaced by a full project review.

### Fixed
- **FX pre-parser: a whole class of valid shaders that previously failed now compiles** (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) / Phase 45). The global-parameter
  annotation heuristic matched the bare token shape `Identifier Identifier <` anywhere in the stream,
  so a **relational, shift, or ternary expression** in a shader body (e.g.
  `return value <= 0.5f ? 0 : 1;`) was misread as an FX annotation and failed with `FX0001`. The path
  is now gated on the genuine annotation-block shape, and several related pre-parser bugs are fixed in
  the same pass: modern `sampler_state` + `.Sample`, `ColorWriteEnable = Red | Green | Blue` masks,
  legacy `texture < ... >` annotations, a texture variable named `Texture`, a vertex shader returning
  `: COLOR`, array-indexed relational/ternary assignment, and sampler register/annotation variants
  (B1-B9). Purely additive: it only enables previously-failing shaders, so existing output stays
  byte-identical (pinned by the cross-host byte-identity gate).
- **OpenGL: a uniform whose name collides with a GLSL reserved word now binds correctly** (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106), B10). `float noise;` is valid HLSL that
  `mgfxc`/`fxc` accept, but SPIRV-Cross renames the colliding uniform (`noise` to `_noise`) for legal
  GLSL, so the GL cbuffer/parameter join — matching by name — missed it and failed loudly with
  `SD0012`. The join now falls back to an offset bridge that recovers the parameter by byte offset
  (keeping its original name), render-proven pixel-identical to `mgfxc` in real MonoGame GL. The
  primary name match is unchanged and runs first, so every shader that compiles today is byte-for-byte
  identical; this only enables the reserved-word case (re-enabling the real Nez `Noise.fx` on GL).
- Closed soft-skip-as-green holes in the validation drivers/tests: `validation/TextureBreadthValidation`
  now honors `SHADOWDUSK_REQUIRE_GL` (a missing GL device fails loudly instead of reporting success),
  and the `mgfxc` cross-validation test fails (rather than silently passing) when a known-good fixture
  stops compiling.

## [0.7.0] - 2026-06-14

The seamless default is unchanged: MGFX **v10** is still the default container and output is
**byte-identical to 0.6.0** for every existing call (`CrossHostByteIdentity` stays green). This
release adds an opt-in **capability-profile** API for naming a full output target (graphics backend
+ container/version) in one value, a matching CLI flag, and runtime detection. It also validates the
KNIFX optimized-matrix (`columnsActual`) fidelity against KNI's own compiler (KNIFXC).

### Added

- **Capability-profile output-target selector.** New `CapabilityProfile` (a **closed set** of
  render-proven (runtime, format) contracts: `MonoGameGL_3_8_2`, `MonoGameDX_SM5`, `MonoGameGL_3_8_5`
  for MGFX v11, `KniGL_4_02` for KNIFX v11, and `Fna_Fx2`) plus `CompilerOptions.Profile`. A profile
  fully specifies the output target, **including the graphics backend**, so setting `Profile` alone
  picks both format and backend; it overrides `Target` / `Container` / `MgfxVersion`. The default
  (`null`) reproduces today's behavior exactly.
- **CLI `--target-runtime <name>`** (also `/target-runtime:<name>`): selects the output target by
  name (`monogame-gl`, `monogame-dx`, `monogame-gl-v11`, `kni-knifx`, `fna`), mapping to a
  `CapabilityProfile`. Overrides `/Profile` and `--mgfx-version`. Unknown values fail with `X0008`.
- **Runtime detection.** `RuntimeProfileDetector` classifies the loaded XNA framework assembly
  (MonoGame / KNI / FNA) and recommends a proven `CapabilityProfile` to pass to
  `CompilerOptions.Profile`. Conservative: it returns the universally-loadable MGFX v10 (fx_2_0 for
  FNA) and never silently upgrades a consumer to a newer container.
- **Shader-feature capability axis.** `ShaderFeatures` + `ShaderFeatureSupport`, which **rejects
  (`SD0201`)** any GL feature no shipping runtime consumes yet, so an unsupported feature can never
  silently compile into bytes no runtime can load. (No shipping runtime supports these today.)

### Changed

- A `CapabilityProfile` **implies its graphics backend**, so a set `CompilerOptions.Profile`
  determines the backend (overriding `Target`); the runtime-detection advisory composes with this so
  one recommended profile picks both format and backend.
- **KNIFX `columnsActual` validated against a KNIFXC golden.** Full matrices match KNI's own compiler
  exactly; the partially-used-matrix case (ShadowDusk emits `columnsActual = columns`) is a
  render-safe, storage-only divergence, not a correctness difference. KNIFX output is unchanged.

## [0.6.0] - 2026-06-14

The seamless default is unchanged: MGFX **v10** is still the default container and you never
set a flag to get correct output. This release adds two **opt-in / experimental** container
writers for newer runtimes (MonoGame MGFX v11 and KNI KNIFX v11), recovers macro-declared
techniques on DirectX, and fixes two OpenGL vertex-shader fidelity bugs.

### Added

- **Faithful MGFX v11 writer (opt-in, experimental).** `CompilerOptions.MgfxVersion = 11`
  (CLI `--mgfx-version 11`) now emits a **correct** MonoGame v11 container — where it was
  previously **corrupt** (a v10 body labeled version 11, which a real v11 reader cannot
  parse). MonoGame 3.8.5's `Effect` loader expects a per-shader `SourceFile` and `Entrypoint`
  string in the shader stream (PR #8813); ShadowDusk now writes them. They are diagnostic-only
  (they appear in shader error messages) and do not affect rendering. **Render-proven in real
  MonoGame 3.8.5**: the corpus loads + renders 10/10, max delta 0 vs the v10 render. **v10
  remains the default and never reads them** — `MgfxVersion` is a non-required escape hatch
  (default 10).
- **KNIFX v11 container target (opt-in, experimental).** New public `EffectContainer` enum
  (`Mgfx` default, `Knifx`) and `CompilerOptions.Container` property (default `Mgfx`). Set
  `Container = EffectContainer.Knifx` to emit KNI's newer KNIFX v11 container for KNI v4.02+
  consumers — signature `KNIF`, a multi-backend directory, a packed-int body, and the GL
  GLSL-version directory KNI's runtime requires. **Render-proven in real KNI v4.2.9001 desktop
  GL**: the corpus loads + renders 10/10, max delta 0 vs the v10 render. Additive, not a
  replacement for the v10 default; `MgfxVersion` is ignored when `Container == Knifx`, and
  `Container` is ignored for `PlatformTarget.Fna` (always D3D9 fx_2_0). `CompiledShaderBlob`
  gained three init-only properties with mgfxc's own safe fallbacks (`ShaderModel = (3,0)`,
  `SourceFile`/`Entrypoint = "<unknown>"`).

### Changed

- **DirectX: macro-declared techniques are now recovered.** Stock-MonoGame-style effects that
  declare their technique via the `TECHNIQUE(...)` macro (e.g. `BasicEffect.fx`) now compile
  on DirectX/Vulkan through a gated zero-technique fallback (DXC-preprocess then re-parse).
  OpenGL and FNA explicitly decline this path; existing behavior is otherwise unchanged.
- **vkd3d: include-heavy effects compile without noise** — `#line` directives are blanked so
  they no longer surface as diagnostics.
- **`ShadowDusk.HLSL` package: removed dead public types** as dead-code cleanup
  (`RenderStateMapper`, `MappedRenderState`, the empty `FxFileParser` stub,
  `ReflectionInput.SpirVBlob`, `ReflectionPipeline.ReflectAsync`; `IDxcShaderCompiler` gained a
  `Preprocess` method). Behavior-neutral — no emitted bytes change. The product surface
  (`IShaderCompiler` in `ShadowDusk.Compiler`) is unaffected; this only matters if you
  referenced `ShadowDusk.HLSL` internals directly.

### Fixed

- **OpenGL vertex-shader geometry fidelity ([#70](https://github.com/kaltinril/ShadowDusk/issues/70)).**
  Two silent GL bugs in custom-vertex-shader effects are corrected, moving the default v10 GL
  output toward `mgfxc`-equivalence: a `float4x4` uniform was reconstructed **transposed** (so
  a non-identity `mul(v, M)` rendered an exploded/garbled mesh), and legacy `: POSITION` vertex
  outputs were not mapped to `gl_Position` (silently broken geometry). Both are now
  render-proven **max delta 0** against the `mgfxc` golden in real MonoGame. This intentionally
  changes the v10 GL bytes for VS-driven effects (12 OpenGL byte-identity fixtures updated;
  **zero** DirectX/FNA fixtures changed) — previously broken output is now correct.

> MGFX v11 and KNIFX v11 are opt-in and experimental; the seamless default remains MGFX v10,
> which loads on every MonoGame 3.8.2+ and KNI runtime with no consumer action. FNA (fx_2_0
> `.fxb`) output is byte-identical to the previous release.

## [0.5.1] - 2026-06-12

### Added

- **Every package now ships a README** on its nuget.org page (previously only
  `ShadowDusk.Wasm` had one — the other five showed nuget.org's "missing a README"
  banner).

### Fixed

- **macOS: native-library resolution now keys on the process architecture, not the OS
  architecture.** Under Rosetta 2 (an x64 build running on an Apple-silicon Mac) the
  resolvers for all three natives (DXC, vkd3d-shader, SPIRV-Cross) probed the arm64
  binaries — which can never load into an x64 process — instead of the x64 ones sitting
  beside them, so compiles failed with `X0099`. Caught by the release pipeline's new
  smoke-run gate during the 0.5.0 publish: the run stopped before creating the GitHub
  Release, so the broken self-contained osx-x64 CLI binary never shipped. The 0.5.0
  NuGet packages remain fine for typical consumers (NuGet's own native layout sidesteps
  the buggy probe); 0.5.1 makes the self-contained osx-x64 CLI work on Apple-silicon
  Macs and completes the GitHub Release that 0.5.0 never got.

## [0.5.0] - 2026-06-12

### Added

- **`InitializeAsync()` + synchronous `Compile()`** on the compiler surface
  (`IShaderCompiler` / `EffectCompiler` / `WasmShaderCompiler`) — issue
  [#28](https://github.com/kaltinril/ShadowDusk/issues/28): compile `.fx` from a
  **synchronous** call site (e.g. MonoGame/KNI `Content.Load<Effect>`) after a one-time
  async warm-up, with no sync-over-async deadlock on single-threaded Blazor WASM.
  `await compiler.InitializeAsync()` once (on WASM it loads all the compiler WASM
  modules; on desktop it is a documented no-op), then `compiler.Compile(source, options)`
  runs the entire pipeline on the calling thread. Sync and async share **one** pipeline
  core, so their output is byte-identical (asserted over the full fixture corpus for
  OpenGL, DirectX, and FNA, on desktop and in a real browser). Calling the synchronous
  `Compile` on WASM before `InitializeAsync` returns a clear `SD1903` error telling you
  to initialize first — never an opaque runtime abort. `CompileAsync` is unchanged for
  existing consumers. The backend interfaces (`IDxcShaderCompiler`,
  `IDxbcShaderCompiler`) and reflection pipelines gained matching synchronous entries.
- **In-browser DirectX and FNA compilation.** `WasmShaderCompiler` now compiles
  `PlatformTarget.DirectX` (SM5 DXBC `.mgfx`) and `PlatformTarget.Fna` (D3D9 `.fxb`) in
  the browser, so every shipping target (OpenGL, DirectX, FNA) works on every host. The
  browser runs the **same pinned vkd3d-shader 1.17** the desktop packages bundle,
  compiled to WebAssembly (0.43 MB gzipped) — never a substitute compiler — and the
  emitted bytes are identical to desktop output, asserted over the full DirectX + FNA
  fixture corpus both in Node and in a real headless browser against the committed
  cross-host manifest.

### Changed

- **DirectX compiles default to the cross-platform vkd3d-shader backend on every OS.**
  A bare DirectX compile (including the CLI's default `DirectX_11` profile with no
  backend flag) previously defaulted to the Windows-only `d3dcompiler_47` and
  hard-failed `SD0210` on Linux and macOS. The default is now host-independent — the
  same vkd3d backend everywhere, so default DX output is byte-identical across OSes.
  `d3dcompiler_47` remains fully supported as the opt-in correctness oracle (CLI escape
  hatch `/DxbcBackend:<vkd3d|d3dcompiler>`), and vkd3d's stderr debug chatter is
  suppressed so the CLI keeps `mgfxc`'s silent-success contract.
- **Vertex-stage texture sampling on the GL target now fails at compile time with a
  clear diagnostic** instead of emitting GLSL that MonoGame's GL runtime cannot bind
  (it was silently broken at runtime in two independent ways).
- Sample: `ShaderFiddle.Web` gained an export station — compile once in the browser and
  download the compiled artifact for each target (OpenGL/DirectX `.mgfx`, FNA `.fxb`).

### Fixed

- **GL: effects with a custom vertex shader rendered upside-down when drawing to the
  backbuffer** (the normal game case — only render-target rendering was correct), and
  `UseHalfPixelOffset` was ignored. ShadowDusk baked a static Y-flip into the vertex
  shader where MonoGame expects `mgfxc`'s dynamic `posFixup` uniform (the runtime flips
  the sign for backbuffer vs render target and applies the half-pixel offset).
  ShadowDusk now emits the exact `posFixup` contract, validated pixel-identical
  (max delta 0) to `mgfxc` in real MonoGame 3.8.2 in **both** backbuffer and
  render-target modes.
- **MGFX: pass render states, annotations, and `sampler_state` filter/address states
  are now written in MonoGame 3.8.2's exact wire format.** A pass carrying render
  states (e.g. `AlphaBlendEnable = TRUE;`) or annotations could desync or fail the real
  `Effect` reader, and sampler filter/address modes were silently dropped on MGFX
  targets. All three are now byte-faithful to the real reader, golden-validated and
  render-validated in real MonoGame.
- **GL: effects with multiple cbuffers, a cbuffer shared by VS and PS, or uniform
  arrays now get a correct uniform/parameter model.** Same-stage cbuffers merge into
  one register space, per-stage records bind correctly (a buffer shared by VS and PS is
  no longer deduped into an unbindable record), and array parameters carry per-element
  records so `Effect.Parameters` behaves as with `mgfxc`. Shapes the GL model does not
  yet cover (int/bool/mat3/struct uniform members) now fail loudly at compile time
  (`SD0210`/`SD0012`) instead of emitting wrong GLSL.
- **GL on Mesa (Linux): explicit-LOD/gradient sampling** (`SampleLevel`, `SampleGrad`,
  projective forms) failed on strict drivers because the rewriter emitted generic
  `textureLod`/`textureGrad` in versionless GLSL. These now lower to the legacy builtin
  names under MojoShader's guarded `GL_ARB_shader_texture_lod` header, matching
  `mgfxc`.
- **A first-use race in all three native-library loaders** (DXC, vkd3d-shader,
  SPIRV-Cross): a concurrent first compile could P/Invoke before the import resolver
  was registered, surfacing as an intermittent `DllNotFoundException` under test
  parallelism. Also revived the SPIRV-Cross resolver, which matched the wrong library
  name and never fired (the library had loaded only via default probing).
- Preprocessor/lexer robustness on real-world `.fx`: `#include` diamonds (the same
  header reachable via two paths) no longer error; directives inside comments are
  ignored; the HLSL lexer no longer silently swallows minus signs or unknown
  characters. SPIR-V reflection now populates struct `Members` (parity with the DXIL
  oracle), and colliding `SDxxxx` diagnostic codes were renumbered behind a registry
  test.
- WASM: the DXC module load retries after a transient fetch failure, and the vkd3d
  shim is hardened (allocation null-checks, bounded string reads, clean retry after a
  failed init) — a flaky first fetch no longer wedges the in-browser compiler.

### Verified

- **The CLI and the in-process library emit byte-identical output** — proven over the
  fixture corpus by a parameterized suite that runs every fixture through both
  invocation modes (the CLI is a delivery shape of the library, now machine-checked).
- **The pre-1.0 verification sweep closed every deferred verify item from the
  foundation phases** with 32+ new tests (negative diagnostics coverage, golden
  parameter-table matches against the `mgfxc` goldens, include-resolver and GLSL
  Y-flip checks), plus scripted pack / global-install / self-contained-publish
  verification of the CLI.

## [0.4.0] - 2026-06-11

### Added

- **macOS shader compilation works.** The upstream `Vortice.Dxc` package ships no macOS
  DXC native, so every OpenGL/WebGL compile on a Mac threw `DllNotFoundException`.
  ShadowDusk now bundles its **own `libdxcompiler.dylib`** for osx-x64 and osx-arm64,
  built from the exact DXC commit the bundled Windows/Linux natives report
  (1.7.2212.40 / `e043f4a1` — same compiler, never a substitute), SHA-256-pinned and
  loaded automatically. The full integration suite is green on macOS in CI.

### Changed

- **DirectX 11 (`.mgfx`) compiles now run end-to-end on Linux and macOS** (Phase 18
  Track A). DXBC reflection no longer P/Invokes Windows-only `D3DReflect`
  (d3dcompiler_47): it is a pure-managed reader of the DXBC container's `RDEF`/`ISGN`/
  `OSGN` chunks (`RdefReader`), proven deeply equal to `D3DReflect`'s output for both
  the d3dcompiler_47 and vkd3d backends, with **zero change to emitted `.mgfx` bytes**
  (full-corpus A/B, DirectX + OpenGL). With the vkd3d backend (which already shipped
  for all four desktop RIDs), no Windows-only native remains on the DX11 path.
- The DXC compiler is now constructed lazily inside the pipeline: DirectX 11 compiles
  never load the DXC native (FNA already did not), so they work on hosts where it is
  unavailable (e.g. macOS, pending the Phase 37 A DXC dylib). OpenGL/Vulkan behavior is
  unchanged.

### Fixed

- **Linux shader compilation no longer fails with `Internal Compiler error`.** Every DXC
  compile on Linux failed (`error X0000`): Vortice.Dxc's managed wrapper marshals DXC's
  `LPCWSTR*` arguments as UTF-16 on every OS, but DXC's non-Windows builds use the
  platform's 4-byte `wchar_t` (UTF-32), so the native compiler read garbage arguments.
  ShadowDusk now invokes `IDxcCompiler3::Compile` with platform-correct argument encoding
  (and an explicit UTF-8 source buffer). The native compiler binary is unchanged; Windows
  output is byte-identical. The same fix is what makes the new macOS dylib work.
- The in-browser render-validation harness (the WebGL-vs-DesktopGL pixel compare behind
  the KNI/WebGL support claims) now runs in CI on every change, on a software-GL baseline
  with documented per-shader tolerances — 10/10 corpus shaders load and render
  equivalently in real KNI WebGL1, and the issue #7 HiDef/WebGL2 guard runs with it.

### Verified

- **Cross-host determinism is machine-verified.** CI now asserts a committed SHA-256
  manifest of compiled output (102 fixture×target entries: OpenGL, DirectX via vkd3d, FNA)
  independently on Windows, Linux, and macOS — the emitted bytes are identical on every
  OS, so the Windows render-validation results apply byte-for-byte everywhere.
- **The consumer experience is machine-verified.** A CI job on all three OSes packs the
  packages, installs `ShadowDusk.Compiler` into a scratch project from a local feed, and
  compiles real shaders through it (including the bundled-natives check that a 0.2.0-style
  empty package can never ship again). `THIRD-PARTY-NOTICES.txt` now also covers the
  bundled DXC dylibs (LLVM Release License).

## [0.3.0] - 2026-06-10

### Added

- **FNA support: the new `PlatformTarget.Fna` output target.** Compiles D3D9-style `.fx`
  to the legacy D3D9 Effects binary (`.fxb`) FNA loads — no `fxc.exe`, no Wine, on every
  desktop OS. Render-validated in real FNA 26.06: the validation corpus (PS-only,
  VS-driven, multi-pass, in-pass render states) draws pixel-equivalent (max Δ ≤ 1/255) to
  `fxc /T fx_2_0` output. Purely additive — existing OpenGL/DirectX output is unchanged.
- **vkd3d-shader natives for all four desktop RIDs now ship inside `ShadowDusk.HLSL`**
  (win-x64, linux-x64, osx-x64, osx-arm64; pinned vkd3d 1.17, SHA-256-verified at
  restore). The FNA target and the opt-in `DxbcBackend.Vkd3d` DirectX backend are
  self-contained from the package — add the package, compile, no manual install.
- `THIRD-PARTY-NOTICES.txt` (vkd3d-shader attribution + LGPL-2.1 text) ships in the
  `ShadowDusk.HLSL` package.
- Docs: new "Choosing a target" guide (OpenGL vs DirectX vs FNA, and why output is raw
  `.mgfx`/`.fxb` rather than `.xnb`).

### Changed

- The release pipeline now refuses to publish if the packed `ShadowDusk.HLSL` package is
  missing any of the four vkd3d natives or the license notice — a stopped release beats
  shipping the FNA target broken.

### Fixed

- **FNA: brace-form sampler blocks (`sampler s = sampler_state { Texture = (tex); … };`)
  now bind their texture correctly** — previously the binding was silently lost and the
  effect rendered wrong with no diagnostic.
- **FNA: all render states FNA honors are now emitted into the `.fxb`** (11 previously
  missing states), and states FNA would throw on are rejected loudly at compile time
  (`SD0303`) instead of failing at runtime.
- FNA: matrix parameters now carry the same parameter class `fxc` emits (column-major
  fidelity, pinned by a new golden); shader-model/stage mismatches are caught at compile
  time; SM1 profiles are rejected with a clear error.

## [0.2.0] - 2026-06-07

### Added

- **`ShaderError` is now the diagnostics contract on every host.** A failed
  `IShaderCompiler.CompileAsync` returns `ShaderError[]` with `File`, `Line`, `Column`,
  and the compiler's `Message` verbatim — usable as a `.fx` validator (ignore the bytes,
  read the errors). This already worked on desktop; the **in-browser (WASM) path now carries
  the same line/column** (see Fixed), so a KNI/Blazor tool can highlight the offending line
  with no API change.

### Changed

- **Sample `ShaderFiddle.Web` highlights compile errors.** Bad shader lines get a wavy
  underline, the line-number gutter shows the message on hover, and each diagnostic is
  clickable to jump to its line — a demonstration of the line/column diagnostics above.

### Fixed

- **In-browser (WASM) compile errors now report the source line and column.** Previously a
  failed in-browser compile surfaced a single opaque error (`[object WebAssembly.Exception]`)
  with no location, while desktop reported file/line/column. DXC captured the diagnostics, but
  the WASM module *threw* them and `-fwasm-exceptions` made the text unreadable in JS. The
  faithful DXC→WASM module now **returns** its diagnostics, so the in-browser path runs them
  through the same reformatter as desktop and yields `ShaderError`s with real `Line`/`Column`.
  Compiled output is byte-identical to before (success-path SPIR-V unchanged, 10/10).

## [0.1.1] - 2026-06-07

Maintenance release: the CLI is rebranded to `ShadowDuskCLI`, plus release-pipeline and CI
reliability fixes. The product libraries (`Core` / `HLSL` / `GLSL` / `Compiler` / `Wasm`)
are functionally identical to 0.1.0 — they remain platform-agnostic .NET packages that work
for Linux, macOS, and Windows consumers from a single install.

### Changed

- **CLI renamed `mgfxc` → `ShadowDuskCLI`.** The `dotnet tool` command and the self-contained
  binary now ship under ShadowDusk's own brand rather than the name of the tool they replace.
  The NuGet package id is unchanged (`ShadowDusk.Cli`). To use it as a drop-in for MonoGame's
  content pipeline, point MGCB's `ExternalTool` at `ShadowDuskCLI` (or alias it to `mgfxc`).

### Fixed

- **Release now produces the per-RID self-contained CLI binaries.** The single-file publish
  names the apphost after the assembly, so the GitHub Release verify/archive steps now target
  `ShadowDuskCLI`; 0.1.0's `Publish CLI` jobs failed looking for a `mgfxc` binary.
- **macOS CI no longer hangs.** The ImageTests GL fixture initialized GLFW on macOS, leaving a
  non-background Cocoa thread that kept the test host from exiting after a green run. The GL
  render proxy is now correctly treated as N/A on macOS (Apple deprecated OpenGL; the proxy is
  covered on Linux + Windows), so macOS is back in the release gate and completes in seconds.
- **Quieter, tighter CI.** Doc-only pushes skip the build matrix, the WASM/browser workflow is
  on-demand, and CI job timeouts were tightened from 25–30 min to 10–12 min.

## [0.1.0] - 2026-06-07

First public release. A single faithful HLSL → `.mgfx` pipeline
(HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL → managed reflect + MojoShader-dialect rewrite +
MGFX writer, or vkd3d-shader → DXBC for DirectX), delivered as a library, a CLI tool, and a
WASM-capable build — the same pipeline on every host, with no substitute compilers.

### Added

- **Cross-platform in-memory `.fx` → `.mgfx` compile.** `ShadowDusk.Compiler`
  (`EffectCompiler : IShaderCompiler`) compiles HLSL `.fx` shaders to MonoGame `.mgfx`
  bytes in-process on Linux, macOS, and Windows — no `fxc.exe`, no `mgfxc`, no Wine, no
  Windows SDK. `IShaderCompiler.CompileAsync(fx)` returns `.mgfx` bytes; no temp files or
  child process required by the API.
- **OpenGL / DesktopGL backend.** HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL with a managed
  MojoShader-dialect rewriter and MGFX writer. SPIRV-Cross rides inside the package via the
  `Silk.NET.SPIRV.Cross.Native` transitive dependency, and DXC via `Vortice.Dxc` — so
  `dotnet add package ShadowDusk.Compiler` and call the API is the entire setup for the GL
  path on a clean machine.
- **DirectX DXBC backend.** Compiles HLSL → SM5 DXBC in-process (no `fxc.exe`/`mgfxc`)
  behind the `IDxbcShaderCompiler` seam, with two backends chosen by
  `CompilerOptions.DxbcBackend`: the **default** `d3dcompiler_47` (Microsoft's HLSL
  compiler — a system DLL already present on Windows; most `fxc`-faithful) and the **opt-in,
  cross-platform** `vkd3d-shader` (`DxbcBackend.Vkd3d`) for compiling DX shaders on
  Linux/macOS where `mgfxc` cannot run. Both render pixel-equivalent to `mgfxc` (Phase 18).
  DXC is not used for DX11 (it emits DXIL/SM6, not DXBC/SM ≤ 5); its `ps_6_0`/`vs_6_0` output
  is retained for the DX12/KNI path. *(Cross-platform `vkd3d` is not yet packaged in the
  NuGet — see Known limitations.)*
- **`mgfxc`-compatible CLI tool.** `ShadowDusk.Cli` ships as a `dotnet tool` named `mgfxc`
  (`dotnet tool install -g ShadowDusk.Cli`) — same CLI flags, same `.mgfx` output format,
  same exit codes, and MGCB-parseable stderr diagnostics, so existing MonoGame content
  pipelines switch with zero code changes (via `ExternalTool` config or PATH override).
- **WASM / in-browser compile engine.** `ShadowDusk.Wasm` (`WasmShaderCompiler`) targets
  `net8.0-browser` and runs the same faithful pipeline in the browser via `[JSImport]`
  bindings to WASM-compiled DXC and SPIRV-Cross — emitting `.mgfx` bytes identical to the
  CLI/desktop path. A pure-managed `SpirvReflector` reflects SPIR-V without a DXIL oracle.
  The in-browser shader-fiddle (`samples/ShaderFiddle.Web`) is a sample of this reach.
- **KNI HiDef / WebGL2 (GLSL ES 3.00) output.** A single `.mgfx` loads and renders in both
  KNI Reach (WebGL1 / GLSL ES 1.00) and KNI HiDef (WebGL2 / GLSL ES 3.00) — the rewriter
  emits `mgfxc`'s `#define ps_oC0 gl_FragColor` form that KNI's runtime converts to a typed
  `out vec4`, with zero consumer input and no new flag or format.
- **GL texture breadth.** Cube maps work on every GL target; 3D textures and explicit
  LOD / gradient sampling work on Desktop and HiDef (WebGL1 cannot, by platform limit). The
  MGFX sampler `Type` byte now carries the reflected texture dimension (2D / Cube / 3D), and
  the rewriter emits per-dimension sampling builtins.
- **VS-driven effects (custom vertex shaders).** Effects that ship their own vertex shader
  (a `float4x4` transform with `POSITION` / `COLOR0` / `TEXCOORD0` attributes) compile
  faithfully on the GL path — the `MonoGameGlslRewriter` emits the symmetric
  `vs_uniforms_vec4` block, the legacy `attribute`/`varying` stage I/O, and the full
  matrix-uniform expansion — not just pixel-shader-only post-process effects.
- **Forward-compatibility with newer MonoGame.** ShadowDusk's default MGFX **v10** output
  loads and renders correctly in MonoGame **3.8.4.1** (the latest stable 3.8.x) as well as
  the pinned **3.8.2.1105** baseline — pixel-identical on the same bytes, within tolerance
  of the `mgfxc` goldens — so a consumer's existing `.mgfx` keeps working forward with no
  action required. A forward-compat regression guard backs this.
- **Self-contained single-file CLI.** `dotnet publish -r <rid> --self-contained` produces a
  working `mgfxc` binary that bundles the native dependencies it needs.

### Validated

- **OpenGL fidelity in the real MonoGame runtime (Phase 17).** All 10/10 shaders of the SM3
  PS-only corpus load in a real MonoGame DesktopGL `Effect` and render pixel-equivalent to
  `mgfxc` — the strongest rung of the evidence ladder: in-engine behavioral equivalence.
- **DirectX fidelity in the real MonoGame runtime (Phase 18).** All 10/10 DX `.mgfx` of the
  SM5 PS-only corpus load in real MonoGame WindowsDX and render pixel-equivalent to `mgfxc`,
  via both the `d3dcompiler_47` oracle and the cross-platform vkd3d-shader backend.
- **VS-driven fidelity in the real MonoGame runtime (Phase 28).** A VS-driven `.fx` (custom
  vertex shader + `float4x4` transform) compiled by ShadowDusk loads in real MonoGame
  DesktopGL **and** WindowsDX and renders pixel-identical (max delta 0) to its `mgfxc`
  golden, on both the `d3dcompiler_47` oracle and the cross-platform vkd3d backend for DX.
- **In-browser render proof (Phases 22–24).** Corpus `.mgfx` load and render in real
  headless KNI WebGL (Reach and HiDef/WebGL2), and the faithful in-browser DXC → WASM path
  emits `.mgfx` byte-identical to the CLI for the corpus.
- **Deterministic, byte-identical output across hosts.** Same ShadowDusk version + same
  source + same target produces the same `.mgfx` bytes on desktop, CLI, and WASM.

### Known limitations

- **DirectX from a pure NuGet add is not yet fully self-contained.** The GL + DXC in-memory
  path ships self-contained from NuGet today; the DirectX vkd3d-shader native is a restored,
  non-redistributed artifact not yet packaged as a `runtimes/<rid>/native/` asset. The
  0.1.0 line advertises GL-from-NuGet as the self-contained path.
- **VS-driven effects** are covered for the SpriteBatch-compatible attribute set
  (`POSITION` / `COLOR0` / `TEXCOORD0`); additional vertex semantics (`NORMAL` / `TANGENT` /
  skinning) and Metal/Vulkan vertex-shader paths are follow-ons.
- **Metal (MSL) and Vulkan backends** are not yet implemented (stubs only).
- **The MGCB content-processor plugin** is a scaffold; the PATH-based `mgfxc` override is the
  shipping MGCB integration path.

[Unreleased]: https://github.com/kaltinril/ShadowDusk/compare/v0.17.0...HEAD
[0.17.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.16.0...v0.17.0
[0.16.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.15.1...v0.16.0
[0.15.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.15.0...v0.15.1
[0.15.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.2...v0.15.0
[0.14.2]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.1...v0.14.2
[0.14.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.0...v0.14.1
[0.14.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.13.0...v0.14.0
[0.13.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.12.1...v0.13.0
[0.12.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.12.0...v0.12.1
[0.12.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.10.0...v0.11.0
[0.10.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.8.0...v0.9.0
[0.8.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/kaltinril/ShadowDusk/releases/tag/v0.1.0
