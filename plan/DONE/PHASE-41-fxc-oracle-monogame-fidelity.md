# Phase 41 — `fxc.exe` oracle comparison & MonoGame fidelity matching

> **📦 Archived to DONE (2026-06-28).** Research complete; GAP-1 (DX + FNA) and GAP-2
> (compile + structural-match) closed. The remaining tails were **moved to
> [Phase 51](../PHASE-51-consolidated-remainder-backlog.md)**: GAP-2's MRT render proof (A2),
> GAP-1's faithfulness-blocked GL macro-techniques (B3), and the deferred `d3dcompiler_47`-vs-
> `fxc` delta study (C1).

**Status:** 🟢 **Research largely complete (2026-06-12).** The full-corpus structural divergence matrix (DX + GL) is built, committed, and triaged — see **Results** below and the data in [`PHASE-41-appendix/structural-divergence-matrix.md`](../PHASE-41-appendix/structural-divergence-matrix.md). It found **two real product gaps** (macro-defined techniques; DeferredSprite GL COLOR semantic) and confirmed every other divergence is known-render-equivalent. The optional `d3dcompiler_47`-vs-`fxc.exe` DXBC delta study (OQ#2) is the one remaining research item, deferred as low-value given the structural fidelity result. *(Renumbered from Phase 39 → 41: while this shell sat unstarted, Phases 39–40 shipped the FNA fx_2_0 target and consumed the `fxc /T fx_2_0` oracle for it. This phase is now scoped to the **remaining DX11 + GL** full-corpus fidelity matrix.)*
**Roadmap track:** Fidelity / completeness.

> **Why now:** the maintainer confirmed a working **`fxc.exe`** is installed locally (the real DirectX HLSL compiler `mgfxc` shells out to on Windows). That unlocks a *direct* fidelity oracle we did not previously have on hand. To date, DX fidelity has been validated against `d3dcompiler_47` (the fxc-faithful system DLL) + the `mgfxc` goldens (Phase 18), and GL against the `mgfxc` goldens (Phase 17) — both only over the **10-shader SM-PS-only corpus**. This phase is the standing home for "how close are we *really* to genuine `fxc`/`mgfxc`, across the *whole* corpus?" research, so findings land in a tracked place instead of scattered notes.

---

## Guardrail — read first

- **This is research/characterization, not a product change.** Any *behavioral* change this phase motivates (e.g. closing a divergence) goes through its own change with rung-4 validation; this phase's job is to **find, quantify, and document** gaps, and decide which are worth closing. Do not bump the MonoGame pin or the `MgfxVersion = 10` default here (see Phase 35 guardrails / memory `backwards-compat-monogame-382-mgfx-v10`).
- **Same-backend comparison only** (DX↔DX, GL↔GL) — never cross-backend (see `CLAUDE.md` → *What success actually means*).
- **`fxc.exe` is Windows-only and not redistributable.** It is a *local oracle for the maintainer's investigation*, exactly like `d3dcompiler_47` — it must **not** become a product dependency or enter the shipping pipeline. The cross-platform promise stands on DXC / vkd3d-shader / SPIRV-Cross; `fxc` is a measuring stick, not a component.

---

## Context snapshot (as of 2026-06-08 — re-verify when starting)

- **What's validated today:** Phase 17 (GL, 10/10 rung-4 vs `mgfxc`), Phase 18 (DX DXBC, 10/10 rung-4 vs `mgfxc` via both `d3dcompiler_47` oracle and vkd3d-shader), Phase 28 (VS-driven effects, maxΔ-0 vs `mgfxc` on GL + DX). All over the **SM3/SM5 PS-only + the VS corpus** — not the full fixture set.
- **Known, documented fidelity gap:** global cbuffer **initializers** are dropped by DXC vs `fxc`/`mgfxc` (stored default `0`) — see `docfx/guides/parameters-and-caveats.md`. Characterized but not closed; the recommended pattern is `SetValue` / inline literals.
- **Oracle relationships:** `mgfxc` (Windows) → `fxc.exe` for DX HLSL → DXBC. ShadowDusk's DX path uses **vkd3d-shader** (shipping, cross-platform) with **`d3dcompiler_47`** as the Windows oracle. `d3dcompiler_47` and standalone `fxc.exe` are closely related but **not guaranteed identical** — having `fxc.exe` lets us check that assumption directly.
- **Goldens:** `tests/fixtures/golden/{DirectX_11,OpenGL}/` hold reference `.mgfx`. Provenance / how they were produced: confirm they came from genuine `mgfxc` and whether the full corpus (not just the validated 10) has goldens.

---

## Open questions this phase should answer

1. **How faithful is ShadowDusk to genuine `fxc`/`mgfxc` beyond the validated corpus?** Compile the **full** `tests/fixtures/shaders` corpus through ShadowDusk *and* through real `mgfxc`/`fxc`, and produce a **divergence matrix** (per shader, per backend: loads? renders-equivalent? structural `.mgfx` diff? where it diverges and why).
2. **Is `d3dcompiler_47` a sound stand-in for `fxc.exe`?** Compare DXBC from ShadowDusk's `d3dcompiler_47` backend vs standalone `fxc.exe` on the corpus — quantify any divergence so Phase 18's oracle choice is evidenced, not assumed.
3. **Which fidelity gaps actually matter?** Triage each divergence: behaviorally-irrelevant (reordering, naming) vs render-affecting (the initializer gap class). Feed render-affecting ones into their own follow-up change.
4. **FNA — already covered.** The FNA fx_2_0 path now exists (`PlatformTarget.Fna`, Phases 39–40) and is validated against the `fxc /T fx_2_0` oracle (gate 17/17). It is **out of scope here** — this phase is the DX11 + GL matrix only; any further FNA divergence work belongs to the FNA phases, not this one.

## Work items (each a self-contained task an agent can take)

- [x] **Inventory the goldens & oracle availability** (2026-06-12): `fxc.exe` (D3D Shader Compiler 10.1, pin the `10.0.26100.0` x64 copy), `dotnet-mgfxc` 3.8.4.1, and `d3dcompiler_47` (10.0.26100.8655) all confirmed invocable on the maintainer's Windows box. **Caveat:** the local mgfxc is 3.8.4.1, NOT the 3.8.2.1105 the committed goldens came from — so the committed goldens stay the canonical reference and the local mgfxc is a forward-version oracle only, never regenerated over them. Corpus: 72 `.fx` (59 root + 13 `examples/`), 46 with committed DX11+GL goldens.
- [x] **Full-corpus divergence matrix (DX + GL)** (2026-06-12): built `Phase41StructuralDivergenceMatrixTests.GenerateDivergenceMatrixReport` in `tests/ShadowDusk.Integration.Tests` (reuses `MgfxBlobReader` + `EffectCompiler`), output [`PHASE-41-appendix/structural-divergence-matrix.md`](../PHASE-41-appendix/structural-divergence-matrix.md). Structural `.mgfx` compare done; **rung-4 render over the full corpus deferred** (the existing render harnesses cover the validated subsets with hand-written parameter inputs — full-corpus render is its own lift, and the structural compare is the high-information artifact).
- [ ] **`d3dcompiler_47` vs `fxc.exe` delta study (OQ#2):** NOT done — deferred as low-value. The structural matrix already shows ShadowDusk's DX output matches the mgfxc (fxc-derived) goldens wherever it compiles, so the Phase 18 oracle choice is evidenced indirectly. Pick this up only if a specific DX divergence ever needs the fxc-vs-d3dcompiler distinction.
- [x] **Triage + gap register** (2026-06-12): every divergence classified — see **Results** and **Gap register** below. The known **global cbuffer initializer** gap was re-confirmed (DXC zeroes cbuffer-global defaults vs fxc); it did not surface as a new structural divergence because `MgfxParameterMatch` compares metadata, not default VALUES — it remains a documented value-gap, recommended pattern `SetValue`/inline literals.
- [x] **Document outcomes** (2026-06-12): recorded here + appendix. No user-facing caveat in `docfx/` changed (the cbuffer-sizing and annotation divergences were already documented in Phase 43 / `glsl-uniform-naming.md`); the two new gaps are ShadowDusk-side and tracked below, not consumer caveats.

## Results (2026-06-12)

Full data: [`PHASE-41-appendix/structural-divergence-matrix.md`](../PHASE-41-appendix/structural-divergence-matrix.md). Headline over the 46 golden-backed fixtures × {DirectX_11, OpenGL} = **92 cells**:

- **64 structurally clean** (parameters, cbuffers, samplers, techniques/passes + render states, annotation counts all match the mgfxc golden). Bytecode bytes differ by construction (vkd3d/SPIRV-Cross vs fxc/MojoShader) and are correctly excluded — the bar is structural/behavioral equivalence, not byte-identity.
- **7 divergent, ALL known and render-equivalent** (no new fidelity problem):
  - *GL per-stage cbuffer sizing* (3: PolygonLight, SharedCbuffer, VertexAndPixel on GL) — mgfxc sizes `{vs,ps}_uniforms_vec4` to used-only members; ShadowDusk emits the full declared layout. Both internally consistent; the pinned, deliberate divergence already tolerated by `Phase43CbufferModelTests` (F4) and recorded in `docs/glsl-uniform-naming.md` (mgfxc+MonoGame GL is in fact broken for statically-partially-read uniform arrays; ShadowDusk emits the correct full layout).
  - *Anonymous-pass naming* (2: ClipShaderNew DX+GL) — mgfxc stores empty name, ShadowDusk synthesizes `P0`; MonoGame addresses passes by index. Irrelevant.
  - *Annotation counts* (2: annotations DX+GL) — mgfxc drops to 0, ShadowDusk preserves the declared count (Phase 43 F2 metadata). Irrelevant.
- **21 compile failures** — the real findings (see Gap register).

Non-golden census (26 fixtures × 2 = 52): **41 compile, 11 fail loudly with a code, none unexpectedly.** Of the 11: 3 SD0210 are correct GL guards (int/mat3/VS-texture, by design); 6 SD0010 are genuinely techniqueless fixtures (`minimal_vs_ps`, `passthrough_vs`, `textured_vs_ps` contain NO `technique` keyword — verified; correct behavior); 2 SD0001 are a **harness artifact** (`MinimalWithInclude.fx` needs `/I includes`, which the matrix runner did not pass — not a ShadowDusk defect).

## Gap register (the two real product gaps — each needs its OWN validated change, not this research phase)

### GAP-1 (HIGH) — macro-defined techniques are not detected → the MonoGame stock effects fail to compile

> **Status update (2026-06-27): GAP-1 is now closed on DX and FNA; only the GL macro-model gap remains.**
> - **DX:** closed by the gated zero-technique fallback in `CompilationPipeline.Run` (DXC `-P` expand + re-parse; the DX macro set selects the SM4 branch). All 6 stock effects + Penumbra* compile on DX; `DirectX_BasicEffect_MacroTechniques_*` golden-tested.
> - **FNA:** closed (2026-06-27) by extending the same recovery to `RunFna` (PreserveSm3 re-parse, **no** modern-branch gate — FNA's vkd3d SM1-3 compiles the legacy macro branch directly, no DXC-codegen crash risk). The SM2-fitting stock effects (SpriteEffect, AlphaTestEffect, DualTextureEffect, Penumbra*) now compile on FNA; BasicEffect/SkinnedEffect hit the honest SM2 register-file limit (`SD0305`) and Gum's FnaSample is declined for `vs_1_1` (`SD0300`) - honest downstream limits, not technique-blindness. Byte-identity preserved (the recovery only fires for zero-technique effects; every effect compiling before is untouched). Tests: `Phase41MacroTechniqueTests` (FNA cases).
> - **GL:** STILL OPEN. OpenGL is gated out of recovery: its macro set lacks SM4/SM6, so the stock effects expand to the legacy DX9/SM2 branch, which crashes DXC's native SPIR-V codegen (uncatchable AV). Closing GL needs a GL-macro-model change (Phase 35 territory), not a pipeline reorder. Pinned by `OpenGl_MacroTechniqueEffect_KeepsLoudSd0010_NoCrash` and `GumFnaSampleShader_MacroTechnique_OpenGl_KeepsSd0010_GlMacroModelGap`.
>   - **GAP-1/GL is faithfulness-blocked, not merely hard (analysis 2026-06-27).** The obvious shortcut — define SM4 in the GL *recovery* path so the effect takes the modern branch DXC can compile — must be **rejected**, because it would diverge from `mgfxc`. `mgfxc`'s own OpenGL target defines only `{MGFX, GLSL, OPENGL}` (no SM4 — exactly what `PlatformMacros.For(OpenGL)` mirrors), so `mgfxc` compiles these stock effects from the **legacy SM2 branch** (`fxc` → DX9 bytecode → MojoShader → GLSL). If ShadowDusk instead took the modern branch, its GL output would be a *different effect* than `mgfxc` emits on GL — a silent break of the "identical to `mgfxc`" promise (THE PURPOSE). So the macro set is correct as-is; the real blocker is that ShadowDusk's GL backend is DXC→SPIR-V and DXC crashes on the legacy SM2 HLSL `mgfxc` happily compiles. A faithful GL fix therefore requires either (a) DXC not crashing on legacy SM2 codegen, or (b) a provably behavior-preserving managed legacy→modern transcription — both real projects, not a pipeline tweak. **Not a blocker for the motivating use case:** Gum targets MonoGame **DX** and **FNA**, where macro-technique recovery is already proven; GL is the one backend where these specific stock effects can't currently be made faithful.

**Originally 20 cells / 10 fixtures** (state at 2026-06-12): `AlphaTestEffect`, `BasicEffect`, `DualTextureEffect`, `EnvironmentMapEffect`, `SkinnedEffect`, `SpriteEffect`, `PenumbraHull/Light/Shadow/Texture` all failed `SD0010: Effect source contains no techniques` on **both** DX and GL. (DX has since been closed by the fallback; FNA added 2026-06-27; GL remains.)

**Root cause:** `CompilationPipeline.Run` runs `FxPreParser.Parse` on the **raw** source (Stage 1) *before* the preprocessor flattens `#include`s and expands macros (Stage 2). `FxPreParser` (`src/ShadowDusk.HLSL/FxPreParser.cs:332-344`) deliberately ignores macro-call forms — a `technique` token followed by `(` is treated as a macro invocation and passed through, not counted. The standard MonoGame idiom declares techniques only via the `TECHNIQUE(name, vs, ps)` macro from `Macros.fxh`, which materializes a real `technique { pass {...} }` block only AFTER preprocessing. So `Techniques.Count == 0` → SD0010. mgfxc preprocesses first, so it sees them.

**Why it is not a one-line fix:** the pre-parser must *strip* technique/pass blocks before DXC sees them (DXC cannot parse FX technique syntax). Macro-expanded technique blocks only exist post-preprocessing, so a faithful fix means **preprocess (expand macros + flatten includes) THEN pre-parse/strip techniques THEN compile** — a pipeline-ordering change with broad blast radius (interacts with platform-macro injection, the FNA `PreserveSm3` path, parameter/annotation stripping). It must be designed and rung-4 validated on its own branch.

**Ready-made validation corpus:** all 10 fixtures HAVE committed 3.8.2.1105 goldens (DX + GL), so a fix converts directly into rung-4-validatable cells — closing this gap would substantially expand validated coverage to the actual MonoGame stock-effect family. The **DX cells are closed** (fallback) and the **FNA cells added 2026-06-27**; the **10 GL cells are the remaining ones, and they are faithfulness-blocked** per the GL bullet above (not a quick win — they need a non-crashing GL path for legacy SM2 HLSL, since the faithful GL output must come from the legacy branch `mgfxc` itself uses). So the highest-value *achievable* follow-up is render-proving the DX/FNA macro-technique cells against their goldens; the GL cells wait on the deeper GL-backend work.

### GAP-2 (MEDIUM) — DeferredSprite fails on the GL target with a COLOR semantic error — ✅ FIXED at compile + structural-match (2026-06-27)

> **Status: closed at compile + golden-structural-match; a true 2-attachment MRT render proof is the one remaining rung.**
> Two-part, byte-identity-safe fix (DX/vkd3d output verified byte-identical by md5; full suite green):
> - **PART A — `GlStructOutputColorRewriter` (new, `src/ShadowDusk.Compiler/Internal/`):** a GL-ONLY HLSL rewrite that runs DOWNSTREAM of the shared `FxPreParser`, applied to a GL-private copy of the source fed only to the OpenGL DXC compiles. It retargets a PIXEL-entry RETURN struct's `: COLOR<n>` members to `: SV_Target<n>` (so DXC's GL/SPIR-V backend accepts them). PS-output structs are identified from the already-parsed `Techniques[].Passes[].PixelEntryPoint` -> the entry's return-type struct; the PS-INPUT interpolant (`VertexShaderOutput.Color : COLOR0`) is never touched. The DX path keeps the untouched source, so DX bytes are unchanged.
> - **PART B — MRT slot-0 builtin (`MonoGameGlslRewriter`):** for true MRT (2+ outputs) slot 0 now emits `gl_FragData[0]` (not `gl_FragColor`). This is a RENDER-CORRECTNESS fix: in legacy GLSL with multiple render targets bound, `gl_FragColor` broadcasts to ALL attachments and corrupts the other target; `gl_FragData[0]` writes only attachment 0. Matches the mgfxc golden (`#define ps_oC0 gl_FragData[0]` + `[1]`). Single-output shaders keep `gl_FragColor`.
> - **Result:** `DeferredSprite [OpenGL]` now compiles and structural-matches its golden (Phase 41 census `OpenGL = OK`). Pinned by `GlStructOutputColorRewriterTests` + `HidefGeneralityFixtureTests.DeferredSprite_Mrt_CompilesOnGl_EmitsFragDataOutputs_Gap2`.
> - **Remaining rung:** a true MRT render proof (bind 2 render targets, draw, read back BOTH attachments, compare to mgfxc) needs a NEW render driver — the current GL render gates are single-target only. Tracked.

**1 cell**: `DeferredSprite [OpenGL]` fails `X0000: Semantic COLOR is invalid for shader model: ps` (it compiles fine on DX, and has a GL golden, so mgfxc handles it). A multi-render-target sprite effect: its pixel shader returns a STRUCT (`PixelMultiTextureOut`) whose members carry `: COLOR0` / `: COLOR1` MRT output semantics. DXC's HLSL->SPIR-V (the GL path) rejects `COLOR` as a PS *output*; vkd3d (the DX path) accepts it, which is why DX passes.

**Investigation (2026-06-27) — NOT a quick fix; constraints established empirically:**
- The existing `): COLOR<n>` -> `): SV_Target<n>` rewrite (`FxPreParser`, B6) only handles the **function-return** form, not **struct-member** outputs, so it does not fire here. It is also entry-point-aware (skips VS entries) and mode-aware (kept verbatim in PreserveSm3/FNA).
- A struct-member rewrite must be **struct-aware**: only the struct that is a PS entry's *return type* gets `COLOR<n>` -> `SV_Target<n>`. The SAME shader's `VertexShaderOutput.Color : COLOR0` is a PS *input* interpolant that DXC accepts and must NOT be rewritten. So the fix needs PS-entry -> return-type-struct -> members resolution (a deferred, multi-pass analysis like the existing B6 path).
- **It must be GL-only.** Empirically, rewriting the struct members to `SV_Target0/1` makes **GL compile**, but **changes the DX output** (2007 -> 1999 bytes) - DX currently compiles with `COLOR0/1` via vkd3d and structurally matches the golden, so a universal (pre-parser, target-shared) rewrite would **regress DX**. The transform therefore belongs on the GL compile branch only, not the shared `FxPreParser`/`StrippedHlsl`.
- **The GL MRT output needs verification.** The rewritten GL `.mgfx` is 1003 bytes vs the 1238-byte golden; a structural + **MRT render** proof (does MonoGame DesktopGL render both render targets like mgfxc?) is required before claiming correctness, and MRT is not covered by the current render gate.

**Verdict:** a real, scoped fix but a genuine feature (GL-only struct-aware COLOR-output rewrite + MRT golden/render validation), not a safe one-liner. Lower reach than GAP-1; deferred as its own validated change.

## Definition of Done

An honest, checked-in answer to "how close are we to genuine `fxc`/`mgfxc` across the whole corpus": a divergence matrix (DX + GL), the `d3dcompiler_47`-vs-`fxc.exe` delta quantified, a triaged gap register, and either a closed gap (with its own rung-4 validation) or a clearly-documented, justified divergence for each item. No product pin / default / pipeline change made *in this phase*; `fxc.exe` never enters the shipping pipeline.

**DoD status (2026-06-12):** divergence matrix ✅ (DX + GL, structural); triaged gap register ✅ (2 real gaps + 7 known-equivalent divergences, each justified); `d3dcompiler_47`-vs-`fxc` delta ⏸️ deferred (low-value given the structural result); no product change made here ✅. The two gaps are handed to their own follow-up phases — Phase 41's research job is done.

## Carry-forward / related

- **FNA effect-format work is done**, not a carry-forward: the legacy D3D9 fx_2_0 `.fxb` path shipped in **Phases [39](PHASE-39-fna-fx2-output-target.md)–[40](PHASE-40-fna-fidelity-hardening.md)** (vkd3d-shader SM1–3 + `Fx2EffectWriter`), rung-4 validated against the `fxc /T fx_2_0` oracle in real FNA. See `docfx/guides/choosing-a-target.md` for the user-facing statement.
- **XNB output** is deliberately **not** in ShadowDusk core — it belongs to the MGCB content-pipeline layer ([Phase 29](../PHASE-29-mgcb-content-processor-plugin.md)). See the `.mgfx`-vs-`.xnb` decision in `plan.md` → *Key Decisions Already Made*.
