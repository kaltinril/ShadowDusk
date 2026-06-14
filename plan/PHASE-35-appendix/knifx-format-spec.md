# KNIFX v11 container format — reverse-engineered spec (the KNIFX writer blueprint)

**Status:** Authoritative format spec, reverse-engineered 2026-06-14 from KNI's own source at
`kniEngine/kni@main` (the writer, the runtime reader, and the supporting enums/hash are the ground truth).
This **answers Area B open-question #2** ("what are KNIFX's exact signature bytes, header layout, and body
deltas vs MGFX v10"). It is the implementation blueprint for ShadowDusk's faithful `KnifxWriter`.

**Why this matters:** Per the 2026-06-14 direction, emitting KNIFX is a **committed** additive output (see the
[kickoff brief](knifx-area-b-kickoff-brief.md) and [main phase doc](../PHASE-35-forward-version-support.md)).
The GLSL **dialect/text** is the same MojoShader-dialect code ShadowDusk already emits for v10 (KNIFX is a
container over a still-MojoShader body, verified in
[shader-pipeline-landscape-2026-06.md](shader-pipeline-landscape-2026-06.md)) — but **the bytes are NOT the
same**, and "re-serialization" is an **understatement** (adversarial validation, 2026-06-14). KNIFX requires
real writer work beyond re-encoding:

