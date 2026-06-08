# Phase 39 — `fxc.exe` oracle comparison & MonoGame fidelity matching

**Status:** 📋 **Shell (not started).** Created 2026-06-08 — a research/validation tracking phase.
**Roadmap track:** Fidelity / completeness.

> **Why now:** the maintainer confirmed a working **`fxc.exe`** is installed locally (the real DirectX HLSL compiler `mgfxc` shells out to on Windows). That unlocks a *direct* fidelity oracle we did not previously have on hand. To date, DX fidelity has been validated against `d3dcompiler_47` (the fxc-faithful system DLL) + the `mgfxc` goldens (Phase 18), and GL against the `mgfxc` goldens (Phase 17) — both only over the **10-shader SM-PS-only corpus**. This phase is the standing home for "how close are we *really* to genuine `fxc`/`mgfxc`, across the *whole* corpus?" research, so findings land in a tracked place instead of scattered notes.

---

## Guardrail — read first

- **This is research/characterization, not a product change.** Any *behavioral* change this phase motivates (e.g. closing a divergence) goes through its own change with rung-4 validation; this phase's job is to **find, quantify, and document** gaps, and decide which are worth closing. Do not bump the MonoGame pin or the `MgfxVersion = 10` default here (see Phase 35 guardrails / memory `backwards-compat-monogame-382-mgfx-v10`).
- **Same-backend comparison only** (DX↔DX, GL↔GL) — never cross-backend (see `CLAUDE.md` → *What success actually means*).
- **`fxc.exe` is Windows-only and not redistributable.** It is a *local oracle for the maintainer's investigation*, exactly like `d3dcompiler_47` — it must **not** become a product dependency or enter the shipping pipeline. The cross-platform promise stands on DXC / vkd3d-shader / SPIRV-Cross; `fxc` is a measuring stick, not a component.

---

## Context snapshot (as of 2026-06-08 — re-verify when starting)

- **What's validated today:** Phase 17 (GL, 10/10 rung-4 vs `mgfxc`), Phase 18 (DX DXBC, 10/10 rung-4 vs `mgfxc` via both `d3dcompiler_47` oracle and vkd3d-shader), Phase 28 (VS-driven effects, maxΔ-0 vs `mgfxc` on GL + DX). All over the **SM3/SM5 PS-only + the VS corpus** — not the full 52-`.fx` fixture set.
- **Known, documented fidelity gap:** global cbuffer **initializers** are dropped by DXC vs `fxc`/`mgfxc` (stored default `0`) — see `docfx/guides/parameters-and-caveats.md`. Characterized but not closed; the recommended pattern is `SetValue` / inline literals.
- **Oracle relationships:** `mgfxc` (Windows) → `fxc.exe` for DX HLSL → DXBC. ShadowDusk's DX path uses **vkd3d-shader** (shipping, cross-platform) with **`d3dcompiler_47`** as the Windows oracle. `d3dcompiler_47` and standalone `fxc.exe` are closely related but **not guaranteed identical** — having `fxc.exe` lets us check that assumption directly.
- **Goldens:** `tests/fixtures/golden/{DirectX_11,OpenGL}/` hold reference `.mgfx`. Provenance / how they were produced: confirm they came from genuine `mgfxc` and whether the full corpus (not just the validated 10) has goldens.

---

## Open questions this phase should answer

1. **How faithful is ShadowDusk to genuine `fxc`/`mgfxc` beyond the validated corpus?** Compile the **full** `tests/fixtures/shaders` corpus through ShadowDusk *and* through real `mgfxc`/`fxc`, and produce a **divergence matrix** (per shader, per backend: loads? renders-equivalent? structural `.mgfx` diff? where it diverges and why).
2. **Is `d3dcompiler_47` a sound stand-in for `fxc.exe`?** Compare DXBC from ShadowDusk's `d3dcompiler_47` backend vs standalone `fxc.exe` on the corpus — quantify any divergence so Phase 18's oracle choice is evidenced, not assumed.
3. **Which fidelity gaps actually matter?** Triage each divergence: behaviorally-irrelevant (reordering, naming) vs render-affecting (the initializer gap class). Feed render-affecting ones into their own follow-up change.
4. **(Stretch) FNA format reality** — *tracked separately; see "Carry-forward / related" below.* If folded in, the `fxc.exe`/MojoShader question overlaps, but FNA's effect format is a distinct backend question, not an `fxc`-vs-ShadowDusk comparison.

## Work items (each a self-contained task an agent can take)

- [ ] **Inventory the goldens & oracle availability:** confirm which fixtures have genuine-`mgfxc` goldens; document the exact `mgfxc`/`fxc.exe` versions available locally and how to invoke them reproducibly (a `validation/` script, mirroring `validation/ForwardCompat/`).
- [ ] **Full-corpus divergence matrix (DX):** ShadowDusk (vkd3d + `d3dcompiler_47`) vs `fxc.exe`/`mgfxc` over all DX-applicable fixtures — structural `.mgfx` compare + rung-4 render where a runtime exists. Output a checked-in matrix doc.
- [ ] **`d3dcompiler_47` vs `fxc.exe` delta study:** quantify and document (answers OQ#2).
- [ ] **Full-corpus divergence matrix (GL):** ShadowDusk vs `mgfxc` OpenGL goldens over all GL-applicable fixtures.
- [ ] **Triage + gap register:** classify every divergence (irrelevant / render-affecting); file render-affecting ones as their own follow-up with a reproduce case. Revisit the **initializer gap** with `fxc` in hand — confirm scope and whether a faithful fix is feasible.
- [ ] **Document outcomes** in this doc + update `docfx/guides/parameters-and-caveats.md` / `choosing-a-target.md` if any user-facing fidelity caveat changes.

## Definition of Done

An honest, checked-in answer to "how close are we to genuine `fxc`/`mgfxc` across the whole corpus": a divergence matrix (DX + GL), the `d3dcompiler_47`-vs-`fxc.exe` delta quantified, a triaged gap register, and either a closed gap (with its own rung-4 validation) or a clearly-documented, justified divergence for each item. No product pin / default / pipeline change made *in this phase*; `fxc.exe` never enters the shipping pipeline.

## Carry-forward / related

- **FNA effect-format research** is a **separate** open thread (FNA appears to consume DX9-era bytecode via MojoShader, not MGFX — so it's a *backend* question, not an `fxc`-comparison). Recommended as its own phase if FNA is pursued; tracked in `plan.md`. See `docfx/guides/choosing-a-target.md` for the current honest user-facing statement.
- **XNB output** is deliberately **not** in ShadowDusk core — it belongs to the MGCB content-pipeline layer ([Phase 29](PHASE-29-mgcb-content-processor-plugin.md)). See the `.mgfx`-vs-`.xnb` decision in `plan.md` → *Key Decisions Already Made*.
