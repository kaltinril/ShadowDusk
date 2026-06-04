# Phase 34 — GL texture breadth: RED reproduction + pipeline map (investigation)

**Author:** reproduce-half (RED) of Phase 34. **Branch:** `phase34-gl-texture-breadth`.
**Date:** 2026-06-04. **Scope of this doc:** establish the failing cases, trace where
each construct breaks across the *whole* pipeline, nail down the MGFX sampler-`Type`
byte values, confirm the KNI converter + WebGL1 walls, and hand the fix agent a
component-by-component change map. **No fix was implemented** — the `SD0210` guards and
the rewriter logic are untouched.

> All command output below was captured live on this machine (Windows 11, GL driver
> **4.6.0 NVIDIA 596.36** for the desktop-GL probe). Where something could not be
> exercised in-env it is called out explicitly.

> **Update (2026-06-03, branch `phase34-render-validation`):** this doc is the RED-phase
> map and stays historical. The GREEN fix shipped (PR #10) and **rung-4 render validation
> is now done to real limitations** — cube + 3D render-validated in real MonoGame DesktopGL,
> LOD/grad render-proven (explicit level/gradient honored) in the real GL driver. The §5
> "render scene is new harness work" note is resolved by `validation/TextureBreadthValidation`
> (real-MonoGame cube+3D) and `Phase34LodGradRenderTests` (GL-render LOD/grad). For the
> full rung-4 outcome, harnesses, and the precise remaining (platform / runtime-cost /
> oracle) limits, see **`plan/PHASE-34-gl-texture-breadth.md` → "RUNG-4 RENDER VALIDATION
> (AS-BUILT)"** and **"What stays a render-validation limitation"**.

---

## 1. RED reproduction (current behavior)

Compiled each fixture for the **OpenGL** target via the CLI
(`dotnet ShadowDusk.Cli.dll <fx> <out.mgfx> /Profile:OpenGL`). Exact captured results:

| Fixture | Construct | Result | Diagnostic (verbatim, abbreviated) |
|---|---|---|---|
| `examples/ExCubeSamplerHidef.fx` | `TextureCube` / `samplerCube` | **FAIL, exit 1, no output** | `error SD0210: Unsupported sampler type … 'samplerCube'. The MojoShader-dialect rewrite models only 'sampler2D' … would be emitted as silently-broken GLSL (e.g. texture2D() on a non-2D sampler) …` |
| `examples/ExVolumeTextureHidef.fx` *(NEW — added this phase)* | `Texture3D` / `sampler3D` | **FAIL, exit 1, no output** | `error SD0210: Unsupported sampler type … 'sampler3D'. The MojoShader-dialect rewrite models only 'sampler2D' …` |
| `examples/ExSampleLevelHidef.fx` | `Texture2D.SampleLevel` (explicit LOD) | **FAIL, exit 1, no output** | `error SD0210: Unsupported texture sampling … 'texture2DLod' (from HLSL tex2Dlod / SampleLevel). LOD/projected/gradient sampling has no single GLSL form valid in both KNI Reach (WebGL1) and KNI HiDef (WebGL2) …` |
| `examples/ExSampleGradHidef.fx` | `Texture2D.SampleGrad` (gradient) | **FAIL, exit 1, no output** | `error SD0210: Unsupported texture sampling … 'textureGrad' (from HLSL tex2Dgrad / SampleGrad). …` |
| `examples/ExMultiSamplerHidef.fx` | 4× `Texture2D` (control) | **PASS, exit 0** | — (compiles; emits `#define ps_oC0 gl_FragColor`, `ps_s0..ps_s3`) |

Both guards live in `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs`:
- **`ThrowIfUnsupportedSamplerType`** (called at line ~112, *before* line-splitting) — fires for cube/3D via the `NonPlain2DSamplerDecl` regex (`samplerCube`, `sampler3D`).
- **`ThrowIfUnsupportedSampling`** (called at line ~299, *after* the `texture*`→`texture2D*` rewrites) — fires for LOD/grad/proj via the `UnsupportedSampling` token list.

Both throw `MonoGameGlslRewriteException`, which `CompilationPipeline.CompileEntryPointAsync`
(src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs ~line 518) catches and maps to
`ShaderError` code **`SD0210`**.

The 3D fixture `tests/fixtures/shaders/examples/ExVolumeTextureHidef.fx` was **added in
this phase** (the others already existed from Phase 33). It is now also covered by the
standing guard test (`HidefGeneralityFixtureTests.LoudFailureFixtures`, 5/5 green).

---

## 2. End-to-end pipeline trace (where each construct breaks, and what it needs)

The pipeline is: **FX9 pre-parser → preprocessor → DXC (HLSL→SPIR-V) → SPIRV-Cross
(SPIR-V→GLSL) → MonoGameGlslRewriter (MojoShader dialect) → reflection → MGFX writer.**
Captured by driving DXC + SPIRV-Cross + `SpirvReflector` directly on minimal HLSL.

### 2a. FX9 pre-parser (`src/ShadowDusk.HLSL/FxPreParser.cs`)

- **Modern resource form (`TextureCube`/`Texture3D` + `.Sample()`):** passes through
  untouched. `LegacyTextureTypeKeywords` *already* maps `texture3D`→`Texture3D`,
  `textureCUBE`→`TextureCube` (and the capital modern spellings are deliberately never
  matched). **→ No frontend change needed for the modern form.**
- **Legacy FX9 *sampling intrinsics* `texCUBE(...)` / `tex3D(...)`:** **NOT** rewritten —
  only `tex2D` is in `Tex2DIntrinsics`. These reach DXC as-is and **DXC rejects them**
  ("undeclared identifier"). Note this is the form mgfxc's own `Macros.fxh` emits in its
  `#else` (OpenGL/MojoShader) branch (`samplerCUBE`/`texCUBE`). **→ Supporting the legacy
  FX9 intrinsic form is a separate, lower-priority frontend gap; the Phase-34 fixtures use
  the modern `.Sample()` form, which works.**

### 2b. DXC (HLSL → SPIR-V)

**All four constructs compile cleanly to SPIR-V** in the modern form — no DXC barrier:
cube → 1204 B, 3D → 748 B, LOD → 768 B, grad → 828 B SPIR-V. So nothing upstream of
SPIRV-Cross needs changing for cube/3D/LOD/grad.

### 2c. SPIRV-Cross (SPIR-V → GLSL, at `#version 140`) — *the exact emitted tokens*

| Construct | sampler decl emitted | texture call emitted |
|---|---|---|
| Cube | `uniform samplerCube _41;` | `texture(_41, in_var_TEXCOORD1)` |
| 3D | `uniform sampler3D _25;` | `texture(_25, in_var_TEXCOORD0)` |
| LOD | `uniform sampler2D _26;` | `textureLod(_26, in_var_TEXCOORD0, 2.0)` |
| Grad | `uniform sampler2D _29;` | `textureGrad(_29, in_var_TEXCOORD0, vec2(…), vec2(…))` |

**Key:** SPIRV-Cross uses the **generic `texture()`** for *all* sampler dimensions
(including cube/3D), and `textureLod`/`textureGrad` for the LOD/grad family.

### 2d. MonoGameGlslRewriter — *exactly where it breaks*

- **Sampler decl** (`SamplerDecl` regex, rewriter line ~58) only matches
  `uniform sampler2D <id>;`. A `samplerCube`/`sampler3D` decl is **not** matched →
  **not renamed** to `ps_s{k}` and **not** added to the sampler list. (Guarded today before
  this even matters.)
- **Texture-call rewrite** (rewriter line ~282): `body = Regex.Replace(body, @"\btexture\s*\(", "texture2D(")`.
  For cube/3D, SPIRV-Cross's generic `texture(cubeSampler, …)` would be turned into
  `texture2D(cubeSampler, …)` — **invalid GLSL** (wrong overload). *This* is the silent
  break the cube/3D guard prevents.
- **LOD/grad:** rules at lines ~280–281 rewrite `textureLod(`→`texture2DLod(`,
  `textureProj(`→`texture2DProj(`; `textureGrad(` is left as-is. `ThrowIfUnsupportedSampling`
  then fires on the resulting `texture2DLod`/`textureGrad` token.

**What the rewriter needs (per construct):**
- **Cube:** match `uniform samplerCube <id>;` → rename to `ps_s{k}` (keeping `samplerCube`);
  rewrite that sampler's `texture(<id>, …)` call → **`textureCube(ps_s{k}, …)`** (NOT
  `texture2D`). Lift the cube branch of `ThrowIfUnsupportedSamplerType`.
