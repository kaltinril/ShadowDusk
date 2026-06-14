# Phase 35 — Forward-version support & validation (newer MonoGame / MGFX / DX — seamless)

**Status:** 🟡 **Area A DONE (2026-06-05); B/C/D not started.** Scaffold created 2026-06-04.
**Roadmap track:** Forward-compatibility (newer versions, seamless).

> **Area A result (2026-06-05):** ShadowDusk's existing **v10 GL `.mgfx`** (product unchanged) **loads + renders pixel-equivalent** on **MonoGame.Framework.DesktopGL 3.8.4.1** (latest stable; 3.8.5 is preview-only) exactly as on the pinned **3.8.2.1105** — **10/10** of the SM3 PS-only corpus, max per-channel delta **0** vs the 3.8.2 renders of the *same bytes* (within tolerance ≤1 vs mgfxc goldens). Product pin (`Directory.Packages.props` = 3.8.2.1105) and default (`MgfxVersion = 10`) **untouched**; the newer runtime is referenced only by a separate `validation/ForwardCompat/` project via `VersionOverride`. Harness + matrix + re-runnable regression guard delivered. **Seamless: the consumer does nothing — their existing `.mgfx` just keeps working forward.** See Area A below + `validation/ForwardCompat/README.md`.

---

## ⚠️ TWO HARD GUARDRAILS — read before doing anything

1. **Seamless for the end user — always.** The consumer **never** chooses a version, target, or flag, and never takes a manual step to get correct output. ShadowDusk automatically produces output that **just works** on their runtime — old or new. *(Same rule as Phase 33/34: if any task would require the consumer to flip a flag / pick a version to get correct output, that is a **defect** — reject it. A flag may exist **only** as a non-required escape hatch, never the path to correct behavior.)*
2. **Backwards compatibility.** The **default output stays MGFX `v10` / the MonoGame 3.8.2.1105 baseline**, which loads everywhere. Nothing here may break older MonoGame/KNI consumers, change the existing OpenGL/DX11/v10 output, or bump the product's MonoGame pin. (See memory `backwards-compat-monogame-382-mgfx-v10`.)

Together: **support newer versions by making one output (or auto-selected output) that just works across old *and* new runtimes — with zero consumer input.** Not by asking the consumer to opt into anything.

---

## Context snapshot (as of 2026-06-04 — re-verify when starting)

> **2026-06-14 live-source re-verification (full detail: [shader-pipeline-landscape-2026-06.md](PHASE-35-appendix/shader-pipeline-landscape-2026-06.md)).**
> - **Both forks still use MojoShader for OpenGL** (verified against live source): KNI latest `v4.2.9001`
>   (Nov 2025; 2026 commits are non-shader) — `ShaderProfileGL.cs` "Use MojoShader to convert the HLSL
>   bytecode to GLSL"; MonoGame latest `v3.8.5-preview.6` (May 2026, **still preview**) — `ShaderProfile.OpenGL.cs`
>   "using MojoShader". Neither has shipped the DXC/SPIRV-Cross GL pipeline. So the "modern GLSL on OpenGL"
>   capability still has **no runtime** to consume it; KNIFX is a container over a still-MojoShader body.
> - **The MojoShader limit is OpenGL-only.** ShadowDusk's **DirectX** target already generates SM4/5 features
>   that the GL path rejects with SD0210 (empirically verified 2026-06-14: vertex texture fetch and
>   `Texture2DArray` compile on `DirectX_11`, SD0210 on `OpenGL`). The modern-shader frontier for these
>   engines is **DirectX (now) and Vulkan/DX12** (MonoGame 3.8.5), not OpenGL — i.e. Areas C/D below.

- **Pinned today:** MonoGame **3.8.2.1105** (`Directory.Packages.props`); MGFX default **v10** (`src/ShadowDusk.Core/CompilerOptions.cs` → `MgfxVersion = 10`).
- **Newer exists:** MonoGame **3.8.4.1** (latest *stable* 3.8.x, Area A validated); **3.8.5** is at **preview.6 (2026-05-22), still not stable** — its Vulkan + DX12 backends keep Areas C/D gated. KNI latest **v4.2.9001** (Nov 2025).
- **DXIL path:** DXC `ps_6_0`/`vs_6_0` → DXIL is **built** ("for DX12/KNI", Phase 4) but **never render-validated in a real MonoGame DX12 runtime** (none existed). `plan.md` calls DX12 "✅ Works" — structural/theoretical, not rung-4.
- **MGFX v11 / KNIFX — these are TWO different formats, not one (see [appendix research](PHASE-35-appendix/knifx-vs-mgfx-v11-research.md)):** MonoGame "v11" keeps the `MGFX` signature (loader accepts range [10,11]); KNI "v11" is **KNIFX**, a *new distinct signature* (KNI still reads `MGFX` v10 as a migration path). The `--mgfx-version 11` flag is a **non-faithful stub** — it bumps only the header byte on a v10 body, so it is **dead-on-arrival in KNI** and unvalidated in MonoGame; never advertise it as "v11 support." Phase 24 found v11 *not needed* for current render *parity* (v10 already renders in KNI WebGL) — but that predates KNI v4.02/KNIFX and weighed loadability, not the KNIFX XNA-compat render fixes. KNIFX background: `DONE/PHASE-33-webgl2-es300-hidef-output.md` § KNI converter.
- **Vulkan:** `PHASE-32-vulkan-backend.md` is **parked** on "no MonoGame/KNI Vulkan runtime + no mgfxc-Vulkan baseline." MonoGame 3.8.5 (Vulkan + a DXC→SPIR-V shader profile) **removes that blocker**.

