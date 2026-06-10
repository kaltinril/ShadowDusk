## Deliverable

**c:\git\ShadowDusk\docs\fx2-binary-format.md** — complete byte-level spec of the fx_2_0 (0xFEFF0901) effects binary exactly as FNA's MojoShader parses it, written as the sole implementation spec for Phase 39's `Fx2EffectWriter`. 16 sections: header (+optional XNA4 wrapper), strings, counts header, parameter/typedef/value records per class (numeric, object, sampler-with-embedded-states, struct), technique/pass/state records, full renderStateType (0–102, 146/147) and samplerStateType (164-based on disk, &~0xA0 masked) tables with FNA-honored markers, small/large object sections with the exact padding formula, shader-object compilation + CTAB name-binding semantics, complete CTAB comment layout, a byte-by-byte worked example (float4 + texture + sampler + 1 technique/1 pass/PixelShader=ps_2_0, with absolute offsets for all 3 file regions), a strictness section (12 hard requirements vs 8 ignored fields), and 6 golden-flagged ambiguities.

## Sources (all pinned)

- **Normative**: icculus/mojoshader @ `6333f74dbd5644789a63e903816441b16c1e8b60` (2026-04-28). FNA-XNA/MojoShader **redirects here** (fork merged upstream; this is what FNA ships via FNA3D). `mojoshader_effects.h` does not exist; effects enums live in `mojoshader.h`.
- **Cross-checks**: vkd3d `libs/vkd3d-shader/fx.c` fx_2_0 writer (confirms header/zero-pool-entry/object-IDs-from-1/string format/state ID numbers; its state-assignment + shader-blob paths are unimplemented stubs, so state/large-object layout rests on MojoShader + Wine), Wine `dlls/d3dx9_36/effect.c` reader (names every field MojoShader ignores; agrees on all counts/orders), FNA `Effect.cs`/`EffectParameter.cs` (runtime constraints).

## Trickiest layout rules (for the implementer)

1. **One data pool, base-relative offsets**: everything offset-like is relative to file offset 8; pool first, structured stream after `pool_size`. Write a zeroed dword at pool offset 0 so offset 0 = null string (vkd3d does this; gives free "no semantic/name" encoding).
2. **Large objects carry no object index** — they back-reference `technique/pass(or param)/state_index`, and the parser dereferences the *state's value blob* to find the object. The state must already exist with an object-index value. `technique == 0xFFFFFFFF` means "parameter sampler state" and the parameter must be sampler-typed.
3. **Sampler parameters embed their state list as their value blob** (count + 4-dword state records with 0xA4-based ops); the Texture state's value is a *separate* object whose data (via a usage=1 large record) is the texture parameter's NUL-terminated *name*; MojoShader retypes that object to the sampler's type after parsing.
4. **Padding**: object data advances by `4*ceil(len/4)` (0 for len 0); strings are `u32 len(incl NUL) + bytes`, pool-padded to 4.
5. **CTAB is effectively mandatory** and every constant name must exactly match a parameter name (assert/UB otherwise); textures must precede samplers in parameter order (FNA assumption).
6. **Object index 0 is reserved**; `object_count` includes it.
7. **Emit only VertexShader(146)/PixelShader(147) pass states** and the FNA-safe sampler-state subset — FNA throws on everything else.
8. **Matrices**: file row = one constant register; square matrices safe; non-square dims order is the one genuine reader/writer conflict (finding F1) — forbid non-square matrix params in the writer until an fxc golden resolves it.

## Ambiguities needing an fxc golden (full list in spec §15)

F1 non-square matrix dims order (major, blocks only non-square support); F2 matrix default-value majority (minor, defaults only); F3 Texture-state typedef type 5 vs dimensioned (cosmetic); F4 VertexShader=NULL encoding (writer omits the state instead); F5 element_index value for pass-state records (ignored by both FNA-path readers); F6 counts-header shader-count formula (ignored).

Scratch downloads were removed; the only working-tree change is the new doc (`git status`: `?? docs/fx2-binary-format.md`).
