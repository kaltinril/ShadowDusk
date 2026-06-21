# GLSL → HLSL Mapping Notes — Prior-Art & Authoritative Reference (Phase 46)

A research companion to [`MAPPING.md`](MAPPING.md). It mines three external sources for GLSL→HLSL
mapping rules, cross-references each against what our `ShadowDusk.ShaderToy` converter actually does,
and produces (1) a consolidated mapping table tagged `[DONE]` / `[GAP]` / `[RISK]`, (2) a prioritized
improvements checklist, and (3) a correctness-risk double-check list. It feeds the next fix wave and a
fidelity re-check. **It does not modify the converter** — it is reference + checklist only.

## Sources

- **[MS]** Microsoft, *GLSL-to-HLSL reference* (OpenGL ES 2.0 → Direct3D 11). Authoritative for type /
  intrinsic / semantic / matrix-default correspondences.
  <https://learn.microsoft.com/en-us/previous-versions/windows/apps/dn166865(v=win.10)>
  (canonical for the `docs.microsoft.com/.../dn166865` URL in the task).
- **[Unity]** Unity Technologies, *Shading Language / Platform-specific rendering differences*.
  Authoritative for Y-flip / clip-space-Z / render-target-origin / precision.
  <https://docs.unity3d.com/Manual/SL-PlatformDifferences.html>
- **[SM]** smkplus, *ShaderMan* — Unity tool that converts ShaderToy GLSL → HLSL/CG. Direct prior art.
  <https://github.com/smkplus/ShaderMan> (README: rules extracted as facts, no code copied).

> **Tag legend.** `[DONE]` = our converter already handles it correctly. `[GAP]` = we are missing it
> or reject it where the reference shows a faithful mapping. `[RISK]` = we may be doing it *wrong* (the
> reference implies a different/opposite rule) — verify against our render gate.

---

## 1. Consolidated GLSL → HLSL mapping table

### 1a. Types

| GLSL | HLSL | Source | Tag | Note vs our MAPPING.md |
|---|---|---|---|---|
| `vec2/3/4` | `float2/3/4` | [MS],[SM] | `[DONE]` | Type table, trap 1. |
| `ivec2/3/4` | `int2/3/4` | [MS] | `[DONE]` | Type table. |
| `bvec2/3/4` | `bool2/3/4` | [MS] | `[DONE]` | Type table. |
| `mat2/3/4` | `float2x2/3x3/4x4` | [MS],[SM] | `[DONE]` | Type table, trap 2. |
| `float/int/bool` scalars | `float/int/bool` | [MS] | `[DONE]` | Same name. |
| `vecN(scalar)` splat | `((floatN)(scalar))` (HLSL has no 1-scalar vector ctor) | [SM] (`vec3(1)`→`float3(1,1,1)`) | `[DONE]` | We emit a cast-splat `((floatN)(s))`; [SM] expands the components. Both are valid; our cast form is equivalent. |
| `sampler2D` | `Texture2D` + `SamplerState` (DX11) / `sampler2D` (FX9) | [MS] | `[DONE]` (FX9 form) | We emit the **legacy FX9** `texture` + `sampler_state` pair, the correct target for our `ps_3_0` `.fx`. [MS]'s `Texture2D`+`SamplerState` is the DX11 form — not our target. |
| `samplerCube` | `TextureCube` | [MS] | `[GAP]` (intentional) | We **reject** `samplerCube`. Faithful FX9 map exists (`textureCUBE`/`samplerCUBE`) but is out of v1 scope. |
| `double`,`uint`,`uvecN`,`dvecN` | `double`,`uint`,… (exist in HLSL) | [MS] | `[GAP]` (intentional) | HLSL *has* these; we **reject** them (SM3/FNA target ceiling). Documented limit, not a defect. |
| `highp`/`mediump`/`lowp` | stripped → `float`/`min16float`/`min10float` | [MS] | `[DONE]` (as strip) | [MS] maps precision to `min16float`/`min10float`; we **strip** to plain `float` (trap 5). Stripping is safe (wider precision); see RISK-5. |

