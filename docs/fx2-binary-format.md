# fx_2_0 Effects Binary Format — MojoShader-Compatible Writer Specification

> **This is the implementation spec for Phase 39's `Fx2EffectWriter`** — the byte-level layout of the
> Direct3D 9 Effects Framework binary ("fx_2_0", version token `0xFEFF0901`) **exactly as FNA's
> MojoShader parses it**. There is no public official spec; **MojoShader's parser is the spec**.
> Derived by reading the parser source at **icculus/mojoshader commit
> `6333f74dbd5644789a63e903816441b16c1e8b60`** (2026-04-28) — note `github.com/FNA-XNA/MojoShader`
> now *redirects* to `icculus/mojoshader` (the FNA fork was merged upstream; this is the copy FNA
> ships via FNA3D). Files read: `mojoshader_effects.c` (the effect parser), `mojoshader.c`
> (CTAB/comment parsing), `mojoshader.h` (enums/structs), `mojoshader_internal.h` (constants).
> Cross-checked against **vkd3d** `libs/vkd3d-shader/fx.c` (fx_2_0 *writer*, master @ 2026-06-09),
> **Wine** `dlls/d3dx9_36/effect.c` (native-d3dx9-compatible *reader*, master @ 2026-06-09), and
> **FNA** `src/Graphics/Effect/Effect.cs` + `EffectParameter.cs` (runtime consumption, master @
> 2026-06-09). MojoShader is zlib-licensed; this document records format *facts* learned from
> reading it — no code is copied.
>
> Convention used below: "**MUST**" = MojoShader hard-fails or corrupts memory without it;
> "**ignored**" = MojoShader reads and discards the field (cross-checked sources tell us what fxc
> puts there). Line references like `effects.c:302` mean `mojoshader_effects.c` at the pinned commit.

---

## 1. Global conventions

- **Endianness: little-endian throughout.** Every field below is a 32-bit unsigned LE integer
  ("dword") unless stated otherwise (CTAB type-info uses 16-bit fields).
- **`base`** = file offset **8** (the byte immediately after the header's two dwords). **Every
  offset stored anywhere in the file** (typedef offsets, value offsets, name/semantic/string
  offsets) **is relative to `base`**, i.e. `absolute = 8 + offset`.
- The file has two regions after the header: the **data pool** ("unstructured" block: typedefs,
  strings, default values) at `[8, 8 + pool_size)`, then the **structured stream** (counts,
  parameter/technique/pass/state records, object records) read strictly sequentially from
  `8 + pool_size` to EOF.
- **No bounds checking** is performed on pool offsets (`readvalue` uses an unbounded length,
  `effects.c:313`; `readstring` has explicit `FIXME: sanity checks!`, `effects.c:274`). A bad
  offset is silent memory corruption, not an error. The writer is fully responsible for validity.
- Floats are IEEE-754 binary32. Bools are stored as dwords `0`/`1` (vkd3d `fx.c:1830` normalizes
  with `!!value`). Ints are dwords.

## 2. File overview

| Region | Contents |
|---|---|
| `+0` | dword `0xFEFF0901` (version token) |
| `+4` | dword `pool_size` — size in bytes of the data pool; the structured stream starts at `8 + pool_size`. vkd3d aligns this to 4 (`fx.c:2157`); keep the pool size a multiple of 4. |
| `+8` | **data pool** (`base`): typedef records, length-prefixed strings, default-value blobs, in any order, addressed only via offsets |
| `+8+pool_size` | **structured stream**: counts header → parameter records → technique records → object-section counts → small-object records → large-object records |

### 2.1 Header parsing details (`effects.c:978–1011`)

- The version token is read as one LE dword and split: `magic=(t>>16)&0xFFFF`, `major=(t>>8)&0xFF`,
  `minor=t&0xFF`; it MUST satisfy `magic==0xFEFF && major==0x09 && minor==0x01` — i.e. the dword
  `0xFEFF0901`, on-disk byte sequence `01 09 FF FE`. Otherwise: hard fail
  `"Not an Effects Framework binary"`.
- **Optional XNA4 wrapper** (`effects.c:984–994`): if the first dword is `0xBCF00BCF` (parsed as
  magic `0xBCF0`, major `0x0B`, minor `0xCF`), MojoShader reads the next dword `n` and skips to
  absolute file offset `n`, where the real `0xFEFF0901` effect must begin (`skip = n - 8` from the
  current position 8). The XNA4 Effect processor emits this; **a bare `0xFEFF0901` file is fully
  accepted** — ShadowDusk's writer MUST NOT emit the wrapper. (Bookkeeping note: MojoShader does
  `len += skip` instead of `-=` — a benign bug; `len` only guards dword reads from running out.)
- Hard EOF checks: total length MUST be ≥ 8 before the header; `pool_size` MUST be ≤ remaining
  length; ≥ 16 bytes MUST remain at the counts header; ≥ 8 bytes MUST remain at the object-section
  counts (`effects.c:975, 1004, 1010, 1043`).

## 3. Strings (`readstring`, `effects.c:269–288`)

A string in the pool is:

| Offset | Type | Meaning |
|---|---|---|
| `+0` | u32 | `len` — byte count **including the terminating NUL** |
| `+4` | byte[len] | characters + `\0` |

- `len == 0` ⇒ MojoShader returns `NULL` (no string).
- **Offset 0 = null-string convention**: write a zeroed dword as the first 4 bytes of the pool
  (vkd3d does exactly this, `fx.c:2134` "First entry is always zeroed and skipped"); then any
  name/semantic offset of `0` reads `len==0` → `NULL`. Use offset `0` for every absent semantic
  and anonymous typedef.
- MojoShader imposes no alignment on reads, but fxc/vkd3d pad each string blob so the *next* pool
  item starts 4-byte aligned (`fx.c:1655–1669`). Do the same: pad `4 + len` up to a multiple of 4
  with zero bytes.
- There is no de-duplication requirement; identical strings MAY be stored once and shared (offsets
  may point anywhere in the pool, including overlapping regions).

## 4. Counts header (`effects.c:1014–1017`)

At `8 + pool_size`, four dwords:

