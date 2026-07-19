# Phase 47 Appendix — ShaderToy converter subset gaps (real-world triage)

**Track:** Reach / additive frontend (the `ShadowDusk.ShaderToy` GLSL → `.fx` converter).
**Status:** Findings recorded 2026-06-28. One **ACTIONABLE** fix below; the other three are
out-of-subset by design and already pinned by minimal `reject/` corpus fixtures.

This appendix records a triage of four real-world ShaderToy shaders that were collected as
candidate converter inputs but **deliberately never added to the corpus** because each fails. The
point of writing it down: three of the four are *correctly* rejected (fundamental limits of the
faithful float-based SM3 subset / the single-pass 2D harness, or undefined behavior in the source),
but **one is a real, bounded enhancement the converter could support** — and without this note that
distinction is invisible to the next agent.

Each shader was run end-to-end through the real CLI path
(`ShadowDuskCLI <shader>.glsl out.mgfx /Profile:OpenGL`), which routes GLSL through
`ShaderToyConverter.Convert` into the unchanged compile pipeline (see
[cli-shadertoy-input.md](cli-shadertoy-input.md)).

---

## ✅ ACTIONABLE — the one fixable gap: initializer-sized arrays

**This is the only one of the four that the converter could faithfully support. A future agent
should pick this up.**

### Symptom

A `const`/local array declared with empty brackets **but an aggregate initializer** is rejected as
"runtime-sized," even though its length is statically knowable from the initializer:

```glsl
// MINIMAL REPRO (self-contained; the size is unambiguously 2)
const float stepLength[] = float[](0.9, 0.25);
```

```
shader.glsl(173,23-23): error SD0010: Unsized / runtime-sized arrays ('type name[]') are outside
the supported subset (only fixed-size arrays 'type name[N]' with a constant integer N are supported).
```

(Real-world instance: `batteredalienplanet.glsl`, ShaderToy `wsjBD3`, line 173.)

### Why it is faithfully fixable (not a fundamental limit)