---

## Work areas (each a self-contained task an agent can take)

### A. Forward-compat validation — ✅ **DONE (2026-06-05)**
Prove our existing **v10 output still loads + renders correctly** in MonoGame **3.8.4.1** (and 3.8.5 when stable), **without changing our pin or output**. This *is* seamless support for newer versions: the consumer's existing `.mgfx` just keeps working forward — they do nothing.
- Use a **separate** validation/test project referencing the newer MonoGame — do **not** bump the product's pin.
- Mirror the `validation/` rung-4 harness (real `Effect` load + render of the SM3 corpus).
- Deliverable: a **forward-compat matrix** (which v10 features load/render in which MonoGame versions) + a regression guard.

**Done — what was delivered (uncommitted; rung-4 render-validated on Windows DesktopGL):**
- **Version landscape (verified live 2026-06-05 vs nuget.org):** `3.8.4.1` is the latest **stable** 3.8.x and restores cleanly under the repo's `nuget.config` on `net8.0`. `3.8.5-*` is **preview/develop only** (not targeted; Areas C/D stay gated). So Area A targets **3.8.4.1**.
- **New project `validation/ForwardCompat/`** (mirrors `validation/Candidate/`) — a **version-matrix** harness: compiles the SM3 PS-only corpus with the **unchanged** `EffectCompiler` (default opts → **v10 GL `.mgfx`**) and renders those exact bytes in a **real `MonoGame.Framework.DesktopGL` `Effect`**, built+run **once per version** in the matrix `{3.8.2.1105 floor, 3.8.4.1 latest stable}` (extensible — add a version string to the runner's `-Versions`). The version is selected per-run via `-p:ForwardCompatMonoGameVersion=<v>` (`VersionOverride`); each cell writes to `output/versionmatrix/<v>/`. The product pin is untouched, the project is not in `ShadowDusk.slnx`, and is never packed. A **runtime-integrity guard** in `Program.cs` fails a cell if the loaded MonoGame version doesn't match the requested label (so a `VersionOverride` that silently didn't apply can't pass).
- **Version matrix** (full table in `validation/ForwardCompat/README.md`): **10/10** compile + load + render on **both** 3.8.2.1105 and 3.8.4.1; the two runtimes are **pixel-identical (maxΔ 0)** on the same bytes; each is **≤ tolerance** vs the mgfxc goldens (maxΔ ≤ 1, same as the original Phase 17 fidelity result).
- **Regression guard:** `validation/ForwardCompat/run-forwardcompat.ps1` (renders every matrix cell + the mgfxc baseline, then pixel-compares each version to the floor and to the goldens; exits non-zero on any divergence) backed by `validation/compare_forwardcompat.py`. Re-run instructions in the README.
- **Guardrails proven held:** `Directory.Packages.props` (3.8.2.1105) and `CompilerOptions.MgfxVersion` (10) have an empty `git diff`; full solution test suite green (551 tests, 0 failures). The only build error is the pre-existing WASM `dxcompiler.wasm` restore guard, unrelated to this work.
- **Forward-compat extends to the 3.8.5 preview too (source-verified, not yet render-run):** the 3.8.5 line bumps the runtime to `MGFXVersion = 11` **but** adds `MGFXMinVersion = 10`, so its `Effect` loader accepts the **range [10, 11]** — our **v10 output is accepted by 3.8.5 unchanged** (verified against `v3.8.5-preview.2` and `develop` tags of `MonoGame.Framework/Graphics/Effect/Effect.cs`). 3.8.4.1 keeps the strict `MGFXVersion = 10` exact-match (no `MGFXMinVersion`), which our v10 output satisfies. Net: **staying on v10 is forward-safe across 3.8.2 → 3.8.4.1 → 3.8.5-preview** — which is exactly why **Area B (emit v11) is very likely never required.** A render-run against 3.8.5 is deferred until it goes stable (Areas C/D gating). **Caveat (2026-06-12): the `[10, 11]` range here is MonoGame-specific. KNI does NOT accept a range under one signature — it accepts `MGFX`@10 OR `KNIFX`@11 (two distinct signatures); v10 stays forward-safe on KNI via its dedicated MGFX-v10 migration path, and ShadowDusk's v10 output has not yet been render-validated against KNI v4.02 specifically. Full detail + loader evidence: [appendix research](PHASE-35-appendix/knifx-vs-mgfx-v11-research.md).**

### B. Newer format (MonoGame `MGFX` v11 AND/OR KNI KNIFX) — *only if a runtime ever requires it, and only seamlessly*
> **Read [the appendix research](PHASE-35-appendix/knifx-vs-mgfx-v11-research.md) first — "v11" is two different formats, not one.** MonoGame v11 = the `MGFX` signature with the byte bumped (a body-format extension); KNI v11 = **KNIFX**, a *new container with its own signature*. So this is **two separate deliverables** (a faithful MonoGame-`MGFX`-v11 writer and/or a faithful KNIFX writer), not a single byte bump. The existing `--mgfx-version 11` flag is **not a head start** — it writes only the header byte over a v10 body, is DOA in KNI, and is unvalidated in MonoGame.
>
> **Starting this area? Use the [Area B kickoff brief](PHASE-35-appendix/knifx-area-b-kickoff-brief.md)** — a self-contained handoff (goal, constraints, code touchpoints at `MgfxWriter`/`CompilerOptions`, the auto-select design crux, open questions, validation gap, and first moves) built from the research appendix.

If/when some target runtime needs more than v10, ShadowDusk must **auto-produce the right format** — the consumer never picks v10 vs v11. Note Area A shows v10 keeps **loading** forward on both forks, so this is **very likely never needed for loadability** — the only real driver is the KNIFX-era *render-quality / XNA-compat fixes* (§4 of the appendix), a product-scope decision.
- **Default stays v10.** Any v11/KNIFX emission must be **auto-selected by ShadowDusk** (e.g. from the chosen target), never a flag the consumer must set. The `--mgfx-version` flag stays a non-required escape hatch only.
- Entry points: `CompilerOptions.MgfxVersion`, `src/ShadowDusk.Core/MgfxWriter.cs` (signature+byte at `:100-101`), KNIFX notes in `DONE/PHASE-33-...` / `DONE/PHASE-24-...`, and the appendix's loader evidence + open questions.

### C. DX12 / DXIL render-validation — *gated on MonoGame 3.8.5 stable*
Render-validate the **already-built** DXIL path in a real MonoGame **DX12** runtime. Seamless means ShadowDusk emits whatever the consumer's DirectX runtime loads (DXBC for DX11, DXIL for DX12) **automatically** — the consumer never picks DXBC vs DXIL or SM5 vs SM6. *(Open design Q for the agent, same shape as Phase 33's one-blob problem: can one artifact serve both, or must it be auto-detected from the target? Resolve reproduce-first.)*
- **DX11 DXBC (vkd3d) stays the default**; keep DX12-DXIL vs Vulkan-SPIR-V SM6 targets distinct (see `PHASE-32` "SM6/DXIL vs SPIR-V symmetry" caveat).

