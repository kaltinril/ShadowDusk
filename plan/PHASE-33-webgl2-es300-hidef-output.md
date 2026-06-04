# Phase 33 — KNI HiDef / WebGL2 (GLSL ES 3.00) compatibility

**Status:** 🟡 Planned — root cause **verified** 2026-06-03; second review pass (consumer-invisibility + adversarial + KNI-runtime) folded in 2026-06-03. Reframed from "add a new output target" to "close a one-spot fidelity gap."
**Tracks:** GitHub issue [#7](https://github.com/kaltinril/ShadowDusk/issues/7) — *"Add WebGL2 / GLSL ES 3.00 output target (for KNI HiDef profile)"*, filed by Victor Chelaru (FlatRedBall) for the **XnaFiddle** in-browser KNI runner.
**Roadmap track:** Fidelity / reach (alongside Phase 28).

---

## TL;DR — the seamless fix

**The bug is NOT that ShadowDusk lacks a WebGL2/ES-3.00 emitter. It is that ShadowDusk emits the pixel-shader colour output as a *raw* `gl_FragColor` write instead of mgfxc's `#define ps_oC0 gl_FragColor` form.** KNI's HiDef/WebGL2 runtime *already* auto-converts the legacy GLSL in a `.mgfx` to ES 3.00 at load time — but its converter only rewrites the `#define`-aliased output (mgfxc's form), so ShadowDusk's raw `gl_FragColor` slips through and crashes under WebGL2.

**Fix:** make `MonoGameGlslRewriter` emit `#define ps_oC{N} gl_FragColor` / `gl_FragData[N]` and write to `ps_oC{N}` (exactly what mgfxc does — verified in the golden). Result, with **zero consumer input, no new `CompilerOptions`/`PlatformTarget`, no new `.mgfx` format, and no KNI changes**:

- One `.mgfx` loads & renders in KNI **Reach** (WebGL1) *and* **HiDef** (WebGL2) — KNI does the ES-3.00 conversion itself.
- It also keeps working on Desktop GL.
- ShadowDusk's output moves **closer** to the mgfxc golden — strictly *more* faithful, not a divergence.

This is the "just works / as seamless as we can make it" outcome. The consumer does **nothing** except (one-time) recompile their `.fx` and be on **KNI ≥ v3.14.9001** — both documentation matters, neither a code/flag change. See *§ Seamlessness scorecard* and *§ The migration gap*.

---

## THE PURPOSE anchor

Serves THE PURPOSE: *"the same `mgfxc`-equivalent result, produced where `mgfxc` can't run."* Today a ShadowDusk `.mgfx` works in a KNI **Reach** game but **fails to load in a KNI HiDef game**. The gap is a fidelity divergence from `mgfxc` that only surfaces under KNI's WebGL2 path. ⚠️ The fix must **not** regress the validated Reach/WebGL1 path (Phases 17/22/23/24), Desktop GL, or DirectX — and it shouldn't, because it makes GL output match the `mgfxc` golden more closely.

---

## The problem (what breaks, and the verified root cause)

ShadowDusk emits the **legacy MojoShader GLSL dialect** (`varying`, `texture2D()`, `#ifdef GL_ES precision mediump float;`, `ps_uniforms_vec4[]`). Valid anywhere a GL context still has `gl_FragColor`:

- ✅ Desktop MonoGame / KNI DesktopGL (compatibility GLSL)
- ✅ KNI **Reach** → WebGL1 → GLSL ES 1.00 *(verified end-to-end in XnaFiddle per issue #7)*

It fails under a strict GLSL ES 3.00 context:

- ❌ KNI **HiDef** → WebGL2 → GLSL ES 3.00 — `gl_FragColor` was removed from the language.

### Reported failure (issue #7)
`new Effect(graphicsDevice, mgfxBytes)` into a KNI **HiDef** game (KNI 4.2.9001, squarebananas/kniSB) throws in `ConcreteShader.CreateShader`:
```
ERROR: 0:16: 'gl_FragColor' : undeclared identifier
ERROR: 0:16: 'assign' : l-value required (can't modify a const)
...
```

### Verified root cause (this is the key finding)
1. **mgfxc/MojoShader emits the fragment output as a `#define` alias**, not a raw write — **verified in our own golden** `tests/fixtures/golden/OpenGL/Grayscale.mgfx`, which contains the literal line `#define ps_oC0 gl_FragColor` and writes to `ps_oC0` (it also uses `varying` + `texture2D`).
2. **KNI's WebGL2/HiDef loader auto-converts legacy GLSL → ES 3.00 at runtime.** `ConcreteShader.CreateShader` → `ConvertGLSLToGLSL300es` (present in **both** `nkast/Kni` *and* the bug-report fork `squarebananas/kniSB`, `main`) prepends `#version 300 es` and rewrites: `varying`→`in/out`, `attribute`→`in`, `texture2D/3D/Cube`→`texture`, `precision mediump`→`highp`, and crucially **`#define X gl_FragColor` → `out vec4 X;`** (and `#define X gl_FragData[n]` → `layout(location=n) out vec4 X;`).
3. **ShadowDusk inlines a *raw* `gl_FragColor` write** at [MonoGameGlslRewriter.cs:246-247](../src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs#L246-L247) — no `#define`. KNI's converter regex matches only the `#define`-aliased form, so ShadowDusk's raw `gl_FragColor =` survives untouched into the ES-3.00 context → `undeclared identifier`.

> So the HiDef failure is a **fidelity gap from `mgfxc`**, surfaced by KNI's WebGL2 conversion path. Fixing the output to match `mgfxc` fixes HiDef *for free*.

### MonoGame HiDef nuance (a question raised this session — now answered)
- **MonoGame *does* support `GraphicsProfile.HiDef`** — it's a **capability tier** (SM/sampler/RT/texture limits), present in MonoGame mainline. HiDef is **not** a KNI-only concept. *(Earlier framing that implied otherwise was wrong.)*
- What is KNI-WebGL-specific is the **context mapping**: KNI maps `Reach → WebGL1`, `HiDef → WebGL2` (strict ES 3.00). **Verified:** MonoGame mainline never switches shader *dialect* by profile — `Shader.OpenGL.cs` compiles the GLSL payload **as-is** (`GL.ShaderSource`), and MonoGame has **no working browser backend** (`Shader.Web.cs` is a `throw new NotImplementedException()` stub). So the precise statement is *"HiDef-on-WebGL2 (a KNI WebGL behavior)"*, **not** *"HiDef is KNI-only."*

---

## § KNI HiDef runtime & the oracle question *(R3 — done)*

| Question | Answer (sourced) |
|---|---|
| Profile→context | KNI BlazorGL `ConcreteGraphicsDevice`: `Reach → WebGL1`, `HiDef`/`FL10_0 → WebGL2`. ✅ confirms the reporter. |
| Does KNI patch or compile as-is? | **Both, by container.** *Legacy MGFX v10* (what ShadowDusk writes): runtime-patched by `ConvertGLSLToGLSL300es` under WebGL2. *KNIFX (v11)*: stores a pre-built ES-3.00 blob, loaded as-is. |
| Naming/binding under HiDef | **Unchanged from desktop GL.** Samplers bound by name `ps_s0/ps_s1…`; uniforms as a `vec4[]` array named `ps_uniforms_vec4` (no native UBOs). ✅ ShadowDusk keeps its current naming — only the fragment-output `#define` needs fixing. |
| MGFX container | KNIFX v11 = multi-variant directory keyed by `(major,minor,es)`; **legacy MGFX v10 = single blob** consumed via the runtime-patch path. |
| Same-backend oracle? | **Yes** — KNI's effect compiler `ShaderProfileGL.cs` (`ConvertGLSL110ToGLSL300es`) emits the canonical ES-3.00 form; ShadowDusk could diff against KNIFXC output. *(KNIFXC may be Windows-only at build time — [UNCONFIRMED]; not required for the fix.)* |
| MonoGame HiDef | Capability tier, yes; no dialect-by-profile switch; no working browser backend (see nuance above). |

### § KNI converter completeness & version floor *(review pass — sourced 2026-06-03)*

**(a) Version floor — KNI ≥ v3.14.9001.** KNI's runtime legacy→ES-3.00 converter (`ConcreteShader.ConvertGLSLToGLSL300es`, originally `ConvertGLES100ToGLES300`) was **added 2024-09-08 (PR #1833, commit `8ff252ed0`) and first shipped in `v3.14.9001`** (2024-09-23). It is **absent in `v3.13.9001` and earlier** (those pass GLSL straight to `GL.ShaderSource`, no conversion). Present v3.14 → v4.2.9001 → `main`; the reporter's v4.2.9001 qualifies. Consumers on older KNI get no conversion → the fix can't help them. This is a **documentation** floor — the compiler must **not** enforce it (no KNI dependency in the compiler; that would break "self-contained").

**(b) Converter is complete for everything ShadowDusk emits EXCEPT the `texture2DLod/Proj/Grad` family.** From [KNI `ConcreteShader.cs`](https://github.com/kniEngine/kni/blob/main/Platforms/Graphics/.BlazorGL/Shader/ConcreteShader.cs):

| Construct ShadowDusk emits | KNI converts it? |
|---|---|
| `#define X gl_FragColor` → `out vec4 X;` | ✅ (the fix relies on this) |
| `#define X gl_FragData[n]` → `layout(location=n) out vec4 X;` | ✅ (MRT) |
| `texture2D/3D/Cube(` → `texture(` | ✅ |
| `varying`→`in/out`, `attribute`→`in`, `precision mediump`→`highp`, `#version 300 es` prepended | ✅ |
| **`texture2DLod(` / `texture2DProj(` / `texture2DGrad(`** | ❌ **GAP** — KNI's regex `texture(2D\|3D\|Cube)(?=\()` requires `(` immediately after the suffix, so the Lod/Proj/Grad variants are skipped → undefined in ES 3.00 → HiDef compile fail. *(Current corpus emits none — verified — so it doesn't block #7; see Task 3b + Risks.)* |
| raw `gl_FragColor` (no `#define`) | ❌ — only the `^#define`-anchored form is matched (this is exactly why ShadowDusk's raw write fails). |

**Two hard constraints the fix must honor:** KNI's regex is `^#define …` (`Multiline`), so the alias must be **at column 0 on its own line**; and for the post-conversion `out vec4 ps_oC0;` to be valid GLSL it must sit at **global scope before `main()`** — i.e. emit it in the header block, where the golden puts it.

---

## § Emission change-set *(R1 — done; superseded by the seamless fix)*

The **only** change needed for HiDef is the fragment-output form. The rest of the legacy dialect ShadowDusk already emits (`varying`, `texture2D`, `mediump`) is exactly what KNI's converter expects and handles.

**The fix** in [MonoGameGlslRewriter.cs:246-247](../src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs#L246-L247):

| | Today (raw — breaks HiDef) | Fix (mgfxc form — works everywhere) |
|---|---|---|
| Single output | `out_var_SV_Target` → `gl_FragColor` | `out_var_SV_Target` → `ps_oC0`, **emit** `#define ps_oC0 gl_FragColor` |
| MRT output | `out_var_SV_Target{N}` → `gl_FragData[N]` | `out_var_SV_Target{N}` → `ps_oC{N}`, **emit** `#define ps_oC{N} gl_FragData[N]` |

Match the golden's placement (the `#define`(s) sit near the top with the precision header). Everything else in the rewriter is unchanged.

> **Rejected heavier alternative (R1's first proposal):** a parallel `GlslDialect.Es300` rewriter that runs SPIRV-Cross at `#version 300 es`, drops the `roundEven`→`floor` lowering, keeps native `out`/`texture()`/UBOs, etc. This *would* produce a native ES-3.00 blob — but it requires emitting the **KNIFX v11 multi-variant container** (new writer + the consumer's KNI must read it), introduces a profile decision, and buys nothing because KNI already adapts the single legacy blob. Keep only as a **far-future** option if a shader ever needs ES-3.00-only constructs the legacy dialect can't express.

---

## § Selection surface & container *(R2 — done)*

- **Dual-emit into one `.mgfx` is NOT feasible** in ShadowDusk's current format: [`MgfxWriter`](../src/ShadowDusk.Core/MgfxWriter.cs) stores exactly **one blob per shader stage** (keyed by an `isVertexShader` bool) and one profile byte in the header — no per-profile GL variant slot. (KNIFX v11 *does* have a multi-variant directory, but that's a different writer + a KNI-only container.)
- **Good news: dual-emit is unnecessary.** The seamless fix produces **one** legacy blob that serves both profiles, so no container/format/profile-byte change is needed. `PlatformTarget`/`MgfxProfile`/`CompilerOptions` are untouched.
- **No new `MgfxProfile` byte** (would force a KNI loader upgrade and break ABI — the profile byte stays `0 = OpenGL`).

---

## § Chosen "just works" design *(R4 — done)*

**Verdict: emit `mgfxc`'s exact `#define`-aliased fragment output; KNI's own loader self-adapts it to ES 3.00. One blob, both profiles, zero consumer input, no KNI change, no new format. Strategy = "single universal payload," achieved by *closing a fidelity gap* rather than adding a target.**

| Strategy | Verdict |
|---|---|
| 1. Dual-emit both variants | ❌ Blocked by the single-blob container; and unnecessary (KNI adapts the one blob). |
| **2. Single universal payload (the `#define` fix)** | ✅ **Chosen.** The universality is *KNI's* runtime conversion, not a `#if __VERSION__` payload trick (which can't work — ES requires `#version` on line 1, controlled by the runtime). No flag, no format change, strictly more `mgfxc`-faithful. |
| 3. Runtime auto-select via `GraphicsDevice.GraphicsProfile` | ❌ Moot once #2 lands (one blob serves both); useless at build time (no device); would re-introduce a profile decision. |

**Default behavior:** the rewriter always emits the `#define` form for all GL output (it's `mgfxc`'s own output and works on WebGL1, WebGL2, and Desktop GL). **No new consumer-facing flag is required.** Add a defensive `CompilerOptions` opt-out *only* if regression testing finds a Desktop-GL driver that dislikes the `#define` alias (none expected). Do **not** add a `Reach/HiDef` enum — that would leak the profile choice to the consumer, violating "seamless."

### § Seamlessness scorecard — what must the consumer do? (goal: nothing)

| Concern | Status |
|---|---|
| New flag / API / `CompilerOptions` knob to get HiDef-correct output | **None** — verified: `IShaderCompiler.CompileAsync(hlsl, options)` takes no profile arg, [`CompilerOptions`](../src/ShadowDusk.Core/CompilerOptions.cs) has no Reach/HiDef field, and CLI + WASM + library all route through the same `MonoGameGlslRewriter`; the `#define` change is default-on for all GL output. |
| Code change to switch Reach ↔ HiDef | **None** — the same single `.mgfx` serves both. |
| KNI version | **KNI ≥ v3.14.9001** (doc note only — see converter-floor subsection). |
| Recompile existing `.fx` | **Yes — the one unavoidable action** (see migration gap). |

**Rule for implementers:** if any task introduces a flag/enum the consumer must set to get HiDef-correct output, that is a *defect* — reject it. The only sanctioned new option is the defensive Desktop-GL opt-OUT above (default on).

### § The migration gap — the single thing a consumer must do

"Just works" covers **freshly-compiled** output only. A `.mgfx` built by an **older** ShadowDusk still contains the raw `gl_FragColor` write and **still fails in KNI HiDef** — upgrading the package does not heal already-built artifacts. Minimize and surface it:
- **Recompile note** (release notes + README known-issues): *"Upgrade ShadowDusk **and recompile your `.fx`** — pre-existing `.mgfx` keep the old fragment output and still fail under KNI HiDef/WebGL2."*
- **Self-diagnosis (grep-able):** a *broken* `.mgfx` contains a raw `gl_FragColor =` write with **no** `#define ps_oC0 gl_FragColor`; a *fixed* one contains the `#define`. A consumer can check a given artifact without guessing.
- **Discoverability:** CHANGELOG + README lines that quote the **exact error string** (`'gl_FragColor' : undeclared identifier`) so a web/grep search lands on the fix; close issue #7 with the resolution + recompile note (that comment becomes the top hit).

---

## § Validation plan *(R4 — done)* — reproduce-first (RED) → fix → validate-same-FX (GREEN)

**Verdict: build the KNI HiDef/WebGL2 Playwright harness FIRST and use it to *reproduce Vic's exact failure* on current (unfixed) `main` — a confirmed RED baseline on a specific raw `.fx`. Apply the fix. Then re-run the *same* harness on the *same* `.fx* — it must flip to GREEN (loads + renders). One harness, used as both the repro and the proof; rung-4 bar = "compiles + renders within tolerance vs the Reach render of the same bytes" (KNI-vs-KNI; no `mgfxc` WebGL2 oracle exists → legitimate "reach `mgfxc` can't" validation).**

### Step A — Reproduce (RED), before any code change
Pin a single canonical raw shader as **the repro case** so repro and validation use byte-identical source: **[tests/fixtures/shaders/Grayscale.fx](../tests/fixtures/shaders/Grayscale.fx)** (minimal PS-only; its current ShadowDusk output emits a raw `gl_FragColor` write — exactly Vic's trigger). Then:
1. Build the HiDef harness capability: add `GraphicsDeviceManager.GraphicsProfile = GraphicsProfile.HiDef` to [ShaderFiddleGame.cs](../samples/ShaderFiddle.Web/ShaderFiddleGame.cs), gated by a `?profile=hidef` query param (mirror the existing `?test=` hook). KNI maps HiDef→WebGL2 automatically; the `index.html` `getContext` shim already accepts `webgl2`.
2. Add a `--corpus=sd-hidef` mode to [run-harness.mjs](../tests/ShadowDusk.BrowserTests/run-harness.mjs) (own publish/captures/results dirs; follow the existing `IS_SD`/`IS_FAITHFUL` pattern), passing `?profile=hidef`.
3. Compile `Grayscale.fx` with **current `main` (unfixed)** ShadowDusk → `.mgfx`; `new Effect(gd, bytes)` in the HiDef/WebGL2 context. **Assert it FAILS with `'gl_FragColor' : undeclared identifier`** (Vic's exact error). Record as `RESULTS-SD-HIDEF-REPRO.md` (the RED baseline).
> This both confirms we're fixing the *real* bug and upgrades the root cause from "verified by code+golden inspection" to "empirically reproduced in a real KNI HiDef runtime."

### Step B — Validate the SAME raw FX now works (GREEN), after the fix
Re-run the **exact same harness** on the **exact same `Grayscale.fx`**, now compiled by the **fixed** ShadowDusk: the previously-failing shader must now **load with no GLSL compile error and render within tolerance** vs its Reach render. The single assertion that expected the error in Step A now expects a successful load+render — a clean RED→GREEN on identical input. Then **broaden to the full corpus**: the 10 SM3 PS-only shaders (Grayscale, Invert, TintShader, Sepia, Saturate, Pixelated, Scanlines, Fading, Dots, Dissolve) → require **10/10 load** + 10/10 render within tolerance; write `RESULTS-SD-HIDEF.md`.

- **Reference for the render compare:** the **existing Reach desktop render of the same `.mgfx`** (`RefRenderer` stays the Reach baseline) — a divergence then isolates "KNI's ES-3.00 conversion of our bytes" from the compiler. Reuse `image-compare.mjs` + the existing tolerance ladder.
- **No byte-compare to `mgfxc`** (it has no WebGL2 emitter) — state explicitly this is KNI-itself reach validation.
- **Watch items:** KNI's converter forces `mediump`→`highp`; this may shift **Dissolve**'s discard boundary vs the Reach `mediump` render (the Phase 24 caveat). Treat a Dissolve delta as expected-different and **document**, don't mask. Dots (transcendental) may need its existing wider tolerance.

---

## Implementation tasks (ordered)

> **Red → green bracket on the SAME raw `.fx`.** Task 0 reproduces Vic's failure on current `main` (RED) via Playwright; Task 5 re-runs the identical harness + identical `.fx` after the fix (GREEN). Do **not** skip Task 0 — reproducing first proves we're fixing the real bug and turns the root cause from "inspected" into "demonstrated."

- [ ] **0. REPRODUCE FIRST (RED) — Playwright** — *before any compiler change.* Per § Validation plan Step A: build the HiDef harness (`?profile=hidef` knob in [ShaderFiddleGame.cs](../samples/ShaderFiddle.Web/ShaderFiddleGame.cs) + `--corpus=sd-hidef` in [run-harness.mjs](../tests/ShadowDusk.BrowserTests/run-harness.mjs)), compile **[tests/fixtures/shaders/Grayscale.fx](../tests/fixtures/shaders/Grayscale.fx)** with current (unfixed) ShadowDusk, load it via `new Effect` in a real KNI **HiDef/WebGL2** context, and **assert it FAILS with `'gl_FragColor' : undeclared identifier`** (Vic's exact error). Record `RESULTS-SD-HIDEF-REPRO.md`. *(Harness built here is reused unchanged in Task 5.)*
- [ ] **1. The fix** — in [MonoGameGlslRewriter.cs](../src/ShadowDusk.GLSL/MonoGameGlslRewriter.cs), map `out_var_SV_Target{N}` → `ps_oC{N}` and emit `#define ps_oC{N} gl_FragColor` (N omitted → `gl_FragColor`; N present → `gl_FragData[N]`), matching the golden. **Placement constraints (from KNI's converter — see §completeness):** emit each `#define` **at column 0, on its own line, in the header block before the body/`main()`** (so the post-conversion `out vec4` is at global scope and valid). Build the `#define` block as a **separate string assembled after the Pass-2 regex rewrites** so those passes never touch it. Replace the raw substitutions at lines 246-247.
- [ ] **2. Unit tests** — in [tests/ShadowDusk.GLSL.Tests/MonoGameGlslRewriterTests.cs](../tests/ShadowDusk.GLSL.Tests/MonoGameGlslRewriterTests.cs): (a) `#define ps_oC0 gl_FragColor` emitted + **no raw `gl_FragColor =` write** remains; (b) **placement** — `IndexOf("#define ps_oC0") < IndexOf("ps_oC0")` first-use, and the `#define` is at column 0; (c) **MRT** synthetic case (`out_var_SV_Target0/1/2` → `#define ps_oC0 gl_FragColor`, `#define ps_oC1 gl_FragData[1]`, `#define ps_oC2 gl_FragData[2]`); (d) **no-output / discard-only** shader → zero `#define ps_oC` lines, zero `gl_FragColor`; (e) **name-collision** defensive case (source already contains `ps_oC0`) → rename or fail loudly, never silently shadow.
- [ ] **3. Re-baseline (precise scope — verify each before assuming)** — GL output bytes change, so re-baseline: the rewriter expected-string tests in `MonoGameGlslRewriterTests`; the **byte-identity / determinism** tests (bytes change **once**, then must stay reproducible — if not byte-stable on re-run, a non-determinism bug was introduced); any **SPIR-V vs DXIL byte-identity** test (both paths must still emit identical bytes); the **mgfxc cross-validation** test (should *improve* — both now emit the `#define` form). Confirm the **image (PNG) goldens are unchanged** (`ps_oC0` ≡ `gl_FragColor` functionally — Phase 17/24 render parity must hold).
- [ ] **3b. Guard the `texture2DLod/Proj/Grad` gap** — KNI's converter does **not** rewrite these (confirmed from source), so a shader emitting them would fail in HiDef *even after the fix*. Current corpus emits none (verified). Add a **compile-time diagnostic** (or unit test) that fails loudly if rewriter output contains `texture2DLod/Proj/Grad`, so a future shader surfaces the issue at build time rather than silently in a HiDef browser. *(Full HiDef-safe emission of these = the Phase-34 follow-up below; not needed for #7.)*
- [ ] **4. Regression gate** — the same `.mgfx` still passes the existing Reach run (`--corpus=sd`) and all current GL/DX/byte-identity/image tests stay green.
- [ ] **5. VALIDATE SAME RAW FX (GREEN) — Playwright** — per § Validation plan Step B: re-run the **exact Task 0 harness** on the **exact same `Grayscale.fx`**, now built by the **fixed** compiler → must **load with no GLSL error and render within tolerance** vs the Reach render (the Task 0 assertion flips RED→GREEN). Then broaden to the full 10-shader corpus: **10/10 load** (kills #7) + 10/10 render within tolerance; write `RESULTS-SD-HIDEF.md`.
- [ ] **6. Housekeeping** — ✅ *done 2026-06-03:* corrected the Key Decision in [plan.md](plan.md) (HiDef runtime-conversion dependency noted) + added Phase 33 to the plan index + roadmap. *Remaining:* fix the under-specified [docs/research.md:737](../docs/research.md#L737); add a **CHANGELOG entry + README compatibility/known-issues line** quoting the exact error string and stating "upgrade + **recompile**; needs KNI ≥ v3.14.9001" (migration-gap discoverability); **close issue #7** with the resolution + recompile note; wire both harness runs (repro + validate) into [Phase 30 §16 CI](PHASE-30-cross-platform-ci.md).
- [ ] **7. Prove it to ourselves (standing guard)** — `samples/ShaderFiddle.Web` currently defaults to **Reach/WebGL1** ([ShaderFiddleGame.cs](../samples/ShaderFiddle.Web/ShaderFiddleGame.cs)), so our flagship sample never exercises the issue-#7 path. **Keep** the Task-0 `?profile=hidef` knob permanently (don't delete after validation) and make the **CI HiDef corpus run (Task 5 / Phase 30 §16) the continuous regression guard** for this exact failure. Do **not** flip the sample's interactive default to HiDef (Reach is the broader-compat default a consumer expects).

---

## Validation / success bar

- **Reach + Desktop GL + DX (regression gate):** all current tests green; image goldens unchanged; byte-identity 10/10 (re-baselined bytes).
- **HiDef (new, rung 4):** the corpus loads in a **real KNI HiDef / WebGL2** context (`new Effect` → no GLSL compile error → SpriteBatch render within tolerance). XnaFiddle / KNI HiDef is the real runtime → genuine rung-4 *reach* validation.
- **"Just works":** a scratch consumer compiles a `.fx` with **no profile-specific input** and the single `.mgfx` loads in both Reach and HiDef KNI games.

## Risks / open questions

- **KNI version floor — KNI ≥ v3.14.9001 (firm, sourced):** the converter was added in v3.14.9001 (PR #1833, commit `8ff252ed0`); ≤ v3.13.9001 has no conversion and our legacy GLSL fails under HiDef. The reporter's v4.2.9001 qualifies. **Communicate in docs only** (README compatibility line); the compiler must not enforce it.
- **`texture2DLod/Proj/Grad` converter gap (second latent bug, confirmed):** KNI's converter skips these variants — a shader emitting them fails in HiDef even after this fix. Corpus emits none today (verified); guarded by Task 3b; full handling deferred to the Phase-34 follow-up.
- **Migration gap:** pre-fix `.mgfx` stay broken until **recompiled** — the one consumer action; surfaced via the migration-gap section + housekeeping discoverability.
- **`mediump`→`highp` forcing** under HiDef may perturb Dissolve's boundary vs Reach — expected-different; document.
- **MRT (`gl_FragData`)**: corpus is single-output PS-only; emit the `#define gl_FragData[N]` form for correctness anyway (DeferredLighting is excluded from the OpenGL profile, so untested here).
- **Empirical confirmation:** the root cause is verified by code + golden inspection; **Task 0 (reproduce-first) upgrades it to a demonstrated RED** before the fix, and Task 5 is the GREEN proof (the same `.fx` rendering in KNI HiDef). If a shader still fails after the `#define` fix, there may be a *second* residual mismatch — investigate per-shader.

## Carry-forwards / out of scope

- **Native KNIFX v11 multi-variant emission** (a pre-built ES-3.00 blob in a KNIFX container) — only if a shader needs ES-3.00-only constructs the legacy dialect can't express. Not needed for issue #7.
- **`texture2DLod/Proj/Grad` HiDef-safe emission (→ Phase 34 follow-up)** — KNI's converter doesn't rewrite these; when a future shader needs them, ShadowDusk should emit the ES-3.00-convertible/native form itself (or the build-time guard from Task 3b rejects it). Corpus-unaffected today.
- **VS-driven effects under HiDef** — inherits from Phase 28; this phase targets the PS-only corpus first (mirrors Phase 17/18).
- **Desktop GL Core-profile (non-ES) ES-3.00 equivalent** — not required by issue #7.

---

### Research provenance
Findings R1–R4 gathered 2026-06-03 by parallel research agents (emission change-set, selection/container, KNI runtime + oracle, "just works" design + validation). Root cause cross-verified against the local golden `tests/fixtures/golden/OpenGL/Grayscale.mgfx` (`#define ps_oC0 gl_FragColor` present) and `MonoGameGlslRewriter.cs:246-247` (raw `gl_FragColor` emitted). **Second review pass (2026-06-03)** by three agents — consumer-invisibility, adversarial correctness, KNI-converter completeness — added: the **KNI ≥ v3.14.9001** version floor (PR #1833 / commit `8ff252ed0`), the **`texture2DLod/Proj/Grad` converter gap** (confirmed from KNI source; corpus emits none), the **migration gap** (pre-fix `.mgfx` need recompiling), the seamlessness scorecard, and the `#define` placement/regex-isolation constraints.
