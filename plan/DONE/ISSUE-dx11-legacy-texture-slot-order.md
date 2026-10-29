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

On the 304 shapes of `tests/fixtures/golden/dx11-texture-slots/` (sources and goldens beside each
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
- **the preprocessed SOURCE gives the evaluation order** (`HlslReadOrder`): token order, a read
  after its own arguments, a user function's body after its arguments. Two earlier revisions read
  the order off a compiled shader and both were wrong, found in review: `d3dcompiler_47`'s stream
  is scheduled (a read feeding `clip`/`discard` first, independent reads reordered; `Dissolve`,
  `BlendShader`, `SamplerStatesFull` broke under that option, maxd 255), and vkd3d 2.1's parser
  swaps the operands of `>` and `<=` (`LESS(b, a)`, `GEQUAL(b, a)` in `hlsl.y`), emitting the
  right-hand read first (maxd 128 on both backends). fxc numbers after inlining, unrolling and
  folding every condition it can evaluate, but before algebraic dead-code removal (a dead store
  still numbers its texture). Which conditions it folds is not predictable from the syntax: a
  third review measured it folding a call that returns a constant, a local set from one, a
  default or `out` argument, `k > 0 && false`, one component of `float2(k, 0)`,
  `saturate(-1 - abs(k)) > 0`, a loop that never runs and a `return` under such a condition,
  and NOT folding a plain uniform or input, `k * 0 > 1`, `k - k > 0` or a `static` initialised
  from a uniform (goldens `K01`..`K38`). A fourth review broke the "certainly run-time"
  exception the walk then had for uniforms and unmodified inputs: fxc also folds a uniform the
  shader assigns or passes `out`, a local declared in a comma list that shadows it, a
  parenthesized write to an input, an `unsigned int` compared with `0` and a loop bound that
  truncates (goldens `P*`, `Q*`, `R*`). So **the walk never evaluates a condition.** What it
  models is only what was measured and depends on no value: straight-line evaluation order; a
  read after an unconditional `return`, `break` or `continue` in the same block is removed (a
  bare block ending in one included; `P14`..`P16`, `Q11`, `Q16`, `T08`, `T09`), while `discard`
  removes nothing (`P25`, `T06`); a `switch` numbers the arm holding `default` after every
  `case` arm, the others in token order, wherever `default` is written (`P20`, `Q02`, `T01`..`T05`;
  fxc rejects an arm that falls through, X3533, so the walk refuses one); both arms of `?:` are
  read whatever the condition. Every read under an `if`/`else`, in a `switch` arm, in a loop not
  certainly entered (only a `do` loop, or a `for` whose counter is a signed `int` from an integer
  literal compared `< <= > >= !=` with an integer literal or a `static const int` from one, the
  first test holding in exact integer arithmetic), in a later iteration of a loop (walked again),
  or after a `return`/`break`/`continue` that may not run sits under a GUARD. The guards are taken
  as independent (more orders, never fewer) and a read is kept only if all its guards are; the
  walk REFUSES with `SD0230` when some choice of guards keeps a read of one texture while
  dropping every earlier read of another that is read again later. Reads under the same guards
  share their fate, so the reads after one early `return` stay or go together (`T10`, `T11`).
  The walk also refuses a read through a sampler parameter and a bound legacy texture it never
  saw read (`K13`: placing it last by guess gave `A` on `t0` where `mgfxc` has `B`);
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
a preprocessed view that cannot be built, an order a conditional read could change, an
overloaded function, a texture passed as a value, a sampler passed as a function argument, a
bound texture whose read was not found, or two resources under one RDEF name.

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

None of these is in a byte-identity manifest, which is why every manifest is unchanged. One warning
list changed: the real Apos.Shapes shader (`tests/fixtures/issues/202/apos-shapes.fx`) now warns
`SD0230` on both backends with unchanged bytes (§5).

## 5. Still open

- **One texture behind two legacy samplers, or behind a legacy sampler and a direct read**
  (`TwoSamplersShareOneTexture`, `S13_ModernSampledAndStateNamed`): fxc gives each legacy sampler its
  own texture slot and `mgfxc` writes one record per slot; the rewrite has one `Texture2D`, so one
  record. Pinned as a known divergence.
- **vkd3d keeps reads fxc folds away** (`D01`..`D10`: `x * 0`, `lerp(a, b, 1)`, `a - a`,
  `dot(x, 0)`, `saturate(-1 - abs(x))`): the dead texture keeps a record and a slot on vkd3d only.
- **An order a condition could change**: fxc folds what it can and the walk refuses with
  `SD0230`. **Known false refusals**, loud, never silent: when fxc does NOT fold the condition (a
  uniform, an input, `k * 0 > 1`, a `static` from a uniform), the warning fires and the slots stay
  in declaration order, which is not `mgfxc`'s when the conditional read came first: 26 of the 304
  shapes (`K16`, `K17`, `K28`, `K33`, `K38`, `P01`..`P04`, `Q08`, ...), listed in
  `DirectX11TextureSlotOrderTests.FalseSD0230`. The class needs a texture whose first read is
  under a guard, read again later, with another texture's first read in between. The real
  Apos.Shapes shader is one: its blur branch reads the dither noise and returns ahead of the ramp
  reads, so were that branch folded away the ramp would come first; it warns, and its
  declaration order is `mgfxc`'s table, so its slots are right (asserted by
  `AposShapes_KeepsMgfxcsTable_AndWarnsTheFalseSD0230`). Any refusal, correct ones included,
  falls back to declaration order, so it differs from `mgfxc` whenever `mgfxc`'s order is not
  declaration order (`K20`, a folded `return`; `D12`, iteration order).
