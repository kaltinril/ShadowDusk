# Phase 35 — Forward-version support & validation (newer MonoGame / MGFX / DX — seamless)

**Status:** 📋 **SHELL — not started.** Scaffold for a future agent to pick up. Created 2026-06-04.
**Roadmap track:** Forward-compatibility (newer versions, seamless).

---

## ⚠️ TWO HARD GUARDRAILS — read before doing anything

1. **Seamless for the end user — always.** The consumer **never** chooses a version, target, or flag, and never takes a manual step to get correct output. ShadowDusk automatically produces output that **just works** on their runtime — old or new. *(Same rule as Phase 33/34: if any task would require the consumer to flip a flag / pick a version to get correct output, that is a **defect** — reject it. A flag may exist **only** as a non-required escape hatch, never the path to correct behavior.)*
2. **Backwards compatibility.** The **default output stays MGFX `v10` / the MonoGame 3.8.2.1105 baseline**, which loads everywhere. Nothing here may break older MonoGame/KNI consumers, change the existing OpenGL/DX11/v10 output, or bump the product's MonoGame pin. (See memory `backwards-compat-monogame-382-mgfx-v10`.)

Together: **support newer versions by making one output (or auto-selected output) that just works across old *and* new runtimes — with zero consumer input.** Not by asking the consumer to opt into anything.

---

## Context snapshot (as of 2026-06-04 — re-verify when starting)

- **Pinned today:** MonoGame **3.8.2.1105** (`Directory.Packages.props`); MGFX default **v10** (`src/ShadowDusk.Core/CompilerOptions.cs` → `MgfxVersion = 10`).
- **Newer exists:** MonoGame **3.8.4.1** (stable, Oct 2025); **3.8.5-preview** (Vulkan + DX12, *preview/experimental*, ~May 2026).
- **DXIL path:** DXC `ps_6_0`/`vs_6_0` → DXIL is **built** ("for DX12/KNI", Phase 4) but **never render-validated in a real MonoGame DX12 runtime** (none existed). `plan.md` calls DX12 "✅ Works" — structural/theoretical, not rung-4.
- **MGFX v11 / KNIFX:** a `--mgfx-version 11` flag stub exists; not built/validated. Phase 24 found v11 *not needed* for current render parity (v10 already renders in KNI WebGL). KNIFX = KNI's multi-variant container — see `DONE/PHASE-33-webgl2-es300-hidef-output.md` § KNI converter.
- **Vulkan:** `PHASE-32-vulkan-backend.md` is **parked** on "no MonoGame/KNI Vulkan runtime + no mgfxc-Vulkan baseline." MonoGame 3.8.5 (Vulkan + a DXC→SPIR-V shader profile) **removes that blocker**.

---

## Work areas (each a self-contained task an agent can take)

### A. Forward-compat validation — *doable now, non-breaking, highest value, inherently seamless*
Prove our existing **v10 output still loads + renders correctly** in MonoGame **3.8.4.1** (and 3.8.5 when stable), **without changing our pin or output**. This *is* seamless support for newer versions: the consumer's existing `.mgfx` just keeps working forward — they do nothing.
- Use a **separate** validation/test project referencing the newer MonoGame — do **not** bump the product's pin.
- Mirror the `validation/` rung-4 harness (real `Effect` load + render of the SM3 corpus).
- Deliverable: a **forward-compat matrix** (which v10 features load/render in which MonoGame versions) + a regression guard.

### B. Newer MGFX format (v11 / KNIFX) — *only if a runtime ever requires it, and only seamlessly*
If/when some target runtime needs more than v10, ShadowDusk must **auto-produce the right format** — the consumer never picks v10 vs v11. Note area A likely shows v10 keeps working forward, so this may not be needed at all.
- **Default stays v10.** Any v11/KNIFX emission must be **auto-selected by ShadowDusk** (e.g. from the chosen target), never a flag the consumer must set. The `--mgfx-version` flag stays a non-required escape hatch only.
- Entry points: `CompilerOptions.MgfxVersion`, `src/ShadowDusk.Core/MgfxWriter.cs`, KNIFX notes in `DONE/PHASE-33-...` / `DONE/PHASE-24-...`.

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
- Docs: `DONE/PHASE-33-...` (KNIFX/converter + the one-blob/seamless design pattern), `DONE/PHASE-34-...` (validation-harness patterns), `PHASE-32-vulkan-backend.md`, `PHASE-4.1-SPIKE-wasm-directx-dxbc.md`, `plan.md` "Resolved Constraint (Phase 18): DXC Cannot Produce SM5 DXBC".
- Verify the 3.8.x landscape live when starting (MonoGame releases + CHANGELOG).

### Provenance
Created as a shell 2026-06-04 at user request ("I do want to support newer versions of things"), reconciled with the two standing user directives: **seamless for the end user** ("everything should always be seamless... they don't even know they have to do anything") and **backwards compatibility** ("don't go changing things"). Hence: newer-version support is delivered seamlessly + non-breakingly, never as a consumer opt-in.