| # | Field | MojoShader behavior |
|---|---|---|
| 0 | `parameter_count` | number of parameter records that follow |
| 1 | `technique_count` | number of technique records after the parameters. MUST be ≥ 1: MojoShader unconditionally sets `current_technique = &techniques[0]` (`effects.c:1040`); 0 techniques crashes at `MOJOSHADER_effectBegin`. (vkd3d also errors on 0, `fx.c:2167`.) |
| 2 | *(ignored)* | Read and discarded (`effects.c:1016`). Per Wine (`effect.c:6366`) this is the **shader count**: "number of shader variables, with each pass contributing one additional slot"; vkd3d writes `#shader-typed parameter elements + #passes` (`fx.c:1109,1736`). Emit `total_pass_count + shader_typed_param_elements` for fxc fidelity; any value parses. |
| 3 | `object_count` | size of the object table, **including reserved index 0** (see §9). MojoShader allocates exactly this many entries, zero-typed (`MOJOSHADER_SYMTYPE_VOID = 0`). Every object index stored in any value blob MUST be `< object_count` (unchecked → corruption). |

## 5. Parameter records (`readparameters`, `effects.c:507–541`)

For each parameter, the structured stream contains, in order:

| # | Field | Notes |
|---|---|---|
| 0 | `typedef_offset` | → pool, typedef record (§6) |
| 1 | `value_offset` | → pool, value blob (§7) |
| 2 | `flags` | **ignored** by MojoShader (`effects.c:529`). fxc/vkd3d: bit 0 = `shared`, plus `D3DX_PARAMETER_*` flags. Write `0`. |
| 3 | `annotation_count` | usually `0` |
| 4… | annotation records | `annotation_count × 2` dwords: each annotation is `{typedef_offset, value_offset}` parsed with the same `readvalue` machinery (`effects.c:494–504`). **Zero annotations is fully supported everywhere** (MojoShader skips, `effects.c:488`; FNA builds an empty collection). ShadowDusk emits 0 annotations always. |

The parameter's typedef and value are then resolved from the pool; nothing else is consumed from
the structured stream.

**Parameter ordering constraint (FNA runtime):** FNA's `Effect.cs` builds the sampler→texture map
assuming *"textures have to be declared before the sampler"* (`Effect.cs:888–897`): when it
processes a sampler parameter it searches only the parameters *already* converted. **Every
texture parameter MUST appear in the parameter list before any sampler parameter that references
it.**

## 6. Typedef records (pool) (`readvalue`, `effects.c:302–476`)

Common prefix (5 dwords):

| # | Field | Constraint |
|---|---|---|
| 0 | `type` | `MOJOSHADER_symbolType`, see §6.1 |
| 1 | `class` | `MOJOSHADER_symbolClass`, see §6.1. MUST be 0–5 (assert, `effects.c:327`) |
| 2 | `name_offset` | → string (§3); `0` = no name |
| 3 | `semantic_offset` | → string; `0` = no semantic |
| 4 | `element_count` | array size; **`0` = not an array** (a non-array and `[1]` differ: 0 vs 1) |

Then, by `class`:

### Numeric classes — SCALAR(0), VECTOR(1), MATRIX_ROWS(2), MATRIX_COLUMNS(3)

Two more dwords. `type` MUST be BOOL(1), INT(2), or FLOAT(3) (assert, `effects.c:335`).

| # | MojoShader reads | fxc / Wine / vkd3d write |
|---|---|---|
| 5 | `column_count` | VECTOR: columns (**agrees**). SCALAR/MATRIX_*: **rows** (**swapped!**) |
| 6 | `row_count` | VECTOR: rows (**agrees**). SCALAR/MATRIX_*: **columns** (**swapped!**) |

> ⚠ **The matrix rows/columns swap.** MojoShader reads dword5 as columns and dword6 as rows for
> *all* numeric classes (`effects.c:337–341`). Wine's reader (`effect.c:5547–5569`) and vkd3d's
> writer (`fx.c:1722–1730`) use columns-then-rows **only for VECTOR**, and rows-then-columns for
> SCALAR and MATRIX classes. For scalars (1×1) and square matrices (`float4x4`) the swap is
> invisible — which is why real XNA content works. For **non-square matrices the two conventions
> disagree**; see §15 finding F1 before emitting any non-square matrix parameter.

