# Phase 48 — `compile <target>` profile validation (mgfxc-reject fidelity)

**Status:** 🟢 **Implemented (2026-06-20).** Recognized-profile validation lands on
`fix/compile-target-profile-validation` (branched off `main`). The `compile <target>` token is now validated
against the recognized-profile set (after macro expansion when needed) on the GL/DX/Vulkan AND FNA paths;
an unrecognized target fails loudly with the new **`SD0013`** diagnostic, matching `mgfxc`/`fxc`. Whole-suite
`dotnet test ShadowDusk.slnx` is green (1469 passed, 0 failed) and the byte-identity corpus is unchanged.
This phase fixes a real fidelity gap a user hit: a `.fx` that **fails in MonoGame's `mgfxc`** (and `fxc`)
**compiled silently in ShadowDusk**, because ShadowDusk never validated the shader-profile token in a
`compile <target> Entry()` statement.
**Roadmap track:** Fidelity / completeness (reject-side sibling to [Phase 41](PHASE-41-fxc-oracle-monogame-fidelity.md)).

> **Implementation note (code chosen vs. the draft's `SD0301`):** the diagnostic ships as **`SD0013`**, NOT
> `SD0301` as this doc originally suggested. `SD0301` was already taken (D3D9 CTAB-reflection failure in
> `CtabReader`), and `SD0304` is a historically-burned code; reusing either would violate the
> one-code-one-condition rule in `docs/error-codes.md`. The check is a **pipeline-level effect validation**
> that fires on every target (GL/DX/Vulkan/FNA), so the pipeline-validation range (`SD0010`–`SD0019`) is its
> correct home; `SD0013` was the first free slot there.

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
   [`FxPreParser.Parse`](../../src/ShadowDusk.HLSL/FxPreParser.cs#L998-L1004) only checks the profile token is an
   *identifier* (so `A` qualifies), then stores `profileTok.Text` verbatim. The comment at
   [FxPreParser.cs:25-26](../../src/ShadowDusk.HLSL/FxPreParser.cs#L25-L26) states the intent: *"All profiles
   accepted at pre-parse time; unrecognized profiles will be rejected by DXC later with a proper diagnostic."*
   That assumption is **false for the profile token** — it never reaches DXC as a target (see step 3).

2. **A `KnownProfiles` set + `IsKnownProfile` helper already exist** but are **not called** by the
   compile-statement parse: [FxPreParser.cs:27-46](../../src/ShadowDusk.HLSL/FxPreParser.cs#L27-L46). The machinery
   to validate is present; it is simply unused here. **Note:** this set currently lacks the `*_level_9_*`
   variants (see the load-bearing item W0 below).

3. **The stored profile string never becomes a DXC `-T` target.** For the GL/DX path it is fed only to
   [`ParseShaderModel`](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L737-L748):

   ```csharp
   var m = Regex.Match(profile, @"_(\d)_(\d)");   // needs "_<digit>_<digit>"
   if (m.Success && ...) return (major, minor);
   return (3, 0);                                  // "A" / "PS_SHADERMODEL" → silent SM3 fallback
   ```

   `A` and an un-expanded `PS_SHADERMODEL` match nothing → the method returns **(3, 0)**. The bogus token is
   swallowed and SM3 is assumed. (Used at
   [CompilationPipeline.cs:358 / 389](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L358).)

4. **Why ShadowDusk can't see the expanded value at parse time.** `FxPreParser` runs as **Stage 1**, on raw
   source, *before* preprocessing ([CompilationPipeline.cs:87](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L87)).
   ShadowDusk's own `Preprocessor` deliberately does **not** expand macros or evaluate `#if` — it flattens
   `#include`s and injects platform macros, *"leav[ing] #if/#define lines for DXC to evaluate"*
   ([Preprocessor.cs:65](../../src/ShadowDusk.Core/Preprocessor/Preprocessor.cs#L65)). Full macro expansion only
   happens later, inside DXC's compile of each shader — by which point the profile token has already been
   consumed and discarded. So at the moment the profile is recorded, `PS_SHADERMODEL` is genuinely
   unknowable, which is *why* the lenient fallback exists. The fallback is reasonable; the missing piece is a
   **post-expansion re-check**.

5. **The FNA path catches more but still not this.**
   [`ResolveFnaProfile`](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L950-L994) does a *shape*
   test and correctly rejects SM4+ literals and cross-stage misuse with `SD0300` — but anything that doesn't
   "look like a profile" (including `A` and an undefined `PS_SHADERMODEL`) is treated as an unexpanded macro
   and **defaults to SM3** ([line 965](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L965)), same
   blind spot.

6. **The existing re-parse-after-expansion path does not cover this.** There *is* a path that DXC-preprocesses
   then re-parses ([CompilationPipeline.cs:157-197](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L157-L197)),
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
[CompilationPipeline.cs:159-169](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L159-L169)) with the
target's `PlatformMacros` so `#if OPENGL` resolves to the right branch. FNA (PreserveSm3) folds the
recognized-profile reject into `ResolveFnaProfile`'s existing "doesn't look like a profile" branch
([line 965](../../src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs#L965)); the FNA pre-parse already has
the source and its `#define`s — resolve the token via vkd3d's preprocessor or an equivalent object-like-macro
lookup (decide during W1).

**Net behavior:** `compile ps_3_0 …` and `compile PS_SHADERMODEL …` (macro defined) compile exactly as today;
`compile A …`, `compile PS_SHADERMODEL …` (macro undefined), and `compile ps_9_9 …` now **fail loudly**,
matching (and improving on) mgfxc.

**Rejected alternative:** a pure parse-time *shape* heuristic (reject a token that is neither a known profile
nor a plausible macro name) is cheap but **not faithful** — it cannot tell an undefined `PS_SHADERMODEL` (mgfxc
rejects) from a defined one, and an uppercase typo like `A` still slips through. Not used as the mechanism.

## Work items (each a self-contained task)

- [x] **W0 — REQUIRED, load-bearing: audit + extend `KnownProfiles`.** Done: added
      `vs_4_0_level_9_0/_9_1/_9_3` and `ps_4_0_level_9_0/_9_1/_9_3` (fxc's documented FL9 set; no `_level_9_2`).
      These are the exact profiles the **standard MonoGame DirectX header expands to**; without them, turning on
      rejection would wrongly fail **every stock MonoGame DirectX shader** — a regression far worse than the bug.
      **Completeness follow-up (post-review, 2026-06-20):** the set was also missing a few profiles fxc/DXC
      accept, which would have over-rejected in the *other* direction (rejecting a valid-to-reference target).
      Added `vs_3_sw`/`ps_3_sw` (the `*_2_sw` siblings were already listed — the omission was an asymmetry) and
      `vs_6_8`/`vs_6_9`/`ps_6_8`/`ps_6_9` (the SM6 list ran to 6_7; DXC, our frontend, accepts these). None are
      used by real MonoGame/FNA/KNI games, but "reject exactly what the reference compiler rejects" means not
      over-rejecting either. Unit-covered in `ProfileRecognitionTests`.
- [x] **W1 — expanded-token plumbing.** GL/DX (and the FNA expansion) reuse DXC's `Preprocess` (`-P`): a
      unique-sentinel probe (`__SD_PROFILE_PROBE__ <rawToken> __SD_PROFILE_PROBE__`) is appended to the
      already-`#include`-flattened source and preprocessed with the target's `PlatformMacros`, so the correct
      `#if OPENGL` branch drives the expansion; the value between the sentinels is read back and re-checked.
      C macros are case-sensitive, so the RAW token (`PS_SHADERMODEL`) is expanded, not the lowercased form.
- [x] **W2 — implement validation** on the GL/DX path (`SD0013`, not `SD0301` — see status note): cheap
      set-lookup (literal known → accept; profile-shaped-but-bogus → reject) + scoped expansion for macro
      tokens, cached per raw token. The same `ValidateCompileProfile` runs on the FNA path with the FNA macro
      set BEFORE `ResolveFnaProfile`, which is unchanged (it still applies the MojoShader SM2–3 ceiling once a
      token resolves to a real profile). Literal-known-profile passes stay zero-cost. No currently-compiling
      output changed (byte-identity corpus green).
- [x] **W3 — GL/DX/Vulkan stage-prefix cross-check (done, 2026-06-20).** Once a compile target resolves to a
      recognized profile, `StagePrefixCheck` verifies its stage prefix matches the slot it is bound to — a
      `vs_*` in a `VertexShader =` slot, a `ps_*` in a `PixelShader =` slot. A mismatch (`VertexShader = compile
      ps_3_0 …`, or via a macro like `VertexShader = compile PS_SHADERMODEL …`) is rejected with the new
      **`SD0014`** (pipeline-validation range), matching mgfxc/fxc, which reject a cross-stage binding;
      ShadowDusk previously ignored the declared prefix and compiled by slot. Gated by an `enforceStagePrefix`
      flag: ON for GL/DX/Vulkan, OFF for FNA — the FNA path keeps the identical condition as `SD0300` in
      `ResolveFnaProfile` (FNA range, untouched, still covered by `FnaProfilePolicyTests`). Regression fixture
      `examples/ExProfileStageMismatch.fx` (rejects SD0014 on GL + DX). No existing fixture has a cross-stage
      compile (grep-verified), so nothing currently-compiling regresses.
- [x] **W4 — regression fixtures + unit tests.**
      - `tests/ShadowDusk.HLSL.Tests/ProfileRecognitionTests.cs` — `IsKnownProfile` (incl. the FL9 W0 set) and
        `LooksLikeProfile` (shaped vs. macro-name) helpers.
      - `tests/ShadowDusk.Integration.Tests/Phase48ProfileValidationCorpusTests.cs` — end-to-end on GL/DX/FNA:
        `compile A` (typo), `compile PS_SHADERMODEL` with the header REMOVED (undefined macro), and
        `compile ps_9_9` (bogus literal) → all reject `SD0013`; the standard `*_level_9_1` header fixture →
        accept on every target (the W0 guard; also proves a DEFINED `PS_SHADERMODEL` still compiles).
      - Fixtures: `examples/ExProfileTypo.fx`, `ExProfileUndefinedMacro.fx`, `ExProfileBogusLiteral.fx`,
        `ExProfileLevel9Header.fx` (auto-copied via the `fixtures/shaders/**` wildcard).
- [x] **W5 — full regression bar:** `dotnet test ShadowDusk.slnx` green — 1469 passed, 0 failed, 0 skipped
      (HLSL 255, GLSL 61, Compiler 126, Core 489, Image 57, Integration 481). Render gates not required (no
      output bytes change; this is a reject-set change only).
- [x] **W6 — consumer docs:** added a "The `compile <target>` profile and the `#if OPENGL` header" section to
      `docfx/guides/parameters-and-caveats.md` covering the `*_SHADERMODEL` + `SV_POSITION` idiom and `SD0013`.

## Definition of done

- A `.fx` whose `compile` target does not resolve (after macro expansion) to a recognized profile is
  **rejected with a clear `SD0013` diagnostic** on GL, DX, and FNA — matching mgfxc's `unrecognized compiler
  target`. Covers `A`, undefined `PS_SHADERMODEL`, and profile-shaped-but-bogus literals (`ps_9_9`).
- Every currently-compiling shader (literal profile or defined-macro profile, including the standard DX
  `*_level_9_1` header) still compiles, byte-for-byte unchanged (spot-check the corpus manifest).
- Permanent regression fixtures + unit tests cover the typo, the defined-macro, the undefined-macro, and the
  bogus-literal cases.
- Whole-suite `dotnet test ShadowDusk.slnx` green.

## Risk / notes (post-review hardening, 2026-06-20)

A purpose/regression review asked "does this break anything in place or bend the library's intent?" Verdict:
**no — it advances the drop-in-`mgfxc` purpose** (rejects what the reference rejects, fails loudly, no new
flag, no `.mgfx`/pin change, output bytes unchanged) and **nothing proven regresses** (full suite green incl.
the byte-identity corpus). The three caveats the review surfaced, and their resolution:

- **Over-rejection (both directions) — RESOLVED.** Under-listing the DX-header `*_level_9_*` profiles was the
  dangerous direction (handled by W0). The review also found the *other* direction: `vs_3_sw`/`ps_3_sw` and
  SM6.8/6.9 were valid-to-fxc/DXC but absent, so they'd be wrongly rejected. Added in the W0 completeness
  follow-up. The `KnownProfiles` set now matches the reference compilers' accepted `vs_*`/`ps_*` targets.
- **Extra preprocess latency — ACCEPTED.** Each macro-profile shader pays one cached DXC `-P` preprocess per
  distinct token (~1–2 per typical effect); literal-profile passes pay nothing. Small vs. a full compile, suite
  unaffected. A lighter source-`#define` scan is a possible future optimization, not needed now.
- **New DXC dependency on the validation step — this was WRONG in the first cut and BROKE `main`; now FIXED.**
  My original note here ("sound by construction") was incorrect. The recognized-profile check macro-expands a
  profile macro via DXC's `-P` preprocessor, but the **WASM DXC shim has no `-P` export and THROWS**
  `NotSupportedException` ([JsShaderBackends.Preprocess](../../src/ShadowDusk.Wasm/JsShaderBackends.cs#L95-L104)).
  The check ran on every macro-profile compile, which the in-browser DX/FNA byte-identity corpus exercises, so
  the standing `wasm.yml` gate went RED on the PR-#113 merge (every macro-profile shader threw, including the
  "vkd3d path must not touch the other modules" isolation scenario). I deferred running that gate instead of
  triggering it; it would have caught this pre-merge. **Fix (PR follow-up):** the macro-token check is now
  strictly **best-effort** — `TryExpandProfileToken` catches a thrown/failed expansion and returns an
  `ExpansionUnavailable` sentinel, and `ValidateCompileProfile` then **defers** (accepts), exactly restoring
  the pre-Phase-48 lenient behavior on a backend without `-P`. Desktop (where `-P` works) still rejects bogus
  macros; literal-token validation (`ps_9_9`, cross-stage with a literal) still works on every backend since it
  needs no expansion. Regression-locked by `DxbcCompilerInjectionTests.MacroProfile_WhenPreprocessThrows_SkipsValidationAndCompiles`
  (pure unit test: a `Preprocess`-throwing DXC + injected vkd3d, FNA macro-profile compile must reach codegen,
  not throw or reject) AND re-proven green by the real in-browser `wasm.yml` gate. Lesson recorded: run the
  WASM/browser gate (or dispatch `wasm.yml`) for any change that touches the shared compile path, not just the
  desktop suite.
- **Scope honesty:** this fixes the **profile-token** class of mgfxc-vs-ShadowDusk divergence only. Other
  accept-side gaps (bad entry-point names — already caught by DXC; unsupported intrinsics; semantic mismatches)
  are separate, some already handled, the rest [Phase 41](PHASE-41-fxc-oracle-monogame-fidelity.md) territory.
  "Reject exactly what mgfxc rejects across the whole language" is a larger goal than this one phase.
