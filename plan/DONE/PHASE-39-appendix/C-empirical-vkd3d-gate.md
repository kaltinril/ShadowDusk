# ShadowDusk Phase 39 — FNA fx_2_0 Feasibility Gate (empirical, Windows)

**Date:** 2026-06-09. **Machine:** Windows 11 Pro 10.0.26200, .NET SDK 10.0.204 (app targets net8.0, runtime 8.0.28).
**Library under test:** `C:\git\ShadowDusk\tools\vkd3d\libvkd3d-shader-1.dll` — `vkd3d_shader_get_version()` → **"vkd3d-shader 1.17"**, SHA256 `500CD915002AA95B17995954E69474031B32837FB16355AE9AA31D7BDD6F6718`.
**Harness:** scratch .NET 8 console app at `C:\Users\jerem\AppData\Local\Temp\sd-fna-gate\app\Program.cs` (P/Invoke via `NativeLibrary.SetDllImportResolver` with the absolute dll path; structs copied from `src\ShadowDusk.HLSL\Vkd3d\Vkd3dNative.cs`, marshalling pattern from `Vkd3dShaderCompiler.cs`, `Vkd3dTargetType.D3dBytecode = 4`). All artifacts in `C:\Users\jerem\AppData\Local\Temp\sd-fna-gate\out\`. **Nothing under `C:\git\ShadowDusk` was modified** (`git status --porcelain` shows only a pre-existing untracked `docs/fx2-binary-format.md` not created by this gate).

**Bottom line: the gate passes.** vkd3d 1.17's `D3D_BYTECODE` backend is real and usable for an FNA fx_2_0 mode; the .fxb *container* must be written by ShadowDusk (vkd3d's own FX writer can't do fx_2_0 passes yet); golden oracles exist on this machine and were captured.

---

## Q1 — vkd3d 1.17: D3D9-style HLSL → D3D_BYTECODE

All probes: source type `HLSL(2)`, target `D3D_BYTECODE(4)`, options NULL, log level WARNING. rc=0 = `VKD3D_OK`; rc=-5 = `VKD3D_ERROR_NOT_IMPLEMENTED`; rc=-4 = `VKD3D_ERROR_INVALID_SHADER` (matches header).

| # | Shader | Profile | rc | Blob | first4 (LE dword) | expected | CTAB |
|---|--------|---------|----|------|-------------------|----------|------|
| 1a | minimal PS `float4 PSMain():COLOR{return float4(1,0,0,1);}` | ps_2_0 | 0 | 112 B | `0xFFFF0200` (00 02 FF FF) | 0xFFFF0200 ✓ | yes |
| 1a | minimal PS | ps_3_0 | 0 | 112 B | `0xFFFF0300` | 0xFFFF0300 ✓ | yes |
| 1b | textured PS (`sampler s0; tex2D`) | ps_2_0 | 0 | 168 B | `0xFFFF0200` ✓ | ✓ | yes |
| 1b | textured PS | ps_3_0 | 0 | 168 B | `0xFFFF0300` ✓ | ✓ | yes |
| 1c | uniform PS (`float4 Tint; sampler s0`) | ps_2_0 | 0 | 240 B | `0xFFFF0200` ✓ | ✓ | yes (dump below) |
| 1d | VS `mul(pos, WorldViewProj)` | vs_2_0 | 0 | 1224 B | `0xFFFE0200` ✓ | ✓ | yes |
| 1d | VS | vs_3_0 | 0 | 1236 B | `0xFFFE0300` | 0xFFFE0300 ✓ | yes |
| 1e | `sampler s0 = sampler_state { Texture = <t>; MipFilter = LINEAR; };` + tex2D | ps_2_0 | 0 | 168 B | `0xFFFF0200` ✓ | ✓ | yes |
| 1e | same, `Texture = (t);` form | ps_2_0 | 0 | 168 B | `0xFFFF0200` ✓ | ✓ | yes |
| 1f | tex2Dlod | ps_3_0 | 0 | 180 B | `0xFFFF0300` ✓ | ✓ | yes |
| 1f | texCUBE | ps_2_0 | 0 | 168 B | `0xFFFF0200` ✓ | ✓ | yes |

### 1c CTAB dump (vkd3d emits fully usable constant tables)
```
CTAB @ blob offset 0x8 (comment token 0x0022FFFE, 34 dwords)
  header: Size=28 Creator="vkd3d-shader 1.17" Version=0xFFFF0200 Constants=2
  const[0]: name="Tint" regset=FLOAT4  reg=c0 count=1 class=VECTOR type=FLOAT 1x4
  const[1]: name="s0"   regset=SAMPLER reg=s0 count=1 class=OBJECT type=SAMPLER