These two dwords also drive the value-blob shape and FNA's `EffectParameter.RowCount/ColumnCount`
(FNA reads MojoShader's `type.rows/type.columns` verbatim, `Effect.cs:907–909`).

### OBJECT class (4)

No additional typedef dwords. `type` MUST be in STRING(4)…VERTEXSHADER(16) (assert,
`effects.c:357`).

### STRUCT class (5)

| # | Field |
|---|---|
| 5 | `member_count` |
| 6… | `member_count` member records, **7 dwords each**: `type, class, name_offset, semantic_offset(read, discarded), element_count, column_count, row_count` (`effects.c:424–433`) |

Member constraints (asserts, `effects.c:436–439`): member `class` MUST be SCALAR…MATRIX_COLUMNS
(0–3), member `type` MUST be BOOL…FLOAT (1–3). **No nested structs** ("FIXME: Nested structs!
-flibit"). Members get the same dims-order caveat as above.

> ⚠ **Struct default values are broken in MojoShader**: it reads the initializer bytes from the
> *type stream immediately after the member records* — explicitly *not* from `value_offset`
> ("Yes, typeptr. -flibit", `effects.c:468`) — and when a member's `element_count` is 0 it copies
> nothing at all (loop bound `rows * elements`, `effects.c:464`). Net effect for normal structs:
> defaults stay zero. **Do not rely on struct parameter defaults**; zero-fill the value blob (Wine
> reads it from `value_offset` for size `Σ 4·rows·cols`, so still emit a correctly-sized blob).

### 6.1 Symbol enums (`mojoshader.h:309–343`)

`MOJOSHADER_symbolClass`: `SCALAR=0, VECTOR=1, MATRIX_ROWS=2, MATRIX_COLUMNS=3, OBJECT=4, STRUCT=5`.

`MOJOSHADER_symbolType`:

| Value | Name | Value | Name |
|---|---|---|---|
| 0 | VOID | 9 | TEXTURECUBE |
| 1 | BOOL | 10 | SAMPLER |
| 2 | INT | 11 | SAMPLER1D |
| 3 | FLOAT | 12 | SAMPLER2D |
| 4 | STRING | 13 | SAMPLER3D |
| 5 | TEXTURE | 14 | SAMPLERCUBE |
| 6 | TEXTURE1D | 15 | PIXELSHADER |
| 7 | TEXTURE2D | 16 | VERTEXSHADER |
| 8 | TEXTURE3D | 17/18/19 | PIXELFRAGMENT / VERTEXFRAGMENT / UNSUPPORTED (never valid in values) |

These match `D3DXPARAMETER_CLASS` / `D3DXPARAMETER_TYPE` numerically.

## 7. Value blobs (pool, at `value_offset`)

### 7.1 Numeric (SCALAR / VECTOR / MATRIX_ROWS / MATRIX_COLUMNS) — `effects.c:343–353`

Let `E = max(1, element_count)`, `C = column_count` (dword5), `R = row_count` (dword6).

The file blob is **densely packed**: `R × E` "rows", each of `C` dwords (float/int/bool-as-dword),
total `4·C·R·E` bytes. Array elements follow one another with no padding.

What MojoShader does with it (this defines what the bytes must mean): it allocates
`value_count = 4·R·E` floats (each file row expanded into a 4-float register slot, tail
zero-padded) and copies `C` floats per row. At draw time `copy_parameter_data`
(`effects.c:1786–1844`) **memcpys this expanded image directly into the shader's constant
registers** (`register_count × 16` bytes starting at the CTAB `register_index`; FLOAT params via
`memcpy`, INT/BOOL converted per-component). Therefore:

> **Each file "row" of `C` values becomes exactly one float4 constant register.** Lay out default
> values so row *i* is the desired contents of register `register_index + i`. For fxc's default
> `column_major` matrix packing each register holds a matrix *column* — i.e. the blob must hold
> the matrix transposed, column after column (this matches FNA's `EffectParameter.SetValue(Matrix)`,
> which writes M11,M21,M31,M41,M12,… — column-major — into the same memory, `EffectParameter.cs:835–861`).
> See finding F2 — verify against an fxc golden before shipping non-trivial matrix defaults.

`value_count` is also what FNA exposes as the parameter's data size (`value_count × 4` bytes).

### 7.2 OBJECT, non-sampler (STRING, TEXTURE*, PIXELSHADER, VERTEXSHADER) — `effects.c:390–409`

`max(1, element_count)` dwords, each an **object-table index** (§9). Side effect: parsing sets
`objects[index].type = typedef.type` — this is how object-table entries acquire their type.
Wine agrees (`effect.c:5283`: one `object_id` per element).

### 7.3 OBJECT, sampler (SAMPLER, SAMPLER1D/2D/3D/CUBE) — `effects.c:359–389`

The value blob *is* the embedded sampler-state list:

| Field | Meaning |
|---|---|
| u32 `state_count` | number of sampler-state records |
| per state, 4 dwords + referenced pool data: | |
| u32 `state_op` | file value is **`0xA0`-biased**: `0xA4`=Texture … `0xB1`=DMapOffset (§8.2). MojoShader masks `& ~0xA0` → `MOJOSHADER_samplerStateType` (`effects.c:377`). Wine requires the 164-based values (indices into its full state table) — **emit 164-based**. |
| u32 `index` | **ignored** by MojoShader (`effects.c:378`); Wine: per-state index (always 0 for sampler states). Write `0`. |
| u32 `state_typedef_offset` | → pool typedef for the state's value (recursive §6) |
| u32 `state_value_offset` | → pool value blob for the state's value (recursive §7) |

Plain states (AddressU, MinFilter, …): typedef = `{type=INT(2) or FLOAT(3), class=SCALAR(0),
name=0, semantic=0, elements=0, C=1, R=1}`, value blob = one dword (enum value / int / float per
§8.2).

**The Texture state** (`state_op = 0xA4`): typedef = `{type=TEXTURE(5) (or matching TEXTURE1D/2D/3D/CUBE),
class=OBJECT(4), name=0, semantic=0, elements=0}`; value blob = **one dword object index**. This
object is *distinct from* the texture parameter's own value object. After parsing it, MojoShader
overwrites that object's type with the **sampler's** type (`effects.c:386–387`), and the object's
*data* — supplied later by a small or large object record — must be the **name of the texture
parameter** as a NUL-terminated string (the "sampler map"). FNA resolves it: sampler param →
find state whose value typedef type is TEXTURE…TEXTURECUBE → `objects[idx].mapping.name` → look
up the parameter with that name → bind its texture to the sampler's register at apply time
(`Effect.cs:875–898, 679–689`).

### 7.4 STRUCT — see §6 STRUCT caveat; blob = densely packed member values (`Σ 4·R·C·max(1,elems)`
bytes per element, members in order, elements consecutive), which MojoShader effectively ignores
(zeros) and Wine honors.

## 8. Techniques, passes, states

### 8.1 Records (`readtechniques`/`readpasses`/`readstates`, `effects.c:543–649`)

**Technique** (structured stream):

| # | Field |
|---|---|
| 0 | `name_offset` → string |
| 1 | `annotation_count` (then `2×count` dwords of annotation records) |
| 2 | `pass_count` |
| … | pass records |

**Pass**:

| # | Field |
|---|---|
| 0 | `name_offset` → string |
| 1 | `annotation_count` (then annotation records) |
| 2 | `state_count` |
| … | state records |

**State** — 4 dwords (`effects.c:563–566`):

| # | Field | Notes |
|---|---|---|
| 0 | `operation` | `MOJOSHADER_renderStateType` value (§8.2) — stored as-is, no masking |
| 1 | `index` | **ignored** by MojoShader (`effects.c:564`); Wine: sub-index for indexed states (`LightEnable[n]`, texture stages). Write `0`. |
| 2 | `typedef_offset` | → pool (§6) |
| 3 | `value_offset` | → pool (§7) |

**Shader states**: `operation` **146 = VERTEXSHADER**, **147 = PIXELSHADER**
(`mojoshader.h:2402–2403`). Typedef = `{type=VERTEXSHADER(16) / PIXELSHADER(15), class=OBJECT(4),
name=0, semantic=0, elements=0}`; value blob = **one dword object index**. At `BeginPass`
MojoShader looks up `effect->objects[*state->value.valuesI].shader` for these two ops
(`effects.c:1737–1752`); the object must have been filled by a large-object record carrying the
shader bytecode (§9). If a pass has no VertexShader/PixelShader state, the previously-bound
shader stays current — **omit the state rather than emitting a "NULL shader"** (see finding F4).

### 8.2 State ID tables

`MOJOSHADER_renderStateType` (`mojoshader.h:2285–2404`) — note the comment: these are **0-based
file values, not D3DRS numbers**, and the 16 `WRAPn` states are contiguous:

| ID | Name | ID | Name | ID | Name |
|---|---|---|---|---|---|
| 0 | ZENABLE † | 35 | WRAP4 | 70 | DEBUGMONITORTOKEN |
| 1 | FILLMODE † | 36 | WRAP5 | 71 | POINTSIZE_MAX |
| 2 | SHADEMODE | 37 | WRAP6 | 72 | INDEXEDVERTEXBLENDENABLE |
| 3 | ZWRITEENABLE † | 38 | WRAP7 | 73 | COLORWRITEENABLE † |
| 4 | ALPHATESTENABLE | 39 | WRAP8 | 74 | TWEENFACTOR |
| 5 | LASTPIXEL | 40 | WRAP9 | 75 | BLENDOP † |
| 6 | SRCBLEND † | 41 | WRAP10 | 76 | POSITIONDEGREE |
| 7 | DESTBLEND † | 42 | WRAP11 | 77 | NORMALDEGREE |
| 8 | CULLMODE † | 43 | WRAP12 | 78 | SCISSORTESTENABLE † |
| 9 | ZFUNC † | 44 | WRAP13 | 79 | SLOPESCALEDEPTHBIAS † |
| 10 | ALPHAREF | 45 | WRAP14 | 80 | ANTIALIASEDLINEENABLE |
| 11 | ALPHAFUNC | 46 | WRAP15 | 81 | MINTESSELLATIONLEVEL |
| 12 | DITHERENABLE | 47 | CLIPPING | 82 | MAXTESSELLATIONLEVEL |
| 13 | ALPHABLENDENABLE † | 48 | LIGHTING | 83 | ADAPTIVETESS_X |
| 14 | FOGENABLE | 49 | AMBIENT | 84 | ADAPTIVETESS_Y |
| 15 | SPECULARENABLE | 50 | FOGVERTEXMODE | 85 | ADAPTIVETESS_Z |
| 16 | FOGCOLOR | 51 | COLORVERTEX | 86 | ADAPTIVETESS_W |
| 17 | FOGTABLEMODE | 52 | LOCALVIEWER | 87 | ENABLEADAPTIVETESSELLATION |
| 18 | FOGSTART | 53 | NORMALIZENORMALS | 88 | TWOSIDEDSTENCILMODE † |
| 19 | FOGEND | 54 | DIFFUSEMATERIALSOURCE | 89 | CCW_STENCILFAIL † |
| 20 | FOGDENSITY | 55 | SPECULARMATERIALSOURCE | 90 | CCW_STENCILZFAIL † |
| 21 | RANGEFOGENABLE | 56 | AMBIENTMATERIALSOURCE | 91 | CCW_STENCILPASS † |
| 22 | STENCILENABLE † | 57 | EMISSIVEMATERIALSOURCE | 92 | CCW_STENCILFUNC † |
| 23 | STENCILFAIL † | 58 | VERTEXBLEND | 93 | COLORWRITEENABLE1 † |
| 24 | STENCILZFAIL † | 59 | CLIPPLANEENABLE | 94 | COLORWRITEENABLE2 † |
| 25 | STENCILPASS † | 60 | POINTSIZE | 95 | COLORWRITEENABLE3 † |
| 26 | STENCILFUNC † | 61 | POINTSIZE_MIN | 96 | BLENDFACTOR † |
| 27 | STENCILREF † | 62 | POINTSPRITEENABLE | 97 | SRGBWRITEENABLE |
| 28 | STENCILMASK † | 63 | POINTSCALEENABLE | 98 | DEPTHBIAS † |
| 29 | STENCILWRITEMASK † | 64 | POINTSCALE_A | 99 | SEPARATEALPHABLENDENABLE † |
| 30 | TEXTUREFACTOR | 65 | POINTSCALE_B | 100 | SRCBLENDALPHA † |
| 31 | WRAP0 | 66 | POINTSCALE_C | 101 | DESTBLENDALPHA † |
| 32 | WRAP1 | 67 | MULTISAMPLEANTIALIAS † | 102 | BLENDOPALPHA † |
| 33 | WRAP2 | 68 | MULTISAMPLEMASK † | **146** | **VERTEXSHADER** † |
| 34 | WRAP3 | 69 | PATCHEDGESTYLE | **147** | **PIXELSHADER** † |

(vkd3d's `fx_2_pass_states` table independently confirms these numbers, e.g. ZEnable=0, SrcBlend=6,
DiffuseMaterialSource=54, `fx.c:881+`.)

> **† = honored by FNA at runtime.** FNA's `Effect.cs` applies *only* the †-marked states (plus a
> silent skip for state **178**, fxc's pass-level `Sampler[n] =` op) and **throws
> `NotImplementedException` for every other state ID** found in a pass (`Effect.cs:604–611`).
> fxc-level texture-stage pass states (164–177) therefore *crash* FNA. **ShadowDusk's writer emits
> only 146/147** (mgfxc-converted effects carry their state in game code, not in `.fx` passes);
> if pass render states are ever added, they must be restricted to the † set.

Value encodings FNA expects for the † states: one dword per state — enum values from
`MOJOSHADER_blendMode` (ZERO=1, ONE=2, SRCCOLOR=3, INVSRCCOLOR=4, SRCALPHA=5, INVSRCALPHA=6,
DESTALPHA=7, INVDESTALPHA=8, DESTCOLOR=9, INVDESTCOLOR=10, SRCALPHASAT=11, …, BLENDFACTOR=14,
INVBLENDFACTOR=15), `MOJOSHADER_cullMode` (NONE=1, CW=2, CCW=3), `MOJOSHADER_compareFunc`
(NEVER=1…ALWAYS=8), `MOJOSHADER_stencilOp` (KEEP=1…DECR=8), `MOJOSHADER_blendOp` (ADD=1…MAX=5),
`MOJOSHADER_fillMode` (POINT=1, WIREFRAME=2, SOLID=3), `MOJOSHADER_zBufferType` (FALSE=0, TRUE=1,
USEW=2) — all identical to the native D3D9 enums (`mojoshader.h:2406–2531`); booleans as 0/1;
BLENDFACTOR as a D3DCOLOR dword; DEPTHBIAS/SLOPESCALEDEPTHBIAS as float dwords.

**`MOJOSHADER_samplerStateType`** (`mojoshader.h:2536–2556`) with the on-disk 164-based op value:

| MojoShader value | On-disk op | Name | FNA honors? |
|---|---|---|---|
| 4 | 164 (0xA4) | TEXTURE | ✔ (binds texture by parameter name) |
| 5 | 165 | ADDRESSU | ✔ `MOJOSHADER_textureAddress`: WRAP=1, MIRROR=2, CLAMP=3, BORDER=4, MIRRORONCE=5 |
| 6 | 166 | ADDRESSV | ✔ |
| 7 | 167 | ADDRESSW | ✔ |
| 8 | 168 | BORDERCOLOR | ✘ **throws** |
| 9 | 169 | MAGFILTER | ✔ `MOJOSHADER_textureFilterType`: NONE=0, POINT=1, LINEAR=2, ANISOTROPIC=3, … |
| 10 | 170 | MINFILTER | ✔ |
| 11 | 171 | MIPFILTER | ✔ |
| 12 | 172 | MIPMAPLODBIAS | ✔ (float) |
| 13 | 173 | MAXMIPLEVEL | ✔ (int) |
| 14 | 174 | MAXANISOTROPY | ✔ (int) |
| 15 | 175 | SRGBTEXTURE | ✘ **throws** |
| 16 | 176 | ELEMENTINDEX | ✘ **throws** |
| 17 | 177 | DMAPOFFSET | ✘ **throws** |

(`Effect.cs:676–748`: unhandled sampler states throw `NotImplementedException`.) The writer MUST
NOT emit BorderColor / SRGBTexture / ElementIndex / DMapOffset. Note FNA applies sampler states
only for samplers the **shader actually uses** (those reaching CTAB with a sampler register).

## 9. Object table and the two object sections

After the last technique: two dwords — `small_object_count` ("strings" in Wine), then
`large_object_count` ("resources" in Wine) — followed by that many records of each kind, smalls
first (`effects.c:1047–1062`).

**The object table itself is virtual**: `object_count` (counts header) entries, all starting as
type VOID; entries acquire types as parameter/state values are parsed (§7.2, §7.3); the small/
large records then attach *data* to them. **Index 0 is reserved/never used** — vkd3d starts IDs
at 1 and writes `object_count = used + 1` (`fx.c:2130,2153`); MojoShader's loops also iterate
record counts from 1. Never reference object 0.

### 9.1 Small objects (`readsmallobjects`, `effects.c:651–770`)

Record: `u32 object_index`, `u32 length`, then `length` raw bytes, then zero-padding so the
*next record* starts 4-byte aligned. MojoShader advances by `(length+3) − ((length−1) mod 4)`
bytes — i.e. **`4·⌈length/4⌉`, and `0` when `length == 0`** (uint32 arithmetic; a zero-length
record is exactly the two header dwords). Behavior by the object's (already established) type:

| Object type | Data |
|---|---|
| STRING | the string's bytes **including NUL** (raw — no inner length prefix); becomes the string parameter's text (FNA `Effect.cs` reads it via the object table) |
| TEXTURE*/SAMPLER* | optional mapped-parameter name (NUL-terminated); `length==0` is the normal case for plain texture objects |
| PIXELSHADER/VERTEXSHADER | SM1–SM3 bytecode, compiled immediately (entry name `ShaderFunction{object_index}`) — fxc puts shaders in the *large* section instead; both work in MojoShader |
| anything else (incl. VOID) | `assert(0 && "Small object type unknown!")` — debug abort. Never emit a record for an object no value referenced. |

fxc/vkd3d use this section for string values and zero-length texture initializers
(`fx.c:1900–1955`).

### 9.2 Large objects (`readlargeobjects`, `effects.c:772–926`)

Record: **6 dwords + data**, same `4·⌈length/4⌉` advance (0 for `length==0`):

| # | Field | Notes |
|---|---|---|
| 0 | `technique` | technique index, or **`0xFFFFFFFF`** = "state lives on a parameter's sampler" |
| 1 | `index` | pass index within the technique; or parameter index when `technique == −1` |
| 2 | `element_index` | **ignored** by MojoShader (`effects.c:796`); Wine: array element selector, `0xFFFFFFFF` = the parameter itself (`effect.c:6202`). Write `0xFFFFFFFF` for the `technique==−1` case, `0` otherwise (finding F5). |
| 3 | `state_index` | state index within the pass (or within the sampler's state list) |
| 4 | `usage` | `0` = raw blob (shader bytecode; or FXLC expression for numeric states — not emitted by ShadowDusk), `1` = parameter-name reference (data = NUL-terminated name), `2` = array-selector standalone preshader. MojoShader checks only `== 2` (`effects.c:814`); Wine/vkd3d name all three (`effect.c:6259–6336`, `fx.c:33–35`). |
| 5 | `length` | data byte count (pad not included) |

**The record carries no object index.** MojoShader (and Wine) resolve the target object *through
the state the record points at*: `objects[ techniques[technique].passes[index].states[state_index].value.valuesI[0] ]`,
or for `technique == −1`: `objects[ params[index].value.valuesSS[state_index].value.valuesI[0] ]`
(`effects.c:801–805`). The referenced state MUST therefore exist and MUST have an object-index
value blob — for `technique == −1` the parameter MUST be a sampler (its value is read as a
sampler-state list). Behavior by resolved object type:

| Object type | Behavior |
|---|---|
| PIXELSHADER / VERTEXSHADER, `usage != 2` | data = SM1–SM3 bytecode → compiled via the backend (`compileShader`, entry `ShaderFunction{objectIndex}`); compile errors abort the whole effect parse with the backend's error list (`effects.c:849–861`) |
| PIXELSHADER / VERTEXSHADER, `usage == 2` | standalone preshader (shader-array selector): data = length-prefixed parameter-name string followed by preshader tokens. **ShadowDusk never emits this.** |
| TEXTURE* / SAMPLER* | data = mapped parameter name (NUL-terminated) → `mapping.name` (the sampler-map string of §7.3) |
| VOID | **silently skipped** (data ignored; `effects.c:916` "FIXME: Why? -flibit") |
| anything else | `assert(0 && "Large object type unknown!")` |

## 10. Shader objects, CTAB, and runtime parameter binding

What MojoShader does with embedded bytecode (`effects.c:701–759`, `843–896`):

1. `compileShader` runs the full `MOJOSHADER_parse*` pipeline on the blob — it must be **valid
   SM1.1–SM3.0 bytecode** (version token `0xFFFF____` ps / `0xFFFE____` vs, instruction stream,
   `0x0000FFFF` end token).
2. **Every CTAB symbol is bound to an effect parameter by exact name match** —
   `findparameter(params, name)` walks parameter names with `strcmp` and **asserts "Parameter not
   found!" on a miss** (`effects.c:290–300`); in release builds a miss is `params[-1]` memory
   corruption. ⇒ **HARD REQUIREMENT: for every constant in every embedded shader's CTAB (uniforms
   *and* samplers) there MUST exist a top-level effect parameter with the identical name.**
3. Symbols with register set SAMPLER produce the sampler-register table: register index from CTAB,
   states from the *effect parameter's* sampler-state list (§7.3), texture bound by FNA through
   the sampler map name. The shared name in CTAB must be the **sampler parameter's** name.
4. Uniform upload (`copy_parameter_data`) uses CTAB `register_index`/`register_count` and the
   parameter's expanded value image (§7.1). **A shader without a CTAB parses fine but binds zero
   parameters** (its uniforms would never be set) — and bytecode using relative addressing
   *hard-fails* without CTAB (`mojoshader.c:419,757`). ⇒ **always emit a CTAB** with one entry per
   referenced uniform/sampler.
5. **Preshaders are OPTIONAL.** Absence = simply: no `PRES`-id comment block inside the bytecode
   and no `usage==2` large objects. `pd->preshader` stays NULL and every preshader code path is
   skipped (`effects.c:748, 885`). ShadowDusk does not emit preshaders.

## 11. CTAB comment layout (`mojoshader.c:2190–2406`, constants `mojoshader_internal.h:434–436`)

The constant table rides inside the shader bytecode as a *comment token block*, conventionally
right after the version token:

| Dword | Value |
|---|---|
| 0 | comment token: `(token_count << 16) | 0x0000FFFE`. **Bit 31 MUST be 0** (hard fail `"comment token high bit must be zero."`, `mojoshader.c:2418`); `token_count` = number of dwords that follow (= `1 + ctab_bytes/4`) |
| 1 | `0x42415443` (`'CTAB'` fourcc, `CTAB_ID`) |
| 2… | the CTAB byte region; **all offsets below are relative to the start of dword 2** (`start`) |

`D3DXSHADER_CONSTANTTABLE` header at `start+0` (28 bytes; all u32):

| Offset | Field | MojoShader validation |
|---|---|---|
| +0 | `Size` | MUST be 28 (`CTAB_SIZE`) |
| +4 | `Creator` (string offset) | MUST be `< bytes` |
| +8 | `Version` | MUST equal the **shader's version token** (e.g. `0xFFFF0300` ps_3_0, `0xFFFE0300` vs_3_0, `0xFFFF0200` ps_2_0) |
| +12 | `Constants` (count) | MUST be ≤ 1,000,000 |
| +16 | `ConstantInfo` (offset) | MUST be `< bytes` and leave room for `Constants × 20` bytes |
| +20 | `Flags` | **not read** by MojoShader |
| +24 | `Target` (string offset) | MUST be a readable NUL-terminated string (content not checked — "ps_3_0" etc. by convention) |

`D3DXSHADER_CONSTANTINFO` records (20 bytes each, `CINFO_SIZE`) at `ConstantInfo`:

| Offset | Type | Field | Notes |
|---|---|---|---|
| +0 | u32 | `Name` | offset to NUL-terminated string; MUST be readable |
| +4 | u16 | `RegisterSet` | **0**=BOOL, **1**=INT4, **2**=FLOAT4, **3**=SAMPLER; anything else = hard fail |
| +6 | u16 | `RegisterIndex` | |
| +8 | u16 | `RegisterCount` | |
| +10 | u16 | `Reserved` | not read |
| +12 | u32 | `TypeInfo` | offset to type info (below) |
| +16 | u32 | `DefaultValue` | MUST be `< bytes`; **content ignored** by MojoShader — write 0 |

`D3DXSHADER_TYPEINFO` (16 bytes, **u16 fields**) at each `TypeInfo` (`mojoshader.c:2210–2283`):

| Offset | Field |
|---|---|
| +0 | u16 `Class` (§6.1 values; ≥ 6 ⇒ error) |
| +2 | u16 `Type` (§6.1 values; ≥ 20 ⇒ error) |
| +4 | u16 `Rows` |
| +6 | u16 `Columns` |
| +8 | u16 `Elements` |
| +10 | u16 `StructMembers` |
| +12 | u32 `StructMemberInfo` offset — ⚠ MojoShader reads this **as the u16 at +12** (`typeptr[6]`), i.e. only the low 16 bits. Keep member-info offsets < 65536. |

Struct member info: `StructMembers` × 8 bytes, each `{u32 name_offset, u32 typeinfo_offset}`,
parsed recursively (depth cap 300).

Validation quirks that constrain the writer:
- A type-info at offset `pos` requires `bytes − pos ≥ 16` **and** `pos + 16 + 8·StructMembers`
  **strictly less than** `bytes` (`mojoshader.c:2242`) — a type-info may not end flush at the end
  of the CTAB region. **Place the creator/target strings after the type-infos** (fxc's natural
  layout) or pad; then this never triggers. (`bytes` here is `token_count × 4`, measured from one
  dword *before* `start`, so there is 4 bytes of inherent slack.)
- CTAB strings are plain NUL-terminated (no length prefix), offsets relative to `start`, must not
  run past the region.
- Only one CTAB per shader (`"Shader has multiple CTAB sections"`).
- Note CTAB type-info uses the **documented D3D order Rows@+4, Columns@+6** — the opposite field
  order from the fx2 typedef quirk in §6; don't conflate the two when reusing reflection code.

For ShadowDusk this section is both (a) what our **SM3 reflection reader** parses from
fxc/vkd3d-produced DXBC-SM3 blobs, and (b) what our writer must synthesize if we ever emit
bytecode lacking a CTAB.

## 12. Worked example — minimal effect, byte-by-byte

Source-equivalent:

```hlsl
float4 TintColor = float4(1,1,1,1);
texture SpriteTexture;
sampler2D SpriteSampler = sampler_state { Texture = (SpriteTexture); };
technique T0 { pass P0 { PixelShader = compile ps_2_0 PS(); } }   // no VertexShader state
```

Object table plan: `0` reserved, `1` = texture object, `2` = sampler's Texture-state reference
object (carries the name `"SpriteTexture"`), `3` = pixel-shader object → `object_count = 4`.
Let `N` = pixel-shader bytecode length, `N4 = 4·⌈N/4⌉`.

**Header** (absolute file offsets):

| Abs | Bytes (LE dwords) | Meaning |
|---|---|---|
| 0x000 | `01 09 FF FE` | `0xFEFF0901` |
| 0x004 | `E8 00 00 00` | pool_size = 0xE8 (232) |

**Data pool** (`base` = abs 0x008; pool offsets shown):

| Pool ofs | Dwords | Meaning |
|---|---|---|
| 0x000 | `0` | null entry (offset 0 ⇒ no string) |
| 0x004 | `3, 1, 0x20, 0, 0, 4, 1` | TintColor typedef: FLOAT, VECTOR, name@0x20, no semantic, no array, C=4, R=1 |
| 0x020 | `10,` `"TintColor\0"` + 2 pad | name (len 10 incl NUL; 4+10→16 bytes) |
| 0x030 | `1.0f, 1.0f, 1.0f, 1.0f` | TintColor value blob (1 row × 4 floats) |
| 0x040 | `5, 4, 0x54, 0, 0` | SpriteTexture typedef: TEXTURE, OBJECT, name@0x54 |
| 0x054 | `14,` `"SpriteTexture\0"` + 2 pad | name (4+14→20 bytes) |
| 0x068 | `1` | SpriteTexture value blob: object index **1** |
| 0x06C | `12, 4, 0x80, 0, 0` | SpriteSampler typedef: SAMPLER2D, OBJECT, name@0x80 |
| 0x080 | `14,` `"SpriteSampler\0"` + 2 pad | name (4+14→20 bytes) |
| 0x094 | `5, 4, 0, 0, 0` | inner typedef for the Texture state: TEXTURE, OBJECT, anonymous |
| 0x0A8 | `2` | inner value: object index **2** |
| 0x0AC | `1,  0xA4, 0, 0x94, 0xA8` | SpriteSampler value blob: state_count=1; state {op=0xA4 Texture, index=0, typedef@0x94, value@0xA8} |
| 0x0C0 | `3,` `"T0\0"` + 1 pad | technique name (4+3→8 bytes) |
| 0x0C8 | `3,` `"P0\0"` + 1 pad | pass name |
| 0x0D0 | `15, 4, 0, 0, 0` | PixelShader-state typedef: PIXELSHADER, OBJECT, anonymous |
| 0x0E4 | `3` | PixelShader-state value: object index **3** |

pool_size = 0x0E8. **Structured stream** (abs 0x0F0):

| Abs | Dwords | Meaning |
|---|---|---|
| 0x0F0 | `3, 1, 1, 4` | 3 parameters, 1 technique, shader_count=1 (ignored; = 1 pass), 4 objects |
| 0x100 | `0x04, 0x30, 0, 0` | param 0 TintColor {typedef, value, flags, annos} |
| 0x110 | `0x40, 0x68, 0, 0` | param 1 SpriteTexture |
| 0x120 | `0x6C, 0xAC, 0, 0` | param 2 SpriteSampler |
| 0x130 | `0xC0, 0, 1` | technique "T0": name, 0 annotations, 1 pass |
| 0x13C | `0xC8, 0, 1` | pass "P0": name, 0 annotations, 1 state |
| 0x148 | `147, 0, 0xD0, 0xE4` | state: PIXELSHADER, index 0, typedef@0xD0, value@0xE4 |
| 0x158 | `1, 2` | 1 small object, 2 large objects |
| 0x160 | `1, 0` | small record: object 1 (texture), length 0 — no data, no padding |
| 0x168 | `0xFFFFFFFF, 2, 0xFFFFFFFF, 0, 1, 14,` `"SpriteTexture\0"` + 2 pad | large record A: technique=−1 ⇒ parameter 2 (SpriteSampler), element −1, state 0 (its Texture state) ⇒ resolves to object **2**; usage=1 (name ref); 24+16 = 40 bytes |
| 0x190 | `0, 0, 0, 0, 0, N,` bytecode + pad to N4 | large record B: technique 0, pass 0, element 0, state 0 ⇒ resolves to object **3**; usage=0 (code blob); ps_2_0 bytecode **with CTAB** declaring `TintColor` (FLOAT4 regset) and `SpriteSampler` (SAMPLER regset) |

Total size = `0x190 + 24 + N4`. Parse walkthrough: param values type objects 1 (TEXTURE) and 2
(TEXTURE → overwritten to SAMPLER2D by the SAMP_TEXTURE post-step) and the pass state types
object 3 (PIXELSHADER); the small record attaches nothing to object 1; large A attaches
`mapping.name = "SpriteTexture"` to object 2; large B compiles object 3 and binds its CTAB
symbols `TintColor` → param 0 and `SpriteSampler` → param 2 (sampler register + state list); FNA's
sampler map then ties `SpriteSampler` to the `SpriteTexture` parameter's texture.

## 13. MojoShader strictness summary

**Hard requirements (parse error, assert-abort, or memory corruption if violated):**

1. First dword `0xFEFF0901` (optionally behind the XNA4 wrapper) — else "Not an Effects Framework binary".
2. File length ≥ 8; `pool_size` ≤ remaining; ≥ 16 bytes at counts header; ≥ 8 at object counts — else "Unexpected EOF".
3. ≥ 1 technique (`techniques[0]` is taken unconditionally).
4. Every pool offset (typedef/value/name/semantic) valid — **nothing is bounds-checked**.
5. Every object index `< object_count`; index 0 never referenced.
6. Typedef asserts: class 0–5; numeric class ⇒ type 1–3; OBJECT class ⇒ type 4–16; struct members: class 0–3, type 1–3, no nested structs.
7. Small-object records only for objects whose type was established by some value (VOID ⇒ assert).
8. Large-object records must back-reference an existing technique/pass/state (or parameter/sampler-state) whose value is an object index; for `technique == −1` the parameter MUST be sampler-typed.
9. Embedded shader blobs must be valid SM1.1–3.0 bytecode; a backend compile error aborts the effect with that error.
10. **Every CTAB constant name must match an effect parameter name exactly** (release-mode UB otherwise).
11. CTAB (when present, which it must be for parameter binding): comment-token bit 31 = 0; `Size`=28; `Version` = shader version token; register set ≤ 3; strings NUL-terminated in-region; type-info not flush against the region end (§11).
12. Object data blocks padded to 4 bytes (the parser *advances* by the padded size; unpadded data desynchronizes the stream).

**Read-but-ignored (writer may emit anything; recommended values for fxc fidelity):**

- Counts-header dword 2 (shader count) → `#passes + #shader-typed param elements`.
- Parameter `flags` → `0`.
- State record dword 1 (`index`) → `0`.
- Sampler-state record dword 1 (`index`) → `0`.
- Large-object `element_index` → `0xFFFFFFFF` for `technique==−1`, else `0`.
- CTAB `Flags`, `Reserved`, `DefaultValue` contents → `0`.
- Annotation *contents* (parsed, exposed, never interpreted) — and **zero annotations everywhere is
  fine** (count 0 ⇒ no bytes, all readers cope).
- Struct default-value blobs (MojoShader reads them from the wrong place; keep them zeroed).
- A large-object record resolving to a VOID-typed object is silently skipped.

**FNA-runtime constraints beyond MojoShader (throw `NotImplementedException`):** pass states
outside the † set of §8.2; sampler states BorderColor/SRGBTexture/ElementIndex/DMapOffset.

## 14. Differences from a byte-perfect fxc clone (acceptable)

ShadowDusk's writer needs MojoShader/FNA compatibility, not byte-equality with fxc (mirroring the
project-wide "behaviorally equivalent, not byte-identical" rule). Known acceptable deviations:
pool item ordering, string deduplication, the ignored fields above, putting shader blobs in the
small vs large section (MojoShader accepts both; fxc uses large), and annotation omission.
Wine/native-d3dx9 compatibility is *not* a goal but falls out almost free if the "recommended
values" above are used — except it additionally requires the 164-based sampler ops (which we emit)
and correct `usage` values (which we emit).

