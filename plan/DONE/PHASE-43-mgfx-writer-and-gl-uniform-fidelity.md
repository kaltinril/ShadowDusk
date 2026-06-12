# Phase 43 — MGFX writer & GL uniform-model fidelity (beyond the validated corpus)

**Status:** ✅ **COMPLETE — 2026-06-12** (43A writer-format → 43B posFixup/LOD →
43C cbuffer/array model; Definition of Done met, see the as-built sections).
Created 2026-06-12 from the four-lens full review (QA / Coder /
cross-platform / shader-expert). The shader-expert lens found **seven HIGH fidelity
defects**, all verified against compiled artifacts and the actual MonoGame 3.8.2 /
MojoShader sources, all in **input shapes the rung-4 corpus never contained**. Nothing
here contradicts the existing rung-4 proofs — it maps exactly where their coverage ends.
**Track:** Fidelity / completeness.

> **2026-06-12 — Phase 43A (writer-format half) landed:** F1 + F1b + F2 + F9 + F10 are
> ✅ fixed with the full validation ladder (PR `feature/phase43a-mgfx-writer-fidelity`).
> As-built details in the per-finding notes below and the **Phase 43A as-built**
> section at the end.

> **2026-06-12 — Phase 43B (posFixup/LOD half) landed:** F3 + F7 + F8 ✅
> (PR `feature/phase43b-gl-posfixup-lod`).

> **2026-06-12 — Phase 43C (GL cbuffer/array model, the FINAL wave) landed:**
> F4 + F5 + F6 are ✅ fixed (per-stage cbuffer records, same-stage merge, array
> modelling with recursive Elements on every target) and the F11 Slang-normalizer
> cleanup is done (the pre-pass is REMOVED). As-built details in the per-finding
> notes and the **Phase 43C as-built** section at the end. **With this, every
> finding in this phase is closed — the Definition of Done is met.**

> **The pattern (why rung-4 missed all of these):** the structural test suite validates
> `.mgfx` with ShadowDusk's *own* reader, and the render-proven corpus contains **no pass
> render-states, no annotations, no shared/multi/array cbuffers, and renders only into
> `RenderTarget2D`** (never the backbuffer). That is precisely the proxy trap
> `docs/the-purpose.md` warns about. **The fix for this phase is therefore corpus-first:**
> every item below gets a fixture exercising the shape, validated by (a) structural
> comparison against the in-repo `mgfxc` goldens where they exist, (b) **loading in real
> MonoGame 3.8.2 `Effect`**, and (c) render comparison where applicable — *before* the
> code change is considered done.

---

## Findings (from the 2026-06-12 shader-expert review — file:line as of main `6c05c91`)

### F1 — HIGH: pass render-state block uses a format MonoGame cannot read ✅ FIXED (43A)

`src/ShadowDusk.Core/MgfxWriter.cs:198-285` writes `(byte fieldId, int32 value)` pairs +
`0xFF` sentinel. **MonoGame 3.8.2 `Effect.ReadEffect` reads a fixed field sequence**
(blend: 11 single-byte enum fields + a 4-byte BlendFactor color + an int32
MultiSampleMask, in alphabetical order; analogous fixed layouts for depth-stencil and
rasterizer). Any `.fx` with pass states (`AlphaBlendEnable = TRUE;` etc.) produces a
`.mgfx` that desyncs the reader → **Effect load failure**. Compounded by **F1b**:
`src/ShadowDusk.Core/RenderStateBlock.cs:88-118`'s Blend enum values do NOT match
MonoGame's ordinals despite the comment claiming "verified" (One/Zero swapped, Dest pairs
swapped, last three wrong; CullMode is D3D9-valued). FNA is unaffected (its
`Fx2EffectBuilder.MapBlend` maps symbolically and is verified correct).
**Fix:** rewrite `WriteBlendState/WriteDepthStencilState/WriteRasterizerState` to
MonoGame's exact fixed field order/types with mgfxc's defaults for unset fields; fix the
enum values + comment together.

### F2 — HIGH: annotation bodies desync the reader ✅ FIXED (43A)

`MgfxWriter.cs:287-311` writes annotation name/type/value after the count — MonoGame
3.8.2 `ReadAnnotations` reads **only the int32 count** ("TODO: Annotations are not
implemented!"). Any annotated `.fx` (`< string ui = "color"; >` — ubiquitous in XNA-era
shaders) → stream desync → load failure. **Fix:** write the count and **no bodies**
(mirror mgfxc). This also moots the annotation type-sniffing defect (int annotations
typed Single, bools become strings — `CompilationPipeline.cs:1046-1080`).