```
1d VS CTAB: `WorldViewProj regset=FLOAT4 reg=c0 count=4 class=MATRIX_COLS type=FLOAT 4x4` — register *counts* for matrices are right, and SAMPLER vs FLOAT4 regsets are correctly separated. This is exactly what the .fxb parameter binding (and FNA/MojoShader symbol table) needs. Sampler declared as generic `sampler` reports type SAMPLER (10); `sampler2D` reports SAMPLER2D (12) — see micro-probe X.

**1e answer: vkd3d ACCEPTS `sampler_state` initializer blocks (both `<t>` and `(t)` forms) and ignores them for plain-shader targets.** The FNA pre-parser does **not** need to strip them before per-shader compiles — it only needs to *read* them itself to populate the .fxb sampler-state records.

### Micro-probes (failure-mode isolation)
| Probe | Profile | rc | Result |
|---|---|---|---|
| `clip((a<0.5) ? -1 : 1)` (int ternary) | ps_3_0 | **-5** | `E5017: ... SM1 cmp expression of type int.` |
| `clip((a<0.5) ? -1.0 : 1.0)` (float) | ps_3_0 | 0 | OK, 324 B, CTAB |
| `float4 Color : register(ps, c0);` | ps_2_0 | **-5** | `E5017: ... Reservation shader target ps.` |
| `float4 Color : register(c0); sampler2D tex0 : register(s0);` | ps_2_0 | 0 | OK; CTAB honors pinning: Color→c0 FLOAT4, tex0→s0 SAMPLER2D |

### Bonus: technique tolerance + vkd3d's own FX target
- **Full effect source with `technique { pass { PixelShader = compile ps_2_0 PSMain(); } }` left in**, compiled to D3D_BYTECODE entry `PSMain` ps_2_0 → **rc=0**, 168 B, CTAB. Technique blocks need not be stripped for vkd3d.
- **`VKD3D_SHADER_TARGET_FX(7)` with profile `fx_2_0`** (vkd3d writing the whole effect): minimal → rc=-5 `E5017: ... Write pass assignments.`; textured adds `Writing fx_2_0 sampler objects initializers is not implemented.` **vkd3d 1.17 cannot emit the fx_2_0 container** — ShadowDusk writes the .fxb itself.

---

## Q2 — Corpus sweep (48 top-level `tests/fixtures/shaders/*.fx`)

Method (crude pre-parse, per task + necessary upgrades): recursive `#include` inlining from the fixtures dir; `#define OPENGL 1` prepended when the file references OPENGL (selects the MonoGame template's SM≤3 branch — the FNA-relevant one; profile macros resolved preferring `(vs|ps)_[1-3]_*` values); XNA `TECHNIQUE(name,vs,ps)` macro calls mapped to vs_2_0/ps_2_0 (Macros.fxh's no-SM4 DX9 branch); technique blocks brace-stripped (later shown unnecessary); annotation blocks stripped with a type-keyword-anchored regex; sampler_state initializers **kept** (per 1e). Profiles ≥ SM4 (incl. `*_level_9_1`) marked out-of-scope without compiling. Files with SM4 markers but an SM≤3 declared profile were compiled anyway (markers are `SV_*` semantics / `Texture2D` in the non-OPENGL branch) and noted. On `Reservation shader target` failure, retried once with `:: register(vs|ps, …)` reservations removed (noted).

**Totals: 136 rows → 105 OK (CTAB=yes in every OK), 2 FAIL, 26 out-of-scope-SM4, 3 no-compile-statements.** Full row-level table: `%TEMP%\sd-fna-gate\out\corpus_sweep.txt`.

| File | Entries × profile | Result | CTAB | Notes |
|---|---|---|---|---|
| AlphaTestEffect.fx | 8 × vs_2_0/ps_2_0 | **OK (8/8)** | yes | TECHNIQUE macro; reg(vs/ps)-reservation strip needed |
| BasicEffect.fx | 30 × vs_2_0/ps_2_0 | **OK (30/30)** | yes | same |
| DualTextureEffect.fx | 6 × vs_2_0/ps_2_0 | **OK (6/6)** | yes | same |
| EnvironmentMapEffect.fx | 8 × vs_2_0/ps_2_0 | **OK (8/8)** | yes | same |
| SkinnedEffect.fx | 12 × vs_2_0/ps_2_0 | **OK (12/12)** | yes | same |
| SpriteEffect.fx | 2 × vs_2_0/ps_2_0 | **OK (2/2)** | yes | same |
| PenumbraHull/Light/Shadow/Texture.fx | 12 × vs_2_0/ps_2_0 | **OK (12/12)** | yes | cbuffer{} + SV_POSITION/SV_TARGET accepted at SM2, no reservation strip needed |
| BasicShader, BlendShader, ClipShader, ClipShaderNew, ClipShaderSpriteTarget, Dissolve, Dots, Fading, Grayscale, Invert, MultiTexture, MultiTextureOverlay, Pixelated, Saturate, Scanlines, Sepia, SimpleLightShader, SpriteAlphaTest, Teleport, TintShader (20 files) | 1 × ps_3_0 each | **OK (20/20)** | yes | OPENGL branch (`ps_3_0`); sampler_state kept |
| PolygonLight, VertexAndPixel, VsTransformColorTexture (3 files) | vs_3_0 + ps_3_0 each | **OK (6/6)** | yes | OPENGL branch |
| ForwardLighting.fx | mainVS vs_3_0 / mainPS ps_3_0 | VS **OK**; PS **FAIL** | yes/- | PS: `E5017 SM1 cmp expression of type int` — `clip((color.a<0.2)?-1:1)` line 57 |
| DeferredSprite.fx | deferredSpritePixel ps_3_0 | **FAIL** | - | same gap — `clip((…)? -1 : 1)` line 40 |
| annotations, basiceffect-mini, multitechnique, platform-macros, render-states (SM5) + cbuffer, Minimal, MinimalWithInclude, multipass, textured (SM4) | 26 entries | out-of-scope-SM4 | - | declare only vs/ps_4_0+ |
| minimal_vs_ps, passthrough_vs, textured_vs_ps | — | no-compile-statements | - | raw shader sources, no technique |

**Failure census: a single vkd3d construct gap (int-typed cmp) accounts for both real failures; a single other gap (stage-scoped register reservation) accounted for all stock-effect failures and has a clean rewrite fix.**

---

## Q3 — Golden-oracle availability on this machine

### 3a. d3dcompiler_47 D3DCompile (system32), P/Invoked
| Target | Source | HRESULT | Output | first4 | Errors blob |
|---|---|---|---|---|---|
| `fx_2_0` | minimal effect (technique/pass, ps_2_0) | **0x00000000** | 268 B | bytes `01 09 FF FE` = **LE 0xFEFF0901** ✓ | `warning X4717: Effects deprecated for D3DCompiler_47` (warning only) |
| `fx_2_0` | textured effect (texture + sampler_state + tex2D) | **0x00000000** | 544 B | `01 09 FF FE` ✓ | X4717 warning only |
| `fx_4_0` (control, technique10/CompileShader syntax) | minimal | 0x00000000 | 564 B | `44 58 42 43` = 'DXBC' (sane: fx_4_0 is a DXBC container) | X4717 |

Notes: entry point must be NULL for fx targets; no include handler needed for these sources.

### 3b. fxc.exe
Found in **three** SDKs: `C:\Program Files (x86)\Windows Kits\10\bin\{10.0.19041.0, 10.0.22621.0, 10.0.26100.0}\{x64,x86,arm64}\fxc.exe`. Ran `10.0.26100.0\x64\fxc.exe /T fx_2_0 /Fo out.fxb effect.fx`:
- minimal: **exit 0**, "compilation object save succeeded", warning X4717, 268 B, first16 `01 09 FF FE 2C 00 00 00 00 00 00 00 01 00 00 00`
- textured: **exit 0**, 544 B, first16 `01 09 FF FE C4 00 00 00 00 00 00 00 05 00 00 00`

### Cross-check
`D3DCompile("fx_2_0")` output vs `fxc /T fx_2_0` output: **byte-identical** for both effects (0 diffs / 268 B; 0 diffs / 544 B). One canonical oracle, two invocation paths. **Golden fx_2_0 fixtures can be generated locally on this machine** (a Windows-only fixture-generation step — fine, fixtures are checked in; the shipping pipeline stays vkd3d).

---

## Q4 — Golden textured fx_2_0 binary: annotated dump (first 512 bytes)

Saved: `out\textured_fx20.fxb` (544 B), `out\textured_fx20.fxb.hex.txt` (full), `out\minimal_fx20.fxb(.hex.txt)` (268 B), plus fxc twins (`*_fxc.fxb`). Layout decode cross-checked against MojoShader's reader (`mojoshader_effects.c` — FNA's actual loader). Offsets *inside records* are relative to **base = 0x008**. Fields marked (?) are the two dwords MojoShader also skips as unknown.

```
00000000  01 09 FF FE C4 00 00 00 00 00 00 00 05 00 00 00   ................
00000010  04 00 00 00 1C 00 00 00 00 00 00 00 00 00 00 00   ................
00000020  01 00 00 00 02 00 00 00 74 00 00 00 0A 00 00 00   ........t.......
00000030  04 00 00 00 94 00 00 00 00 00 00 00 00 00 00 00   ................
00000040  02 00 00 00 05 00 00 00 04 00 00 00 00 00 00 00   ................
00000050  00 00 00 00 00 00 00 00 02 00 00 00 02 00 00 00   ................
00000060  02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00   ................
00000070  01 00 00 00 01 00 00 00 02 00 00 00 A4 00 00 00   ................
00000080  00 01 00 00 3C 00 00 00 38 00 00 00 AB 00 00 00   ....<...8.......
00000090  00 01 00 00 54 00 00 00 50 00 00 00 03 00 00 00   ....T...P.......
000000A0  73 30 00 00 03 00 00 00 0F 00 00 00 04 00 00 00   s0..............
000000B0  00 00 00 00 00 00 00 00 00 00 00 00 02 00 00 00   ................
000000C0  50 00 00 00 02 00 00 00 54 00 00 00 02 00 00 00   P.......T.......
000000D0  01 00 00 00 03 00 00 00 04 00 00 00 04 00 00 00   ................
000000E0  18 00 00 00 00 00 00 00 00 00 00 00 24 00 00 00   ............$...
000000F0  70 00 00 00 00 00 00 00 00 00 00 00 BC 00 00 00   p...............
00000100  00 00 00 00 01 00 00 00 B4 00 00 00 00 00 00 00   ................
00000110  01 00 00 00 93 00 00 00 00 00 00 00 A0 00 00 00   ................
00000120  9C 00 00 00 01 00 00 00 02 00 00 00 01 00 00 00   ................
00000130  00 00 00 00 00 00 00 00 00 00 00 00 FF FF FF FF   ................
00000140  00 00 00 00 00 00 00 00 B8 00 00 00 00 02 FF FF   ................
00000150  FE FF 1E 00 43 54 41 42 1C 00 00 00 4B 00 00 00   ....CTAB....K...
00000160  00 02 FF FF 01 00 00 00 1C 00 00 00 00 00 00 20   ............... 
00000170  44 00 00 00 30 00 00 00 03 00 00 00 01 00 00 00   D...0...........
00000180  34 00 00 00 00 00 00 00 73 30 00 AB 04 00 0C 00   4.......s0......
00000190  01 00 01 00 01 00 00 00 00 00 00 00 70 73 5F 32   ............ps_2
000001A0  5F 30 00 4D 69 63 72 6F 73 6F 66 74 20 28 52 29   _0.Microsoft (R)
000001B0  20 48 4C 53 4C 20 53 68 61 64 65 72 20 43 6F 6D    HLSL Shader Com
000001C0  70 69 6C 65 72 20 31 30 2E 31 00 AB 1F 00 00 02   piler 10.1......
000001D0  00 00 00 80 00 00 03 B0 1F 00 00 02 00 00 00 90   ................
000001E0  00 08 0F A0 42 00 00 03 00 00 0F 80 00 00 E4 B0   ....B...........
000001F0  00 08 E4 A0 01 00 00 02 00 08 0F 80 00 00 E4 80   ................
```
Annotated walk:
- **0x000** `0xFEFF0901` — fx_2_0 magic/version token (the bytes on disk are `01 09 FF FE`).
- **0x004** `0x000000C4` — offset, relative to base 0x008, to the **directory** → 0x0CC.
- **0x008–0x0CB — data pool** (types, names, values):
  - 0x00C type record (param "t"): type=**5 TEXTURE**, 0x010 class=**4 OBJECT**, 0x014 name@base+0x1C → 0x024 `len=2,"t\0"` (0x028), 0x018 semantic=0.
  - 0x020 texture value = **object handle 1**.
  - 0x02C type record (param "s0"): type=**10 SAMPLER**, 0x030 class=4, 0x034 name@base+0x94 → 0x09C `len=3,"s0\0"` (0x0A0).
  - 0x078 sampler value: **2 state assignments**; state#1 @0x07C id=**0xA4=164 (Texture)** → object ref; state#2 @0x08C id=**0xAB=171 (MipFilter)**, value @base+0x54 → 0x05C = **2 (D3DTEXF_LINEAR)**.
  - 0x0BC `len=2,"P\0"` (pass name), 0x0C4 `len=2,"T\0"` (technique name).
- **0x0CC directory**: params=**2**, 0x0D0 techniques=**1**, 0x0D4 =3 (?), 0x0D8 object count=**4**.
- **0x0DC param records** (typeOffset, valueOffset, flags, annotationCount): "t" = (0x04, 0x18, 0, 0); "s0" = (0x24, 0x70, 0, 0) @0x0EC.
- **0x0FC technique**: name@base+0xBC ("T"), 0x100 annotations=0, 0x104 passes=**1**; pass @0x108: name@base+0xB4 ("P"), 0x10C annos=0, 0x110 states=**1**; state @0x114: type=**0x93=147 (PIXELSHADER)**, 0x118 (?)=0, 0x11C valueType@base+0xA0 → 0x0A8 {type=**15 PIXELSHADER**, class=4}, 0x120 value@base+0x9C → 0x0A4 = **object index 3**.
- **0x124–0x147 object tables**: small-object record(s) (sampler's texture-name ref machinery, `FF FF FF FF` sentinel @0x13C), then large-object header with **blob size 0x0B8=184** @0x148.
- **0x14C–0x203 embedded ps_2_0 D3DBC blob (184 B)**: version token `0xFFFF0200` @0x14C; comment token `0xFFFE`, 30 dwords @0x150 = **CTAB** (creator "Microsoft (R) HLSL Shader Compiler 10.1", 1 constant: **"s0" @0x188, regset SAMPLER (regset/regidx/regcount words `04 00 0C 00 / 01 00 01 00` area), target "ps_2_0"**); code @0x1CC: `dcl t0.xy / dcl_2d s0 / texld r0,t0,s0 / mov oC0,r0`; end token `FF FF 00 00` @0x200.
- **0x204–0x21F (beyond 512-byte window)**: second large object — the texture name for the sampler state: `FF FF FF FF, 01, 00, 00, size=2, "t\0"`; file ends at 0x220 = 544.

The 268-byte minimal effect was fully walked with the same structure (dir @0x034: 0 params, 1 technique, (?)=2, 2 objects; PIXELSHADER state 147 → object 1; 128-byte ps_2_0 blob @0x08C) — useful as the smallest reference vector.

---

## Implications for Phase 39 (what the implementer should take from this)

1. **Per-shader compile path is settled**: HLSL → vkd3d `D3D_BYTECODE` at the declared SM≤3 profile. Version tokens, CTAB names/regsets/registers all correct. Reuse the existing `Vkd3dShaderCompiler` marshalling with `TargetType = D3dBytecode` and the d3d9 profile string.
2. **ShadowDusk writes the .fxb container** (vkd3d TARGET_FX/fx_2_0 can't yet — E5017 on pass assignments). The captured goldens + decoded layout above (MojoShader-compatible) are the spec cross-check; `docs/fx2-binary-format.md` (pre-existing untracked draft in the repo) should be validated against these bytes.
3. **FX pre-parser obligations (evidence-based)**: must parse techniques/passes/sampler_state *for the writer's sake*, but does **not** need to strip technique blocks or sampler_state initializers from the HLSL handed to vkd3d (both tolerated). It **must** rewrite stage-scoped reservations: `register(vs, rN)`/`register(ps, rN)` → plain `register(rN)` for the matching stage, dropped for the other (plain form is supported and honored). Without this, every XNA stock effect fails.
4. **Known limitation to document (or fix upstream)**: int-typed ternary at SM≤3 (`clip(c ? -1 : 1)`) fails with E5017; float literals work. 2/48 corpus files affected.
5. **Golden fixtures**: generate locally via fxc or D3DCompile (byte-identical) on Windows dev machines; check them in for cross-platform byte-level writer tests.

## Artifacts (all under `C:\Users\jerem\AppData\Local\Temp\sd-fna-gate\`)
`app\` (harness source + build) · `minimal_effect.fx`, `textured_effect.fx` (oracle inputs) · `out\probes.txt`, `out\corpus_sweep.txt` (full 136-row table), `out\oracle.txt`, `out\fxtarget.txt` · `out\{minimal,textured}_fx20.fxb` + `.hex.txt` + `*_fxc.fxb` twins + `minimal_fx40.fxb` (DXBC control) · `out\1a..1f, X_*.bin` (vkd3d blobs).
