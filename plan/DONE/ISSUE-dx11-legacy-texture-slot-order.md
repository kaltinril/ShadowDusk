# DirectX 11 legacy sampler textures followed declaration order, not fxc's first-read order

**Status:** ✅ **Fixed and render-proved, 2026-10-28** (branch `fix/dx11-texture-slot-order`), on
both DXBC backends after an independent review.
**Reported by:** an adversarial reviewer, measured in real KNI 4.2.9001 WinForms.DX11 against a
real `mgfxc` 3.8.4.1 build: the XNA/MonoGame `BloomCombine.fx` shape rendered maxd 188 against the
reference, `texture Tex2;` declared above a `register(s0)` sprite sampler maxd 129 to 244.
**Supersedes:** the "DirectX is NOT being fixed" half of
[ISSUE-189](ISSUE-189-gl-sampler-slot-declaration-order.md) §6.3 for TEXTURE slots ("revisit only
if one is reported": it was), and the `project_facts.md` "Still divergent, NOT fixed: DirectX 11
TEXTURE slots" line.
**Rung-4 gates:** `validation/DxModernFeatures` arm "texture-slots" (real MonoGame WindowsDX, both
backends), the texture-slot rows of `validation/KniWinFormsDX` (real KNI WinForms.DX11, MGFX and
KNIFX) and `validation/CandidateDx -- d3dcompiler` + `compare_dx.py` (the DX corpus under the
public d3dcompiler option); all in the Windows gate script and the CI WARP job.

## 1. The symptom

SpriteBatch binds the sprite to texture slot `t0` right after `EffectPass.Apply()`. In
`BloomCombine.fx`

```hlsl
sampler BloomSampler : register(s0);
sampler BaseSampler : register(s1) = sampler_state { Texture = (BaseTexture); ... };
```

(`BaseTexture` never declared; ShadowDusk declares it ahead of the first top-level code) the SM4
rewrite produced `Texture2D BaseTexture; ... Texture2D BloomSampler_SDTexture; ...`, and the backend
numbered them in declaration order, `BaseTexture` on `t0`. The bloom pass sampled the base image as
bloom and the scene as base. `mgfxc` 3.8.4.1: `BloomSampler@t0s0 BaseTexture@t1s1`; ShadowDusk:
`BaseTexture@t0s1 BloomSampler@t1s0`. Measured again here: maxd 200 with the previous compiler in
real MonoGame WindowsDX and real KNI WinForms.DX11, maxd 0 after.

## 2. The rule fxc follows (measured, `mgfxc` 3.8.4.1 `/Profile:DirectX_11`)

On the 78 shapes of `tests/fixtures/golden/dx11-texture-slots/` (sources and goldens beside each
other) plus the 27 of `legacy-sampler-units/`, per compiled shader:

1. Every explicit `register(tN)` on a `t`-register declaration (any texture, `Buffer`,
   `StructuredBuffer`, `ByteAddressBuffer`, `tbuffer`) takes `tN` out of circulation, even when
   nothing reads it (`texture Unused : register(t0); sampler2D A;` puts `A` on `t1`). A MODERN
   resource read directly keeps its register.
2. Then each LEGACY sampler's texture, in the order the shader first reads it. That is
   **evaluation** order, not token order: `tex2D(A, tex2D(B, uv).xy)` is `B` first;
   `F(tex2D(A, uv))` where `F` reads `B` is `A` first (arguments before the callee's body);
   `tex2D(C) + tex2D(A, tex2D(B).xy)` is `C, B, A`; a read fxc's optimiser drops takes no slot. Each
   compiled shader has its own order. fxc gives every legacy sampler its own texture object, so a
   register on the `texture2D T : register(tN)` a `sampler_state` names only RESERVES `tN`; the
   sampler's texture takes the next free slot (`DeferredBasicEffect`'s `register(t0..t2)` land on
   `t3`/`t4`). A legacy sampler's `register(sN)` sets its sampler slot only.
3. Then each MODERN resource without a register, in declaration order, even when it is read first.

Each takes the lowest free slot. FNA (`fxc /T fx_2_0`) is different and needs nothing: D3D9 sampler
registers are allocated in declaration order, which ShadowDusk's FNA output already matches.

**`mgfxc` versus XNA.** KNI's EffectTest expects `DeferredBasicEffect` on `t0..t2` and
`OldSyntax_t1t0`'s Texture0 on `t0`; `mgfxc` (and KNI's own pipeline) give `t3/t4` and Texture1 on
`t0`. ShadowDusk follows `mgfxc` in both. An earlier revision of this fix honoured the register "as
XNA does"; it rendered differently from `mgfxc` whenever a texture arrived through SpriteBatch or
`GraphicsDevice.Textures[N]` (maxd 200 in real WindowsDX, found in review), and the XNA claim had
no evidence behind it in the repository, so it was dropped.

## 3. The fix

