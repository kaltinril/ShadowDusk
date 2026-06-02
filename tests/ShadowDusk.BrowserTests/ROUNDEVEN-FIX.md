# `roundEven` WebGL-reach fix — ShadowDusk's own GLSL fails to load in KNI WebGL

**Date:** 2026-06-01 · **Scope:** fix the real WebGL1-reach bug Phase 24 surfaced
as an out-of-scope finding (DISSOLVE-INVESTIGATION.md, footnote †): ShadowDusk's
*own* `Pixelated.mgfx` fails to **load** in real KNI WebGL because the emitted GLSL
uses `roundEven()`, which GLSL ES 1.00 / WebGL1 does not provide. **Outcome: root
cause confirmed, fix applied in the dialect rewrite, desktop ImageTests still
green (Pixelated cross-validates with mgfxc), and ShadowDusk's own bytes now LOAD +
render in a real headless KNI WebGL run.**

This violated THE PURPOSE: our output must run where mgfxc can't. The golden corpus
masked it because mgfxc emits a WebGL1-valid construct for the same `round`.

---

## 1. The offending GLSL (ShadowDusk's own output, before the fix)

`tests/fixtures/shaders/Pixelated.fx` uses HLSL `round()` twice:

```hlsl
float x = round(mx / pixelation) * pixelation;
float y = round(my / pixelation) * pixelation;
```

ShadowDusk's OpenGL pipeline (DXC → SPIR-V → SPIRV-Cross → MojoShader-dialect
rewrite) emitted this PS (extracted from `Pixelated.mgfx`):

```glsl
#ifdef GL_ES
precision mediump float;
precision mediump int;
#endif

uniform sampler2D ps_s0;
varying vec4 vTexCoord0;

void main()
{
    gl_FragColor = texture2D(ps_s0, vec2(roundEven(vTexCoord0.x * 32.0) * 0.03125, roundEven(vTexCoord0.y * 32.0) * 0.03125));
}
```