### D. Un-park Vulkan (coordinate with Phase 32) — *gated on MonoGame 3.8.5 stable*
3.8.5's Vulkan runtime + DXC→SPIR-V profile give Phase 32 a **render target + mgfxc oracle** — the exact blocker that parked it. When a consumer's game targets Vulkan, it just works (the platform the consumer already runs, not a ShadowDusk flag).
- This phase is the **trigger/validation**; `PHASE-32-vulkan-backend.md` is the implementation.

---

## Gating
- **A + B (if needed):** doable now (3.8.4.1 stable).
- **C + D:** gated on **3.8.5 going stable** — Vulkan/DX12 are preview/experimental today. **Do not target a preview** as the product baseline.

## Validation discipline
Same as Phase 33/34: **reproduce-first → implement → validate** (rung-4 render where a real runtime exists). Area A is validation-only.

## Definition of done
Each area is either **delivered seamlessly** (zero consumer input; validated; default v10/3.8.2 unchanged) or **documented as blocked on a real limitation** (e.g., 3.8.5 not yet stable).

## Out of scope / non-negotiable
- Anything that requires the consumer to **opt in / pick a version / set a flag** to get correct output (seamless is mandatory).
- Changing the default `.mgfx` format or the product's MonoGame pin; anything that breaks older MonoGame/KNI consumers.

---

### Pointers for the implementing agent
- Memory: `seamless-for-end-user` + `backwards-compat-monogame-382-mgfx-v10` (the two guardrails), `phase33-kni-hidef-webgl2`, `phase34-gl-texture-breadth`.
- Docs: `DONE/PHASE-33-...` (KNIFX/converter + the one-blob/seamless design pattern), `DONE/PHASE-34-...` (validation-harness patterns), `PHASE-32-vulkan-backend.md`, `DONE/PHASE-4.1-SPIKE-wasm-directx-dxbc.md`, `plan.md` "Resolved Constraint (Phase 18): DXC Cannot Produce SM5 DXBC".
- Verify the 3.8.x landscape live when starting (MonoGame releases + CHANGELOG).

### Provenance
Created as a shell 2026-06-04 at user request ("I do want to support newer versions of things"), reconciled with the two standing user directives: **seamless for the end user** ("everything should always be seamless... they don't even know they have to do anything") and **backwards compatibility** ("don't go changing things"). Hence: newer-version support is delivered seamlessly + non-breakingly, never as a consumer opt-in.