GLSL permits omitting the array size when an aggregate initializer is present —
`float a[] = float[](x, y, z)` is exactly equivalent to `float a[3] = float[3](x, y, z)`. The length
is a compile-time constant (the initializer's element count), so it maps cleanly to HLSL
`float a[3] = {x, y, z};`. There is no runtime/unknown size here — the converter is simply not
looking at the initializer when it sees `[]`.

This is distinct from the genuinely-unsized form `float data[];` (no initializer), which has no
knowable length and **must** stay a loud reject. That truly-unsized form is the one pinned by
`tests/ShadowDusk.ShaderToy.Tests/corpus/reject/unsized_array.glsl` — do **not** make that one pass.

### Where the fix goes

[`src/ShadowDusk.ShaderToy/Parser.cs`](../../../src/ShadowDusk.ShaderToy/Parser.cs) →
`ParseArraySuffix()` (the `[` … `]` handling, ~line 440). Today it does:

```csharp
Token open = Expect(TokenKind.LBracket, "'['");
if (Check(TokenKind.RBracket))        // saw "[]"
{
    throw Reject("Unsized / runtime-sized arrays ... ", open);   // <-- fires unconditionally
}
```

The fix is to defer that reject when the declarator carries an aggregate initializer, and infer the
size from it. Sketch (final shape is the implementer's call):

1. In the declaration parse path, when an array declarator is `[]` (empty), do **not** reject
   immediately — record "size to be inferred."
2. Parse the initializer. If it is a GLSL array constructor `T[](e0, e1, …)` (or a brace
   initializer `{ e0, e1, … }`), set `N` = element count and proceed as a fixed-size `T name[N]`.
3. If `[]` has **no** initializer (the `float data[];` case), keep the existing loud reject.
4. If the array constructor's explicit size and the bracket size ever disagree
   (`float a[3] = float[2](…)`), reject loudly — never silently pick one.

Note `float[](…)` is the GLSL array-constructor expression; confirm the lexer/parser already tokenize
`identifier '[' ']' '(' … ')'` as a constructor call (or add that) so step 2 can read the element
count.

### How to verify the fix

1. Add an `authored/` corpus fixture for the initializer-sized form (and keep the existing
   `reject/unsized_array.glsl` red) so both branches are pinned.
2. Re-run the real-world shader end-to-end:
   `ShadowDuskCLI batteredalienplanet.glsl out.mgfx /Profile:OpenGL` should clear **this** error.
   It may then surface *further* downstream issues (as `sunset` did below) — clearing the array
   reject is necessary, not a guarantee the whole shader compiles. Re-test and record what remains.
3. `dotnet test ShadowDusk.slnx` green (the converter has no native dep, so no render gate is needed
   for a convert-only change — but if a new fixture is added to the OpenGL render set, run the GL gate).

---

## The other three — correctly rejected, fundamental / source-broken

These are **working as intended**. They sit outside the faithful subset (or the source is broken),
and each category is already covered by a minimal `reject/` (or compile-fail) fixture, which is
exactly why adding the full real-world shaders was redundant.

| Shader (ShaderToy id) | Fails at | Root cause | Why it is not fixable faithfully | Already pinned by |
|---|---|---|---|---|
| **aurora** (`XtGGRt`) | Convert (SD0010), Lexer | **uint bit-hash arithmetic** — `uvec3 p = …; p = p*uvec3(374761393U,…); p ^ (p >> 3U); 0xffffffffU`. An integer hash relying on exact 32-bit unsigned overflow + bit ops. | The faithful target is the **float-based SM3 subset**; 32-bit integer wraparound and bitwise `^`/`>>` have no exact float representation. Emulating them would be lossy and non-faithful. | `reject/unsigned_int_literal.glsl` |
| **tiledgildedrainbow** (`tc2cWh`) | Convert (SD0010), HlslEmitter | **Cubemap sample** — `texture(iChannel0, r)` with `r` a `vec3` reflection direction. | The single-pass 2D harness binds each `iChannelN` as a **2D** sampler; there is no cubemap to bind, so a 3D direction lookup has no faithful 2D mapping. | `reject/texture_cubemap_coord.glsl`, `reject/second_entry_cubemap.glsl` |
| **sunset** (`wXjSRt`) | **Converts OK**, then HLSL compile (X0000) | **Source undefined behavior** — line 38 `z += d = .005 + max(s=.3-abs(p.y), -s*.2)/4.;` assigns `s` in one `max()` argument and reads `-s*.2` in the sibling argument. Function-argument evaluation order is unspecified. DXC rejects under `-Wunsequenced -Werror`. | The source (a golfed XorDev shader) relies on an **undefined** evaluation order; the "intended" pixel result is itself undefined. There is nothing faithful to translate. (We are right to surface the loud DXC error rather than silently pick an order.) | n/a — surfaced as a loud `X0000` compile error on the generated `.fx` |

### Reading the table

- **aurora / tiledgildedrainbow** are clean, intentional `SD0010` rejects: the converter refuses a
  construct outside the documented supported subset and says exactly why. The minimal `reject/`
  fixtures already lock that behavior in, so these full shaders add no coverage.
- **sunset** is a different shape worth remembering: the converter **accepts** it and emits `.fx`,
  but the downstream HLSL compile fails because the *source* has UB. This is the converter behaving
  correctly (faithful translation of broken source → loud downstream error), not a converter bug.

---

## Source material

The four shaders live in the untracked working-tree folder `othersahadertoytests/` (a local
collection of real ShaderToy shaders, never committed). The minimal repros above are inlined so this
doc stands alone; the full bodies are not required to act on the actionable fix. If that folder is
ever cleaned up, the only thing lost is the four full originals — none are referenced by any tracked
test, and the relevant boundaries are captured here plus in the `reject/` corpus.
