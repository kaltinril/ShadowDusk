# Phase 49 — Gum / Apos.Shapes regression corpus (Gum / FlatRedBall real-world shaders)

**Status:** Option A implemented (2026-06-27) — vendored, probed, classified, wired, suite green. Option B (render-proof) deferred.

## As-built (2026-06-27)

Option A (compile-regression) is done on branch `phase-49-apos-gum-regression-corpus`. The Phase-0 compile probe (CLI, GL/DX/FNA) produced the authoritative classification:

| Shader | OpenGL | DirectX_11 | FNA | Wired as |
|---|---|---|---|---|
| `third-party/Apos.Shapes/apos-shapes.fx` | ✅ PASS | ✅ PASS | ❌ `X0000` (no SM3/FNA profile branch; dense PS over the fx_2_0/SM3 ceiling) | GL + DX cells |
| `third-party/Gum/MonoGameInCode-Grayscale.fx` | ✅ PASS | ✅ PASS | ✅ PASS | all-runtime (+ `Sm3Corpus()`) |
| `third-party/Gum/KniInCode-Shader.fx` | ❌ deprecated-effect-syntax | ❌ `X0000` | ✅ PASS | FNA only |
| `third-party/Gum/FnaSample-Shader.fx` | ❌ `SD0010` | ❌ `X0000` | ❌ `SD0010` | **known-failure pin (Phase 41 GAP-1)** |

**Headline:** `apos-shapes.fx` — the shader Gum's shape rendering actually depends on — **compiles on GL and DX**, the targets Gum ships on. **Finding:** Gum's `FnaSample-Shader.fx` confirms **Phase 41 GAP-1** (the `TECHNIQUE()` macro idiom is invisible to `FxPreParser` → `SD0010`); pinned by a known-failure test that flips when GAP-1 is fixed.

