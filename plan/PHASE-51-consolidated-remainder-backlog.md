# Phase 51 — Consolidated remainder backlog (render-rung tails & ext-blocked validation)

**Status:** 🔵 **Open (collector phase, created 2026-06-28).** This phase exists so that
no *otherwise-finished* phase has to sit open at "95% with 1-2 tail items." The parent
phases below were each substantively complete; their **single remaining work items were
moved here verbatim** and the parents were archived to `plan/DONE/`. This doc is the live
home for those tails. Nothing here blocks v1.0 or the drop-in `mgfxc` promise; each item is
either an *additive* validation rung or is gated on an **external** dependency.

**Why this phase exists (owner directive, 2026-06-28):** *"I don't want to leave a phase doc
95% complete sitting open for 1 or 2 remainder items."* When a phase is done except for a
small tail, the tail moves here and the parent moves to DONE.

---

## Provenance — where each item came from

| Item | Source phase (now archived) | Original status when moved |
|---|---|---|
| Browser diagnostics squiggle confirmation | [Phase 38](DONE/PHASE-38-wasm-compile-diagnostics.md) | 🟢 Implemented; only the in-browser confirmation rung left |
| DeferredSprite GL MRT render proof (GAP-2) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | Closed at compile + structural-match; render rung left |
| Apos.Shapes render-proof (Option B) | [Phase 49](DONE/PHASE-49-apos-shapes-regression-corpus.md) | Option A shipped; Option B render-proof decision-gated |
| GL macro-defined techniques (GAP-1 / GL) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | DX + FNA closed; GL faithfulness-blocked |
| DX12 / DXIL render-validation (Area C) | [Phase 35](DONE/PHASE-35-forward-version-support.md) | Built; ext-blocked on MonoGame 3.8.5 stable |
| Un-park Vulkan trigger (Area D) | [Phase 35](DONE/PHASE-35-forward-version-support.md) | ext-blocked on MonoGame 3.8.5 stable |
| DX/FNA/KNI-DX render-in-CI gates | [Phase 44](DONE/PHASE-44-validation-breadth-and-matrix-coverage.md) | Effectively done; ext-blocked on a WARP CI runner |
| `d3dcompiler_47` vs `fxc.exe` DXBC delta study (OQ#2) | [Phase 41](DONE/PHASE-41-fxc-oracle-monogame-fidelity.md) | Deferred, low-value |

---

## A. Internally actionable (no external dependency)

### A1 — Browser compile-diagnostics squiggle confirmation rung
*From Phase 38 (WASM compile diagnostics).* The code is done and headless-verified: bad
HLSL in the browser now yields `file:line:col: error: message` (not the opaque
`[object WebAssembly.Exception]`), with **G1 byte-identity 10/10** preserved and the new G2
diagnostics gate green. The C# reformatter is desktop-unit-tested and the format matches.

**Remaining (verbatim from Phase 38):** *"run the sample in a real browser and confirm the
squiggle/gutter lands on the offending line (the C# reformatter is desktop-unit-tested and
the format matches, so this is a confirmation rung, not a risk)."*

**Done = ** the `samples/ShaderFiddle.Web` sample, run in a real KNI/Blazor browser, shows
the diagnostic squiggle/gutter on the offending line for a deliberately-broken `.fx`.

### A2 — DeferredSprite GL true 2-attachment MRT render proof (Phase 41 GAP-2)
*From Phase 41.* GAP-2 (Nez DeferredSprite failed on GL with `Semantic COLOR is invalid`)
is **closed at compile + golden-structural-match (2026-06-27)** via the GL-only
`GlStructOutputColorRewriter` (DX byte-identical) plus the true-MRT `gl_FragData[0]` slot-0
fix. One render rung remains.

**Remaining (verbatim from Phase 41):** *"a true MRT render proof (bind 2 render targets,
draw, read back BOTH attachments, compare to mgfxc) needs a NEW render driver — the current
GL render gates are single-target only."*

**Done = ** a new `validation/*` GL driver that binds 2 render targets, draws DeferredSprite,
reads back BOTH attachments, and asserts pixel-equivalence to the `mgfxc` golden (rung-4
pattern). Background: [structural-divergence-matrix.md](DONE/PHASE-41-appendix/structural-divergence-matrix.md).

### A3 — Apos.Shapes render-proof (Phase 49 Option B, decision-gated stretch)
*From Phase 49.* Option A (the Gum / Apos.Shapes compile-regression corpus) shipped
2026-06-27. Option B is a real but owner-decision-gated render-proof stretch — **not blocked**.

**Remaining (verbatim from Phase 49):** *"generate the `mgfxc` (GL/DX) and `fxc /T fx_2_0`
(FNA) goldens for `apos-shapes.fx`, build/extend a render driver that draws Apos.Shapes
geometry (its vertex format carries the shape parameters in the `TEXCOORD` attributes, so
this is a real vertex-buffer harness, not a fullscreen triangle), and assert pixel-equivalence
to the reference compiler per the `validation/*` rung-4 pattern + the Windows render gate."*