## 15. Open ambiguities — resolve with an fxc golden before relying on them

- **F1 — Non-square matrix dims order** (§6): MojoShader reads `columns,rows`; fxc/Wine/vkd3d write
  `rows,columns` for SCALAR/MATRIX classes. Square matrices are safe. Before emitting any
  non-square matrix parameter, dump an fxc-compiled effect containing `float4x3`/`float3x4` and
  decide which order (and which value-blob stride) actually renders correctly under FNA.
- **F2 — Matrix default-value majority** (§7.1): the blob must be the *register image* for
  MojoShader (one file row = one register), which for fxc-default `column_major` means transposed
  data; whether fxc itself bakes defaults row-major (with native d3dx9 transposing at upload) or
  register-shaped needs one golden with a known non-symmetric default. Runtime `SetValue` is
  unaffected (FNA writes column-major into the same memory).
- **F3 — `texture` vs `texture2D` typedef type for the sampler's Texture state**: we emit
  TEXTURE(5); fxc may emit the dimensioned type. Both satisfy MojoShader (5–9) and FNA's range
  check; cosmetic only.
- **F4 — `VertexShader = NULL` encoding**: unknown whether fxc emits a state with a never-filled
  object or omits it; a 146-state pointing at an empty object makes MojoShader bind a NULL vertex
  shader (backend-dependent behavior). **Writer policy: omit the state entirely** when a stage is
  absent; confirm with a golden if NULL-stage `.fx` files must round-trip.
