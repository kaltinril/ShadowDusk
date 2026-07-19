# Phase 52 — MonoGame 3.8.5 (stable) support & validation matrix

**Track:** Forward-compatibility (newer versions, seamless).
**Status:** Planned (created 2026-07-18). **MonoGame 3.8.5 shipped stable 2026-07-15** — the
external event several parked items were explicitly waiting on ("add when stable", "ext-blocked
on 3.8.5 stable"). This phase is the coordinated sweep that cashes those in.

**Depends on:**
- [Phase 35](DONE/PHASE-35-forward-version-support.md) — built the two harnesses this phase
  re-runs against stable: the ForwardCompat version-matrix (Area A) and the MGFX-v11 render
  harness (Area B, `validation/MonoGameV11`).
- [Phase 32](DONE/PHASE-32-vulkan-backend.md) — the Vulkan target, **already implemented and
  render-proven on 3.8.5 stable (Done 2026-07-18)**. Referenced as prior art (especially its
  reverse-engineer-the-real-container method), not re-done here.

**Blocks:** [Phase 51](PHASE-51-consolidated-remainder-backlog.md) close-out — its two
"ext-blocked on MonoGame 3.8.5 stable" items are resolved by this release: **B1** (DX12/DXIL
render-validation, ex-Phase 35 Area C) is **absorbed here as Area D**; **B2** (un-park Vulkan)
already landed via Phase 32.

> The product is unchanged by this phase. The pin stays **MonoGame 3.8.2.1105** and the default
> output stays **MGFX v10** (CLAUDE.md → *Backwards compatibility*; `plan.md` Key Decisions).
> A v10 `.mgfx` loads in 3.8.2 **and** every newer MonoGame **and** KNI — that is the product's
> compat promise, and 3.8.5's own loader accepts the range `[MGFXMinVersion=10, 11]`, so the
> promise extends into 3.8.5 by design. "Supporting 3.8.5" therefore means **proving** (rung-4
> render evidence) and **documenting** that the existing output works there with zero consumer
> action — plus lighting up the newly-possible validation rungs (DX12) — never bumping the pin.

---

## 1. What shipped in MonoGame 3.8.5 (research, verified 2026-07-18)

Verified live against nuget.org and the release announcement
([monogame.net blog, 2026-07-15](https://monogame.net/blog/2026-07-15-3.8.5-release-2026/),
[GitHub releases](https://github.com/MonoGame/MonoGame/releases)):

- **Stable on 2026-07-15**, version string is plain **`3.8.5`** (not a 4-part build number like
  `3.8.2.1105` / `3.8.4.1`). Previously latest stable was 3.8.4.1 (2025-10-20); the preview line
  ended at `3.8.5-preview.7` (2026-07-10).
- **The classic per-platform packages continue**: `MonoGame.Framework.DesktopGL` **3.8.5** and
  `MonoGame.Framework.WindowsDX` **3.8.5** are both published stable (confirmed on nuget.org).
  Our existing DesktopGL/WindowsDX validation drivers work unchanged against it.
- **New native architecture (additive)**: a C++ native layer with new packages —
  `MonoGame.Framework.Native` (shared core) + `MonoGame.Runtime.<OS>.<Backend>` runtime packages,
  selected via `<MonoGamePlatform>` — shipping the **new backends: `DesktopVK`** (SDL2 + Vulkan +
  FAudio, Win/Mac/Linux) and **`WindowsDX12`** (SDL2 + D3D12 + XAudio2, "Microsoft GDK
  compatible"). Note upstream wording is mixed: the packages are stable-versioned, but the NuGet
  blurb still calls VK/DX12 "preview support". Phase 32 empirically proved DesktopVK runs a real
  Vulkan device on real hardware; treat the wording as a doc nuance, not a blocker.
- **New code-centric Content Builder**: content is now built by a C# project referencing
  assemblies, instead of (only) the `.mgcb` data file — relevant to our Tier-1 drop-in story
  (Area E). `dotnet-mgcb` **3.8.5** is still published as a tool package (confirmed).
- **MGFX format**: no break. 3.8.5's `Effect` loader accepts `[MGFXMinVersion=10, MGFXVersion=11]`
  (source-verified in Phase 35; v11 = v10 body + the two per-shader `SourceFile`/`Entrypoint`
  diagnostic strings from PR #8813). Our default v10 output stays forward-safe; our opt-in v11
  output was already render-proven on the 3.8.5 preview line.
- Misc (no ShadowDusk impact expected): ARM dev/runtime packages, 8-controller GamePad, HSL/HSV
  color conversion, new `Random` implementation, ~100+ fixes.

## 2. What this means for ShadowDusk — and what it does NOT mean

**Does NOT mean:** bumping `Directory.Packages.props` (stays `3.8.2.1105`), changing the default
`CompilerOptions.MgfxVersion` (stays `10`), or taking any dependency on the new
`MonoGame.Framework.Native` packages in the product. All of that is settled by the standing
backwards-compat directive and is a **non-goal** here.

**Does mean** (everything below is validation, documentation, or additive rungs):

| Waiting item | Where it was parked | Now |
|---|---|---|
| Add 3.8.5 stable to the forward-compat version matrix | `validation/ForwardCompat` README: "When 3.8.5 goes stable, add it to `-Versions`" | **Area A** |
| Re-prove MGFX v11 on stable (currently proven on `3.8.5-preview.6`) | `validation/MonoGameV11` csproj ("3.8.5 is PRE-RELEASE… VALIDATION only") | **Area B** |
| mgfxc oracle version (`dotnet-mgcb` pinned 3.8.4.1) | `.config/dotnet-tools.json` | **Area C** (decision) |
| DX12 / DXIL render-validation | Phase 51 **B1** (ex-Phase 35 Area C): "Blocked on: MonoGame 3.8.5 going stable" | **Area D** |
| Un-park Vulkan | Phase 51 **B2** | ✅ already landed (Phase 32, Done 2026-07-18) — nothing to do |
| Docs saying "3.8.5 is preview-only" | CLAUDE.md, ForwardCompat README, validation-matrix, READMEs | **Area F** (staleness sweep) |

## 3. Scope & Non-Goals

**In scope:**
- Rung-4 render proof that the **unchanged v10 output** works on MonoGame 3.8.5 stable (Area A).
- Rung-4 render proof that the **opt-in v11 output** works on 3.8.5 stable, not just preview (Area B).
- An evidenced **decision** on the mgfxc oracle tool version (Area C).
- The **DX12/DXIL render-validation** rung that 3.8.5 finally makes possible (Area D) — absorbed
  from Phase 51 B1, with an explicit decision gate to split it out if research reveals a large
  new container format (the Phase 32 precedent).
- Verifying the **drop-in mgfxc story still holds under 3.8.5's new Content Builder** (Area E).
- **Documentation staleness sweep** — no repo doc may keep claiming 3.8.5 is preview (Area F).

**Out of scope / Non-Goals:**
- Bumping the product MonoGame pin or the default MGFX version (**rejected by standing directive**).
- Making DX12/DXIL a default output or exposing any consumer-facing version/target flag
  (seamless rule: auto-select from the target only).
- KNI / FNA: unaffected. KNI pins its own `nkast.*` 4.2.9001 packages; FNA is not MonoGame.
- The ARM / console / mobile packaging of 3.8.5 (Android has its own [Phase 50](PHASE-50-android-runtime-support.md)).
- Adopting the new Content Builder for our own repo's samples (only the *consumer* story matters here).

## 4. Areas

### Area A — ForwardCompat version matrix gains 3.8.5 stable

`validation/ForwardCompat` (Phase 35 Area A) proves the **same v10 bytes** render pixel-identical
across MonoGame versions, with `3.8.2.1105` as the immovable floor. The harness was explicitly
built for this moment: *"Extending the matrix: add the NuGet version string to `-Versions` (e.g. a
future 3.8.5 stable)."* Add `3.8.5` to the default matrix in `run-forwardcompat.ps1` (floor stays
first), run the full sweep on the Windows GPU box, and record the new three-version result table
in the README (same format as the 2026-06-05 table). The runtime-integrity guard already fails a
cell whose loaded MonoGame version doesn't match, so a silently-ineffective `VersionOverride`
can't fake a pass.

### Area B — MGFX v11 render proof moves from preview.6 to stable

`validation/MonoGameV11` pins `MonoGame.Framework.DesktopGL 3.8.5-preview.6` and its csproj
comment says outright that 3.8.5 is pre-release. Bump the pin to **`3.8.5`** stable, update the
comment (still validation-only, still not the product baseline), and re-run both arms (v11 loads +
renders; v11 render == v10 render on the same runtime). This closes the loop on the Phase 35
Area B claim "render-proven in MonoGame 3.8.5" with the *stable* runtime rather than a preview.

### Area C — mgfxc oracle version decision (`dotnet-mgcb` 3.8.4.1 → 3.8.5?)

The mgfxc oracle tool is pinned at 3.8.4.1 in `.config/dotnet-tools.json`; `dotnet-mgcb` 3.8.5 is
now published. The committed goldens are **canonical on 3.8.2.1105** and stay so (Phase 41
appendix). Decision task, evidence-first: regenerate the golden corpus with mgfxc 3.8.5 into a
scratch dir, run the structural comparison vs the current goldens, and then either (a) bump the
tool pin (if output is unchanged / only known-equivalent drift) or (b) keep 3.8.4.1 and record
the drift in the structural-divergence matrix. Either outcome is fine; an unexamined stale oracle
is not.

### Area D — DX12 / DXIL render-validation (absorbed Phase 51 B1, ex-Phase 35 Area C)

The DXIL path is **already built**; what was missing was a real MonoGame **DX12 runtime**, which
3.8.5 stable now provides (`MonoGame.Runtime.Windows.DX12`, `<MonoGamePlatform>WindowsDX12</MonoGamePlatform>`).
Carried scope, verbatim from Phase 35/51: *"Seamless means ShadowDusk emits whatever the
consumer's DirectX runtime loads (DXBC for DX11, DXIL for DX12) automatically — the consumer
never picks DXBC vs DXIL or SM5 vs SM6"*, with the open design question *"can one artifact serve
both, or must it be auto-detected from the target? Resolve reproduce-first."*

**Method — follow the Phase 32 playbook, research before code:** Phase 32 found on inspection
that the "provisional" container assumptions were wrong (real profile byte `80`, a genuinely
distinct shader-record shape). Expect the same: **source-inspect MonoGame 3.8.5's WindowsDX12
effect/shader load path first** — what container profile does it declare, what bytecode does it
actually feed to D3D12 (DXIL? DXBC-on-12? a wrapped descriptor header like Vulkan's?) — then
build the rung-4 driver pair (`validation/BaselineDx12`-style, per the existing DX pattern,
Windows-GPU-only) and wire it into `run-windows-render-gates.ps1` once proven.

**Decision gate:** if the source inspection reveals a full new container format on the scale of
Phase 32's Vulkan work, split Area D into its own scoped phase (committing the research here) —
this phase then closes on Areas A/B/C/E/F, and the Phase 51 B1 tail points at the new phase
instead. Do not let a large DX12 build-out hold the 3.8.5 support sweep open.

### Area E — the drop-in story under 3.8.5's new Content Builder

The drop-in `mgfxc` promise (Tier-1: MGCB `ExternalTool` config / PATH-based override) was
verified against the classic `.mgcb` + `dotnet-mgcb` pipeline. 3.8.5 introduces a **code-centric
Content Builder** (a C# content project). Investigate and document: does the classic
`dotnet-mgcb` path remain fully supported on 3.8.5 (it is still published at 3.8.5, so
presumably yes)? Does the new builder still route `.fx` through an overridable external
`mgfxc`, and if the override mechanism changed, what does a 3.8.5 consumer do instead? Verify
with a scratch consumer (the Phase 37 pack→consume pattern). If the new builder has **no**
external-tool seam, record that honestly and note that [Phase 29](PHASE-29-mgcb-content-processor-plugin.md)
(the in-process processor plugin) becomes the native integration path for it — that is a finding
to feed Phase 29, not a defect of this phase.

### Area F — documentation staleness sweep

Every repo statement that 3.8.5 is preview/unreleased is now stale. Known sites (grep for
`3.8.5` to catch stragglers):
- `CLAUDE.md` — "Newer MonoGame exists (3.8.4.1 stable, 3.8.5-preview), but bumping is rejected"
  → update to "(3.8.5 stable)" while keeping the directive itself untouched.
- `validation/ForwardCompat/README.md` — "Version landscape" section + the extend-the-matrix
  note; refresh with the Area A results.
- `validation/MonoGameV11/*` — csproj comment + README (Area B).
- `docs/validation-matrix.md` — v10-on-3.8.5-stable and v11-on-3.8.5-stable rows once Areas A/B
  are green; DX12 row when Area D lands.
- `README.md` / `src/ShadowDusk.Wasm/README.md` — the "MonoGame 3.8.5+ → `MgfxVersion = 11`"
  guidance is now about a stable version; check wording.
- `plan/plan.md` — Phase 51 B1/B2 status notes (B1 → here, B2 → Phase 32 done).
- Optional: `validation/AndroidGl` pins `MonoGame.Framework.Android 3.8.4.1` — check whether a
  3.8.5 stable Android package exists and bump the validation driver if it does (low priority,
  feeds [Phase 50](PHASE-50-android-runtime-support.md)).

## 5. Tasks

Sequenced; A/B/C/F are small and independent, D is the big rung, E is investigation.

- [ ] **A1** — add `3.8.5` to the default `-Versions` in `validation/ForwardCompat/run-forwardcompat.ps1`
  (floor stays `3.8.2.1105` first) and to the README examples.
- [ ] **A2** — run the full ForwardCompat sweep on the Windows GPU box; all cells green
  (pixel-identical to floor + within tolerance of mgfxc goldens); record the result table +
  refreshed version landscape in the README.
- [ ] **B1** — bump `validation/MonoGameV11/MonoGameV11.csproj` to `3.8.5` stable; update the
  csproj comment (drop the PRE-RELEASE caveat; keep the validation-only / not-the-baseline note).
- [ ] **B2** — re-run the MonoGameV11 harness: v11 loads + renders, and v11 == v10 render on
  stable; record the result.
- [ ] **C1** — regenerate the golden corpus with `mgfxc` 3.8.5 (scratch dir, goldens untouched),
  structural-compare vs current goldens, and record the bump-or-keep decision for
  `.config/dotnet-tools.json` with the evidence.
- [ ] **D1** — source-inspect MonoGame 3.8.5's WindowsDX12 effect load + shader-creation path
  (container profile byte, expected bytecode form, any descriptor/wrapper header); write up the
  findings (appendix if substantial) **before any driver code**.
- [ ] **D2** — decision gate: proceed in-phase (bounded work) or split Area D to its own phase
  (large new container) with the D1 research committed; update Phase 51 B1 pointer accordingly.
- [ ] **D3** — (if in-phase) build the DX12 rung-4 render driver per the existing `validation/*`
  DX pattern; prove ShadowDusk output renders equivalently in real MonoGame WindowsDX12; wire
  into `validation/run-windows-render-gates.ps1`; resolve the one-artifact-vs-auto-detect design
  question reproduce-first (seamless: never a consumer flag).
- [ ] **E1** — verify the classic `dotnet-mgcb` 3.8.5 pipeline still honors the Tier-1
  `ExternalTool`/PATH mgfxc override with a scratch consumer; investigate the new code-centric
  Content Builder's external-shader-compiler seam; document both outcomes (feed Phase 29 if the
  new builder needs the in-process plugin).
- [ ] **F1** — documentation staleness sweep per Area F list (grep `3.8.5` for stragglers);
  includes the CLAUDE.md wording fix and Phase 51 B1/B2 notes.
- [ ] **F2** — check `MonoGame.Framework.Android` for a 3.8.5 stable package; bump
  `validation/AndroidGl` if it exists (optional, non-blocking).
- [ ] **Guard** — confirm the product is byte-untouched: `Directory.Packages.props` still
  `3.8.2.1105`, `MgfxVersion` default still 10, full `dotnet test ShadowDusk.slnx` green, and no
  golden/manifest churn (this phase is validation + docs only, unless D3 lands compiler-side
  auto-detect changes — which then also trigger the full Windows render gate per the HARD RULE).

## 6. Acceptance Criteria

- [ ] ForwardCompat matrix is green **including `3.8.5` stable** — same v10 bytes, pixel-identical
  to the 3.8.2.1105 floor, within tolerance of the mgfxc goldens — with the result table recorded.
- [ ] MGFX v11 render proof is green on **stable** 3.8.5 (not just preview.6), recorded.
- [ ] The mgfxc oracle decision (bump `dotnet-mgcb` or keep 3.8.4.1) is made and recorded with
  corpus evidence, not by default.
- [ ] DX12: the load-path research (D1) is committed; and either the rung-4 DX12 render proof is
  green and wired into the Windows render gate, or Area D is explicitly split to its own phase
  with Phase 51 B1 repointed (decision gate exercised, not ignored).
- [ ] The 3.8.5 Content Builder drop-in story is verified and documented (classic path confirmed;
  new-builder seam answered honestly, feeding Phase 29 if needed).
- [ ] No repo doc still describes MonoGame 3.8.5 as preview/unreleased; `docs/validation-matrix.md`
  carries the new stable rows.
- [ ] The product is untouched: pin `3.8.2.1105`, default MGFX v10, full test suite green, zero
  output-byte churn (byte-identity manifest unchanged) — except any explicitly render-gated D3 work.

## 7. Definition of Done

MonoGame 3.8.5 stable is a **proven, documented, first-class member of the support matrix**: the
unchanged v10 output and the opt-in v11 output are both rung-4 render-proven on it, the version
matrix and validation docs say so, the oracle question is settled on evidence, the new Content
Builder's implications for the drop-in promise are answered, and the DX12 rung that 3.8.5
unblocked is either proven or promoted to its own scoped phase — all with **zero change to the
product's pin, default format, or output bytes**, because the consumer was never supposed to
notice a MonoGame release. When this closes, Phase 51 has no remaining "ext-blocked on 3.8.5
stable" items.

## 8. Open questions / risks

- **DX12 effect container is unknown until D1.** The Vulkan precedent (Phase 32) is that
  assumptions about the container were wrong on inspection (profile byte `80`, distinct record
  shape, wrapped descriptor header). Budget for the same discovery on DX12 — hence the decision
  gate.
- **Upstream calls VK/DX12 "preview support" inside a stable release.** If MonoGame iterates the
  DX12/Vulkan effect container in 3.8.6, validation chases a moving target. Phase 32 accepted
  this risk for Vulkan; DX12 inherits it. Mitigation: the drivers pin exact versions, so drift
  becomes a visible red cell, not silent breakage.
- **The new Content Builder may have no external-tool seam.** Then the classic `dotnet-mgcb` path
  (still shipped at 3.8.5) remains the documented drop-in route, and native new-builder
  integration lands via Phase 29 — an honest documented answer, not a regression.
- **mgfxc 3.8.5 output drift.** If the 3.8.5 mgfxc emits different structure, the oracle stays at
  3.8.4.1 and the drift gets a divergence-matrix entry; goldens remain canonical on 3.8.2.1105
  regardless.
- **Version-string shape.** `3.8.5` is 3-part (vs `3.8.2.1105`/`3.8.4.1`); anything parsing or
  sorting the matrix version labels (compare scripts, the integrity guard) should be checked
  against the shorter form.
