# Dissolve WebGL render-divergence — investigation (Phase 24 carry-forward)

**Date:** 2026-06-01 · **Scope:** root-cause the single Phase 24 mode-1 failure
(Dissolve rendering differently in KNI **WebGL** vs desktop **DesktopGL** of the
**same `.mgfx` bytes**), then fix if small + clear. **Outcome: root cause found,
small fix applied, verified in a real headless-browser re-run — Dissolve now
passes (10/10).**

---

## TL;DR

- **Root cause is NOT precision, NOT the `discard`/`clip` lowering, NOT the MGFX
  format.** It is the **second texture's sampler state (texture slot 1) being
  left at the runtime default**, which KNI WebGL and desktop DesktopGL resolve
  **differently** for the non-power-of-two (960×1282) cat texture. That shifts the
  value sampled from `_dissolveTex` everywhere, which flips Dissolve's
  data-dependent **threshold-band tint** comparison across the whole band.
- **Evidence:** pinning slot 1 to `LinearClamp` before the effect draw collapsed
  the divergence from **Δ198 over 4406 px (1.68%) → Δ128 over 380 px (0.145%)**,
  and Dissolve now **PASSES** the harness budget. The 9 already-passing shaders are
  unchanged; full corpus is **10/10 load + 10/10 render**.