> **⚠️ Correction (2026-06-14, validation agent).** An earlier draft of this spec wrongly said the body is
> "byte-for-byte the same GLSL." It is not. Three load-bearing items make KNIFX **more** than re-serialization:
> 1. **The GL `ShaderCode` is a GLSL-version bytecode DIRECTORY, not raw GLSL** (see [§ GL ShaderCode](#gl-shadercode-the-glsl-is-wrapped-in-a-version-directory-not-raw) below). With a non-default `ShaderVersion`, KNI's `ConcreteShader.FindShaderByteCode` parses `ShaderCode` as this directory; raw GLSL there throws `"Invalid shader bytecode"`. **Hard correctness gate.**
> 2. **`columnsActual` is load-bearing** — it drives the runtime matrix uniform-upload stride (`ConstantBufferStrategy.cs` uses `ColumnCountActual`, not `ColumnCount`). ShadowDusk gets columns from SPIRV-Cross, not MojoShader register-packing, so it **cannot yet compute the optimized value** for matrices that pack into fewer registers. `columnsActual == columns` is safe for scalar/vector/full-matrix; the optimized-matrix case (exactly the "Matrix4x4 not demoted" fix KNIFX advertises) is a **known fidelity gap**.
> 3. **The validation bar is NOT "renders identical to our v10 render."** For the optimized-matrix and sampler-without-texture fixes, a *correct* KNIFX render can legitimately **differ** from v10. "maxd 0 vs v10" is only a smoke test for the simple corpus; feature-bearing cases must be checked against a **KNIFXC golden**.

So a faithful KNIFX writer re-serializes the same *container* data **and** wraps the GL shader code in the
version directory **and** carries a non-default per-shader `ShaderVersion`. The compute stage is the only true
new *compiler* capability the container exposes, and it is **dead at runtime** (KNI v4.2.9001's reader throws
`NotImplementedException` for compute), so we emit the `-1` sentinel and build no `cs_*` path.

## Source of truth (KNI @ main, read 2026-06-14)

| Piece | File |
|---|---|
| Writer (body) | `src/Xna.Framework.Content.Pipeline.Graphics.MojoProcessor/EffectCompiler/KNIFXWriter11.cs` |
| Writer (header / multi-backend directory) | `.../MojoProcessor/Processors/MojoEffectProcessor.cs` (the `MemoryStream`/`BinaryWriter` block) |
| Runtime reader | `src/Xna.Framework.Graphics/Graphics/Effect/Effect.cs` (`KNIFXHeader` path + `KNIFXReader11`) |
| Shader stage enum | `.../MojoProcessor/EffectCompiler/ShaderStage.cs` |
| Shader version | `.../MojoProcessor/EffectCompiler/ShaderVersion.cs` |
| effectKey hash | `src/Xna.Framework.Graphics/Graphics/Utilities/HashHelpers.cs` |

## The headline differences from MGFX v10

KNIFX is **not** a version-byte bump on the MGFX body. Three structural changes:

1. **Multi-backend directory container.** One `.knifx` file can carry **several backend bodies** (e.g. the
   OpenGL body *and* the DirectX body); the runtime reads a directory and picks the body matching its
   `GraphicsDevice`. MGFX v10 is single-backend (one `.mgfx` per target). **This is directly useful for the
   seamless "one artifact everywhere" goal**, a single KNIFX could serve GL and DX consumers.
2. **`WritePackedInt` everywhere.** Almost every count and index in the body is written as a **zigzag +
   7-bit-encoded int** (`((v << 1) ^ (v >> 31))` then `Write7BitEncodedInt`), replacing MGFX v10's plain
   `Write(int)` / `Write(byte)`. This re-encodes nearly every length field in the body.
3. **New fields:** a per-shader **`ShaderVersion`** (major/minor), a **compute-shader stage** (`Stage` is a
   byte with Compute=5, and each pass writes a *third* shader index for compute), and a **`columnsActual`**
   byte on every parameter.

Render-state blocks (blend / depth-stencil / rasterizer) are **identical** between v10 and KNIFX (same fields,
same order, same plain-int encodings), so that code is reused unchanged.

## Header (written by MojoEffectProcessor, read by Effect.cs)

All integers little-endian. `EntrySize = 10`.

```
offset 0   : "KNIF"                      4 bytes  (KNIFXWriter11.KNIFXSignature = "KNIF")
offset 4   : version                     int16    = 11
offset 6   : reserved                    int16    = 0
offset 8   : backendCount                int16    = N
offset 10  : directory[N], each entry (EntrySize = 10 bytes):
               backend                   int16    (GraphicsBackend enum)
               effectKey                 int32    (FNV-1a hash of THIS body, see below)
               fxOffset                  int32    (absolute offset of this body's length prefix)
offset 10 + N*10 : bodies[N], each:
               effectLength              int32    (byte length of the serialized body that follows)
               body                      effectLength bytes (see "Body" below)
```

- The reader (`Effect.cs`) loops the directory, finds the entry whose `backend` its adapter supports, seeks to
  `fxOffset`, reads `effectLength`, then hands the slice to `KNIFXReader11`.
- **`effectKey` is a cache key only** — the reader uses it for `EffectCache.TryGetValue`, it does **not**
  re-hash and validate the body. So a load+render works even with a "wrong" key; we replicate the real hash
  only for byte-faithfulness to KNIFXC.
- **No trailing footer** (MGFX v10 writes a trailing `"MGFX"`; KNIFX does not).

### `GraphicsBackend` enum values (confirm before shipping)

The directory stores `(short)backend`. Get the exact `GraphicsBackend` enum integer for `OpenGL` (and `DX11`)
from KNI's `Xna.Framework.Graphics` before writing, the reader compares against
`graphicsDevice.Adapter.Backend`. (TODO: pin these two values from `GraphicsBackend.cs`.)

### effectKey = FNV-1a/32 + avalanche (HashHelpers.ComputeHash)

```
hash = 2166136261u (as int)
for each body byte b:  hash = (hash ^ b) * 16777619
hash += hash << 13;  hash ^= hash >> 7;  hash += hash << 3;  hash ^= hash >> 17;  hash += hash << 5;
effectKey = hash   (int32)
```
Hashed over the **per-backend body bytes** (the `effectLength`-counted slice, i.e. the `WriteEffect` output
including the leading `integersAsFloats` bool). ShadowDusk already has `ManagedMd5` for the v10 key; this is a
new, simpler managed hash (no BCL dependency, WASM-safe).

## Body (KNIFXWriter11.WriteEffect), field by field vs ShadowDusk's MgfxWriter

Legend: **PI** = `WritePackedInt` (zigzag+7bit). Everything not marked PI is a plain `BinaryWriter.Write`.

```
body:
  integersAsFloats : bool           # NEW vs v10. true when the backend is OpenGL_Mojo.
  constantBuffers
  shaders
  parameters
  techniques
```

### Constant buffers
| Field | MGFX v10 (ShadowDusk) | KNIFX v11 |
|---|---|---|
| count | int32 | **PI** |
| per cb: Name | string | string |
| per cb: Size | int16 | **PI** |
| per cb: paramCount | int32 | **PI** |
| per param: index | int32 | **PI** |
| per param: offset | uint16 | uint16 (unchanged) |

### Shaders
| Field | MGFX v10 (ShadowDusk) | KNIFX v11 |
|---|---|---|
| count | int32 | **PI** |
| stage | `bool isVertexShader` | **`byte Stage`** (Pixel=0, Vertex=1, Compute=5) |
| **shaderVersion** | (none) | **PI major, PI minor** (NEW) |
| codeLength | int32 | int32 (unchanged) |
| code bytes | raw | raw |
| samplerCount | byte | **PI** |
| per sampler: type/textureSlot/samplerSlot | 3 bytes | 3 bytes (unchanged) |
| per sampler: hasState + state | bool [+ state block] | bool [+ state block] (state block unchanged*) |
| per sampler: GLsamplerName | string | string |
| per sampler: textureParameter | **byte** | **PI** |
| cbufferIndexCount | byte | **PI** |
| per cbIndex | byte | **PI** |
| attributeCount | byte | **PI** |
| per attr: Name | string | string |
| per attr: Usage | byte | byte |
| per attr: Index | byte | **PI** |
| per attr: Location | int16 | int16 |

\* Sampler-state block: AddressU/V/W (3 bytes), BorderColor R/G/B/A (4 bytes), Filter (byte), then
**MaxAnisotropy** and **MaxMipLevel** (MGFX: plain int32; **KNIFX: PI**), MipMapLevelOfDetailBias (single).

\*\* `codeLength` + `code`: in MGFX v10 the `code` is the raw GLSL (GL) or DXBC (DX). In KNIFX the **GL**
`ShaderCode` is **not** raw GLSL — see the next section. The DX `ShaderCode` stays raw DXBC.

### GL ShaderCode: the GLSL is wrapped in a version directory, not raw

> Source: KNI `ShaderProfileGL.CreateGLSL` (writer) + `ConcreteShader.FindShaderByteCode` (runtime reader).

For a **GL** backend, KNI does not store raw GLSL in `ShaderCode`. It stores a **GLSL-version bytecode
directory**, and the runtime selects the entry matching its GL context. Layout (all little-endian; the GLSL
bytes are ASCII):

```
ShaderCode (GL):
  reserved        int16  = 0
  count           int16  = number of GLSL version entries
  directory[count], EntrySize = 7 bytes each:
     Major        byte    (GLSL language version major)
     Minor        byte    (GLSL language version minor)
     ES           bool    (1 byte; true for GL-ES/WebGL entries)
     offset       int32   (absolute offset within ShaderCode of this blob's length prefix)
  blobs[count], each:
     length       int32
     glsl         length bytes (ASCII)
```
`HeaderSize = 4` (reserved + count), `EntrySize = 7`, so `offset[0] = 4 + 7*count`.

Per-backend entries KNI emits:
- **`OpenGL` (desktop)** -> **one** entry `(Major=1, Minor=1, ES=false)` = GLSL 1.10. **This is exactly
  ShadowDusk's existing MojoShader-dialect GL output** (the same bytes that render raw on the v10 path), just
  wrapped. This is what `validation/KniDesktopGL` exercises.
- `GLES` -> `(3,0,ES=true)` (GLSL 300 es), plus `(1,0,ES=false)` for SM2. Needs GLSL-version *conversion*
  ShadowDusk does not yet do on this path -> a documented refinement.

**Trigger:** the directory parse only happens when the per-shader `ShaderVersion` is **non-default**. If
`ShaderVersion == (0,0)`, the runtime treats `ShaderCode` as **raw GLSL** (the legacy MGFX-v10 path). So a
faithful v11 GL shader **must** carry both the non-default `ShaderVersion` **and** the directory wrapper; a
v10-style "raw GLSL with (0,0)" would also load but is not the v11 path.

### Parameters (recursive: elements then members)
| Field | MGFX v10 (ShadowDusk) | KNIFX v11 |
|---|---|---|
| count | int32 | **PI** |
| Class | byte | byte |
| Type | byte | byte |
| Name | string | string |
| Semantic | string | string |
| annotations count | int32 | **PI** (bodies still none) |
| rows | byte | byte |
| columns | byte | byte |
| **columnsActual** | (none) | **byte** (NEW) |
| elements (recursive) | list | list |
| members (recursive) | list | list |
| leaf data blob | rows*cols*4 zero bytes (value-type leaf) | same (`Write((byte[])param.data)`) |

`columnsActual` semantics (**load-bearing, not cosmetic**): KNI's runtime
`ConstantBufferStrategy` uses **`ColumnCountActual`, not `ColumnCount`**, as the matrix register stride when
uploading uniforms. KNI computes it as `Math.Min(columns, register_count)` from MojoShader's register packing.
**For scalar / vector / full matrices `columnsActual == columns`** (safe to default), but for an **optimized
matrix array** (the exact "Matrix4x4 not demoted to Matrix4x3" fix KNIFX advertises) it is smaller. ShadowDusk
derives its column count from **SPIRV-Cross, not MojoShader register packing**, so it **cannot currently
compute the optimized value** -> defaulting `columnsActual = columns` silently loses that fix for optimized
matrices. **Known fidelity gap**; pin against a KNIFXC golden when implementing the optimized-matrix case.

### Techniques / passes
| Field | MGFX v10 (ShadowDusk) | KNIFX v11 |
|---|---|---|
| technique count | int32 | **PI** |
| per tech: Name | string | string |
| per tech: annotations | int32 count | **PI** count |
| pass count | int32 | **PI** |
| per pass: Name | string | string |
| per pass: annotations | int32 count | **PI** count |
| vertexShaderIndex | int32 | **PI** |
| pixelShaderIndex | int32 | **PI** |
| **computeShaderIndex** | (none) | **PI** (NEW; -1 sentinel when absent) |
| blend/depth/rasterizer blocks | bool + fields | **identical** (unchanged) |

`GetShaderIndex` returns the index or a "none" sentinel (confirm -1) for a missing stage; we always have no
compute shader, so write that sentinel via PI.

## ShaderVersion: what value to write

KNI parses it from the pass's `compile vs_X_Y` / `ps_X_Y` directive (`ShaderVersion.ParseVertexShaderModel`
etc.). ShadowDusk already parses the technique passes, so thread the **declared shader model major/minor** of
each shader into the IR `ShaderBlob`. For the SM3 PS-only corpus that is `(3,0)`. (For `vs_4_0_level_9_1`
KNI maps to `(2,?)`, ignore until we target those.)

## Implementation plan for `KnifxWriter` (ShadowDusk.Core)

> **Status (2026-06-14):** `KnifxWriter` + `KnifxWriterOptions`/`KnifxBackend` are **implemented** in
> `src/ShadowDusk.Core/KnifxWriter.cs` with the corrected GL ShaderCode directory wrapping, and **unit-tested
> for byte structure** (`tests/ShadowDusk.Core.Tests/KnifxWriterTests.cs`, 15 tests incl. a GL-wrap/DX-raw
> regression guard). `CompiledShaderBlob.ShaderModel` was added (default (3,0)). **Not yet done:** wiring the
> compile path to emit KNIFX, threading the real `ShaderModel` from the pass profile, and the **render
> validation** on the KNI rig (the actual proof). `columnsActual` defaults to `columns` (optimized-matrix gap).

1. **IR additions:** add `ShaderVersionMajor`/`Minor` to the shader blob (populated from the parsed pass model)
   and, if not already present, `columnsActual` to the parameter info (default = column count). No other IR
   change, the GLSL bytes, samplers, cbuffers, attributes, render-states are all already produced for v10.
2. **`KnifxWriter`** paralleling `MgfxWriter`: a `WritePackedInt` helper (zigzag+7bit), the multi-backend
   directory header, the FNV-1a effectKey, and the body writer with the field deltas above. Reuse the
   render-state writers verbatim (identical bytes).
3. **Single- vs multi-backend:** start single-backend (one body, the GL one), since ShadowDusk compiles one
   target at a time. Leave the directory loop ready for a future "bundle GL+DX in one KNIFX" (the seamless
   one-artifact win).
4. **Validation (the bar, corrected):** emit KNIFX for the SM3 corpus, load + render in the
   **`validation/KniDesktopGL` rig** (already built, KNI v4.2.9001). For the **simple corpus** (no optimized
   matrices, textured samplers), a *loads + renders, and matches the v10 render (maxd 0)* result is a valid
   **smoke test** (same GLSL, same params). But **do NOT treat "maxd 0 vs v10" as the success criterion in
   general**: the optimized-matrix and sampler-without-texture fixes mean a *correct* KNIFX render can
   legitimately **differ** from v10 — so feature-bearing cases must be checked against a **KNIFXC golden**, not
   against our own v10 output (which would mask exactly the fixes KNIFX exists to provide). A KNIFXC sample also
   pins `columnsActual` (optimized case), the `GraphicsBackend` value, and the effectKey for byte-faithfulness.
5. **Seamless wiring (separate step, see [auto-select design](knifx-autoselect-design.md)):** default stays
   v10; KNIFX is auto-selected from a KNI target signal or available as a non-required escape hatch. Never a
   flag a consumer must set for correct output.

## Open items to pin against a KNIFXC golden (not load-critical, needed for byte-identity)
- The exact `GraphicsBackend` integer for OpenGL (and DX11) in the directory entry.
- `columnsActual` for matrix parameters (equal to `columns` for the current corpus).
- The compute-"absent" sentinel value from `GetShaderIndex` (expected -1).
- Whether KNIFXC ever emits multi-backend bundles by default, or one backend per file.