- **3D:** same shape with `sampler3D` → call → **`texture3D(ps_s{k}, …)`**. Keep a guard
  only for the Reach wall (see §6).
- **LOD/grad:** keep SPIRV-Cross's **generic `textureLod(…)`/`textureGrad(…)`** (do **not**
  down-rewrite to `texture2DLod`/`textureGrad`-with-`texture2D`). See §6 for why the generic
  form is the single-blob-correct one. Keep a guard only for the Reach wall.

> The rewriter currently does a *blanket* `texture(`→`texture2D(`. The fix must make this
> **per-sampler-dimension** (2D→`texture2D`, cube→`textureCube`, 3D→`texture3D`), which means
> the rewriter has to know each sampler's dimension. Two options: (i) read the decl keyword it
> already sees on each `uniform sampler* <id>;` line (self-contained, recommended), or (ii)
> thread the reflected dimension in. (i) is simplest and keeps the rewriter a pure string pass.

### 2e. Reflection — *dimension IS captured on the texture, NOT on the sampler*

`SpirvReflector` (pure-managed, the WASM path) and the DXIL/DXBC oracles **all already
capture the texture dimension** correctly:

- `SpirvReflectionParser.MapDim` → `TextureDimension.{Texture2D,Texture3D,TextureCube,…}`
  (verified live: cube→`TextureCube`, 3D→`Texture3D`, LOD/grad textures→`Texture2D`).