- **The precision hypothesis was tested and refuted in this harness.** Emitting
  `precision highp float;` instead of `mediump` produced a **byte-identical**
  Dissolve delta (Δ198/4406) — because the headless renderer (ANGLE/**SwiftShader**)
  evaluates `mediump` and `highp` identically. highp is still the theoretically
  correct choice for *real* WebGL hardware, but it does **nothing** for the
  divergence observed here, so it was **not** shipped (would needlessly diverge our
  GLSL from mgfxc's with zero verified benefit).
- **This is a sample/harness binding issue, not a compiler-output bug.** mgfxc's
  golden `.mgfx` and ShadowDusk's own `.mgfx` are *byte-identical in the relevant
  layout* and diverge **identically** (both Δ198/4406) — so the cause cannot be in
  either compiler's output; it is purely how the two *runtimes* default an *unset*
  sampler slot. The fix lives in the sample (`ShaderFiddleGame.Draw`) and the test
  reference renderer (`RefRenderer`).

---

## 1. The emitted GLSL for Dissolve

The Phase 24 harness loads `samples/ShaderFiddle.Web/wwwroot/shaders/OpenGL/*.mgfx`,
which are **byte-identical to the mgfxc goldens** in `tests/fixtures/golden/OpenGL/`
(verified: `cmp` reports identical, both 1450 bytes). So the Phase 24 numbers are
**mgfxc's own GLSL** in WebGL vs DesktopGL. ShadowDusk's *own* output for the same
`.fx` was also produced and tested (below) and reproduces the divergence identically.

### mgfxc golden Dissolve PS (MojoShader-dialect GLSL, what Phase 24 actually ran)

```glsl
#ifdef GL_ES
precision mediump float;
precision mediump int;
#endif

uniform vec4 ps_uniforms_vec4[3];
const vec4 ps_c3 = vec4(1.0, -0.0, -1.0, 0.0);
vec4 ps_r0; vec4 ps_r1; vec4 ps_r2;
#define ps_c0 ps_uniforms_vec4[0]
#define ps_c1 ps_uniforms_vec4[1]
#define ps_c2 ps_uniforms_vec4[2]
uniform sampler2D ps_s0;          // s0           (SpriteBatch texture, slot 0)
uniform sampler2D ps_s1;          // _dissolveTex (slot 1)  <-- the second texture
varying vec4 vTexCoord0;
#define ps_v0 vTexCoord0
#define ps_oC0 gl_FragColor

void main()
{
    ps_r0 = texture2D(ps_s1, ps_v0.xy);                       // sample dissolve tex
    ps_r0.x = -ps_r0.x + ps_c3.x;                             // dissolveAmount = 1 - r
    ps_r0.y = ps_r0.x + -ps_c0.x;                             // (1-r) - (progress-thr)
    ps_r1 = ((ps_r0.y >= 0.0) ? ps_c3.yyyy : ps_c3.zzzz);     // sign select
    if (any(lessThan(ps_r1.xyz, vec3(0.0)))) discard;         // <-- clip()->discard
    ...
    ps_r1 = ps_r1.yyyz + ps_c2;                               // ps_c2 = thresholdColor
    ps_r1 = (ps_r0.yyyy * ps_r1) + ps_c3.wwwx;
    ps_r2 = texture2D(ps_s0, ps_v0.xy);                       // sample scene color
    ps_r1 = ps_r1 * ps_r2;                                    // tinted scene
    ps_oC0 = ((ps_r0.x >= 0.0) ? ps_r2 : ps_r1);              // <-- band tint select
}
```

### ShadowDusk's own Dissolve PS (SPIRV-Cross SSA GLSL — produced by our pipeline)

```glsl
#ifdef GL_ES
precision mediump float;
precision mediump int;
#endif
uniform vec4 ps_uniforms_vec4[3];
uniform sampler2D ps_s0;
uniform sampler2D ps_s1;
varying vec4 vTexCoord0;
void main()
{
    float _40 = ps_uniforms_vec4[0].x + ps_uniforms_vec4[1].x;   // progress + thr
    vec4  _44 = texture2D(ps_s0, vTexCoord0.xy);                 // scene color
    vec4  _48 = texture2D(ps_s1, vTexCoord0.xy);                 // dissolve tex
    float _50 = 1.0 - _48.x;                                     // dissolveAmount
    float _51 = _40 - ps_uniforms_vec4[1].x;                     // progress
    if (_50 < _51) { discard; }                                  // <-- discard
    gl_FragData[0] = mix(_44,
        _44 * mix(vec4(0,0,0,1), ps_uniforms_vec4[2],            // thresholdColor [2]
                  vec4(mix(1.0, 0.0, 1.0 - clamp(abs(_51 - _50)/ps_uniforms_vec4[1].x, 0.0, 1.0)))),
        vec4(float(_50 < _40)));                                 // <-- band tint select
}
```

Both forms compute (a) a `discard` from `(1 - dissolveTex.r) < threshold`, and
(b) a **tint select** `b = (1 - dissolveTex.r) < (progress)` that chooses between
the raw scene color and the `_dissolveThresholdColor`-tinted scene color. Both feed
on `dissolveTex.r` — the **slot-1** sample. The `_dissolveThresholdColor` (orange,
`(1, 0.5, 0)`) sits at uniform register **`ps_uniforms_vec4[2]`**.

`_dissolveTex` is bound by `WebShaderInputs.SetParams`/`RefRenderer.SetParams` to
**the same cat image**, which is **960×1282 — non-power-of-two (NPOT)**.

---

## 2. What actually diverges (pixel forensics)

512×512 render, `_progress=0.5`, `_dissolveThreshold=0.04`, `_dissolveThresholdColor=(1,0.5,0)`.

**Original (golden bytes, no fix):** Δ198, 4406/262144 px (1.68%) over tolerance.

Classifying the 4406 divergent pixels:
- **4397 of 4406 (99.8%)** are *"desktop shows the orange-tinted band, WebGL shows
  the brighter untinted scene"*. i.e. desktop takes the tint branch (`b = 1`), WebGL
  takes the no-tint branch (`b = 0`) for the same texels.
- Discard masks differed by only **258** pixels (`onlyRef-black = 258`,
  `onlyCap-black = 0`) — WebGL discards a strict *subset* of what desktop discards.

So the dominant effect is **not** the `discard` and **not** a global color shift —
it is the **band-tint comparison `b` flipping** across the whole threshold band. `b`
depends on `(1 - dissolveTex.r)`, i.e. the **slot-1 sample**. A small, *systematic*
offset in `dissolveTex.r` (a filtering/sampler difference, not random noise) pushes
band texels across the `b` boundary — exactly this pattern.

---

## 3. Ranked root-cause analysis (with evidence)

| # | Hypothesis | Verdict | Evidence |
|---|---|---|---|
| **1** | **Second-texture (slot 1) sampler-state mismatch** between KNI WebGL and DesktopGL defaults (NPOT texture). | **CONFIRMED — dominant cause.** | `SpriteBatch.Begin(..., SamplerState.LinearClamp)` only sets **slot 0**. `_dissolveTex` is bound to **slot 1**, whose state stays at the runtime default — and KNI WebGL (Reach, with the logged `"Reach profile support only Clamp mode for non-power of two Textures"` warning) resolves it differently than DesktopGL for the NPOT cat. **Pinning `GraphicsDevice.SamplerStates[1] = LinearClamp` collapsed the divergence Δ198/4406 → Δ128/380 and made Dissolve PASS.** |
| 2 | **Precision** (`mediump` vs effective `highp`) flipping `discard`/tint at the band edge. | **Refuted in this harness; real-hardware-relevant only.** | Recompiling the corpus with `precision highp float;` gave a **byte-identical** Dissolve delta (Δ198/4406). The headless renderer is ANGLE/**SwiftShader**, whose software FP evaluates `mediump` ≡ `highp`, so the qualifier has **zero** effect here. On real WebGL hardware `mediump` *could* contribute to the ~380 px residual edge; see Recommendation. |
| 3 | **`clip()` → `discard` lowering** (sign / `<0` vs `<=0` / `any(lessThan(...))`). | **Not the cause.** | The lowering is identical in golden and ShadowDusk output and renders the same on desktop; after fix #1 the discard masks match to within ~60 px (was 258). The discard was never the dominant term (258 of 4406 px). |
| 4 | **MGFX container / KNIFX-v11** format problem. | **Ruled out (already, by Phase 24).** | All 10 shaders *load* (`new Effect` succeeds). The uniform layout for Dissolve is correct and **identical** between golden and ShadowDusk: `ps_uniforms_vec4` size 48 = 3×vec4, with `_dissolveThresholdColor` at offset 32 → register `[2]`. Nothing format-side to fix. |

### Why only Dissolve diverged (and the 9 others did not)

Dissolve is the **only** corpus shader that (a) samples a **second texture** (slot 1,
whose sampler state the app never sets) **and** (b) makes a **data-dependent branch**
(`discard` + tint select) on that sample. The other 9 either use a single texture
(slot 0, which *is* pinned by `SpriteBatch.Begin`) or do only continuous math, where
a small sampling offset shows up as ≤1–3 LSB drift (exactly what the table shows:
8/10 at Δ1, Saturate Δ3, Dots Δ12). Dissolve's hard `b` comparison turns the same
small offset into full-color tint on/off → Δ198.

---

## 4. Where the fix lives

The divergence is a **runtime sampler-binding** difference, so the fix is in the code
that *binds* the second texture for rendering — **not** in the compiler:

- **`samples/ShaderFiddle.Web/ShaderFiddleGame.cs`** (the product-sample draw path):
  set `GraphicsDevice.SamplerStates[1] = SamplerState.LinearClamp` before the
  effect `SpriteBatch.Begin`.
- **`tests/ShadowDusk.BrowserTests/RefRenderer/Program.cs`** (the desktop reference
  renderer): the same pin, so the WebGL-vs-DesktopGL comparison stays apples-to-apples
  (both runtimes now use a *defined, identical* slot-1 state instead of two different
  defaults).

The emit/rewrite path was inspected and is **correct as-is**:
`src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs` (GLSL 1.40, `GlslEs=false`),
`src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs` (`PrecisionHeader`), and
`src/ShadowDusk.Core/MgfxWriter.cs` (writes `hasState=false` for samplers, deferring
to `GraphicsDevice.SamplerStates` — same as mgfxc). The only `src/` change made is a
**comment** in `MonoGameGlslRewriter.PrecisionHeader` documenting the precision
finding; the emitted bytes are unchanged.

---

## 5. Recommendation

1. **Adopt the slot-1 sampler-state fix (done).** It is the real fix, it is tiny, it
   makes the sample render exactly as desktop, and it makes the harness 10/10.
   *Trade-off:* none for the 9 passing shaders — they only use slot 0, so pinning
   slot 1 is a no-op for them (re-verified: their numbers are unchanged). It is also
   what a real game would do: a shader that takes a second texture should bind that
   sampler's state rather than rely on an undefined default.

2. **Do NOT change the precision qualifier to `highp` (kept at `mediump`).** It is a
   **no-op in the SwiftShader harness** (proven) and would make our GLSL diverge from
   mgfxc's for no *verified* gain. The ~380-px residual (Δ128, 0.145%, at the band
   edge) is within the harness budget and is the kind of localized edge drift already
   tolerated (cf. Saturate Δ3, the discard boundary). If a future test on **real WebGL
   hardware** shows a precision-sensitive shader's edge needs it, revisit `highp` then
   — the rewriter comment records this. (This honors "no substitute / no masking":
   we are not widening tolerance; we removed a real, systematic binding error and the
   remainder genuinely fits the existing budget.)

3. **Not a KNIFX-v11 trigger.** Confirmed: this is a dialect/runtime binding issue,
   not a container-format one — a `KNIF` v11 writer would not have helped. The Phase
   24 / long-standing carry-forward ("9/10 render-equivalent, Dissolve diverges") is
   now **closeable**: with the slot-1 binding fixed, MGFX v10 renders **10/10
   pixel-equivalent** in real KNI WebGL.

---

## 6. Verification (real headless re-runs)

All runs: `node publish-sample.mjs` (or targeted recompile) + `node run-harness.mjs`,
headless Chromium ANGLE/SwiftShader, KNI WebGL, 512×512, reference = same bytes on
desktop DesktopGL (`RefRenderer`).

| Run | Corpus | Precision | slot-1 pinned? | Dissolve result | Corpus result |
|---|---|---|---|---|---|
| A (reproduce Phase 24) | mgfxc golden | mediump | no | **FAIL** Δ198, 4406/262144 (1.68%) | 9/10 pass, 10/10 load |
| B | ShadowDusk own | mediump | no | **FAIL** Δ198, 4406 (1.68%) — *identical to A* | 8/10 pass (Pixelated load-fail†), 9/10 load |
| C | ShadowDusk own | **highp** | no | **FAIL** Δ198, 4406 — *byte-identical to B* (precision = no-op) | same as B |
| D | ShadowDusk own | mediump | **yes** | **PASS** Δ128, **380/262144 (0.145%)** | 9/10 pass (Pixelated load-fail†) |
| **E (final, reviewer state)** | **mgfxc golden** | mediump | **yes** | **PASS** Δ128, **380/262144 (0.145%)** | **10/10 pass, 10/10 load** |

Run A == Phase 24's recorded numbers exactly (toolchain reproduces). B vs A proves the
divergence is **not** mgfxc-specific (our own output diverges identically). C vs B
proves **precision is irrelevant here**. D vs C (only the slot-1 pin changed) proves the
**slot-1 pin is the fix**. E is the state a reviewer gets from the committed corpus +
the fix: **all 10 shaders load and render within tolerance, harness exits 0.**

† **Out-of-scope finding (NOT Dissolve):** when the corpus is compiled by **ShadowDusk**
(runs B–D), `Pixelated.mgfx` *fails to load* in KNI WebGL with
`'roundEven' : no matching overloaded function found` — ShadowDusk emits `roundEven()`,
which GLSL ES 1.00 / WebGL1 lacks (mgfxc's golden uses a different construct, so it
loads). The committed corpus is the golden, so this does not affect the Phase 24
result, but it is a **real gap in ShadowDusk's own WebGL output** that the golden
corpus masks. Recommend a separate follow-up: lower `roundEven` → `floor(x+0.5)` (or
equivalent) for the GL/WebGL profile in the rewriter.

### Desktop regression check
- `dotnet test` (filtered): `ShadowDusk.GLSL.Tests` 9/9 (incl. `PrecisionHeaderIsPrepended`),
  `ShadowDusk.Compiler.Tests`, `ShadowDusk.Integration.Tests` 14/14 — all pass.
- Byte-identity / determinism / mgfxc cross-validation tests run separately (desktop;
  the `#ifdef GL_ES` block is inactive on desktop, so the precision comment-only change
  cannot affect them). The slot-1 pin is in the sample + RefRenderer only (neither is
  in `ShadowDusk.slnx` test discovery).

---

## Files touched
- `samples/ShaderFiddle.Web/ShaderFiddleGame.cs` — pin `SamplerStates[1] = LinearClamp` before the effect draw (the fix).
- `tests/ShadowDusk.BrowserTests/RefRenderer/Program.cs` — same pin for the desktop reference (keeps the comparison apples-to-apples).
- `src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs` — **comment only** (records the precision finding; bytes unchanged).
- `tests/ShadowDusk.GLSL.Tests/MonoGameGlslRewriterTests.cs` — unchanged net (highp experiment reverted).
- `tests/ShadowDusk.BrowserTests/{RESULTS.md, references/*.png}` — regenerated by the final passing run.
- `tests/ShadowDusk.BrowserTests/compile-corpus-sd.mjs` — investigation helper (compile the corpus with ShadowDusk's own CLI to A/B against the goldens); not required by the committed harness.
