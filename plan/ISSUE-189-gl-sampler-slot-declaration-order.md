# ISSUE-189 — OpenGL sampler slots followed first-use order, not declaration order

**Status:** ✅ Fixed and render-proved (2026-08-02), **both halves** — the allocation ORDER and
the explicit register VALUE. One residual (DirectX 11) recorded in §6.
**Reported by:** Apostolique (Apos.Shapes), GitHub issue
[#189](https://github.com/kaltinril/ShadowDusk/issues/189), against ShadowDusk CLI 0.14.2.
**Fixed in:** `fix/code-scanning-and-issue-189`.
**Rung-4 gate:** [`validation/SamplerRegisterOrderGl`](../validation/SamplerRegisterOrderGl).

---

## 1. The report

> The OpenGL profile hands out GLSL texture units in the order the pixel shader first samples
> from each sampler. The `register(sN)` annotations are ignored. When the sampling order doesn't
> match the registers, the shader reads a different texture than the one bound at that slot, and
> nothing warns.

Found in Apos.Shapes: an arc-length lookup table on `register(s3)`, declared after a blue-noise
sampler on `register(s2)`, but sampled *earlier* — the two came out swapped and the dashes
rendered as the noise tile.

## 2. Confirmed, and worse than reported

Reproduced byte-for-byte against the pinned oracle (`dotnet-mgcb` 3.8.4.1 `mgfxc`), on the
reporter's own repro:

| | `mgfxc` | ShadowDusk (before) |
|---|---|---|
| `SamplerA : register(s0)` (sampled 2nd) | `ps_s0`, unit 0 | `ps_s1`, unit 1 |
| `SamplerB : register(s1)` (sampled 1st) | `ps_s1`, unit 1 | `ps_s0`, unit 0 |

The emitted GLSL matched the report verbatim:
`ps_oC0 = texture2D(ps_s1, …*999.0) + texture2D(ps_s0, …*111.0)`.

Two findings the report did not contain:

1. **It is not only about `register(sN)`.** With every annotation stripped, `mgfxc` *still* maps
   the first-**declared** sampler to `ps_s0`. **fxc allocates in declaration order**, period. So
   any GL shader with 2+ samplers whose declaration order differs from its sampling order was
   affected, annotated or not.
2. **`register(sN)` was discarded entirely, not merely reordered.** With `register(s2)`/`(s3)`,
   `mgfxc` emits `ps_s2`/`ps_s3` at units 2 and 3; ShadowDusk emitted `ps_s0`/`ps_s1` at units 0
   and 1. Root cause: [`FxPreParser.cs:652`](../src/ShadowDusk.HLSL/FxPreParser.cs#L652) rewrites
   `sampler A : register(s2);` to `Texture2D A_SDTexture; SamplerState A;`, dropping the register
   clause before DXC ever sees it.

## 3. Why it actually broke a render (and why nothing caught it)

`EffectPass.SetShaderSamplers` does, unconditionally:

```csharp
textures[sampler.textureSlot] = _effect.Parameters[sampler.parameter].Data as Texture;
```

So the record's slot fully determines binding. Under **parameter binding** ShadowDusk's table was
internally consistent — each record named the uniform its own GLSL declared and pointed at the
texture that uniform read — so it rendered *correctly*. That is why **every one of the 30-plus
existing GL render gates stayed green**: all of them bind through `effect.Parameters[...]`.

What makes the numbering observable needs no manual slot binding at all:
`SpriteBatcher.FlushVertexArray` assigns `_device.Textures[0] = texture` **after** `pass.Apply()`.
Unit 0 belongs to `SpriteBatch`. So the sampler ShadowDusk placed on unit 0 read the **sprite**,
and the one it placed on unit 1 read whatever nothing had bound. `sampler s : register(s0)`
meaning "the SpriteBatch texture" is the most common custom-effect idiom in MonoGame.

**The most damaging part of the gap was a documented false claim.**
`validation/SamplerPairsGl` arm B was described — in `docs/validation-matrix.md`, in
`project_facts.md`, and in the driver's own output — as proving that our first-use numbering was
"behaviorally equivalent" to mgfxc's. It could not: that arm gives its two textures **identical
pixels** by design, so it isolates per-pair sampler *state* and is structurally blind to which
unit each pair lands on. All three statements are retracted.

## 4. The rule, measured

`mgfxc`'s allocation, read off committed goldens rather than inferred:

| Fixture | Declaration order | Sampling order | `mgfxc` `ps_s0` |
|---|---|---|---|
| `SharedSamplerPair.fx` | DiffuseMap, Lightmap | same | DiffuseMap |
| `SamplerPairMirror.fx` | LinearTexture, PointTexture | **reversed** | LinearTexture |
| `SamplerRegisterOrder.fx` | SpriteSampler(s0), MaskSampler(s1) | **reversed** | SpriteSampler |
| two-sampler probe, annotations stripped | A, B | **reversed** | A |

Declaration order in every case.

## 5. The fix

- `CombinedSamplerPair` gained `TextureDeclarationIndex`: the rank of the pair's texture among the
  stage's sampled images, ordered by the raw SPIR-V `Binding` decoration (which DXC allocates in
  declaration order). Recovered the same way `SpirvReflectionParser` recovers `t#`, so it stays
  host-independent and CLI/WASM bytes still agree.
- `SpirvCombinedSamplerPairs.ResolveSlots` is the **single** definition of the rule. Two callers
  deriving it independently is how a table and the GLSL it describes drift apart, so both go
  through it: `MonoGameGlslRewriter` (naming uniforms `ps_s{slot}`) and the `.mgfx` sampler table.
- Records are emitted **sorted by slot**, matching mgfxc's golden layout.
- Where two pairs share one texture (one texture, two `SamplerState`s) the declaration indices
  collide. No mgfxc golden exists for that shape, so `ResolveSlots` falls back to positional
  numbering for the whole stage — the previously shipping, A7-render-proved behaviour — rather
  than inventing an answer.

## 6. The sparse/offset half, and the one residual left

### 6.1 Closed: the fxc allocator is now modelled, not special-cased

The first pass at this honoured `register(sN)` on the **legacy** form only, on the reading that
`mgfxc` "ignores modern registers on OpenGL". **That reading was drawn from one measurement and was
wrong.** Widening the sweep to ten shapes showed a modern `SamplerState : register(sN)` is
**reserved, not ignored** — the pair is allocated *around* it:

| Source | `mgfxc` |
|---|---|
| no explicit register, 2 textures | `ps_s0`, `ps_s1` |
| 1 texture + `S : register(s0)` | **`ps_s1`** (s0 occupied) |
| 2 textures + `S : register(s1)` | `ps_s0`, **`ps_s2`** (s1 skipped, not shifted) |
| 3 textures + `S : register(s1)` | `ps_s0`, `ps_s2`, `ps_s3` |
| 2 textures + `P : register(s0)`, `Q : register(s1)` | `ps_s2`, `ps_s3` |
| 2 textures + `P : register(s2)`, `Q : register(s3)` | `ps_s0`, `ps_s1` |
| legacy `sampler S : register(s5)` | `ps_s5` |
| legacy `MaskA : s2`, `MaskB : s3` | `ps_s2`, `ps_s3` |

**One rule reproduces all of them.** In **texture-declaration order**: a pair whose sampler declared
an explicit register takes it; every other pair takes the **lowest register neither already taken
nor reserved**.

And that is *why* the legacy form stops looking like an arbitrary special case. Compiling for
OpenGL means compiling at `ps_3_0`, where a texture and a sampler are ONE object in ONE register
namespace:

- a legacy `sampler X : register(sN)` **is** that combined object → it lands on `N`;
- a modern `SamplerState` is a sampler-*only* object that still occupies its register → the
  combined samplers fxc synthesizes are allocated around it.

Same allocator, different starting facts. `FxPreParser` supplies both inputs
(`ExplicitGlSamplerSlots` for the legacy assignment, `ReservedGlSamplerSlots` for the modern
reservation), recording what it already parsed rather than re-emitting it, so DXC still sees
identical source and the DirectX/DX12/Vulkan/FNA bytes do not move.

All ten shapes now match `mgfxc` exactly.

### 6.2 Found while fixing 6.1: the declaration rank was taken from the wrong thing

`TextureDeclarationIndex` originally ranked by the SPIR-V `Binding` decoration. That equals
declaration order only while DXC auto-allocates; once the source is annotated it equals *register*
order. So `Texture2D TexA : register(t3); Texture2D TexB : register(t2);` put **TexB** on unit 0
where `mgfxc` puts **TexA**. This was **pre-existing** — the original #189 fix inherited it — and was
caught by the deliberately-adversarial "modern registers must not be assigned" test rather than by
any render. The rank now comes from module order (the order `OpVariable` appears in the SPIR-V),
which is HLSL declaration order.

### 6.3 Residual, and why "just fix DirectX too" would be a BUG, not an improvement

DX11 emits sampler slots 0/1 where `mgfxc` emits the declared 2/3. `SamplerRegisterSparse.fx`
makes this visible as a new `XX` cell in the Phase 41 structural-divergence matrix
(`SamplerRegisterSparse [DirectX_11]`, the existing "sampler slot / baked-state delta" class).
That cell is **expected and correct**, and the reasoning is worth keeping because the obvious
"fix" is actively harmful:

- On DX11 the record's `samplerSlot` is what MonoGame assigns baked state to
  (`samplerStates[sampler.samplerSlot] = sampler.state`). It must therefore match the sampler
  register **the shipped bytecode actually reads**, not the one the source text asked for.
- `FxPreParser` drops the register before DXC sees it, so our DXBC reads sampler register 0/1.
  Writing 2/3 into the record while the bytecode samples register 0 would send the baked state to
  a register the shader never reads — a real regression, traded for a cosmetic match.
- Making the bytecode itself use 2/3 means re-emitting the clause into the rewritten HLSL, which
  changes what DXC compiles and moves the DirectX/DX12/Vulkan/FNA bytes for a defect none of them
  have.

So DX11's records are internally consistent with DX11's bytecode, which is the property that
actually matters. The separate DX divergence — `fxc` auto-assigns DX **texture** registers by
first use where we use declaration order — is not closable either, for the same reason: the
allocation lives inside each compiler's own bytecode. Vulkan binds through a descriptor layout
rather than a slot number and is unaffected. Recorded in `project_decisions.md`.

## 7. Evidence

- `validation/SamplerRegisterOrderGl`, two arms, each committed RED first then green:
  - **order** (`SamplerRegisterOrder.fx`, `115ecf9`) — maxd **255 across all 4096 px** before, **0**
    after.
  - **sparse** (`SamplerRegisterSparse.fx`, `d9d8a54`) — candidate `(0,255,0)` vs mgfxc's
    `(255,255,0)`, maxd **255 / 4096 px** before, **0** after; records go from `ps_s0`/`ps_s1` at
    units 0/1 to `ps_s2`/`ps_s3` at units 2/3, structurally identical to the golden.
  Each arm asserts the golden's OWN render as a control, so both builds being wrong in the same
  direction cannot pass.
- Full suite **3139/3139** (+7 new: 5 pre-parser unit tests, 2 end-to-end). Windows render gates
  **15/15**. All eight in-process GL gates green.
- The reporter's real vendored shader `apos-shapes-aa.fx` (explicit `s0`, an unannotated sampler,
  explicit `s2`) is **byte-identical to the mgfxc golden** through both fixes, and was measured
  identical before them too - it never exhibited the bug, because its first-use order happens to
  coincide with its declaration order.
- **No existing output moved:** all 48 OpenGL entries of the cross-host byte-identity manifest are
  unchanged; golden corpus untouched.
- Three integration tests that had encoded the old first-use rule were rewritten to assert
  declaration order (`ReviewRegressionTests`).