- `DxilReflectionExtractor.MapSrvDimension` / `DxbcReflectionExtractor.MapSrvDimension` →
  same `TextureDimension` enum.

**But the dimension never reaches the MGFX sampler record:**
- `SamplerReflection` (src/ShadowDusk.Core/Reflection/SamplerReflection.cs) has **no
  dimension field** — only `Name`, `BindSlot`, `TextureName`.
- `CompilationPipeline` builds each `MgfxSamplerInfo` with **`Type: 0` hardcoded**
  (CompilationPipeline.cs ~line 356, comment `// Sampler2D`).

**What reflection needs:** carry the dimension onto the sampler so the writer can encode it.
The texture↔sampler pairing already exists in `CompilationPipeline` (it resolves
`samp.TextureName` / matches `textureRefs` by slot to find `texName`). The fix can look up
the matched `TextureReflection.Dimension` there and map it to the sampler `Type` byte — **or**
add a `Dimension` field to `SamplerReflection` and populate it in the parser/extractors
(the combined sampled-image case in `SpirvReflectionParser` already has the `ImageType` in
hand at the point it creates the `SamplerReflection`).

### 2f. MGFX writer — the per-sampler `Type` byte

`MgfxWriter.WriteShaders` (src/ShadowDusk.Core/MgfxWriter.cs ~line 121) writes
`bw.Write(s.Type)` as the first byte of each sampler record. `s.Type` is
`MgfxSamplerInfo.Type` — **currently always 0**. This is the load-bearing fix for binding;
see §3 for the exact values.

### Trace summary (one line per construct)

| Construct | FX9 | DXC | SPIRV-Cross | Reflection (dim) | Rewriter | MGFX Type byte |
|---|---|---|---|---|---|---|
| **Cube** | OK (modern) | OK | `samplerCube` + `texture()` | captures `TextureCube` ✓ | **breaks**: decl not renamed; `texture()`→`texture2D()` | needs **1** (writes 0) |
| **3D** | OK (modern) | OK | `sampler3D` + `texture()` | captures `Texture3D` ✓ | **breaks**: same as cube | needs **2** (writes 0) |
| **LOD** | OK | OK | `sampler2D` + `textureLod()` | `Texture2D` ✓ | **breaks**: →`texture2DLod`, guard fires | 0 (already correct) |
| **Grad** | OK | OK | `sampler2D` + `textureGrad()` | `Texture2D` ✓ | **breaks**: guard fires on `textureGrad` | 0 (already correct) |

---

## 3. MGFX sampler `Type` byte — VERIFIED values per dimension (do NOT invent)

The per-sampler `Type` byte is **MonoGame's `SamplerType` enum**, read by the runtime as
`Samplers[s].type = (SamplerType)reader.ReadByte();`
(`MonoGame.Framework/Graphics/Shader/Shader.cs`). Enum (verified from MonoGame source):

| `SamplerType` | value | Dimension |
|---|---|---|
| `Sampler2D` | **0** | 2D |
| `SamplerCube` | **1** | cube |
| `SamplerVolume` | **2** | 3D / volume |
| `Sampler1D` | **3** | 1D |