**`roundEven()` is a GLSL ES 3.00 / desktop-GL 1.30 builtin. WebGL1 (GLSL ES 1.00,
which KNI's Reach profile compiles to) does not provide it**, so KNI's
`new Effect(gd, bytes)` fails to load with:

```
'roundEven' : no matching overloaded function found
```

**Why `roundEven`:** DXC lowers HLSL `round` to SPIR-V `OpRoundEven`
(round-half-to-even), and SPIRV-Cross emits the GLSL builtin `roundEven()` for it.
The transpiler targets `#version 140` (`GlslEs=false`) where `roundEven` is legal,
so neither DXC, SPIRV-Cross, nor the desktop GL tests ever flag it — the failure
only appears in a WebGL1 runtime. (Desktop GL ≥ 1.30 has `roundEven`, so the
existing desktop cross-validation passed even though the output is not WebGL1-valid.)

## 2. What mgfxc emits for the same construct (the faithfulness target)

The golden `Pixelated.mgfx` (`tests/fixtures/golden/OpenGL/Pixelated.mgfx`) — which
Phase 24 confirmed loads+renders in KNI WebGL — expresses `round` in
WebGL1-valid arithmetic (MojoShader/fxc dialect):

```glsl
const vec4 ps_c0 = vec4(32.0, 0.5, 0.03125, 0.0);
void main()
{
    ps_r0.xy = (ps_v0.xy * ps_c0.xx) + ps_c0.yy;   // x + 0.5
    ps_r0.zw = fract(ps_r0.xy);                    // fract(x + 0.5)
    ps_r0.xy = -ps_r0.zw + ps_r0.xy;               // (x+0.5) - fract(x+0.5)  ==  floor(x+0.5)
    ps_r0.xy = ps_r0.xy * ps_c0.zz;                // * (1/32)
    ps_oC0 = texture2D(ps_s0, ps_r0.xy);
}
```

i.e. mgfxc computes **`round(x) = floor(x + 0.5)`** (round-half-up), built only from
`fract`/`+`/`*` — all valid in every GLSL profile including ES 1.00. So the faithful,
same-backend lowering for ShadowDusk is exactly **`roundEven(x) → floor(x + 0.5)`**.

(Note the semantics: HLSL `round` is spec'd round-half-to-even, but fxc/MojoShader
in practice emit round-half-up `floor(x+0.5)`. Matching mgfxc — the same-backend
reference — means using `floor(x+0.5)`, not GLSL `roundEven`. On the desktop
cross-validation the two already agree within tolerance; switching to `floor(x+0.5)`
makes ShadowDusk match the golden's rounding *more* closely, never less.)

## 3. The fix

**File:** `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs` — a guarded textual lowering
in the MojoShader-dialect rewrite (the same place `texture()→texture2D`,
`out_var_SV_Target→gl_FragColor`, etc. already happen), added as "Rule 8":

```csharp
// Rule 8: lower roundEven()/round() to floor(x + 0.5) (WebGL1-valid).
body = LowerRoundToFloorHalfUp(body);
```

`LowerRoundToFloorHalfUp` rewrites every `roundEven(expr)` and bare `round(expr)`
(both ES-3.00-only) to `floor((expr) + 0.5)`. It uses a **balanced-parenthesis
scan** to capture the argument, so a nested call like `round(abs(x) * 8.0)` is
lowered correctly, and a **whole-identifier** match so it never fires inside a
longer identifier (the `round` inside `roundEven`, or a user `myround`). `roundEven`
is processed before `round` so the longer name wins.

**Resulting ShadowDusk PS (after the fix):**

```glsl
void main()
{
    gl_FragColor = texture2D(ps_s0, vec2(floor((vTexCoord0.x * 32.0) + 0.5) * 0.03125, floor((vTexCoord0.y * 32.0) + 0.5) * 0.03125));
}
```

`roundEven` is gone; this is numerically `floor(x+0.5) * (1/32)` — the same value
mgfxc's golden computes — and valid in WebGL1.

### Why the rewriter, not a SPIRV-Cross option

SPIRV-Cross has no option to avoid `roundEven` for `OpRoundEven`; it emits the
builtin whenever the target GLSL version supports it (and would *error* if asked to
target ES 1.00 directly, which is not how the pipeline works — it targets desktop
`#version 140` and the MojoShader rewrite then produces the legacy dialect the KNI
runtime loads). The dialect rewrite is the layer that already adapts SPIRV-Cross's
modern GLSL into the WebGL1/MojoShader dialect, so the lowering belongs there.

### Determinism / byte-identity

The lowering is applied unconditionally to all GL pixel-shader output, downstream of
reflection, so it is fully deterministic (same source → same bytes) and is applied
identically on **both** reflection paths (native DXIL and the WASM `SpirvReflector`)
— the `SpirvReflectionByteIdentityTests` stay byte-identical (10/10), and
`DeterminismTests` stay byte-identical across recompiles. **It does intentionally
change the emitted GL bytes** for any shader using `round` (Pixelated:
427 → 435 bytes) — this is the point of the fix and is consistent across all GL
hosts (CLI, desktop, WASM), so ShadowDusk's own reproducibility model holds.
Byte-equality with mgfxc was never a goal.

## 4. Harness extension — validate ShadowDusk's OWN output, not just the golden

The Phase 24 harness loaded only the committed mgfxc goldens, which is exactly why
this bug hid. Added a **`--corpus=sd`** path that runs the same mode-1 load+render
validation against **ShadowDusk's own compiled `.mgfx`**, keeping the golden path
(`--corpus=golden`, the default) intact:

- **`compile-corpus-sd.mjs`** — refactored into a reusable `compileCorpusSd(outDir)`
  (still runnable standalone) that compiles the 10-shader corpus with ShadowDusk's
  own CLI into a target dir.
- **`publish-sample-sd.mjs`** (new) — mirrors `publish-sample.mjs`: builds the CLI,
  publishes the KNI sample into `.publish-sd`, **overwrites** the served
  `wwwroot/shaders/OpenGL/*.mgfx` with ShadowDusk's own bytes, and renders the
  desktop DesktopGL references from **those same ShadowDusk bytes** into
  `references-sd/` (so the WebGL-vs-DesktopGL comparison stays same-bytes for our
  own output).
- **`run-harness.mjs`** — added `--corpus=sd`, which serves `.publish-sd`, compares
  against `references-sd/`, and writes `RESULTS-SD.md` / `captures-sd/` / `diffs-sd/`
  (separate from the golden artifacts so neither clobbers the other). The default
  (`golden`) behavior is unchanged.

Usage:
```bash
node publish-sample-sd.mjs            # ShadowDusk's own bytes + same-byte desktop refs
node run-harness.mjs --corpus=sd      # load + render OUR output in real KNI WebGL
```

This closes the "our output ≠ loadable in WebGL" blind spot: a future regression
that makes ShadowDusk emit a non-WebGL1 construct now fails this harness path
instead of hiding behind the golden corpus.

## 5. Verification

### 5a. Real headless KNI WebGL run (the bar — ladder rung 4)

Headless Chromium (ANGLE/SwiftShader), real KNI WebGL `Effect`, 512×512, reference =
the SAME ShadowDusk bytes rendered on desktop DesktopGL (`RefRenderer`, Reach).
Both runs are `node run-harness.mjs --corpus=sd` against ShadowDusk's OWN compiled
`.mgfx`; only `Pixelated.mgfx` differs (pre-fix `roundEven` bytes vs post-fix
`floor` bytes — all 9 others identical).

| Run | Pixelated in KNI WebGL | Mode-1 corpus |
|---|---|---|
| **BEFORE** (ShadowDusk emits `roundEven`) | **LOAD-FAIL** — `new Effect(gd, bytes)` threw `InvalidOperationException: Shader Compilation Failed. ERROR: 0:12: 'roundEven' : no matching overloaded function found` | **9/10** pass, **9/10** loaded (Pixelated fails to load) |
| **AFTER** (ShadowDusk emits `floor(x+0.5)`) | **PASS** — loads (`new Effect` returns null) and renders, maxDelta **1** LSB vs desktop, 0/262144 px over tolerance | **10/10** pass, **10/10** loaded |

The before run is a genuine real-browser capture using ShadowDusk's actual pre-fix
output (the `roundEven` `Pixelated.mgfx` produced by the compiler before this change);
the after run uses the post-fix `floor(x+0.5)` bytes. The other 9 shaders are
unchanged across both runs and pass identically (Grayscale/Invert/TintShader/Sepia/
Scanlines/Fading at Δ1; Saturate Δ3/0.004%; Dots Δ11 within the documented 12-LSB
sin/cos headroom; Dissolve Δ128/0.145% with the sample's slot-1 pin from the prior
investigation). Harness exit: BEFORE = 1 (Pixelated load-fail), AFTER = 0.

### 5b. Desktop regression (`dotnet test ShadowDusk.slnx --settings ShadowDusk.runsettings`)

Full solution, all green (498 passed, 0 failed, 0 skipped):

| Project | Result |
|---|---|
| ShadowDusk.GLSL.Tests | **12/12** (was 9 — +3 new roundEven/round lowering tests) |
| ShadowDusk.ImageTests | **25/25** — incl. `MgfxcCrossValidationTests.CrossValidate("Pixelated")` and all 10 mgfxc cross-validations |
| ShadowDusk.Integration.Tests | **128/128** — incl. `SpirvReflectionByteIdentityTests` (10/10 byte-identical) + `DeterminismTests` (byte-identical recompiles) |
| ShadowDusk.HLSL.Tests | 89/89 |
| ShadowDusk.Core.Tests | 231/231 |
| ShadowDusk.Compiler.Tests | 13/13 |

The **critical** desktop check is `MgfxcCrossValidationTests.CrossValidate("Pixelated")`:
it renders ShadowDusk's `floor(x+0.5)` GLSL and mgfxc's golden GLSL in the same real
GL context and compares at tolerance 4/255. It passes — the lowering keeps
same-backend render parity with mgfxc on desktop GL (no tolerance was widened).

The **critical** desktop check is `MgfxcCrossValidationTests.CrossValidate("Pixelated")`:
it renders ShadowDusk's `floor(x+0.5)` GLSL and mgfxc's golden GLSL in the same real
GL context and compares at tolerance 4/255. It passes — the lowering keeps
same-backend render parity with mgfxc on desktop GL (no tolerance was widened).

## Files touched
- `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs` — Rule 8 + `LowerRoundToFloorHalfUp`
  / `FindCallStart` / `FindMatchingParen` / `IsIdentChar` helpers (the fix).
- `tests/ShadowDusk.GLSL.Tests/MonoGameGlslRewriterTests.cs` — 3 new tests:
  `RoundEven_IsLoweredToFloorHalfUp_AndNoRoundEvenRemains`,
  `BareRound_IsLoweredToFloorHalfUp`,
  `Round_NestedArgument_BalancedParensLoweredCorrectly`.
- `tests/ShadowDusk.BrowserTests/compile-corpus-sd.mjs` — exported reusable
  `compileCorpusSd` + `SHADERS`.
- `tests/ShadowDusk.BrowserTests/publish-sample-sd.mjs` — new (SD-corpus publish + refs).
- `tests/ShadowDusk.BrowserTests/run-harness.mjs` — `--corpus=sd` path.
- `tests/ShadowDusk.BrowserTests/ROUNDEVEN-FIX.md` — this file.