- **Shapes the walk does not model, each once numbered silently wrong** (the `U*` shapes, final
  review, measured): fxc DROPS a bare read or constructor statement before it numbers
  (`tex2D(B, uv);`, in parentheses, a block, a loop, a void helper, a `for` init,
  `float4(tex2D(B, uv));`, `Tb.Sample(SB, uv);`) but keeps `.r`, `* 2`, a cast, `max(...)`,
  `clip(...)`, `sincos(...)` and a user function's call; the walk refuses every bare call statement
  that reads other than `clip`, `sincos` and a user function's (`U50`..`U56` are false
  refusals). A read or user-function call in a global initializer is numbered before the entry
  point's, even for an unused global (`U70`..`U74`), and a function defined in a struct or
  namespace is never walked (`U80`..`U83`, d3dcompiler only: vkd3d rejects the syntax): both
  refuse. A legacy sampler's texture the shader also uses through a method of its own
  (`GetDimensions`, `Load`, even `Sample`) gets a second `mgfxc` record (`A@t0 Tb@t1 Tb@t2`,
  the `S13` class) and refuses, read off the original source (`GlSamplerSlots.ModernMethodTextures`)
  since the rewrite's `Tb.Sample(SB, uv)` looks the same as the author's (`U60`..`U67`).
- **A legacy sampler passed as a function argument** (`K13_SamplerParam`) compiles natively: its
  sampler slot is `s0` where `mgfxc` has `s1` (pre-existing), and its texture slots warn
  `SD0230`. Passed AND read directly (`F(B, uv) + tex2D(B, uv)`) it does not compile on vkd3d
  (`E5005`) and gets two texture records under d3dcompiler (pre-existing rewrite gap, not pinned).
- **Legacy samplers a macro declares** (`B02_MacroTexture`) are compiled natively by the backend:
  texture slots match, the parameter is named after the sampler and the sampler slots differ
  (pre-existing, `project_facts.md`). A legacy sampler in an `#include`d header compiles natively
  the same way and joins under the sampler's name (no false `SD0230`).
- **Declarator lists** (`sampler2D A, B;` sampler slots, `Texture2D TA, TB;` not compiling) and
  **resource arrays** beside a legacy sampler (`SD0230`), all recorded in
  `docs/validation-matrix.md` §7.
- **DirectX 12** keeps declaration order (self-consistent). No reference table was established:
  the test reader found no sampler records in `mgfxc` 3.8.5's `DirectX_12` builds of these shapes,
  modern-only shapes included, so that reading is itself unverified.
- **KNIFX DirectX at `ps_4_0_level_9_1` does not load on a HiDef KNI device** (SM 4.0 recorded;
  fixed by PR #430). The KNI gate renders the KNIFX rows at `GraphicsProfile.FL10_0`.