`ShadowDusk.Compiler/Internal/DxTextureSlotOrder.cs`, run after each DirectX 11 compile of an
effect that has a legacy sampler (`CompilationPipeline.ReorderDirectX11TextureSlots`):

- the shipped compile's RDEF gives the `t`-register resources that survived optimisation and their
  slots (`DxbcResourceUseOrder.TRegisterBindings`);
- **a vkd3d compile's instruction stream gives the evaluation order**
  (`DxbcResourceUseOrder.NamesInFirstReadOrder`, which decodes only the texture-reading opcodes).
  It is the shipped compile itself on the default backend and an extra vkd3d compile of the same
  source under `DxbcBackend.D3DCompiler`, because `d3dcompiler_47` schedules: it emits a read feeding
  `clip`/`discard` first and reorders independent reads (`tex2D(A) * tex2D(B) + tex2D(C) * 2` is C
  first). An earlier revision read the order off the shipped compile and broke `Dissolve`,
  `BlendShader`, `SamplerStatesFull` and others under that option (maxd 255, found in review).
  That vkd3d 2.1 keeps evaluation order is an assumption about the pinned native, guarded by the
  d3dcompiler rows of `DirectX11TextureSlotOrderTests` and `LegacySamplerUnitTests`;
- the preprocessed view gives the legacy textures and every explicit `t` register
  (`GlSamplerSlots.ExplicitTextureRegisters`, stage-qualified registers included);
- when the planned slots differ, the shader is recompiled with every `t` resource's top-level
  declaration carrying `: register(tN)` (comments, preprocessor lines, function parameters and
  bodies are masked; a declaration in a dead `#if` branch is pinned too, harmlessly). The pinned
  compile must land exactly on the plan.

A shader whose slots already agree is not recompiled, so its bytes cannot move. Rejected
alternatives (see `project_decisions.md`): a source-side call-graph walk (it would still need the
compiler's dead-code result), and compiling the legacy syntax natively in vkd3d (it would change
every legacy shader's bytes and parameter naming).

Every case the check cannot model or apply keeps the previous slots and warns `SD0230`: a
declaration through a macro or in a declarator list, a resource array, an unreadable RDEF or
instruction stream, a preprocessed view that cannot be built, a failed vkd3d order compile.

## 4. Which bytes changed

Every `.fx` under `tests/fixtures` was compiled for OpenGL, DirectX 11 (vkd3d and d3dcompiler),
DirectX 12, Vulkan, FNA, KNIFX DirectX and KNIFX OpenGL with `origin/main` (`eff61497`) and with the
fix, output hashes and warning lists compared. Only DirectX 11 (both backends) and KNIFX DirectX
moved, the same files on each, and only:

- the new `dx11-texture-slots` shapes (now `mgfxc`'s tables, `DirectX11TextureSlotOrderTests`);
- the 11 `legacy-sampler-units` shapes listed in `LegacySamplerUnitTests.DirectX11TextureOrderDivergence`
  before (the list is now empty);
- three corpus fixtures, each now exactly `mgfxc`'s table: `ForwardLighting.fx`
  (`s0@t0 _normalMap@t1` became `_normalMap@t0s1 s0@t1s0`), `SamplerPairMirror.fx`
  (`PointTexture@t0s1 LinearTexture@t1s0`) and `SamplerRegisterOrder.fx`
  (`MaskSampler@t0s1 SpriteSampler@t1s0`).

None of these is in a byte-identity manifest, which is why every manifest is unchanged. No warning
list changed.

## 5. Still open

- **One texture behind two legacy samplers, or behind a legacy sampler and a direct read**
  (`TwoSamplersShareOneTexture`, `S13_ModernSampledAndStateNamed`): fxc gives each legacy sampler its
  own texture slot and `mgfxc` writes one record per slot; the rewrite has one `Texture2D`, so one
  record. Pinned as a known divergence.
- **vkd3d keeps reads fxc folds away** (`D01`..`D10`: `x * 0`, `lerp(a, b, 1)`, `a - a`,
  `dot(x, 0)`, `saturate(-1 - abs(x))`): the dead texture keeps a record and a slot on vkd3d only.
- **Declarator lists** (`sampler2D A, B;` sampler slots, `Texture2D TA, TB;` not compiling) and
  **resource arrays** beside a legacy sampler (`SD0230`), all recorded in
  `docs/validation-matrix.md` §7.
- **DirectX 12** keeps declaration order (self-consistent). No reference table was established:
  the test reader found no sampler records in `mgfxc` 3.8.5's `DirectX_12` builds of these shapes,
  modern-only shapes included, so that reading is itself unverified.
- **KNIFX DirectX at `ps_4_0_level_9_1` does not load on a HiDef KNI device** (SM 4.0 recorded;
  fixed by PR #430). The KNI gate renders the KNIFX rows at `GraphicsProfile.FL10_0`.