**Empirically confirmed against an mgfxc-produced OpenGL golden** by decoding the raw
sampler records of `tests/fixtures/golden/OpenGL/EnvironmentMapEffect.mgfx` (which has a 2D
`Texture` at slot 0 **and** a cube `EnvironmentMap` at slot 1):

```
shader[0] stage=PS samplerCount=2
    sampler[0] TYPE=0  texSlot=0 sampSlot=0 name='ps_s0'   <- 2D   Texture
    sampler[1] TYPE=1  texSlot=1 sampSlot=1 name='ps_s1'   <- CUBE EnvironmentMap
```

Cross-checks (all-2D goldens) show **every** sampler `TYPE=0`
(`DualTextureEffect`, `Grayscale`, `MultiTexture`). And mgfxc's embedded GLSL for the cube
golden uses `uniform samplerCube ps_s1;` + `textureCube(ps_s1, …)` (and `texture2D` for the
2D sampler) — i.e. the legacy dialect, dimension-specific builtins.

**→ The fix must emit the sampler `Type` byte as:** `2D=0, Cube=1, Volume(3D)=2` (1D=3 if
ever needed). No golden exists for a 3D sampler in this repo, but the enum value (2) is from
MonoGame source. **This byte is critical: cube/3D will not bind at runtime if it stays 0.**

> Separately (and *already correct*): the global **parameter** `Type` byte uses
> `EffectParameterType` (`Texture2D=7`, `TextureCube=9`, `Texture3D=8`). The cube golden's
> `EnvironmentMap` parameter is `TYPE=9` and ShadowDusk's `ParameterListBuilder.MapTextureDimensionToType`
> already produces these from the reflected dimension. So only the **sampler-table** byte is wrong.

---

## 4. KNI HiDef/WebGL2 converter handling (confirmed from KNI source)

KNI's runtime legacy→ES-3.00 converter
(`Platforms/Graphics/.BlazorGL/Shader/ConcreteShader.cs`, `ConvertGLSLToGLSL300es`) rewrites
the texture-call name with exactly:

```
regex:  texture(2D|3D|Cube)(?=\()      replacement: texture
```

- ✅ **Handled:** `texture2D(`, `texture3D(`, `textureCube(` → `texture(` (valid ES 3.00).
  So the legacy **dimension-specific** spellings the fix emits for cube/3D are converted
  cleanly under HiDef.
- ❌ **NOT handled:** `textureLod(`, `textureGrad(`, `textureProj(` (and the `texture2DLod`
  etc. variants) — the `(?=\()` requires `(` *immediately* after the `2D|3D|Cube` suffix, so
  these are skipped. They survive into the ES-3.00 context unchanged.
  - This is fine for **`textureLod`/`textureGrad`** *only because they are already core in
    GLSL ES 3.00* — the converter doesn't need to touch them. (It IS fatal for the legacy
    `texture2DLod`/`texture2DGrad` spellings, which are not ES-3.00 builtins — hence the fix
    must emit the **generic** `textureLod`/`textureGrad`, not the `texture2D*` forms.)
- The output `#define ps_oC{N} …` form (Phase 33) is also converted; unaffected here.

---

## 5. WebGL1 / KNI Reach walls (confirmed)

- **3D textures:** **not in WebGL1 (OpenGL ES 2.0) at all** — `sampler3D`/`texture3D` were
  added only in WebGL2 (ES 3.0). Genuine platform wall.
- **Explicit-LOD / gradient in a *fragment* shader:** **not core WebGL1** — gated behind the
  optional `GL_EXT_shader_texture_lod` extension (`texture2DLodEXT` / `textureCubeLodEXT`),
  which the spec does not require. Core (default) only in WebGL2. Genuine platform wall.
- **Cube maps:** **ARE in WebGL1 / GLSL ES 1.00** (`samplerCube` + `textureCube`). **No wall**
  — cube is supportable on Reach too.

So after Phase 34, the only honest hard-error left (where detectable) is **3D + explicit-LOD
on Reach/WebGL1**. Cube has no wall.

---

## 6. The one-blob design decision — concrete evidence (this resolves the plan's open question)

ShadowDusk emits **one** GLSL blob for `OpenGL` and cannot know at compile time whether the
consumer's KNI game is Reach (WebGL1) or HiDef (WebGL2). The plan asks: for 3D/LOD, does
emitting the capable form compile on Desktop GL, pass KNI HiDef, and what's the precise Reach
failure?