### 1b. Intrinsics

| GLSL | HLSL | Source | Tag | Note |
|---|---|---|---|---|
| `mix` | `lerp` | [MS]-impl,[SM] | `[DONE]` | trap 4 rename. |
| `fract` | `frac` | (HLSL std) | `[DONE]` | trap 4 rename. |
| `inversesqrt` | `rsqrt` | (HLSL std) | `[DONE]` | trap 4 rename. |
| `dFdx`/`dFdy` | `ddx`/`ddy` | (HLSL std) | `[DONE]` | trap 4 rename. |
| `atan(y,x)` | `atan2(y,x)` | [MS]-impl,[SM] | `[DONE]` | **Arg order preserved** `atan(y,x)`→`atan2(y,x)`. ([SM]'s README writes "`atan(x,y)`→`atan2(y,x)`" loosely; the GLSL 2-arg signature is `atan(y,x)` and the *values* line up 1:1 — we map them positionally, which is correct. See RISK-6.) |
| `atan(x)` | `atan(x)` | (HLSL std) | `[DONE]` | 1-arg stays `atan`. |
| `mod(x,y)` | `glsl_mod` helper `x - y*floor(x/y)` (NOT `fmod`) | (semantics) | `[DONE]` | trap 3 — critical sign fix; see RISK-2. |
| `texture`/`texture2D`(s,uv) | `tex2D(s,uv)` | [MS] (`texture2D`→`texture.Sample`),[SM] | `[DONE]` | FX9 `tex2D` form (not DX11 `.Sample`); correct for our target. |
| `textureLod(s,uv,lod)` | `tex2Dlod(s, float4(uv,0,lod))` | (HLSL std) | `[DONE]` | trap 4. See RISK-4 (LOD plumbing on SM3). |
| `textureGrad` | `tex2Dgrad` | (HLSL std) | `[DONE]` | trap 4. |
| `texture(s,uv,bias)` (mip bias) | `tex2Dbias(s, float4(uv,0,bias))` | [SM] ("remove the bias param") | `[RISK]`/`[GAP]` | We **reject** it (does not compile on our GL/DX SM4 targets). [SM] just **drops the bias arg** → `tex2D(s,uv)`. Dropping silently changes results; our reject is *safer* but blocks otherwise-valid shaders. See RISK-4 / IMPROVE-1. |
| `matrixCompMult(a,b)` | `(a * b)` (componentwise; HLSL `*` on matrices is componentwise) | (HLSL semantics) | `[DONE]` | Must NOT go through the `mul` trap. |
| `roundEven` | (no faithful HLSL map) | (semantics) | `[DONE]` (reject) | Banker's rounding has no faithful HLSL form; loud reject. |
| same-name set: `clamp,min,max,abs,floor,ceil,round,trunc,sign,sqrt,exp,log,exp2,log2,pow,sin,cos,tan,asin,acos,sinh,cosh,tanh,step,smoothstep,length,distance,dot,cross,normalize,reflect,refract,radians,degrees,saturate,fwidth` | same | (HLSL std) | `[DONE]` | Carried over verbatim. |
| `texelFetch`,`textureProj`,`textureSize`,`textureOffset`,`fwidthFine/Coarse`,`dFdxFine/Coarse`, bit/packing intrinsics | various / no clean SM3 map | (semantics) | `[GAP]` (intentional) | Loud rejects (SM3 ceiling / no faithful map). |

### 1c. Pre-defined globals / semantics (harness)

| GLSL pre-defined | HLSL semantic | Source | Tag | Note |
|---|---|---|---|---|
| `gl_FragColor` | `SV_Target` (DX11) / `COLOR` (D3D9) | [MS] | `[DONE]` | Our `ps_3_0` harness returns `COLOR0` (the D3D9/FX9 spelling [MS] lists as "COLOR in Direct3D 9"). |
| `gl_FragCoord` | `SV_Position` pixel-shader input (screen-space coords) | [MS] | `[DONE]`* | We **synthesize** `fragCoord` from uv*resolution rather than reading the `VPOS`/`SV_Position` PS input. Equivalent result; see RISK-3 for the Y/half-pixel detail. |
| `gl_Position` | `SV_Position` VS output (`POSITION` in D3D9) | [MS] | `[DONE]` | Harness VS passes NDC `POSITION` through. |
| `gl_FragData[n]` | `SV_Target[n]` | [MS] | `[GAP]` (intentional) | MRT — single image pass only in v1. |
| `gl_FrontFacing` | `SV_IsFrontFace` (bool) / `VFACE` (float, D3D9) | [MS] | `[GAP]` | Not handled; would reject as undeclared. Rare in ShaderToy fullscreen shaders. |
| `gl_FragDepth` | `SV_Depth` | [MS] | `[GAP]` | Not handled. Rare in ShaderToy. |
| `gl_PointCoord`/`gl_PointSize` | `SV_Position`/`PSIZE` | [MS] | n/a | Point primitives — irrelevant to fullscreen ShaderToy. |

### 1d. Uniforms / harness inputs

| GLSL (ShaderToy) | HLSL global | Source | Tag | Note |
|---|---|---|---|---|
| `iResolution` (vec3) | `float3 iResolution;` | (ShaderToy) | `[DONE]` | Always emitted (harness needs it). [SM] maps to Unity `_ScreenParams.xy`; we keep the ShaderToy name as a drivable global (engine-agnostic). |
| `iTime` (float) | `float iTime;` | (ShaderToy) | `[DONE]` | [SM] maps `iGlobalTime`→`_Time.y`; we alias `iGlobalTime`→`iTime` and keep it drivable. |
| `iTimeDelta`,`iFrame`,`iFrameRate`,`iMouse`,`iDate`,`iSampleRate`,`iChannelTime[4]`,`iChannelResolution[4]` | matching `float`/`int`/`float4`/array globals | (ShaderToy) | `[DONE]` | Emitted only when referenced. [SM] omits most of these — we cover more. |
| `iChannel0..3` (sampler2D) | `texture iChannelNTexture;` + `sampler2D iChannelN = sampler_state {...};` | (ShaderToy) | `[DONE]` | FX9 sampler form; `texture(iChannelN,uv)`→`tex2D`. [SM] does not document channel binding — we cover more. |
| deprecated `iGlobalTime`/`iGlobalFrame` | aliased → `iTime`/`iFrame` | (ShaderToy legacy) | `[DONE]` | Token-rewritten pre-parse. |

### 1e. Operators / language

| GLSL | HLSL | Source | Tag | Note |
|---|---|---|---|---|
| `A * B` where an operand is a matrix | `mul(B, A)` (reversed args) | [MS] (storage default differs),(semantics) | `[DONE]` | trap 2 — highest-risk. See RISK-1. |
| `v *= M` (M matrix) | `v = mul(v, M)` | (semantics) | `[DONE]` | Matrix compound assignment desugar. [SM] vaguely says "`*=`→`mul()`"; we do exactly this only when the RHS is a matrix. |
| scalar/vector `*` and `*=` | unchanged (componentwise) | (semantics) | `[DONE]` | Not a `mul`. |
| matrix constructor scalar list | emitted **identical** to GLSL (yields Mᵀ, which cancels with `mul(v,Mᵀ)`) | (semantics) | `[DONE]`/`[RISK]` | trap 2 mechanism. See RISK-1 — verify the cancellation holds for mat3/mat4, not just mat2. |
| vector `==`/`!=` in bool context | `all(a==b)` / `any(a!=b)` | (HLSL semantics) | `[DONE]` | HLSL vector `==` yields a bool-vector. |
| narrowing `vec4`→`vec2` etc. | explicit `.xy`/`.xyz` truncation | (HLSL strictness) | `[DONE]` | fxc `-Werror,-Wconversion`. |
| float literal `1` | `1.0` | (HLSL typing),[SM] | `[DONE]` | Normalized. |

---

## 2. Prioritized improvements checklist (the GAPs and RISKs)

Ordered by fidelity impact × likely frequency in real ShaderToy shaders. Each = source citation +
concrete change. **Do not implement here** — this is the spec for the next fix wave.

1. **IMPROVE-1 — Mip-bias `texture(s, uv, bias)`: reconsider the hard reject.** [SM] handles this
   common form by *dropping the bias arg* (→`tex2D(s,uv)`); we **reject** outright. Dropping is
   silently-wrong (loses LOD selection) so our reject is *safer*, but it blocks many real shaders. Two
   faithful options for our SM3 `.fx`: (a) map to `tex2Dbias(s, float4(uv, 0, bias))` and verify it
   compiles+renders on GL+DX+FNA before un-rejecting; (b) keep the reject but improve the diagnostic to
   suggest precomputing the LOD via `textureLod`. **Verify with the render gate before changing.**
   Source: [SM] README; our trap-4 reject. (Cross-check RISK-4.)

2. **IMPROVE-2 — `samplerCube` / `textureCube`.** [MS] gives the faithful map `samplerCube`→`TextureCube`
   (FX9: `samplerCUBE` + `texCUBE`). We reject it. Many ShaderToy shaders use a cubemap `iChannel`.
   Additive opportunity: support `samplerCube` channels with `texCUBE` for the GL/DX targets (FNA SM3
   supports `texCUBE`). Source: [MS] type table. *(Larger scope; flag, do not assume v1.)*

3. **IMPROVE-3 — `gl_FrontFacing` / `gl_FragDepth`.** [MS] maps these to `SV_IsFrontFace`/`VFACE` and
   `SV_Depth`. We have no handling → they fall to the undeclared-identifier reject with a generic
   message. Low frequency in fullscreen ShaderToy, but at minimum the **diagnostic** should name them as
   known-unsupported built-ins rather than "undeclared identifier." Source: [MS] pre-defined-globals
   table.

4. **IMPROVE-4 — Precision-qualifier fidelity note (not a code change, a documented caveat).** [MS] maps
   `mediump`→`min16float`, `lowp`→`min10float`; we **strip to full `float`**. This is *higher* precision,
   so visually safe in the overwhelming majority of cases, but a shader that *relies on* lowp wraparound
   (rare, e.g. deliberate banding) would differ. Document in MAPPING.md trap 5 as a known intentional
   fidelity choice. Source: [MS] precision table; [Unity] precision section.

5. **IMPROVE-5 — Document the Y-flip provenance against authoritative sources.** Our harness uses
   `fragCoord = float2(uv.x, 1 - uv.y) * iResolution.xy`. [Unity] is the authoritative confirmation
   (DirectX/Metal/Vulkan = top-left origin, OpenGL/WebGL = bottom-left; flip when
   `UNITY_UV_STARTS_AT_TOP`). Add a one-line [Unity] citation to MAPPING.md's "origin trap" section.
   **Note the [SM] README states the *opposite* orientation** ("GLSL top, HLSL bottom") — that is a
   loose/incorrect README phrasing; trust [Unity] + [MS], whose convention matches ours. Source: [Unity]
   Y-flip section; [SM] (as the disagreeing claim to NOT follow).

---

## 3. Correctness RISKS to double-check (verify against the Windows render gate)

These are places the references imply a subtlety where a wrong call is **silently-wrong**, not a
compile error. Each lists what the authoritative doc says and the exact thing to confirm.

### RISK-1 — Matrix multiply order & storage default (the highest-risk trap)
- **[MS] says, verbatim:** GLSL default = "**Row-major matrices (default)**"; HLSL default =
  "**Column-major matrices (default)**." This is the **memory-storage** default (how a constructor's
  scalar list fills the matrix), *separate from* the mathematical `M*v` vs `v*M` convention.
- **Our rule (trap 2):** emit the matrix constructor with the **identical** scalar list (which, given
  the opposite storage defaults, yields Mᵀ) **and** translate `A*B`→`mul(B,A)`; the two transposes
  cancel so `mul(v, Mᵀ) == M·v`.
- **Verdict:** Our rule is **consistent with [MS]** — the row-vs-column storage-default difference [MS]
  documents is exactly the transpose our constructor relies on, and `mul(rowVector, M)` is the HLSL
  row-major product. **CONFIRM:** the cancellation is proven for **mat2** (rotation, by hand + smoke).
  **Re-verify for mat3 and mat4** (and for a struct matrix member, G6) on the render gate — the
  argument is symmetric but unproven beyond mat2. Watch for any code path that builds an HLSL matrix
  constructor from a *non-identical* (already-transposed) scalar list — that would double-transpose.
  Source: [MS] GLSL/HLSL comparison table.

### RISK-2 — `mod` sign for negative operands
- **Semantics:** GLSL `mod(x,y) = x - y*floor(x/y)` (sign follows **y**). HLSL `fmod` truncates toward
  zero (sign follows **x**). They differ whenever `x` and `y` have opposite signs.
- **Our rule (trap 3):** we emit a `glsl_mod` helper `x - y*floor(x/y)`, **not** `fmod`.
- **Verdict:** **CORRECT and matches the GLSL definition.** Neither [MS] nor [SM] documents `mod` (a
  known [SM] omission), so our explicit helper is *more* faithful than the prior art. **CONFIRM:** the
  helper is overloaded for all `floatN` + `floatN`/`float` operand shapes a shader can pass, and that no
  `mod` slips through to a raw `fmod`. Source: GLSL spec semantics; [SM] omission.

### RISK-3 — `gl_FragCoord` / Y-flip / half-pixel origin
- **[MS] says:** `gl_FragCoord` → `SV_Position` PS input = "Screen space coordinates," type float4.
- **[Unity] says:** D3D/Metal/Vulkan render-target origin is **top-left**; OpenGL/WebGL is
  **bottom-left**; flip vertical UV when `UNITY_UV_STARTS_AT_TOP` / when `_ProjectionParams.x == -1`.
- **Our rule:** harness computes `fragCoord = float2(uv.x, 1 - uv.y) * iResolution.xy` (uv is top-left
  origin from the VS), giving ShaderToy's bottom-left `fragCoord`. Render-proven by `main_gradient` /
  `gradient_uv`.
- **Verdict:** **Y-flip direction matches [Unity]/[MS]** (we flip to recover the GL/ShaderToy
  bottom-left orientation; this matches what Unity prescribes for the D3D target). **CONFIRM two
  subtleties the references hint at:** (a) **half-pixel center** — real `gl_FragCoord.xy` is at pixel
  *centers* (x+0.5, y+0.5). Our `uv*resolution` lands on centers only if the VS `uv` is the
  interpolated [0,1] across the quad (it is) — but verify no half-texel offset is needed for
  `tex2D(iChannelN, fragCoord/iResolution.xy)` round-trips on D3D9-style rasterizers (the classic D3D9
  half-pixel rule; FNA/SM3 path is the one to watch). (b) `gl_FragCoord.z`/`.w` — we set `.z=0,.w=1`;
  fine for shaders that only read `.xy` (the norm), but a shader reading `.z`/`.w` gets constants.
  Source: [Unity] Y-flip & render-target-origin; [MS] gl_FragCoord row.

### RISK-4 — Texture LOD / bias on the SM3 target
- **Our rule:** `textureLod`→`tex2Dlod(s, float4(uv,0,lod))`; mip-bias `texture(s,uv,bias)` is
  **rejected**.
- **[SM] prior art:** drops the bias arg entirely.
- **Verdict / CONFIRM:** verify `tex2Dlod` actually selects the requested LOD on **all three** backends
  (GL via SPIRV-Cross path, DX, FNA SM3) — `tex2Dlod` requires the LOD in `.w` and on some SM3 paths a
  full mip chain. If the rendered LOD is ignored on a backend, a `textureLod` shader is silently-wrong
  even though it compiles. This is the same risk that justifies the mip-bias reject; confirm
  `textureLod` is genuinely faithful before relying on it. Source: trap-4 mapping; [SM] bias-drop
  behavior.

### RISK-5 — `normalize` / precision after stripping qualifiers
- **[MS]/[Unity]:** `mediump`/`lowp` carry reduced precision; HLSL `min16float`/`min10float` are the
  faithful maps; we strip to full `float`.
- **Verdict:** Stripping to higher precision is **safe for almost all shaders** (results are *more*
  accurate, not wrong). The only divergence is a shader that *depends on* low precision (deliberate
  banding/quantization), which is rare. **No code change required**; record as a documented intentional
  choice (IMPROVE-4). `normalize` itself maps name-for-name and needs no special handling. Source: [MS]
  precision table; [Unity] precision note.

### RISK-6 — `atan` two-argument order
- **Semantics:** GLSL `atan(y, x)` ≡ HLSL `atan2(y, x)` — **same argument order**, both `(y, x)`.
- **Our rule:** `atan(y,x)`→`atan2(y,x)` positionally (args unchanged).
- **Verdict:** **CORRECT.** [SM]'s README phrases it as "`atan(x,y)`→`atan2(y,x)` (order reversed)",
  which is a *naming* artifact (it labels GLSL's first arg `x`); the underlying value-to-value mapping is
  identity-order and matches ours. **CONFIRM** only that we never accidentally swap to `atan2(x,y)`.
  Source: GLSL/HLSL `atan`/`atan2` signatures; [SM] (loosely-worded but value-equivalent).

### RISK-7 — Clip-space Z / reversed-Z (informational — low risk for fullscreen 2D)
- **[Unity] says:** D3D/Metal/Vulkan clip-space Z = [0..1] (and may be reversed-Z 1→0); OpenGL/WebGL =
  [-1..1]. Detect via `UNITY_REVERSED_Z` / `SystemInfo.usesReversedZBuffer`.
- **Relevance:** Our harness VS passes a fullscreen quad already in NDC and ShaderToy image shaders are
  2D (no depth test, no perspective). So clip-space Z range does **not** affect our output today.
  **CONFIRM** only if a future path ever emits a perspective VS or uses depth — then the [0..1] vs
  [-1..1] difference must be handled. Source: [Unity] clip-space depth table.

---

## Summary of verdicts on our known traps

| Trap | Authoritative source says | Our converter | Verdict |
|---|---|---|---|
| **Matrix multiply order** | [MS]: GLSL row-major default, HLSL column-major default (the transpose we rely on); `mul(rowVec, M)` is the row-major product | identical scalar list (→Mᵀ) + `A*B`→`mul(B,A)`; transposes cancel | **Consistent.** Proven for mat2; re-verify mat3/mat4 on render gate (RISK-1). |
| **`mod` sign** | GLSL `x - y*floor(x/y)` (sign follows y); HLSL `fmod` ≠ this | `glsl_mod` helper = GLSL formula, never `fmod` | **Correct — more faithful than [SM], which omits it** (RISK-2). |
| **`gl_FragCoord` / Y-flip** | [Unity]: D3D top-left, GL bottom-left → flip to recover GL orientation; [MS]: `gl_FragCoord`→`SV_Position` | `fragCoord = (uv.x, 1-uv.y) * iResolution` | **Direction matches Unity/MS.** Double-check half-pixel center + `.z/.w` constants (RISK-3). NB: [SM] README states the opposite orientation — disregard it. |
