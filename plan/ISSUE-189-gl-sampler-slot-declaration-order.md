# ISSUE-189 — OpenGL sampler slots followed first-use order, not declaration order

**Status:** ✅ Fixed and render-proved (2026-08-02). Two residuals recorded in §6.
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

## 6. Residuals (open, measured)

1. **Sparse/offset explicit registers.** `register(s2)`/`(s3)` with no `s0`/`s1` declared still
   compacts to units 0/1 where mgfxc emits 2/3. DXC's SPIR-V `Binding` namespace is flat and
   auto-allocated, so the explicit register number is **not** recoverable from the SPIR-V; closing
   this needs the clause threaded out of `FxPreParser` (§2). Contiguous-from-zero sets — every
   SpriteBatch custom effect, and the entire corpus — are exact.
2. **DirectX 11 sampler slot.** DX11 also ignores `register(sN)` (we emit 0/1 where mgfxc emits
   the declared 2/3). Deliberately unchanged: `fxc` itself auto-assigns DX *texture* registers by
   first use (measured — mgfxc puts the second-declared sampler's texture on `t0`), so honouring
   the annotation there would move away from the reference compiler. Vulkan binds through a
   descriptor layout rather than a slot number. Recorded in `project_decisions.md`.

## 7. Evidence

- `validation/SamplerRegisterOrderGl`: **maxd 255 across all 4096 px before, maxd 0 after**,
  against a real mgfxc golden rendered in the same scene, with the golden's own render asserted as
  a control so both builds being wrong the same way cannot pass. Committed RED first (`115ecf9`),
  then green.
- Full suite **3123/3123**. Windows render gates **15/15**. All eight in-process GL gates green.
- **No existing output moved:** all 48 OpenGL entries of the cross-host byte-identity manifest are
  unchanged; golden corpus untouched.
- Three integration tests that had encoded the old first-use rule were rewritten to assert
  declaration order (`ReviewRegressionTests`).