**Done = ** `apos-shapes.fx` rendered pixel-equivalent to `mgfxc`/`fxc` in real MonoGame
GL/DX (and FNA fx_2_0) via a vertex-buffer Apos.Shapes harness, behind the Windows render gate.

---

## B. Externally blocked (gated on an outside event)

### B1 — DX12 / DXIL render-validation (Phase 35 Area C)
*From Phase 35.* The DXIL path is **already built**; what is missing is render-validation in
a real MonoGame **DX12** runtime, which only MonoGame 3.8.5 provides — currently preview only.

**Remaining (verbatim from Phase 35):** *"Render-validate the **already-built** DXIL path in
a real MonoGame **DX12** runtime. Seamless means ShadowDusk emits whatever the consumer's
DirectX runtime loads (DXBC for DX11, DXIL for DX12) **automatically** — the consumer never
picks DXBC vs DXIL or SM5 vs SM6."* Open design Q (same shape as Phase 33's one-blob problem):
*"can one artifact serve both, or must it be auto-detected from the target? Resolve
reproduce-first."* DX11 DXBC (vkd3d) stays the default.

**Blocked on:** MonoGame 3.8.5 going **stable** (do not target a preview as the product baseline).

### B2 — Un-park Vulkan (Phase 35 Area D → trigger for Phase 32)
*From Phase 35.* 3.8.5's Vulkan runtime + DXC→SPIR-V profile give [Phase 32](PHASE-32-vulkan-backend.md)
a **render target + mgfxc oracle** — the exact blocker that parked it. When a consumer's game
targets Vulkan, it just works (the platform the consumer already runs, not a ShadowDusk flag).
This item is the **trigger/validation**; [Phase 32](PHASE-32-vulkan-backend.md) (still open) is
the implementation.

**Blocked on:** MonoGame 3.8.5 going **stable**.

### B3 — GL macro-defined techniques (Phase 41 GAP-1 / GL) — faithfulness-blocked
*From Phase 41.* GAP-1 (the `TECHNIQUE()` macro idiom invisible to the pre-preprocess
technique count) is **closed on DX and FNA**; GL remains and is **faithfulness-blocked, not
merely hard**. The stock effects expand to the legacy DX9/SM2 branch on GL (because `mgfxc`'s
own OpenGL target defines only `{MGFX, GLSL, OPENGL}` — no SM4 — and ShadowDusk faithfully
mirrors that), and DXC's native SPIR-V codegen crashes (uncatchable AV) on that legacy SM2
HLSL. Defining SM4 in the GL recovery path is **rejected** — it would diverge from `mgfxc`
(a different effect on GL), breaking THE PURPOSE.

**Remaining:** a faithful GL fix needs **either (a)** DXC not crashing on legacy SM2 SPIR-V
codegen, **or (b)** a provably behavior-preserving managed legacy→modern HLSL transcription —
both real projects, not a pipeline tweak. **Not a blocker for the motivating use case:** Gum
targets MonoGame **DX** and **FNA**, where macro-technique recovery is already proven. Pinned
by `OpenGl_MacroTechniqueEffect_KeepsLoudSd0010_NoCrash` and
`GumFnaSampleShader_MacroTechnique_OpenGl_KeepsSd0010_GlMacroModelGap`.

**Blocked on:** an upstream DXC fix, or a dedicated managed-transcription project.

### B4 — DX / FNA / KNI-DX render-in-CI gates (Phase 44)
*From Phase 44.* Desktop + browser **GL** render-in-CI is done (`validation-render.yml`,
ubuntu/llvmpipe). The DirectX-family gates can't run in CI: there is no verified headless
D3D path on the runners (Mesa is GL-only).

**Remaining (verbatim from Phase 44):** *"the **DX / FNA / KNI-DX** render gates
(`validation/CandidateDx`, `VsDrivenDx`, `DxModernFeatures`, `FnaValidation`,
`KniWinFormsDX`) need a **Windows runner with a software D3D driver (WARP)** — unverified, so
deliberately not wired yet."*

**Mitigation already in place:** these are a baked-in **local pre-release gate**
(`validation/run-windows-render-gates.ps1`, required by the `/release` skill), so the product
bar is enforced outside CI. **Blocked on:** a verified WARP-on-GitHub-Actions story.

---

## C. Deferred / low-value (record only, no action unless triggered)

### C1 — `d3dcompiler_47` vs `fxc.exe` DXBC delta study (Phase 41 OQ#2)
*From Phase 41.* **Deferred as low-value.** The structural matrix already shows ShadowDusk's
DX output matches the `mgfxc` (fxc-derived) goldens wherever it compiles, so the Phase 18
oracle choice is evidenced indirectly. **Pick this up only if** a specific DX divergence ever
needs the `fxc`-vs-`d3dcompiler` distinction.

---

## Definition of done for this phase

This collector phase closes when every **A** item is render-proven (or explicitly retired by
the owner) and every **B** item has either landed (its external gate cleared) or been promoted
to its own scoped phase. The **C** item needs no action; it is recorded so the question is not
re-asked from scratch. Until then, this is the single open home for these tails instead of
keeping five 95%-complete phases on the board.