I probed the **exact GL 3.3 Compatibility context the image-regression suite uses**
(driver reported GL 4.6 / GLSL 4.60 NVIDIA), compiling minimal fragment shaders in **two
dialects**: the modern `#version 330` (raw SPIRV-Cross body) and the **unversioned legacy
MojoShader dialect ShadowDusk actually ships** (the 2D corpus is unversioned and renders in
real MonoGame DesktopGL). Captured results:

| Form | `#version 330` | **Unversioned legacy (as shipped)** |
|---|---|---|
| `textureCube(samplerCube,…)` | **FAILS** (`requires GL_NV_shadow_samplers_cube`) | **COMPILES** ✓ (== mgfxc golden form) |
| `texture3D(sampler3D,…)` | **FAILS** (`removed after version 140`) | **COMPILES** ✓ |
| `textureLod(sampler2D,…)` | COMPILES | **COMPILES** ✓ |
| `texture2DLod(sampler2D,…)` | **FAILS** (`removed after version 140`) | **COMPILES** ✓ (but see HiDef note) |
| `texture(samplerCube/sampler3D,…)` (generic) | COMPILES | n/a (not how legacy emits) |
| `texture2D(samplerCube/sampler3D,…)` (current rewriter's silent break) | **FAILS** (no overload) | **FAILS** (no overload) |
| `texture2D(sampler2D,…)` (baseline corpus form) | — | **COMPILES** ✓ |

**Findings:**

1. **Cube/3D — emit the legacy dimension-specific builtin (`textureCube`/`texture3D`),
   staying in the existing MojoShader dialect.** In the unversioned dialect ShadowDusk
   already uses, both **compile on Desktop GL** *and* match mgfxc's own cube golden byte-form.
   KNI HiDef converts `textureCube(`/`texture3D(`→`texture(` cleanly (§4). On Reach: `textureCube`
   is native ✓; `texture3D`/`sampler3D` are absent → **fails to load (platform wall)** — the
   precise Reach failure is an "undeclared `sampler3D`/`texture3D`" GLSL link error in WebGL1.
   → **Cube = option (a) with no tradeoff (works everywhere). 3D = option (a): enable on
   Desktop+HiDef, accept the Reach wall.**

2. **The current rewriter's `texture(`→`texture2D(` blanket rewrite is the actual cube/3D bug**
   — it produces `texture2D(non-2D-sampler)` which fails in *both* dialects (no overload).
   The fix is per-dimension emission, not a different version.

3. **LOD/grad — emit the *generic* `textureLod`/`textureGrad` (NOT `texture2DLod`).** The
   generic forms compile on Desktop GL (legacy dialect) and are **core in GLSL ES 3.00**, so
   KNI HiDef passes them through untouched (the converter doesn't need to rewrite them, §4).
   The legacy `texture2DLod` *also* compiles on this desktop driver, **but it is not an
   ES-3.00 builtin and KNI does not convert it → it fails HiDef** — so `texture2DLod` is NOT a
   valid single-blob choice; the generic `textureLod` is. On Reach: explicit-LOD in a fragment
   shader is extension-gated → **unreliable/fails (platform wall)**.
   → **3D-style option (a): emit generic `textureLod`/`textureGrad`, enable on Desktop+HiDef,
   keep a guard for the Reach wall.**

4. **Rejected (confirmed):** the generic `texture()` for cube/3D would work on Desktop+HiDef
   but **breaks Reach** (ES 1.00 has no generic `texture()`), and it diverges from mgfxc's
   legacy form. The legacy `textureCube`/`texture3D` is strictly better (adds Reach-cube,
   matches mgfxc). No per-profile knob / no second blob (anti-seamless, out of scope per plan).

> **Net:** there is no profile conflict for **cube** (legacy `textureCube` works on Desktop +
> HiDef + Reach). For **3D** and **LOD/grad**, the single capable blob (legacy `texture3D` /
> generic `textureLod`) works on Desktop + HiDef and *only* fails on Reach — the genuine
> platform wall, surfaced as an honest diagnostic (Plan Task 3), never a silent wrong render.

---

## 7. Handoff — exactly what the fix agent changes, by component

### Cube maps (full support — Desktop + HiDef + **Reach**)
1. **Rewriter** (`MonoGameGlslRewriter.cs`):
   - Add a `samplerCube` decl match (alongside `SamplerDecl`) → rename to `ps_s{k}`, keep
     `samplerCube`, add to the sampler list (so it gets a `ps_s{k}` and an MGFX record).
   - Make the texture-call rewrite **per-sampler-dimension**: the cube sampler's
     `texture(<id>, …)` → **`textureCube(ps_s{k}, …)`** (must run *before* / instead of the
     blanket `texture(`→`texture2D(`).
   - Remove the **cube** branch of `ThrowIfUnsupportedSamplerType` (keep it for any still-
     unsupported kinds).
2. **Reflection → MGFX `Type`**: set the cube sampler's `MgfxSamplerInfo.Type = 1`
   (`SamplerCube`). Source the dimension from the matched `TextureReflection.Dimension` in
   `CompilationPipeline` (or add `Dimension` to `SamplerReflection`). The parameter `Type`
   (9=TextureCube) is already correct via `ParameterListBuilder`.
3. **MGFX writer:** no change (it already writes `s.Type`); just stop hardcoding 0 upstream.
4. **Tests:** flip `ExCubeSamplerHidef.fx` from the loud-failure list to a *success* case;
   assert emitted GLSL has `samplerCube ps_s0` + `textureCube(ps_s0,` and the MGFX sampler
   record `Type==1`. Add a render scene (cube-map sample is new harness work).

### 3D / volume textures (Desktop + HiDef; Reach = honest wall)
1. **Rewriter:** mirror the cube changes for `sampler3D` → `texture3D(ps_s{k}, …)`; remove the
   3D branch of `ThrowIfUnsupportedSamplerType`.
2. **MGFX `Type`:** `SamplerVolume = 2` for the 3D sampler.
3. **Reach wall:** where detectable, keep/repurpose a clear diagnostic (Plan Task 3) — *"3D
   textures require KNI HiDef/WebGL2 or desktop; not available on KNI Reach/WebGL1."* (The
   single blob is emitted in the capable form; Reach simply fails to load it. There is no
   compile-time profile signal, so this is primarily a **documentation** + runtime-error
   reality, mirroring the KNI-version-floor pattern from Phase 33.)
4. **Fixture:** `ExVolumeTextureHidef.fx` already added (currently RED).

### Explicit-LOD / gradient (Desktop + HiDef; Reach = honest wall)
1. **Rewriter:** **stop** rewriting `textureLod(`→`texture2DLod(` and `textureProj(`→
   `texture2DProj(`; **keep the generic `textureLod`/`textureGrad`/`textureProj`** (core ES
   3.00; KNI passes them through). Remove / narrow `ThrowIfUnsupportedSampling` so it no
   longer blocks Desktop/HiDef. (`textureProj` only if it falls out naturally — lower priority.)
2. **MGFX `Type`:** unchanged (these are 2D samplers → 0).
3. **Reach wall:** keep a clear diagnostic only for the genuinely-unserveable Reach case
   (documentation-level, as above).
4. **Fixtures:** `ExSampleLevelHidef.fx`, `ExSampleGradHidef.fx` flip to success; assert the
   emitted GLSL contains the generic `textureLod(`/`textureGrad(` and **no** `texture2DLod`/
   `texture2DGrad`.

### Cross-cutting
- **Sampler-dimension plumbing is the one structural addition** (today `Type` is hardcoded 0
  and `SamplerReflection` has no dimension). Everything else is localized string-rewrite work.
- **Re-baseline:** byte-identity / determinism / goldens must be re-checked — the cube/3D
  sampler `Type` byte and the new dimension-specific builtins change emitted bytes for those
  shaders (the existing 2D corpus is unaffected: 2D still → `texture2D` + `Type==0`).
- **Keep guards loud** for anything still unmodeled (e.g. `sampler2DArray`, shadow samplers).

---

## Appendix — fixtures touched / added by this RED phase

- **Added:** `tests/fixtures/shaders/examples/ExVolumeTextureHidef.fx` (3D RED case).
- **Reused (unchanged):** `ExCubeSamplerHidef.fx`, `ExSampleLevelHidef.fx`,
  `ExSampleGradHidef.fx`, `ExMultiSamplerHidef.fx`.
- **Test:** `ExVolumeTextureHidef.fx` added to
  `tests/ShadowDusk.Integration.Tests/Tests/HidefGeneralityFixtureTests.cs`
  `LoudFailureFixtures` (5/5 green — 4 loud-fail incl. 3D + 1 multi-2D control).
- **Investigation tooling** (a throwaway desktop-GL compile probe + scratch HLSL→GLSL dumpers)
  was used to capture the evidence above and then **removed** — no compiler/guard behavior was
  modified.