- **F5 — `element_index` for pass-state large objects**: MojoShader ignores it; we write `0`
  (Wine ignores it for the technique path too). Verify fxc's actual value if byte-diffing goldens.
- **F6 — counts-header shader count**: emit `#passes + #shader params`; ignored by MojoShader,
  asserted by nobody. Only matters for golden byte-diffs.

## 16. Source citations (pinned)

| Fact | Source |
|---|---|
| Header/version/XNA4 skip, counts, section order | `mojoshader_effects.c:928–1076` |
| Strings | `mojoshader_effects.c:269–288` |
| Typedef/value parsing (all classes, sampler states) | `mojoshader_effects.c:302–476` |
| Annotations / parameters / states / passes / techniques | `mojoshader_effects.c:478–649` |
| Small/large objects, padding formula, shader binding | `mojoshader_effects.c:651–926` |
| `findparameter` name-match requirement | `mojoshader_effects.c:290–300` |
| Pass shader-state runtime lookup; uniform upload | `mojoshader_effects.c:1715–1844` |
| CTAB comment/constant-table layout | `mojoshader.c:2190–2406, 2413–2425, 2828–2848`; `mojoshader_internal.h:434–436` |
| Enums (symbol class/type, render/sampler state, D3D9 value enums) | `mojoshader.h:300–343, 2285–2576` |
| fx_2_0 writer cross-check (header, zero pool entry, object IDs from 1, string format, state ID tables, small-object format) | vkd3d `libs/vkd3d-shader/fx.c` (`hlsl_fx_2_write` @2122, `write_fx_2_string` @1655, `write_fx_2_type_iter` @1698, state tables @881/1075) |
| Reader cross-check (field names: state operation/index; resource technique/index/element/state/usage; typedef dims order; object id per element) | Wine `dlls/d3dx9_36/effect.c` (`d3dx_parse_state` @5732, `d3dx_parse_effect_typedef` @5507, `d3dx_parse_resource` @6166, `d3dx_parse_effect` @6352, `d3dx9_copy_data` @5401) |
| FNA runtime: honored render/sampler states, sampler map, texture-before-sampler ordering, matrix transposition | FNA `src/Graphics/Effect/Effect.cs` (@321–643, 855–960), `EffectParameter.cs` (@835–960) |
