# Phase 48 — `compile <target>` profile validation (mgfxc-reject fidelity)

**Status:** 🟡 **In progress (2026-06-20).** Characterized + design settled; implementation underway on
`fix/compile-target-profile-validation` (branched off `main`). This phase fixes a real fidelity gap a user
hit: a `.fx` that **fails in MonoGame's `mgfxc`** (and `fxc`) **compiles silently in ShadowDusk**, because
ShadowDusk never validates the shader-profile token in a `compile <target> Entry()` statement.
**Roadmap track:** Fidelity / completeness (reject-side sibling to [Phase 41](PHASE-41-fxc-oracle-monogame-fidelity.md)).

> **Why this phase exists:** surfaced 2026-06-20 from a user report (Victor Chelaru / XnaFiddle context). Two
> reproductions of the same root cause:
> 1. `PixelShader = compile A MainPS();` (a typo'd profile token `A`) **compiles fine in ShadowDusk**.
> 2. Removing the `#if OPENGL … #define PS_SHADERMODEL ps_3_0 … #endif` header from a stock MonoGame
>    `.fx` makes **mgfxc fail** with `unrecognized compiler target 'PS_SHADERMODEL'`, while **ShadowDusk
>    still compiles it**.
>
> Both violate the core promise (`CLAUDE.md` → THE PURPOSE): ShadowDusk is a **drop-in `mgfxc` replacement**.
> A shader that mgfxc rejects should not silently succeed in ShadowDusk, or the user is blindsided when the
> same `.fx` later fails in a real MonoGame Content Pipeline build.

---

## Guardrail — read first

- **This is a `reject`-set change, not an output change.** The fix makes ShadowDusk **reject** inputs it
  currently accepts; it must **not** change the bytes of any `.fx` that currently compiles correctly. So the
  Windows render gates are unaffected, but the **full `dotnet test ShadowDusk.slnx` regression suite is the
  bar** (this is parser/acceptance behavior — exactly the class `CLAUDE.md` says must run the whole suite, not
  a filtered subset).
- **Every fixed bug earns a permanent regression fixture/test** (`CLAUDE.md`). This phase adds `FxPreParser`
  unit cases **and** `.fx` regression fixtures (see Work items).
- **Do NOT regress the legitimate macro idiom.** The overwhelmingly common MonoGame pattern is
  `compile PS_SHADERMODEL MainPS()` where `PS_SHADERMODEL` is `#define`d (often via the `#if OPENGL` header)
  to a real profile. That must keep compiling, byte-for-byte unchanged. The fix rejects only profile tokens
  that are **still not a recognized profile after macro expansion** — which is precisely what mgfxc does.
- **Seamless-for-the-end-user still holds** (`CLAUDE.md` → User Directives). Rejecting a genuinely-broken
  shader with a clear diagnostic is *more* faithful, not a new flag the consumer must set. No opt-in.

---

## The divergence, precisely

A MonoGame technique pass names its shaders like:

```hlsl
pass P0
{
    VertexShader = compile VS_SHADERMODEL MainVS();
    PixelShader  = compile PS_SHADERMODEL MainPS();   // or:  compile ps_3_0 MainPS();
}
```

The token after `compile` (`PS_SHADERMODEL`, `ps_3_0`, or in the bug report `A`) is the **compile target /
shader profile**.

| Compiler | When the `compile` target is parsed | What it does with an unrecognized target |
|---|---|---|
| **mgfxc / fxc** | **after** the C preprocessor runs (full macro expansion) | **hard error**: `unrecognized compiler target 'X'` |
| **ShadowDusk** | **before** macro expansion (its `FxPreParser` runs on raw source) | **silently accepted**; later regex-mapped to a Shader Model number, falling back to **SM 3.0** when the token doesn't look like a profile |

Because the two compilers parse the target at opposite ends of preprocessing, they disagree:

- **Header present** → `PS_SHADERMODEL` expands to `ps_3_0` before mgfxc sees it; ShadowDusk's regex also reads
  `ps_3_0`. **Both agree, both compile.** (No divergence — this is the normal case.)
- **Header removed / typo** → mgfxc sees a bare `PS_SHADERMODEL` (or `A`) that isn't a real target → **rejects**.
  ShadowDusk's regex finds no `_<digit>_<digit>` → **silently defaults to SM3 and compiles.** **Divergence.**

## Root cause (code walk — line refs against `main`)

1. **The pre-parser accepts any identifier as the profile, by design.**
   [`FxPreParser.Parse`](../src/ShadowDusk.HLSL/FxPreParser.cs#L998-L1004) only checks the profile token is an
   *identifier* (so `A` qualifies), then stores `profileTok.Text` verbatim. The comment at
   [FxPreParser.cs:25-26](../src/ShadowDusk.HLSL/FxPreParser.cs#L25-L26) states the intent: *"All profiles
   accepted at pre-parse time; unrecognized profiles will be rejected by DXC later with a proper diagnostic."*
   That assumption is **false for the profile token** — it never reaches DXC as a target (see step 3).

2. **A `KnownProfiles` set + `IsKnownProfile` helper already exist** but are **not called** by the
   compile-statement parse: [FxPreParser.cs:27-46](../src/ShadowDusk.HLSL/FxPreParser.cs#L27-L46). The machinery
   to validate is present; it is simply unused here. **Note:** this set currently lacks the `*_level_9_*`
   variants (see the load-bearing item W0 below).

3. **The stored profile string never becomes a DXC `-T` target.** For the GL/DX path it is fed only to
   [`ParseShaderModel`](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L737-L748):

   ```csharp
   var m = Regex.Match(profile, @"_(\d)_(\d)");   // needs "_<digit>_<digit>"
   if (m.Success && ...) return (major, minor);
   return (3, 0);                                  // "A" / "PS_SHADERMODEL" → silent SM3 fallback
   ```

   `A` and an un-expanded `PS_SHADERMODEL` match nothing → the method returns **(3, 0)**. The bogus token is
   swallowed and SM3 is assumed. (Used at
   [CompilationPipeline.cs:358 / 389](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L358).)

4. **Why ShadowDusk can't see the expanded value at parse time.** `FxPreParser` runs as **Stage 1**, on raw
   source, *before* preprocessing ([CompilationPipeline.cs:87](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L87)).
   ShadowDusk's own `Preprocessor` deliberately does **not** expand macros or evaluate `#if` — it flattens
   `#include`s and injects platform macros, *"leav[ing] #if/#define lines for DXC to evaluate"*
   ([Preprocessor.cs:65](../src/ShadowDusk.Core/Preprocessor/Preprocessor.cs#L65)). Full macro expansion only
   happens later, inside DXC's compile of each shader — by which point the profile token has already been
   consumed and discarded. So at the moment the profile is recorded, `PS_SHADERMODEL` is genuinely
   unknowable, which is *why* the lenient fallback exists. The fallback is reasonable; the missing piece is a
   **post-expansion re-check**.

5. **The FNA path catches more but still not this.**
   [`ResolveFnaProfile`](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L950-L994) does a *shape*
   test and correctly rejects SM4+ literals and cross-stage misuse with `SD0300` — but anything that doesn't
   "look like a profile" (including `A` and an undefined `PS_SHADERMODEL`) is treated as an unexpanded macro
   and **defaults to SM3** ([line 965](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L965)), same
   blind spot.

6. **The existing re-parse-after-expansion path does not cover this.** There *is* a path that DXC-preprocesses
   then re-parses ([CompilationPipeline.cs:157-197](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L157-L197)),
   but it only triggers when Stage 1 found **zero** techniques **and** the target's macros select the modern
   (SM4/SM6) branch, and even then it only re-checks the technique *count* — it never re-validates the profile
   token. The bug-report shader has a literal `technique { … }` block, so this path is skipped entirely.

### Related, same family: `SV_POSITION` / the `#if OPENGL` header

The header that triggers the second reproduction is the standard MonoGame cross-platform shim:

```hlsl
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif
```

`SV_POSITION` is a **Direct3D 10+ / HLSL Shader Model 4+ system-value semantic** (the clip-space vertex output
position). On the **DirectX** branch the profiles are SM4 (`vs_4_0_level_9_1` / `ps_4_0_level_9_1`), where
`SV_POSITION` is a real, recognized semantic, so it is used **as-is** (redefining it would be wrong). On the
**OpenGL** branch the profiles are the legacy **SM3** (`vs_3_0` / `ps_3_0`), whose D3D9-era toolchain expects
the old `POSITION` semantic, so the header **maps `SV_POSITION` → `POSITION`** only there. So the answer is the
same axis as this bug: **DirectX = SM4 (modern semantic), OpenGL = SM3 (legacy semantic)** — the `#define` only
bridges the SM3 side. The header expresses the same split twice: once for the position **semantic**
(`SV_POSITION` → `POSITION`) and once for the **`*_SHADERMODEL`** profile tokens. (Worth a one-line
consumer-docs note alongside the fix so a user who trips the new diagnostic understands the header's role.)

---

## Proposed fix (settled design)

**Validate the `compile` target against the recognized-profile set, after macro expansion when needed.** For
each pass, in every pipeline (GL, DX, FNA):

1. **Cheap path — every token gets an `IsKnownProfile` set lookup, no expansion.** A token that already
   resolves to a literal profile (`ps_3_0`, `ps_4_0_level_9_1`, …) is accepted; a token that is *profile-shaped
   but bogus* (`ps_9_9`, `ps_2_5`) is **rejected here** without any preprocess work. (This closes a hole the
   first draft of this plan missed: "looks like a profile" via the `_<digit>_<digit>` regex is **not** the
   same as "is a real profile" — `ps_9_9` matched the regex and would have slipped through a naive
   skip-if-shaped optimization.)
2. **Expensive path — only for tokens that are NOT already a known literal profile** (i.e. macro names like
   `PS_SHADERMODEL`): macro-expand the token with the target's platform macros, then `IsKnownProfile` the
   result. Expansion is confined to exactly the macro/typo cases, so the common `compile ps_3_0 …` literal path
   pays nothing.
3. **On failure**, emit a new coded diagnostic (suggest **`SD0301`**, in the technique/profile family beside
   `SD0300`) with the source line/column and a helpful hint, e.g.:
   `compile target 'PS_SHADERMODEL' is not a recognized shader profile (did you forget to #define
   VS_SHADERMODEL / PS_SHADERMODEL, e.g. via the standard '#if OPENGL … #else …' header?)`. This is strictly
   **more** helpful than mgfxc's bare `unrecognized compiler target 'X'`.

**Expansion source:** GL/DX reuse DXC's `Preprocess` (already used in the zero-technique fallback at
[CompilationPipeline.cs:159-169](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L159-L169)) with the
target's `PlatformMacros` so `#if OPENGL` resolves to the right branch. FNA (PreserveSm3) folds the
recognized-profile reject into `ResolveFnaProfile`'s existing "doesn't look like a profile" branch
([line 965](../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L965)); the FNA pre-parse already has
the source and its `#define`s — resolve the token via vkd3d's preprocessor or an equivalent object-like-macro
lookup (decide during W1).

**Net behavior:** `compile ps_3_0 …` and `compile PS_SHADERMODEL …` (macro defined) compile exactly as today;
`compile A …`, `compile PS_SHADERMODEL …` (macro undefined), and `compile ps_9_9 …` now **fail loudly**,
matching (and improving on) mgfxc.

**Rejected alternative:** a pure parse-time *shape* heuristic (reject a token that is neither a known profile
nor a plausible macro name) is cheap but **not faithful** — it cannot tell an undefined `PS_SHADERMODEL` (mgfxc
rejects) from a defined one, and an uppercase typo like `A` still slips through. Not used as the mechanism.

## Work items (each a self-contained task)

- [ ] **W0 — REQUIRED, load-bearing: audit + extend `KnownProfiles`.** The set at
      [FxPreParser.cs:27-43](../src/ShadowDusk.HLSL/FxPreParser.cs#L27-L43) is **missing the `*_level_9_*`
      variants** (`vs_4_0_level_9_1/_9_3`, `ps_4_0_level_9_1/_9_3`, and `*_level_9_0` if fxc accepts it) — the
      exact profiles the **standard MonoGame DirectX header expands to**. If rejection is turned on before this
      set is extended, **every stock MonoGame shader on the DirectX path is wrongly rejected** — a regression
      far worse than the bug. Audit against what `fxc`/`mgfxc` actually accept, then extend the set. This is a
      **prerequisite** for W2/W3, not a "risk to watch."
- [ ] **W1 — confirm the expanded-token plumbing** for both pipelines: GL/DX via DXC `Preprocess` scoped to
      non-literal tokens (confirm it yields the macro-expanded target with the right `#if` branch); determine
      and pin the FNA (PreserveSm3) expansion mechanism.
- [ ] **W2 — implement validation** on the GL/DX path (new `SD0301`): cheap set-lookup for all tokens +
      scoped expansion for macro tokens. Fold the recognized-profile reject into `ResolveFnaProfile` for FNA.
      Keep literal-known-profile passes zero-cost. No change to any currently-compiling output.
- [ ] **W3 — optional, same change:** add the GL/DX stage-prefix cross-check (mirror the FNA `SD0300` logic:
      `VertexShader = compile ps_3_0 …` is cross-stage misuse mgfxc rejects).
- [ ] **W4 — regression fixtures + unit tests** (mandatory per `CLAUDE.md`):
      - `FxPreParser` / pipeline unit cases: `compile A MainPS()` → reject; `compile PS_SHADERMODEL …` with the
        macro **defined** → accept; with the macro **undefined** → reject; `compile ps_9_9 …` → reject;
        `compile ps_3_0 …` and `compile ps_4_0_level_9_1 …` literals → accept.
      - `.fx` regression fixtures under `tests/fixtures/shaders/` for each shape, wired into the integration /
        structural corpus so the whole-suite gate exercises them on GL/DX/FNA.
- [ ] **W5 — run the full regression bar:** `dotnet test ShadowDusk.slnx` (whole suite, not a filtered
      subset). Render gates are *not* required (no output bytes change); state that explicitly in the PR.
- [ ] **W6 — consumer docs:** a short note in `docfx/` on the `compile` target and the `#if OPENGL`
      `*_SHADERMODEL` + `SV_POSITION` idiom, so a user who hits `SD0301` understands the header's role.

## Definition of done

- A `.fx` whose `compile` target does not resolve (after macro expansion) to a recognized profile is
  **rejected with a clear `SD0301` diagnostic** on GL, DX, and FNA — matching mgfxc's `unrecognized compiler
  target`. Covers `A`, undefined `PS_SHADERMODEL`, and profile-shaped-but-bogus literals (`ps_9_9`).
- Every currently-compiling shader (literal profile or defined-macro profile, including the standard DX
  `*_level_9_1` header) still compiles, byte-for-byte unchanged (spot-check the corpus manifest).
- Permanent regression fixtures + unit tests cover the typo, the defined-macro, the undefined-macro, and the
  bogus-literal cases.
- Whole-suite `dotnet test ShadowDusk.slnx` green.

## Risk / notes

- **Risk: over-rejection (the W0 trap).** Mitigated by making the `KnownProfiles` audit a hard prerequisite —
  the `*_level_9_*` DX-header profiles MUST be present before rejection is enabled.
- **Risk: extra preprocess latency.** Mitigated by scoping expansion to non-literal tokens only; quantify on
  the corpus during W2.
- **Scope honesty:** this fixes the **profile-token** class of mgfxc-vs-ShadowDusk divergence only. Other
  accept-side gaps (bad entry-point names — already caught by DXC; unsupported intrinsics; semantic mismatches)
  are separate, some already handled, the rest [Phase 41](PHASE-41-fxc-oracle-monogame-fidelity.md) territory.
  "Reject exactly what mgfxc rejects across the whole language" is a larger goal than this one phase.
