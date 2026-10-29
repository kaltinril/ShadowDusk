# DirectX 11 legacy sampler textures followed declaration order, not fxc's first-read order

**Status:** ✅ **Fixed and render-proved, 2026-10-28** (branch `fix/dx11-texture-slot-order`).
**Reported by:** an adversarial reviewer, measured in real KNI 4.2.9001 WinForms.DX11 against a
real `mgfxc` 3.8.4.1 build: the XNA/MonoGame `BloomCombine.fx` shape rendered maxd 188 against the
reference, `texture Tex2;` declared above a `register(s0)` sprite sampler maxd 129 to 244.
**Supersedes:** the "DirectX is NOT being fixed" half of
[ISSUE-189](ISSUE-189-gl-sampler-slot-declaration-order.md) §6.3 for TEXTURE slots ("revisit only
if one is reported": it was), and the `project_facts.md` "Still divergent, NOT fixed: DirectX 11
TEXTURE slots" line.
**Rung-4 gates:** `validation/DxModernFeatures` arm "texture-slots" (real MonoGame WindowsDX, in the
Windows gate script and the CI WARP job) and the texture-slot rows of `validation/KniWinFormsDX`
(real KNI WinForms.DX11, same two places).

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
`BaseTexture@t0s1 BloomSampler@t1s0`. Measured again here in real MonoGame WindowsDX: maxd 200 with
the previous compiler, maxd 0 after.

## 2. The rule fxc follows (measured, `mgfxc` 3.8.4.1 `/Profile:DirectX_11`)

On the 39 shapes of `tests/fixtures/golden/dx11-texture-slots/` (sources and goldens beside each
other) plus the 27 of `legacy-sampler-units/`, per compiled shader:

1. A texture with an explicit `register(tN)` keeps `tN`, and every explicit texture register is
   out of circulation even when nothing reads that texture (`texture Unused : register(t0);
   sampler2D A;` puts `A` on `t1`).
2. Then each LEGACY sampler's texture, in the order the shader first reads it. That is
   **evaluation** order, not token order: `tex2D(A, tex2D(B, uv).xy)` is `B` first;
   `F(tex2D(A, uv))` where `F` reads `B` is `A` first (arguments before the callee's body);
   `tex2D(C) + tex2D(A, tex2D(B).xy)` is `C, B, A`; a read the optimiser drops takes no slot
   (`d = tex2D(A, uv); d = 1;` leaves `B` on `t0`). Each compiled shader has its own order (two
   passes reading `A, B` and `B, A` get opposite tables). A legacy sampler's own `register(sN)` sets
   its sampler slot only.
3. Then each MODERN `Texture2D` without a register, in declaration order, even when it is read
   first.

Each takes the lowest free slot. FNA (`fxc /T fx_2_0`) is different and needs nothing: D3D9 sampler
registers are allocated in declaration order, which ShadowDusk's FNA output already matches.

### Where `mgfxc` deviates from XNA, and what ShadowDusk does

A `texture2D T : register(tN)` that a `sampler_state` names is not bound at `tN` by `mgfxc`: fxc
keeps `tN` for the (unread) texture object and puts the sampler's own texture on the next free slot
(`DeferredBasicEffect`'s `register(t0..t2)` land on `t3`/`t4`; `texture2D Diffuse : register(t1)`
and `Spec : register(t0)` land on `t2`/`t3`). XNA, and KNI's own effect tests, expect `tN`.
ShadowDusk honours the author's register (it used to drop it and fall back to declaration order,
which only coincided for `DeferredBasicEffect`). Through their parameters both layouts render the
same picture (`ExplicitTextureRegistersReversed` in both gates). KNI's `OldSyntax_t1t0`
expectation (Texture0 on `t0`) is the other XNA/`mgfxc` difference: there ShadowDusk follows `mgfxc`
and KNI's own pipeline (Texture1 on `t0`), since no explicit register is involved.

## 3. The fix

`ShadowDusk.Compiler/Internal/DxTextureSlotOrder.cs`, run after each DirectX 11 compile of an
effect that has a legacy sampler (`CompilationPipeline.ReorderDirectX11TextureSlots`):

- the first compile's RDEF gives the textures that survived optimisation and their slots;
- its own `SHDR`/`SHEX` instruction stream gives the first-read order
  (`ShadowDusk.Core/Reflection/DxbcResourceUseOrder.cs` decodes only the texture-reading opcodes,
  so a declaration's payload is never read as an operand);
- the preprocessed view gives the legacy textures and every explicit texture register
  (`GlSamplerSlots.ExplicitTextureRegisters`, new);
- when the planned slots differ, the shader is recompiled with every texture's declaration
  carrying `: register(tN)`. The pinned compile must land exactly on the plan.

A shader whose slots already agree is not recompiled, so its bytes cannot move. Rejected
alternatives (see `project_decisions.md`): a source-side call-graph walk (it would still need the
compiler's dead-code result), and compiling the legacy syntax natively in vkd3d (it would change
every legacy shader's bytes and parameter naming).

A texture that cannot be pinned (declared through a macro, in a declarator list such as
`Texture2D A, B;`, or already on another register) keeps the previous slots and warns `SD0230`;
the compile does not fail.

## 4. Which bytes changed

Every `.fx` under `tests/fixtures` was compiled for OpenGL, DirectX 11, DirectX 12, Vulkan, FNA,
KNIFX DirectX and KNIFX OpenGL with `origin/main` (`eff61497`) and with the fix, output hashes and
warning lists compared. Only DirectX 11 and KNIFX DirectX moved, and only for:

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

- **Two legacy samplers sharing one texture** (`TwoSamplersShareOneTexture`): fxc gives each its own
  texture slot and `mgfxc` writes two `Tex` records; the rewrite has one `Texture2D Tex`, so one
  record, and the second sampler's baked state is never applied. Pinned as a known divergence.
- **DirectX 12** keeps declaration order (self-consistent). No reference table was established:
  the test reader found no sampler records in `mgfxc` 3.8.5's `DirectX_12` builds of these shapes,
  modern-only shapes included, so that reading is itself unverified.
- **KNIFX DirectX cannot be rendered in the KNI gate**: a KNIFX DirectX effect at
  `ps_4_0_level_9_1` throws `Shader model 4.0 is not supported by the current graphics profile
  'HiDef'` in KNI 4.2.9001 WinForms.DX11, on `main` too (all nine KNIFX rows of
  `validation/InitializerDefaultsKniDx` fail there). Unrelated to the slots, fixed by PR #430
  (`_level_9_1` records SM 2.0, `_level_9_3` 3.0), recorded in `docs/validation-matrix.md` §7. The KNIFX body carries the same DXBC and sampler table as MGFX.