**Landed:** both `LICENSE` + `NOTICE.md` per directory (both MIT — Apos.Shapes commit `3fb73b8d…`, Gum commit `771bc5c3…`); shader bodies verbatim (verified); wiring in `ThirdPartyShaderCorpusTests` (full inline paths per §5.4) + `FnaCompileFixtureTests.Sm3Corpus()` (the all-runtime Grayscale); `Phase41StructuralDivergenceMatrixTests` census auto-regenerated; `docs/test-shader-corpus.md` §4 updated. `THIRD-PARTY-NOTICES.txt` deliberately NOT touched (it covers redistributed native binaries only; Nez isn't in it either). **No `src/` change** → no output-byte change → Windows render gate not triggered. Full `dotnet test ShadowDusk.slnx` green (1910 passed, 0 failed, 0 skipped).

**Remaining:** Option B render-proof (deferred); `plan/plan.md` index row (add when merging); fixing GAP-1 is Phase 41's job (recommended as a follow-on).

---

**Track:** Correctness / drop-in `mgfxc` fidelity — third-party shader corpus breadth
**Depends on:** Phase 45 (FX pre-parser robustness) · the existing `tests/fixtures/shaders/third-party/` fixture wiring this phase plugs new files into (`ThirdPartyShaderCorpusTests`, the auto-glob censuses) — referenced for *mechanics only*, not as a requirements bar
**Owner-context:** Requested directly. **Victor Chelaru (vchelaru)** — creator of **Gum** (the UI tool) and FlatRedBall, and the filer of issues [#7](https://github.com/kaltinril/ShadowDusk/issues/7) (KNI HiDef `gl_FragColor`) and [#28](https://github.com/kaltinril/ShadowDusk/issues/28) (sync `Compile()`) in this project — uses **Apos.Shapes** for Gum's UI shape rendering and asked us to make sure those shaders work through ShadowDusk. Adding them as a permanent regression set is the durable way to guarantee that.

**Scope note (corrected after the 2026-06-27 review):** the headline target is the one `Apos.Shapes` shader, but the **Gum repo itself ships its own `.fx` shaders** (in its sample projects) that exercise syntax Apos.Shapes does *not* — and that overlap known ShadowDusk gaps (the `TECHNIQUE()` macro idiom = Phase 41 GAP-1; `vs_4_0_level_9_1`/`ps_4_0_level_9_1` profiles; legacy `uniform extern texture` / `sampler_state { Texture = <…> }` / `: VIEWPROJ` / `: COLOR`). They are in scope for this phase as a second vendored set (see §3.2 / §4a). "Make sure Gum's shaders work" is the real ask, and Gum's shader surface is *Apos.Shapes plus those.*

- Upstream UI tool: <https://github.com/vchelaru/gum>
- Upstream shader library: <https://github.com/Apostolique/Apos.Shapes>

---

## 1. Why this phase exists

Gum (and any game using Apos.Shapes for SDF shape rendering) ships **exactly one** `.fx` shader: `Source/Content/apos-shapes.fx`. If ShadowDusk is to be a true drop-in `mgfxc` replacement **for the people who actually asked us to be one**, that shader has to compile and render correctly through our pipeline — and it has to *stay* compiling as the parser/transpiler/writers evolve. The way we guarantee that is the same way we guaranteed it for the Nez corpus in Phase 45: **vendor the real, shipping shader verbatim and pin it as a compile-level regression input**, classified per target with a documented reason for any target it cannot take.

Apos.Shapes is an unusually dense stress test: one large, feature-dense effect rather than a small post-process pass. In a single file it exercises, all at once:

- A **VS + PS effect** (vertex-driven, not PS-only) with a **10-interpolant** I/O struct (`TEXCOORD0..TEXCOORD9` plus `SV_Position`).
- A `float4x4 view_projection` uniform applied with `mul()` in the VS.
- **Two samplers**, one with an **explicit `register(s0)`** (`TextureSampler : register(s0)`) and one without (`FontSampler`).
- A `__KNIFX__` / `OPENGL` / else **macro target-branch** selecting `vs_4_0`/`ps_4_0` vs `vs_3_0`/`ps_3_0` — directly relevant to ShadowDusk's KNIFX-container output (Phase 35).
- A **Newton-iteration `for` loop** with a runtime-bounded trip count (`newton_steps`, up to 12) inside `EllipseSDF` — a genuine loop, not a literal-unrolled one.
- **`int` locals** (`newton_steps`, loop counter `i`), **relational ternaries**, **chained `if/else if` shape dispatch** (11 branches), float **`%` modulo** (`Mod`), `frac`, `atan2`, `pow`, `clamp`, `smoothstep`, `normalize`, **`discard`** (clip-rect reject), **`tex2D`**, and a 2x2 `mul`/`float2x2`.
- A non-trivial amount of math (Oklab color conversion, ~11 gradient functions) that pushes instruction counts toward the SM3 ceiling on the FNA path.

That is precisely the bug surface Phase 45 was opened for (relationals, ternaries, helper bodies, dropped operators, reserved-word joins), concentrated in one file that a real, named user depends on. A regression on any of it would silently break Gum.

---

## 2. Scope and the evidence bar

**In scope (the committed deliverable):** vendor `apos-shapes.fx` verbatim into `tests/fixtures/shaders/third-party/Apos.Shapes/`, classify the targets it compiles on, and pin it as **compile-level regression coverage**. The baseline bar is simply *a green compile to a well-formed container, asserted on every run* — the same kind of coverage the existing third-party fixtures already get (we reuse their plumbing; we're not measuring ourselves against them).

**The honest distinction (read this before promising more):**

- **Compile-regression (rung 1-2)** is the floor and the committed deliverable. It is **not** a pixel-equivalence claim — a green compile to a well-formed container, asserted on every run.
- **Render-equivalence to `mgfxc`/`fxc` (rung 4)** is a *legitimate stretch for this shader specifically*, because unlike the ShaderToy frontend (which has no `mgfxc` oracle), `apos-shapes.fx` is an ordinary `.fx` that `mgfxc` and `fxc` compile. So we **can** generate a golden and render-prove it the way the rest of the corpus is proven. Whether to do that in this phase or defer it is a **decision point** (§6) — the render driver work is real and this shader's geometry-from-vertex-attributes draw is not a trivial fullscreen-triangle harness.

We do **not** modify the shader source (verbatim-vendoring rule), and we do **not** change any existing output byte — this is purely additive corpus breadth.

---

## 3. Provenance / licensing (vendoring is clean)

### 3.1 Apos.Shapes (the headline shader)

- **Project:** Apos.Shapes
- **Author / copyright:** Copyright (c) 2021 **Jean-David Moisan** (Apostolique)
- **License:** **MIT** — vendorable under ShadowDusk's existing third-party-fixture policy (MIT/BSD/Apache-2.0/public-domain allowed). Fetch the verbatim upstream `LICENSE` alongside the shader.
- **Repository:** <https://github.com/Apostolique/Apos.Shapes>
- **Upstream path:** `Source/Content/apos-shapes.fx`
- **Pin (commit SHA, for reproducibility):** `3fb73b8d0a51f86678269a4ad28391459cc771b1` (resolved 2026-06-27; re-confirm at vendor time and record the exact SHA in the NOTICE).

### 3.2 Gum's own sample shaders (the review's Gap C — these are real and in scope)

The 2026-06-27 review correctly caught that the doc had *assumed* Gum routes all rendering through Apos.Shapes. It does not: a `*.fx` enumeration of `vchelaru/gum` (default branch `main`, resolved 2026-06-27) finds **three Gum-authored shaders in its sample projects**, each exercising distinct syntax worth pinning:

| Upstream path | What it exercises (distinct from Apos.Shapes) |
|---|---|
| `Samples/FnaGum/FnaSample/Content/Shader.fx` | The **`TECHNIQUE()` / `SAMPLE()` `#define` macro idiom** (a technique defined entirely inside a macro) — this is **Phase 41 GAP-1** (macro-defined techniques invisible to `FxPreParser` → `SD0010`). Plus `uniform extern texture`, `vs_1_1`/`ps_2_0` profiles, premultiply-alpha + linearize helpers. **The single most valuable file in this phase** for catching a known product gap. |
| `Samples/MonoGameGumInCode/MonoGameGumInCode/Content/Grayscale.fx` | `vs_4_0_level_9_1` / `ps_4_0_level_9_1` **level_9 profiles** (Phase 48 `KnownProfiles`), `Texture2D` + `sampler2D` + `sampler_state`, `: COLOR0` PS output, PS-only technique. |
| `Samples/KniGumInCode/KniGumInCodeContent/Shader.fx` | Legacy D3D9 `uniform extern texture`, two `sampler_state { Texture = <CurrentTexture>; … }` blocks, a `float4x4 … : VIEWPROJ` **matrix semantic**, `: COLOR` PS output, `clip()`. |

- **Project:** Gum
- **Author / copyright:** Gum contributors (Victor Chelaru et al.)
- **License:** **MIT** — confirmed 2026-06-27 (`vchelaru/gum` SPDX `MIT`, `LICENSE.md`). Vendorable under the same third-party-fixture policy; commit the verbatim `LICENSE.md`. (Fallback retained for completeness: if a future re-fetch finds a non-vendorable license, author equivalent ShadowDusk-owned `examples/Ex*.fx` fixtures that reproduce the same syntax instead.)
- **Repository / branch:** <https://github.com/vchelaru/gum> (`main`); pin the exact commit SHA at vendor time.

These are **sample/demo shaders, not Gum's core rendering library** (Gum's library leans on `SpriteBatch` + Apos.Shapes for shapes), but they are genuine, Vic-authored real-world `.fx` and they hit syntax the rest of the corpus under-covers — so they directly serve the "more syntax to validate" goal. Vendor them into a sibling `third-party/Gum/` directory under the same verbatim rule.

**Verbatim rule (both sets):** the shader code is copied byte-for-byte. The *only* permitted change is a prepended provenance/attribution comment block (project, repo URL, commit SHA, upstream path, license, one-line "what it exercises" note). No statement, declaration, technique, profile, or whitespace inside the original source is altered. Commit each upstream `LICENSE` verbatim in its directory. (`tests/fixtures/shaders/third-party/Nez/NOTICE.md` is a concrete example of this provenance-header format if you want one to copy.)

---

## 4. Open questions the Phase-0 compile probe must answer (do NOT guess)

Classify by *actually compiling each shader on each target*, not by reading the source. The following are the specific risks this shader raises; the probe resolves each into a fact for the NOTICE table.

1. **10 `TEXCOORD` interpolants on the GL path.** Nez `Reflection.fx` is **GL-excluded** because its multi-`TEXCOORD` *cbuffer* interpolant block could not be expressed in std140/std430 by SPIRV-Cross (`SD0100`). Apos.Shapes carries even more interpolants — **but** here they are **VS-output varyings, not a cbuffer**, which is a different packing path, so it may well be fine. **Verify, do not assume.** If it fails, capture the exact diagnostic code and record it as a documented, legitimate limit (or, if it is a ShadowDusk defect, that is a finding worth its own fix — this shader exists in the wild on KNI OpenGL via Gum, so `mgfxc` clearly accepts it).
2. **The `__KNIFX__` macro.** The shader selects `vs_4_0`/`ps_4_0` when `__KNIFX__` is defined. Confirm **whether ShadowDusk defines `__KNIFX__` when `CompilerOptions.Container = Knifx`** (Phase 35). If it does not, the KNIFX output silently takes the `else` (SM4) branch anyway, which may be fine — but the *intent* matters for fidelity, and "does our preprocessor expose the same target macros `mgfxc`/KNI's build does?" is a real drop-in-faithfulness question. Record the answer; open a follow-up if `__KNIFX__` should be defined and isn't.
3. **FNA / SM3 ceiling.** The FNA target is vkd3d SM &lt;= 3. The `EllipseSDF` Newton loop + the Oklab/gradient math is a lot of instructions, and the shader's *own* `OPENGL` branch already drops to `ps_3_0`, so SM3 *should* be expressible — but the instruction count, the runtime-bounded loop, and the 10 interpolants (D3D9 ps_3_0 input-register limit) are all places SM3 can reject. Probe `PlatformTarget.Fna` and record the result; an SM3 rejection here is a *legitimate* limit to document, not necessarily a defect.
4. **`register(s0)` + an unregistered second sampler.** Confirm both samplers bind correctly on all targets that compile (explicit-`register` plus implicit-slot mixing has bitten the corpus before — Phase 40 sampler-block fidelity).
5. **`discard` + `SV_Position0` / `SV_TARGET`.** Standard, but confirm under the VS-driven path on each target.
6. **The Gum sample shaders (§3.2) — expect at least one real finding.** Probe each on GL/DX/FNA. `FnaGum/Shader.fx`'s `TECHNIQUE()` macro is **Phase 41 GAP-1** — if `FxPreParser` still counts techniques pre-preprocess it will fail `SD0010` on **every** target, which is the gap surfacing, not a legitimate limit. Decide in this phase whether GAP-1 is fixed here (it would make the macro-technique idiom actually work, a real fidelity win Vic's own shader needs) or recorded as a known-failing fixture with a tracked follow-up. Confirm the `level_9_1` profiles (`Grayscale.fx`) and the legacy `uniform extern texture` / `: VIEWPROJ` / `: COLOR` forms (`KniGumInCode/Shader.fx`) compile or fail with a documented reason.

Each answer becomes a row/footnote in the NOTICE table with the exact diagnostic code for any exclusion.

---

## 5. Tasks

- [ ] **5.1 Vendor the shaders.** Create `tests/fixtures/shaders/third-party/Apos.Shapes/` and fetch `apos-shapes.fx` at the pinned SHA. If Gum's license is vendorable (§3.2), also create `tests/fixtures/shaders/third-party/Gum/` and fetch the three Gum sample shaders. Prepend the provenance comment block to each (no other change), and fetch each upstream `LICENSE` verbatim into its directory.
- [ ] **5.2 Write a `NOTICE.md`** in each vendored directory, cloning `third-party/Nez/NOTICE.md`: upstream project/author/repo/license, pinned commit, fetch date, the `curl` command used, the verbatim-rule statement, and the per-target classification table — **filled in from the §4 probe results**, not guessed. One row per file, with a footnote per excluded target giving the exact diagnostic code and the reason it is a legitimate shader-model limit (or a tracked defect + follow-up).
- [ ] **5.3 Run the Phase-0 compile probe** (§4) on `OpenGL`, `DirectX_11`, and `Fna` for **every** vendored file. Capture exact exit codes / diagnostics. This produces the classification; everything downstream consumes it. Treat a `TECHNIQUE()`-macro `SD0010` on `FnaGum/Shader.fx` as the Phase 41 GAP-1 finding, not a benign limit (§6 decision).
- [ ] **5.4 Wire them into the harness.** Add each file to `ThirdPartyShaderCorpusTests.cs` in the per-target `TheoryData` sets (`OpenGLShaders`/`DirectXShaders`/`FnaShaders`) it is classified for, with an inline comment noting what it exercises. The existing `[Theory]`/`[FnaTheory]` bodies already assert a well-formed `MGFX`/`fx_2_0` container, so no new assertion code is needed — only membership in the right sets. **`Root` fix (do this, don't add a second constant):** the harness hardcodes `Root = "third-party/Nez/"` ([ThirdPartyShaderCorpusTests.cs:51](../tests/ShadowDusk.Integration.Tests/ThirdPartyShaderCorpusTests.cs#L51)) and every Nez entry is written `Root + "File.fx"`. A *second* root constant cannot re-prefix those existing entries, so adding the new files as `Root2 + "…"` would be inconsistent. Instead, add the new files as **full inline relative paths** (e.g. `"third-party/Apos.Shapes/apos-shapes.fx"`, `"third-party/Gum/FnaSample/Shader.fx"`); `TestHelpers.FixturePath` joins `fixtures/shaders/<string>` so a literal path resolves correctly. (Leave the existing `Root + "…"` Nez entries untouched.)
- [ ] **5.5 Confirm the two census mechanisms — they are DIFFERENT (review Gap B):**
  - **`Phase41StructuralDivergenceMatrixTests`** uses a *recursive auto-glob* of `tests/fixtures/shaders/**/*.fx` ([Phase41StructuralDivergenceMatrixTests.cs:483-492](../tests/ShadowDusk.Integration.Tests/Tests/Phase41StructuralDivergenceMatrixTests.cs#L483-L492)), so the new files are picked up **automatically** — no change needed; just confirm they appear in the GL+DX structural census.
  - **`FnaCompileFixtureTests.Sm3Corpus()`** is a **hardcoded string list** ([FnaCompileFixtureTests.cs:125-142](../tests/ShadowDusk.Integration.Tests/Tests/FnaCompileFixtureTests.cs#L125-L142)), NOT a glob. If a file is classified FNA all-runtime, it must be **manually added** there, or it will silently miss the SM3 census cell.
- [ ] **5.6 Document them in `docs/test-shader-corpus.md` §4** (the third-party corpus section), adding Apos.Shapes and Gum rows to the table with classification and "what it exercises / why excluded" notes, in the format the other rows already use.
- [ ] **5.7 Update `THIRD-PARTY-NOTICES.txt`** (`src/ShadowDusk.HLSL/THIRD-PARTY-NOTICES.txt` and any aggregate notice) to list Apos.Shapes (MIT, Jean-David Moisan) and Gum (MIT) if vendored third-party shaders are tracked there. Confirm whether test-fixture shaders belong in that file or only the directory-local `LICENSE`/`NOTICE.md`.
- [ ] **5.8 Run the full regression suite** — `dotnet test ShadowDusk.slnx` — and confirm green (the corpus compile, the pre-parser unit tests, the structural census). Per `CLAUDE.md`, a subset run is not sufficient for a corpus/parser-touching change. If GAP-1 is fixed in this phase, that change touches the FX pre-parser → also run the Windows render gate per `CLAUDE.md`.
- [ ] **5.9 (Decision-gated, §6) Render-proof.** If the owner elects the stretch: generate the `mgfxc` (GL/DX) and `fxc /T fx_2_0` (FNA) goldens for `apos-shapes.fx`, build/extend a render driver that draws Apos.Shapes geometry (its vertex format carries the shape parameters in the `TEXCOORD` attributes, so this is a real vertex-buffer harness, not a fullscreen triangle), and assert pixel-equivalence to the reference compiler per the `validation/*` rung-4 pattern + the Windows render gate.

---

## 6. Decision point: compile-regression only, or also render-proof?

This is the one genuine fork and should be an explicit owner choice, captured here.

- **Option A — compile-regression only (low cost, ship now).** Tasks 5.1-5.8. Guarantees the shader keeps compiling to a well-formed container on every classified target and can never silently regress. Does **not** assert it renders identically to `mgfxc`. This is the recommended *first* deliverable: it is the bulk of the protective value (Gum breaks loudest at compile time) for a fraction of the effort.
- **Option B — also render-proof (rung 4, higher cost).** Adds 5.9. Stronger ("renders like `mgfxc` in the real engine") and legitimate for this shader because it has a real `mgfxc`/`fxc` oracle. Costs a bespoke render driver (Apos.Shapes' attribute-packed geometry is not a fullscreen-triangle case) plus golden generation plus the Windows render gate. Best done as a follow-on once Option A is in.

**Recommendation:** ship Option A in this phase; track Option B as a tracked follow-on (it is real rung-4 work and shouldn't gate the protective compile coverage Vic actually asked for).

---

## 7. Acceptance criteria

- `apos-shapes.fx` (and, if Gum's license permits, the three Gum sample shaders from §3.2) are vendored verbatim (provenance header only) under `tests/fixtures/shaders/third-party/Apos.Shapes/` and `tests/fixtures/shaders/third-party/Gum/`, each with the verbatim upstream `LICENSE` and a complete `NOTICE.md`.
- Every vendored file is classified by an **actual compile probe** on GL / DX / FNA, with every exclusion backed by a recorded exact diagnostic code and a one-line legitimate reason (or a filed defect + follow-up if an exclusion turns out to be a ShadowDusk bug — given Apos.Shapes ships in the wild on KNI, a GL/DX rejection deserves real scrutiny; the `TECHNIQUE()`-macro `SD0010` is a known gap, Phase 41 GAP-1).
- Each file is exercised by `ThirdPartyShaderCorpusTests` on exactly its classified targets (via full inline relative paths, per the 5.4 `Root` fix) and appears in the Phase-41 structural-divergence auto-glob census; any FNA all-runtime file is manually added to `FnaCompileFixtureTests.Sm3Corpus()`.
- `docs/test-shader-corpus.md` §4 and the third-party notices reflect every vendored file.
- `dotnet test ShadowDusk.slnx` is green (plus the Windows render gate if GAP-1 is fixed here, since that touches the pre-parser).
- (If Option B is chosen) `apos-shapes.fx` is render-equivalent to `mgfxc`/`fxc` on its classified targets, proven via a `validation/*` driver and the Windows render gate.

---

## 8. Notes / risks

- **This shader may surface a real defect, not just add coverage.** It is materially more complex than the rest of the vendored corpus, and it ships in production on KNI OpenGL via Gum — so a GL or DX *compile* rejection from ShadowDusk would more likely indicate a ShadowDusk gap than a legitimate shader-model wall. Treat any such rejection as a finding to investigate (and, per `CLAUDE.md`, every fixed bug earns its own permanent regression fixture/test), not merely a row to mark "excluded."
- **Few shaders, high symbolic value.** File count is low (one Apos.Shapes `.fx` + up to three small Gum samples), but these are the shaders the Gum/Apos ecosystem depends on, and the requester is a repeat, named stakeholder. Getting them green (and Apos.Shapes ideally render-proven) is a concrete, citable "ShadowDusk runs the real Gum UI shaders" result.
- **The Gum `TECHNIQUE()` macro is the highest-leverage item here.** It is `FnaGum/Shader.fx`'s real syntax AND it is Phase 41 GAP-1 (macro-defined techniques → `SD0010`). Fixing it in this phase would close a known product gap with a Vic-authored shader as its regression fixture — strongly aligned with "make sure Gum's shaders work." If it is instead deferred, record it as a known-failing fixture with an explicit follow-up so it is never silently forgotten.
- **Licensing is clean for both.** Apos.Shapes is MIT (Jean-David Moisan) and Gum is MIT (confirmed 2026-06-27) — both vendorable under the existing third-party-fixture policy. No licensing blocker remains.
- Add this phase to the `plan/plan.md` index (Active & Planned table + the "Correctness / drop-in `mgfxc` fidelity track" / third-party-corpus grouping) when it lands.
