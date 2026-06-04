# Phase 35 — Forward-version support & validation (newer MonoGame / MGFX / DX — opt-in)

**Status:** 📋 **SHELL — not started.** Scaffold for a future agent to pick up. Created 2026-06-04.
**Roadmap track:** Forward-compatibility (newer versions, opt-in).

---

## ⚠️ HARD GUARDRAIL — read before doing anything

**Backwards compatibility is a hard requirement** (user directive 2026-06-04; see memory `backwards-compat-monogame-382-mgfx-v10`). The **default output stays MGFX `v10` / the MonoGame 3.8.2.1105 baseline.** Everything in this phase is **additive / opt-in, or pure validation** — it must **never** change the existing OpenGL / DX11 / v10 output a current consumer relies on. If a task would change the default target, bump the product's MonoGame pin, or break older MonoGame/KNI consumers, it is **out of scope**.

**Goal:** support *newer versions of the things we already ship* — without sacrificing backwards compatibility.

---

## Context snapshot (as of 2026-06-04 — re-verify when starting)

- **Pinned today:** MonoGame **3.8.2.1105** (`Directory.Packages.props`); MGFX default **v10** (`src/ShadowDusk.Core/CompilerOptions.cs` → `MgfxVersion = 10`).
- **Newer exists:** MonoGame **3.8.4.1** (stable, Oct 2025); **3.8.5-preview** (Vulkan + DX12, *preview/experimental*, ~May 2026).
- **DXIL path:** DXC `ps_6_0`/`vs_6_0` → DXIL is **built** ("for DX12/KNI", Phase 4) but **never render-validated in a real MonoGame DX12 runtime** (none existed). `plan.md` calls DX12 "✅ Works" — that's structural/theoretical, not rung-4.
- **MGFX v11 / KNIFX:** a `--mgfx-version 11` flag stub exists; not built/validated as a real opt-in. Phase 24 found v11 *not needed* for current render parity. KNIFX = KNI's multi-variant container (per-profile GL blobs) — see `DONE/PHASE-33-webgl2-es300-hidef-output.md` § KNI converter.
- **Vulkan:** `PHASE-32-vulkan-backend.md` is **parked** specifically on "no MonoGame/KNI Vulkan runtime + no mgfxc-Vulkan baseline." MonoGame 3.8.5 (Vulkan + a DXC→SPIR-V shader profile) **removes that blocker**.

---

## Work areas (each is a self-contained task an agent can take)

### A. Forward-compat validation — *doable now, non-breaking, highest value*
Prove our existing **v10 output still loads + renders correctly** in MonoGame **3.8.4.1** (and 3.8.5 when stable), **without changing our pin or output**.
- Use a **separate** validation/test project that references the newer MonoGame — do **not** bump the product's `Directory.Packages.props` pin.
- Mirror the `validation/` rung-4 harness pattern (real `Effect` load + render of the SM3 corpus).
- Deliverable: a **forward-compat matrix** (which v10 features load/render in which MonoGame versions) + a regression guard.

### B. Opt-in newer MGFX format (v11 / KNIFX multi-variant)
Make `--mgfx-version 11` a **real, validated opt-in** (today it's a flag stub). KNIFX multi-variant container = per-profile GL blobs.
- **Default stays v10.** Purely additive.
- Entry points: `CompilerOptions.MgfxVersion`, `src/ShadowDusk.Core/MgfxWriter.cs`, the KNIFX format notes in `DONE/PHASE-33-...` and `DONE/PHASE-24-...`.

### C. DX12 / DXIL render-validation — *gated on MonoGame 3.8.5 stable*
Render-validate the **already-built** DXIL path in a real MonoGame **DX12** runtime.
- **DX11 DXBC (vkd3d) stays the default** DirectX path. Additive validation only.
- Entry points: the DXC `ps_6_0`/`vs_6_0` path (Phase 4); keep DX12-DXIL vs Vulkan-SPIR-V SM6 targets distinct (see `PHASE-32` "SM6/DXIL vs SPIR-V symmetry" caveat).

### D. Un-park Vulkan (coordinate with Phase 32) — *gated on MonoGame 3.8.5 stable*
3.8.5's Vulkan runtime + DXC→SPIR-V profile give Phase 32 a **render target + mgfxc oracle** — the exact blocker that parked it.
- This phase is the **trigger/validation**; `PHASE-32-vulkan-backend.md` is the implementation. Un-park it when 3.8.5 is stable.

---

## Gating
- **A + B:** doable now (3.8.4.1 is stable).
- **C + D:** gated on **3.8.5 going stable** — it's preview/experimental (source-only Vulkan/DX12) today. **Do not target a preview** as the product baseline.

## Validation discipline
Same as Phase 33/34: **reproduce-first → implement → validate** (rung-4 render where a real runtime exists). Area A is validation-only (no output change).

## Definition of done
Each area is either (a) **delivered** — additive/opt-in, validated — or (b) **documented as blocked on a real limitation** (e.g., 3.8.5 not yet stable). The **backwards-compat default (v10 / MonoGame 3.8.2) is unchanged throughout.**

## Out of scope / non-negotiable
Changing the default `.mgfx` format or the product's MonoGame pin; anything that breaks older MonoGame/KNI consumers.

---

### Pointers for the implementing agent
- Memory: `backwards-compat-monogame-382-mgfx-v10` (the guardrail), `phase33-kni-hidef-webgl2`, `phase34-gl-texture-breadth`.
- Docs: `DONE/PHASE-33-...` (KNIFX/converter), `DONE/PHASE-34-...` (validation-harness patterns), `PHASE-32-vulkan-backend.md`, `PHASE-4.1-SPIKE-wasm-directx-dxbc.md`, `plan.md` "Resolved Constraint (Phase 18): DXC Cannot Produce SM5 DXBC".
- The 3.8.x landscape (verify versions live when starting): MonoGame releases + CHANGELOG; the `--mgfx-version` flag.

### Provenance
Created as a shell 2026-06-04 at user request: "I do want to support newer versions of things" — after the backwards-compat directive ("don't go changing things"). Hence: additive/opt-in only, v10/3.8.2 stays default.