### F3 — HIGH: static Y-flip instead of the dynamic `posFixup` contract — ✅ FIXED (PR: Phase 43B)

> **Fixed 2026-06-12** (`feature/phase43b-gl-posfixup-lod`). `FlipVertexY` is off in
> `SpirvCrossGlslTranspiler` AND the WASM `JsShaderBackends` path (kept identical);
> `MonoGameGlslRewriter.InjectPosFixup` emits `uniform vec4 posFixup;` (after
> `vs_uniforms_vec4[]`, the golden's declaration order) + the two fixup lines before
> the kept depth-convention line. Note: the spec text below says the depth line
> "already matches the golden byte-for-byte" — almost: SPIRV-Cross spells it
> `2.0 * gl_Position.z` where the golden spells `gl_Position.z * 2.0`
> (mathematically identical; kept as SPIRV-Cross emits it).
> **Evidence:** (a) string-decisive — `Phase43PosFixupRenderTests.EmittedVs_…` reads
> the posFixup lines OUT OF the golden and asserts ShadowDusk's VS carries them;
> (b) render — same test class: render-target fixup (y=-1) pixel-equivalent to the
> golden VS+PS, backbuffer fixup (y=+1) is its exact vertical mirror;
> (c) **real-runtime backbuffer** — `validation/VsDriven` gained a backbuffer mode
> (real MonoGame 3.8.2 `GetBackBufferData`): candidate vs mgfxc baseline **maxd 0
> in BOTH render-target and backbuffer modes** (run 2026-06-12, verdict PASS).
> Proxy renderers now set posFixup per the MonoGame rule (`MojoPosFixup`); KNI
> sets it automatically (`ConcreteGraphicsContext.ApplyPosFixup`, verified).
> GL manifest regenerated: exactly the 13 VS-bearing entries changed.

`src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs:73-75` bakes
`gl_Position.y = -gl_Position.y;` via SPIRV-Cross `FlipVertexY`. mgfxc/MojoShader emit a
**runtime `posFixup` uniform** — MonoGame sets `posFixup.y = +1` for the backbuffer and
`-1` only when a render target is bound (and skips it entirely if the uniform is absent),
plus the half-pixel offset via `posFixup.zw`. ShadowDusk's VS-driven GL effects therefore
render **vertically inverted in the normal game case** (drawing to the backbuffer) and
ignore `UseHalfPixelOffset`. The rung-4 harnesses all render into `RenderTarget2D`
(`validation/Shared/VsEffectImageRenderer.cs:107`) — the one case where the static flip
matches. The golden `VsTransformColorTexture.mgfx` VS contains the exact target form:
`uniform vec4 posFixup; … gl_Position.y *= posFixup.y; gl_Position.xy += posFixup.zw * gl_Position.ww;`.
**Fix:** disable `FlipVertexY`; emit the `posFixup` declaration + mgfxc's two fixup lines
in the VS rewriter (keep the existing depth line — it already matches the golden).
**Validation must include a backbuffer render**, not only RT.

### F4 — HIGH: shared VS+PS cbuffer deduped into an unbindable record — ✅ FIXED (43C)

> **Fixed 2026-06-12** (`feature/phase43c-gl-cbuffer-array-model`). GL cbuffer
> records are now built ONE PER SHADER from the uniform register layout the
> `MonoGameGlslRewriter` returns (`MonoGameGlslResult.Uniforms` — the same
> allocation the emitted GLSL indexes), named `vs_/ps_uniforms_vec4` by stage and
> deduplicated across shaders mgfxc-style (`ConstantBufferData.SameAs`); each GL
> shader's cbuffer-index list points at its own record by construction. The mgfxc
> golden for the new `SharedCbuffer.fx` pins the model: a record per stage; the
> SkinnedEffect golden additionally pins that several records may share a NAME
> (3× `vs_uniforms_vec4`). **Pinned divergence:** mgfxc's per-stage records carry
> only the constants fxc kept per stage (vs=64/ps=16); ShadowDusk's carry the full
> declared block per stage (80/80) — self-consistent with its own GLSL, set by
> name, render-proven equivalent. **Evidence:** structural —
> `Phase43CbufferModelTests.SharedCbuffer_EmitsPerStageRecords_VsArrayIsBindable`;
> real bar — `validation/CbufferModel` SharedCbuffer row: real MonoGame 3.8.2
> `Effect` load + render **maxDelta 0** vs the mgfxc-golden arm with the transform
> + DiffuseColor set by name, plus a non-black guard (the pre-43C failure was a
> black frame). GL manifest: exactly the 3 shared-cbuffer entries changed
> (`cbuffer.fx`, `VertexAndPixel.fx`, `PolygonLight.fx`); 0 DX/FNA changes.

`src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs:343-355, 974-975`: a cbuffer
bound by both stages becomes ONE record named `ps_uniforms_vec4` (the `vsBound` test
requires `Vs && !Ps`), but the VS GLSL reads `vs_uniforms_vec4[]` → MonoGame never sets
the VS array → **VS uniforms silently read zero** (a shared `WorldViewProj` kills the
vertex stage). Reproduced with the repo's own `tests/fixtures/shaders/cbuffer.fx`.
**Fix:** emit per-stage cbuffer records (`vs_uniforms_vec4` + `ps_uniforms_vec4`,
mgfxc's model) instead of deduping across stages by reflection name.

### F5 — HIGH: multiple cbuffers break the GLSL and the parameter model — ✅ FIXED (43C)

> **Fixed 2026-06-12.** The rewriter parses EVERY std140 block (named cbuffers'
> `type_<Name>` included) and merges all of a stage's blocks into the ONE
> `{vs,ps}_uniforms_vec4[]` register space in declaration order — MojoShader's
> model (one float-constant file per stage), pinned by the `MultiCbuffer.fx`
> golden (ONE `ps_uniforms_vec4`: TintA@0/TintB@16/MixAmount@32, size 48 —
> ShadowDusk's record matches it EXACTLY, offsets included). The accidental
> Slang-normalizer UBO rename that F5 tripped over is gone (the whole legacy
> pre-pass is removed — F11). Unparseable blocks/members fail loudly (SD0210).
> **Evidence:** structural — `Phase43CbufferModelTests.MultiCbuffer*` (record ==
> golden, no `std140`/`type_` survives in the GLSL); real bar —
> `validation/CbufferModel` MultiCbuffer + MultiCbufferVs rows: **maxDelta 0**
> vs the golden arm with members of BOTH cbuffers set by name (PS and VS
> variants).

`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs:146` (+ the stale Slang normalizer at
708-718 whose UBO-rename branch accidentally fires): only the first block is rewritten;
the second ships as raw `layout(binding = 1, std140) uniform type_B { … } B;` inside
**versionless legacy GLSL** → GL compile error at Effect load; the `.mgfx` also carries
two cbuffers both named `ps_uniforms_vec4`. Compile exits 0 today.
**Fix:** merge all same-stage cbuffers into one `{vs,ps}_uniforms_vec4` register space
(MojoShader's model) — or fail loudly until merging lands.

### F6 — HIGH: array uniforms unmodeled (GLSL emission + parameter elements) — ✅ FIXED (43C)

> **Fixed 2026-06-12.** (a) GLSL: array members pack at their element stride
> (1 register per float/vec2/vec3/vec4 element — exactly how MonoGame's
> `ConstantBuffer.SetParameter` advances one 16-byte row per written row, and
> D3D9's float-register packing; 4 per mat4 element), every indexed use is
> rewritten to the packed `{vs,ps}_uniforms_vec4[base + idx]` form (literals
> folded; dynamic indices keep the arithmetic — MojoShader's relative form).
> (b) `.mgfx`: `EffectParameterInfo` carries RECURSIVE Members/Elements and the
> writer emits MonoGame 3.8.2 `ReadParameters`' exact recursive wire format
> (elements first, then struct members; leaf-only data blobs) — the old flat
> int32-list writer (wrong order too) was a guaranteed reader desync for any
> array. `BuildEffectParameterInfoList` emits element sub-records (empty
> name/semantic, parent shape — mgfxc's `GetParameterFromSymbol`) on EVERY
> target, so `SetValue(array)` / `.Elements[i]` work beyond element 0 on DX too.
> Unmodelled member types (int/bool/ivec/mat3/mat2/struct/qualified), whole-array
> uses, and any surviving block reference FAIL LOUDLY (SD0210; fixtures
> `examples/ExIntUniformMember.fx`, `examples/ExMat3UniformMember.fx`; documented
> in `docfx/guides/parameters-and-caveats.md`).
> **Evidence:** the mgfxc goldens decode-pin the element-record shape (SkinnedEffect
> `Bones[72]` now decodes byte-clean with the fixed recursive decoders); structural —
> `Phase43CbufferModelTests.ArrayParameters_CarryElementSubRecords…` compares the
> element trees recursively vs the goldens on GL AND DX; real bar —
> `validation/CbufferModel` ArrayUniform + ArrayUniformVs rows: **maxDelta 0** with
> the whole array SET FROM MANAGED CODE plus an individual `.Elements[2].SetValue`
> overwrite (PS) and a two-element `Bones[]`/`PosOffsets[]` blend (VS).
> **mgfxc-bug finding (golden correction):** an array read at only SOME static
> indices is broken in mgfxc+MonoGame GL itself — fxc references only the used
> registers, MojoShader emits a COMPACTED uniform array
> (`vs_c4..c7,c9 → vs_uniforms_vec4[0..4]`) while mgfxc's record keeps the full
> 160-byte layout, so MonoGame's full-buffer `glUniform4fv` lands element 0's data
> where the shader reads element 1 — **verified: that golden renders garbage in
> real MonoGame 3.8.2 while ShadowDusk's full-layout output renders the source
> semantics correctly** (PNGs from the 2026-06-12 harness run). ShadowDusk
> deliberately does NOT replicate the compaction; `ArrayUniformVs.fx` references
> every element (the real-world skinning shape) so its golden arm is itself
> correct and comparable.

`MonoGameGlslRewriter.cs:150-151`: the `UniformMember` regex skips `vec4 Colors[4];`
(also `int`, `mat3`, `layout(…)`-qualified members) → emitted GLSL still references the
deleted block (`_Globals.Colors[1]`) → invalid GLSL, load failure, compile exits 0.
Independently `CompilationPipeline.cs:1031-1040` (`BuildEffectParameterInfoList`) writes
`Elements` count 0 for **every** target — array params un-settable beyond element 0 even
on DX (MonoGame reads elements as recursive sub-parameter collections).
**Fix:** model array members (N×registers, indexed rewrite) + emit element sub-parameter
records; until then **fail loudly (SD-coded)** on array/int/mat3 members instead of
emitting broken output.

### F7 — HIGH (Linux/Mesa): generic `textureLod`/`textureGrad` in versionless GLSL — ✅ FIXED (PR: Phase 43B)

> **Fixed 2026-06-12.** Rule 6b now rewrites per the sampler's modelled dimension
> (`texture2DLod`/`textureCubeLod`/`texture3DLod`, `texture2DGrad` 2D-only with a
> loud error for cube/3D gradients — no GLSL or extension defines a legacy
> spelling, MojoShader's own `textureCubeGrad` output can never link — and
> `texture2DProj`/`texture3DProj`), prepending MojoShader's
> `prepend_glsl_texlod_extensions` header composed with its GLSLES3 preflight:
> `#if __VERSION__ >= 300` (KNI HiDef maps legacy → generic; one artifact, two
> profiles) / `#elif defined(GL_ARB_shader_texture_lod)` /
> `#elif defined(GL_EXT_gpu_shader4)` / `#else` graceful degrade. Deviation from
> MojoShader: `defined(GL_…)` instead of bare `#if GL_…` because GLSL ES 1.00
> errors on undefined macros in `#if` (bare form would hard-fail WebGL1/Reach).
> Header emitted ONLY when a LOD/grad/proj call was rewritten — PS-only corpus
> bytes unchanged (manifest proof). Stale fixture headers + the
> `glsl-uniform-naming.md` Rule-6 row fixed; the **PR #48 Linux-lane exclusion of
> `Phase34LodGradRenderTests` is REMOVED** — the ubuntu Mesa lane referees the fix
> (NVIDIA dev box already renders the new form with the explicit mip honored).
> Watch item: `Phase34LodGradRenderTests.SampleLevel…` live-compiles its fixture;
> if the per-OS DXC compile divergence (F11) bites it on the Linux lane, that is
> F11 evidence to handle there, not a reason to re-mask the Mesa coverage.

`MonoGameGlslRewriter.cs:486-501` (Rule 6b) leaves generic `textureLod()`/`textureGrad()`
in a GLSL-1.10 (no `#version`) shader — invalid before 1.30; Mesa enforces strictly →
Effect-load compile failure on Linux DesktopGL. (This confirms and root-causes the
Phase 37/34 watch item; NVIDIA/Windows lenience is why it passes locally.) MojoShader's
faithful form: dimension-specific `texture2DLod/textureCubeLod/texture3DLod` (+
`texture2DGrad`) with the guarded extension header
(`#if GL_ARB_shader_texture_lod … #elif GL_EXT_gpu_shader4 … #else #define texture2DLod(a,b,c) texture2D(a,b) #endif`)
— degrades gracefully, never fails to compile. For KNI HiDef/ES3, MojoShader's own ES3
header maps `texture2DLod → textureLod`.
**Fix:** rewrite to the dimension-specific legacy names + prepend the guarded extension
block; ES3-guarded define for HiDef. Also fix the stale fixture header
(`tests/fixtures/shaders/examples/ExSampleLevelHidef.fx` claims "Expect: FAILS SD0210"
but compiles green) and `docs/glsl-uniform-naming.md:65`'s Rule-6 row (claims the
dimension-specific rewrite already happens).

### F8 — MEDIUM: VS texture fetch (GL) silently broken twice — ✅ CLOSED (loud error; PR: Phase 43B)

> **Closed 2026-06-12 with the sanctioned STOPGAP** (loud `SD0210`), not the
> `vs_s{k}` contract — and deliberately so: implementing `vs_s{k}` cannot meet the
> real-runtime bar because **MonoGame 3.8.2's GL runtime cannot bind vertex
> textures at all**. Verified against the v3.8.2 sources:
> `ShaderProgramCache.Link` calls ONLY `pixelShader.ApplySamplerTextureUnits(program)`
> (the VS's sampler records never get a texture unit), and
> `GraphicsDevice.OpenGL.cs` has no `VertexTextures`/`VertexSamplerStates` apply
> path. Even a perfectly-named `vs_s0` uniform would read texture unit 0's
> incidental contents at draw time — silently-wrong output, the failure mode the
> purpose forbids. The rewriter now throws (`Vertex-stage texture sampling…`) for
> any VS sampler decl; end-to-end pinned by the new `ExVsTextureFetch.fx` fixture
> (`HidefGeneralityFixtureTests.VsTextureFetch_FailsLoudly_SD0210…`); limitation
> documented in `docfx/guides/parameters-and-caveats.md`. Revisit only if the
> runtime gap itself is ever solved (KNI does support VS textures; a KNI-specific
> contract would be a separate, additive decision).

`MonoGameGlslRewriter.cs:272-287` (`!isVertex` gate): VS sampler decls/uses are not
renamed (ships `uniform sampler2D _35;`) while the `.mgfx` VS sampler record says
`ps_s0` → texture never binds (black); plus generic `textureLod` in VS (F7).
**Fix:** implement the `vs_s{k}` contract end-to-end, or throw
`MonoGameGlslRewriteException` for VS samplers (fail-loudly) until implemented.

### F9 — MEDIUM: `sampler_state` filter/address states dropped on MGFX targets ✅ FIXED (43A)

`CompilationPipeline.Run` never consumes `fxParsed.Samplers` state members (only `RunFna`
does); `MgfxWriter.cs:125` writes `hasState = 0` always. mgfxc bakes
`MinFilter/AddressU/...` into the `.mgfx` sampler record and MonoGame applies them at
`EffectPass.Apply` → silent filtering/addressing divergence (Point becomes Linear).
Corpus sampler blocks only contain `Texture = <…>`, so rung-4 never saw it.
**Fix:** map parsed sampler states into the sampler record (`hasState = 1`) with
MonoGame's SamplerState field layout.

### F10 — MEDIUM-LOW: SPIR-V reflection drops struct `Members` ✅ FIXED (43A)

`src/ShadowDusk.Core/Reflection/Spirv/SpirvReflectionParser.cs` (`BuildVariable`) never
populates struct `Members` (the DXIL oracle does, recursively); the parity test
(`SpirvVsDxilReflectionTests`) compares 7 fields but not `Members`, so the gap is
invisible. **Fix:** extract members + add `Members` to the parity assertion.

### F11 — carried context (fixed elsewhere or follow-up)

- Duplicate render-state key crash (last-wins) and `tex2Dlod`/`tex2Dgrad` forwarding were
  handed to the 2026-06-12 contained-fix PR (`fix/review-compiler-bugs`); if that PR
  shipped only a loud error for tex2Dlod, the full `.SampleLevel` forwarding lands here.
- Per-OS DXC compile divergence (Phase 34 3D/LOD/grad intrinsics fail to compile on the
  Vortice linux/mac DXC builds — `ci.yml:140-146`) — root-cause here alongside F7, since
  both touch the same feature surface. Same pinned commit ≠ same binary.
- The stale "Browser path uses Slang" comment + the accidental Slang-normalizer UBO
  rename (`MonoGameGlslRewriter.cs:178-181, 708-718`) — remove/repair with F5.
  *(Update 2026-06-12, Phase 43B: the COMMENT is repaired — it now states the browser
  path runs the faithful DXC→WASM frontend and points at F5 for the normalizer
  itself.)* ***(Update 2026-06-12, Phase 43C: `NormalizeSlangNaming` is REMOVED
  entirely — the browser path runs the faithful DXC frontend so Slang-shaped GLSL
  cannot reach the rewriter, and named cbuffer blocks are now parsed natively by
  the generalized block handler. The two Slang unit tests were replaced by the
  F5/F6 cbuffer/array-model tests.)***

---

## Validation plan (the bar for every item)

1. **Corpus first:** add fixtures for each shape — pass render-states (blend/depth/raster
   combinations), annotated parameters/techniques, a shared VS+PS cbuffer, two+ cbuffers,
   `float4 x[4]` arrays (VS and PS), VS texture fetch, `sampler_state` with filter/address
   members, `tex2Dlod`. Each compiles through ShadowDusk **and** has an `mgfxc` golden
   where obtainable (the maintainer's Windows box can regenerate goldens — see
   `tests/fixtures/golden/README` conventions).
2. **Structural oracle:** byte/structure comparison against the mgfxc golden (the
   `MgfxParameterMatch` / golden-reader machinery exists). For F3 the golden already
   contains the exact `posFixup` lines — string-level GLSL comparison is decisive.
3. **The real bar:** every new fixture **loads in real MonoGame 3.8.2 `Effect`**
   (extend `validation/` / ImageTests' mgfxc cross-validation rows), and renders
   pixel-equivalent where a visual exists. **F3 additionally requires a backbuffer
   render test** — rendering only to RenderTarget2D is precisely how this bug survived.
4. **No regression:** the existing byte-identity manifest stays green for the existing
   corpus *until* a writer fix intentionally changes bytes — those manifest entries are
   then regenerated on win-x64 **in the same PR** with the change called out, and the
   browser G2 gate re-proven.
5. **Beyond-corpus guard:** once fixed, the new fixtures join the byte-identity manifest
   and (where render-proven) the rung-4 sets, so this class of gap cannot silently
   reopen.

## Suggested sequencing

1. **F1 + F1b + F2 (the writer-format trio)** — highest blast-victim count (any pass
   state or annotation bricks the file), cleanly golden-validatable, contained to
   `MgfxWriter` + `RenderStateBlock` + the annotation path.
2. **F3 (posFixup)** — golden-decisive, but needs the backbuffer validation harness
   extension.
3. **F4 + F5 + F6 (the GL cbuffer/array model)** — the deep one; design against
   MojoShader's register-space model; fail-loudly stopgaps acceptable as a first PR.
4. **F7 + F8 (LOD dialect + VS samplers)** — closes the Mesa watch item with the
   MojoShader-faithful header.
5. **F9, F10** — independent, can ride along.

## Phase 43A as-built (2026-06-12, branch `feature/phase43a-mgfx-writer-fidelity`)

**Scope: F1 + F1b + F2 + F9 + F10 (the writer-format half). F3–F8 untouched (43B).**

- **F1 (pass render states):** `MgfxWriter.Write{Blend,DepthStencil,Rasterizer}State`
  rewritten to MonoGame 3.8.2 `Effect.ReadPasses`' fixed alphabetical field layout
  (verified against the v3.8.2 tag source, == mgfxc's `EffectObject.writer.cs`), with
  mgfxc's state-object-constructor defaults for unset fields and mgfxc's PassInfo
  materialization semantics mirrored exactly: the `AlphaBlendEnable=TRUE` premultiplied
  preset (One/InvSrcAlpha), `SrcBlend/DestBlend → ToAlphaBlend`-derived alpha factors,
  and the **BlendOp → AlphaBlendFunction quirk** (ColorBlendFunction always ships Add —
  mirrored deliberately; rendering identically to mgfxc beats D3D9 correctness here).
- **F1b (enums):** `BlendValue`/`CullModeValue` now carry MonoGame's ordinals (One=0,
  Zero=1, Dest **Color** pair = 6/7 before the Alpha pair 8/9, BlendFactor=10/11,
  SrcAlphaSat=12; CullMode None=0/CW=1/CCW=2); the false "verified" comment replaced
  with a field-by-field citation. FNA unaffected: `Fx2EffectBuilder` maps symbolically
  (a new `MapCullMode` replaces the one raw-ordinal cast) — **proven** by zero FNA
  manifest hash changes after regeneration AND the live FNA gate (17/17 PASS).
- **F2 (annotations):** `WriteAnnotations` writes the int32 count and **no bodies**
  (MonoGame reads only the count; mgfxc's `annotation_handles` are always null, so it
  writes count 0 — ShadowDusk preserves the declared count, metadata-only, loadable
  either way). The pipeline's annotation type-sniffing is moot for output bytes.
- **F9 (sampler states):** new `MgfxSamplerStateResolver` (Core) mirrors mgfxc's
  `SamplerStateInfo` verbatim — SamplerState-ctor defaults, the Min/Mag/Mip →
  `TextureFilter` combination if-chain, `MipFilter=None` ⇒ LOD bias −16, mgfxc's
  `ParseColor` (0xRRGGBB/0xRRGGBBAA) for BorderColor, float-floor int parsing,
  `MipLodBias`/`MaxLod` key spellings; unparseable values fail loudly (**SD0024**).
  `CompilationPipeline.Run` resolves `fxParsed.Samplers` by sampler name and the writer
  emits `hasState=1` + MonoGame's exact field sequence. `MultiTexture`/
  `MultiTextureOverlay`/`ClipShaderNew`/`FnaMultiPassStates` (whose sampler members were
  silently dropped before) now match their mgfxc goldens' sampler records **exactly**.
- **F10 (struct Members):** `SpirvReflectionParser` populates `Members` recursively
  (member offsets within the struct, member SizeBytes 0, nested recursion — mirroring
  `DxilReflectionExtractor.ExtractStructMembers`), plus struct `DescribeVariable`
  support (Class=Struct/Type=Void, Rows=1, Columns=total component count, packed size).
  `SpirvVsDxilReflectionTests` now asserts `Members` recursively and gained an inline
  struct-cbuffer parity fact (oracle-guarded against vacuous pass).

**Validation ladder, as run (all on win-x64, 2026-06-12):**

1. **Corpus:** new fixtures `StateBlendAdditive.fx`, `StateDepthStencil.fx`,
   `StateRasterizer.fx` (negative DepthBias/SlopeScaleDepthBias), `SamplerStatesFull.fx`,
   `AnnotatedTechnique.fx` (technique/pass annotations — ShadowDusk-only: mgfxc's
   grammar has no annotation production); `render-states.fx`/`annotations.fx` reauthored
   mgfxc-compilable (ZEnable spelling, SM-macro profiles, TRUE SV_POSITION — the
   `#define SV_POSITION POSITION` alias produces a dead varying on the DXC path, a known
   F3/F8-adjacent trap left for 43B).
2. **mgfxc golden oracle:** goldens generated with the real `dotnet-mgfxc 3.8.2.1105`
   for all six mgfxc-compilable fixtures × {OpenGL, DirectX_11} into
   `tests/fixtures/golden/`. `MgfxStateGoldenMatchTests` (12 + 4 facts) parses golden
   and ShadowDusk output with the same real-reader-layout `MgfxBlobReader` (now with
   MonoGame's tail-signature desync guard): pass state records and per-slot baked
   sampler states match the goldens **field-for-field**; decode-level diff showed them
   byte-identical within the state blocks.
3. **Real bar:** new `validation/StateFidelity` harness — every fixture loads in real
   MonoGame 3.8.2 DesktopGL `Effect` (the AlphaBlendEnable/annotation rows are exactly
   the loads that desynced before), and the four PS-only rows render
   **pixel-identical (maxDelta = 0)** to the mgfxc-golden arm through the identical
   SpriteBatch path: **7/7 rows PASS**.
4. **Manifest:** regenerated on win-x64 (`SHADOWDUSK_REGENERATE_BYTE_MANIFEST=1`):
   6 changed + 5 new entries per MGFX target (OpenGL, DirectX_Vkd3d); **zero FNA
   entries changed**. `decode_mgfx*.py` updated to the real layouts.
5. **Suites:** full `dotnet test` green (1,142: 41 GLSL + 174 HLSL + 121 Compiler +
   421 Core + 51 ImageTests + 334 Integration); `validation/FnaValidation` gate
   **17/17 PASS** (run live against this branch); browser G2 re-proven in CI
   (`run-browser` label).

## Phase 43C as-built (2026-06-12, branch `feature/phase43c-gl-cbuffer-array-model`)

**Scope: F4 + F5 + F6 (the GL cbuffer/array model) + the F11 normalizer removal —
the final wave; per-finding details in the notes above.**

- **The model source of truth:** GL cbuffer records are derived from
  `MonoGameGlslResult.Uniforms` — the rewriter's own register allocation — so the
  `.mgfx` offsets and the GLSL indices cannot diverge (SD0012 guards the
  parameter-name join). Non-GL targets keep the reflection-based records unchanged.
- **Oracle derivations** (mgfxc 3.8.2 sources + pinned `dotnet-mgfxc 3.8.2.1105`
  goldens): `ShaderData.CreateGLSL` builds up to three cbuffers PER SHADER from the
  MojoShader symbol table (`{vs,ps}_uniforms_{bool,ivec4,vec4}`) deduped via
  `ConstantBufferData.SameAs`; `EffectObject.WriteParameter` writes elements THEN
  struct members as recursive parameter records with leaf-only data;
  `ConstantBufferData.GetParameterFromSymbol` gives array elements empty names and
  the parent's shape. `Effect.ReadParameters` (v3.8.2) confirms the read side.
- **Tooling brought to the real layout:** `MgfxBlobReader` (now exposes cb records,
  per-shader bindings, and recursive parameter trees) and
  `validation/decode_mgfx{,_dx}.py` — both previously desynced on any array param;
  the SkinnedEffect golden (`Bones[72]`) now decodes byte-clean to the footer.

**Validation ladder, as run (win-x64 + NVIDIA dev box, 2026-06-12):**

1. **Corpus:** `SharedCbuffer.fx`, `MultiCbuffer.fx`, `MultiCbufferVs.fx`,
   `ArrayUniform.fx` (literal indices so fxc compiles ps_3_0; dynamic-index
   coverage is rewriter-unit-level), `ArrayUniformVs.fx` (blends BOTH elements of
   `Bones[2]`+`PosOffsets[2]` — see the F6 note for why partial static reads are
   an mgfxc bug, not a golden to match), plus loud-fail examples
   `ExIntUniformMember.fx` / `ExMat3UniformMember.fx`. mgfxc 3.8.2.1105 goldens
   committed for all five × {OpenGL, DirectX_11}.
2. **Structural:** `Phase43CbufferModelTests` (10 facts: per-stage records, exact
   merged-record layouts vs golden, recursive element trees on GL AND DX, GLSL
   register assertions, SD0210 loud-fails) + the five stems added to
   `MgfxParameterMatchTests` (18 rows green).
3. **Real bar:** new `validation/CbufferModel` harness — every fixture loads in
   real MonoGame 3.8.2 DesktopGL `Effect` and renders **pixel-identical
   (maxDelta = 0, 5/5 rows)** to the mgfxc-golden arm through identical paths
   (SpriteBatch for PS rows, custom vertex-buffer quad for VS rows), with all
   parameters set BY NAME including `SetValue(Vector4[])`, an individual
   `.Elements[2].SetValue` overwrite, and a per-element `Bones[]` blend; a
   non-black guard makes a both-arms-broken outcome unpassable.
4. **Manifest:** regenerated on win-x64: exactly 3 changed entries — OpenGL
   `cbuffer.fx` / `VertexAndPixel.fx` / `PolygonLight.fx`, the three shared-cbuffer
   shapes F4 fixes — plus 10 new entries (5 fixtures × GL/DX_Vkd3d). **Zero
   existing DX entries changed** (no existing DX fixture has arrays) and **zero FNA
   entries** (FNA's CTAB path untouched). All other GL entries byte-identical,
   proving the per-shader record path reproduces the old bytes for every
   non-shared shape.
5. **Suites:** full `dotnet test` green (1,184: 58 GLSL + 174 HLSL + 121 Compiler +
   421 Core + 53 ImageTests + 357 Integration); browser G2 via `run-browser` in CI
   (the WASM path shares `CompilationPipeline` and the SPIR-V reflector already
   models arrays/Elements).

## Definition of Done

Every finding above is either fixed with the full validation ladder (fixture → golden →
real-`Effect` load → render where applicable → manifest/G2 regenerated) or explicitly
converted to a loud SD-coded compile error with the limitation documented in
`docfx/guides/parameters-and-caveats.md`. The corpus permanently covers pass states,
annotations, shared/multi/array cbuffers, VS texturing, sampler states, and backbuffer
rendering, so the proxy trap that hid all seven HIGHs is structurally closed.

> **2026-06-12 — DoD met.** 43A closed F1/F1b/F2/F9/F10, 43B closed F3/F7/F8, and
> 43C closed F4/F5/F6 (+ the F11 normalizer cleanup; F11's remaining bullets were
> handed to `fix/review-compiler-bugs` and the per-OS DXC divergence is tracked as
> its own follow-up, outside this phase's findings list). Each finding carries the
> fixture → golden → real-`Effect` → render ladder or a documented SD-coded loud
> error; the corpus and byte-identity manifest now permanently cover every shape
> named here.
