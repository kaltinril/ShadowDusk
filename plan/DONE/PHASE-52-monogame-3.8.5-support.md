# Phase 52 — MonoGame 3.8.5 (stable) support & validation matrix

**Track:** Forward-compatibility (newer versions, seamless).
**Status:** ✅ **Done (2026-07-28).** Created 2026-07-18 after **MonoGame 3.8.5 shipped stable
2026-07-15** — the external event several parked items were explicitly waiting on ("add when
stable", "ext-blocked on 3.8.5 stable"). This phase is the coordinated sweep that cashed those in.
Areas A, B, C, E, F are complete; Area D was split out to
[Phase 54](PHASE-54-dx12-dxil-backend.md) (also done) when its own decision gate tripped.

**Outcome in one line:** MonoGame 3.8.5 stable is render-proven for both the unchanged v10 output
and the opt-in v11 output, with **zero change to the product's pin, default format, or output
bytes** — and the sweep incidentally caught **three pieces of broken validation infrastructure and
one false documented claim** that had been invisible precisely because nobody re-measured them.

**What the sweep found that it was not looking for** (each recorded in its Area below):
- The golden-generation oracle probe selected an `mgfxc.exe` that **cannot run at all**, so a golden
  regeneration would have reported 0/46 compiled (Area C).
- `tools/compile-fixtures.ps1` found **0 shaders on a no-argument run** — a `??`-on-empty-string bug
  that had quietly disabled the script's default path (Area C).
- The documented **Tier-1 MGCB `mgfxc` PATH override never worked**, on any supported MonoGame
  version, because MGCB compiles effects in-process (Area E).
- Our Android `libdxcompiler.so` is **not 16 KB-page-size aligned**, which Android 16 will require
  (Area F2, filed into [Phase 50](../PHASE-50-android-runtime-support.md)).

**Depends on:**
- [Phase 35](PHASE-35-forward-version-support.md) — built the two harnesses this phase
  re-runs against stable: the ForwardCompat version-matrix (Area A) and the MGFX-v11 render
  harness (Area B, `validation/MonoGameV11`).
- [Phase 32](PHASE-32-vulkan-backend.md) — the Vulkan target, **already implemented and
  render-proven on 3.8.5 stable (Done 2026-07-18)**. Referenced as prior art (especially its
  reverse-engineer-the-real-container method), not re-done here.

**Blocks:** [Phase 51](../PHASE-51-consolidated-remainder-backlog.md) close-out — its two
"ext-blocked on MonoGame 3.8.5 stable" items are resolved by this release: **B1** (DX12/DXIL
render-validation, ex-Phase 35 Area C) is **absorbed here as Area D**; **B2** (un-park Vulkan)
already landed via Phase 32.

> The product is unchanged by this phase. The pin stays **MonoGame 3.8.2.1105** and the default
> output stays **MGFX v10** (CLAUDE.md → *Standing owner directives*; [`project_decisions.md`](../../project_decisions.md)).
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
- The ARM / console / mobile packaging of 3.8.5 (Android has its own [Phase 50](../PHASE-50-android-runtime-support.md)).
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

### Area C — mgfxc oracle version decision (`dotnet-mgcb` 3.8.4.1 → 3.8.5?) — ✅ DONE, decision: KEEP + PIN

The mgfxc oracle tool is pinned at 3.8.4.1 in `.config/dotnet-tools.json`; `dotnet-mgcb` 3.8.5 is
now published. The committed goldens are **canonical on 3.8.2.1105** and stay so (Phase 41
appendix). Decision task, evidence-first: regenerate the golden corpus with mgfxc 3.8.5 into a
scratch dir, run the structural comparison vs the current goldens, and then either (a) bump the
tool pin (if output is unchanged / only known-equivalent drift) or (b) keep 3.8.4.1 and record
the drift in the structural-divergence matrix. Either outcome is fine; an unexamined stale oracle
is not.

#### What the measurement showed (2026-07-28)

The whole 60-shader fixture corpus was recompiled for `OpenGL` and `DirectX_11` with three mgfxc
builds into scratch dirs and byte-compared against the committed goldens (goldens never written):

| Oracle | vs the 46 committed goldens per profile |
|---|---|
| mgfxc **3.8.2.1105** (`dotnet-mgcb-editor-windows` or `dotnet-mgcb`) | **46/46 IDENTICAL**, both profiles |
| mgfxc **3.8.4.1** via `dotnet-mgcb` | **46/46 IDENTICAL**, both profiles |
| mgfxc **3.8.4.1** via `dotnet-mgcb-editor-windows` | **0/46 — cannot run at all** |
| mgfxc **3.8.5** via the new `dotnet-mgfxc` | **0/46 identical**; every file differs |

Two independent problems, both invisible until someone actually ran it:

1. **mgfxc 3.8.5 emits MGFX v11, not v10.** Header version byte is `11` vs the goldens' `10`, and
   every file grew (58 B on `StateRasterizer` up to 2232 B on `BasicEffect`) — consistent with
   PR #8813's per-shader `SourceFile`/`Entrypoint` diagnostic strings. `mgfxc 3.8.5 --help` exposes
   only `/Profile`, `/Debug`, `/Defines`; **there is no flag to ask it for v10.** So 3.8.5 is a
   perfectly faithful compiler for a 3.8.5 consumer and a **useless oracle for a v10 corpus.**
2. **The `dotnet-mgcb-editor-windows` 3.8.4.1 package ships a broken `mgfxc.exe`** — it throws
   `Could not load file or assembly 'SharpDX.D3DCompiler'` on every shader, because that package
   (unlike 3.8.2.1105's) does not include `SharpDX.D3DCompiler.dll` beside the executable. The
   *same* mgfxc version from the `dotnet-mgcb` package works fine, so this is a packaging defect in
   one distribution channel, not a compiler regression.

Problem 2 mattered far more than the version question, because `tools/compile-fixtures.ps1` located
mgfxc by **recursively globbing `dotnet-mgcb-editor-windows` for `mgfxc.exe` and taking the
highest-sorting path** — which resolves to exactly that broken 3.8.4.1 binary. Regenerating goldens
today would have compiled **0/46** and printed `FAIL` for every shader. And once anyone installs
3.8.5 tooling, the same heuristic would have started silently rewriting the corpus as v11.

#### Decision

**(b) Keep the pin — and stop letting the oracle float.** Implemented in `tools/compile-fixtures.ps1`:

- resolve mgfxc from the **`dotnet-mgcb` version pinned in `.config/dotnet-tools.json`** (one source
  of truth, the package whose payload is intact), invoked as `dotnet <path>/mgfxc.dll` so it behaves
  the same on every OS rather than depending on a Windows-only apphost;
- **assert the MGFX version byte of every file written** and abort with an explicit ORACLE MISMATCH
  message rather than overwrite the corpus with a different container version;
- fail loudly with a `dotnet tool restore` hint when the pinned version is not present, instead of
  silently falling back to whatever else is on the machine.

`validation/ReservedWordGl` used a copy of the same highest-version-wins probe (and additionally
preferred any `mgfxc` on `PATH`); it now resolves through the identical pin, and its bonus
mgfxc-equivalence arm runs green at maxd 0.

No goldens changed. Verified after the fix: a full regeneration into a scratch dir reproduces
**92/92** committed goldens byte-for-byte with the committed corpus untouched.

*Separately found and fixed in the same script:* `param([string]$ShaderDir = $null)` arrives as an
**empty string**, not `$null`, so `$ShaderDir ?? (default)` never substituted the default and a
plain no-argument `.\tools\compile-fixtures.ps1` reported `Shaders: 0 files` and did nothing. The
script only worked if you passed every path explicitly — which is a fair part of why its oracle
drifted this far unnoticed.

### Area D — DX12 / DXIL render-validation (absorbed Phase 51 B1, ex-Phase 35 Area C) — ➡️ split to Phase 54 (2026-07-23)

**Decision gate resolved: split.** Source inspection (this area's own required first step) found
no `PlatformTarget.DirectX12` anywhere in the codebase — "the DXIL path is already built" was
accurate only for a DXC-to-SM6-DXIL reflection side-path already used by the DirectX11 target,
not for an actual DX12 container/writer/`PlatformTarget`. That is a full new-backend build on the
scale of Phase 32's Vulkan work, exactly the condition this area's decision gate named for
splitting out. Filed as **[Phase 54 — DirectX 12 (DXIL) backend](PHASE-54-dx12-dxil-backend.md)**,
research committed to
[`PHASE-54-appendix/dx12-dxil-container-research.md`](PHASE-54-appendix/dx12-dxil-container-research.md).
This phase closes on Areas A/B/C/E/F; the Phase 51 B1 tail now points at Phase 54 instead of here.

Original scope (carried into Phase 54 verbatim): The DXIL path is **already built**; what was
missing was a real MonoGame **DX12 runtime**, which 3.8.5 stable now provides
(`MonoGame.Runtime.Windows.DX12`, `<MonoGamePlatform>WindowsDX12</MonoGamePlatform>`). Carried
scope, verbatim from Phase 35/51: *"Seamless means ShadowDusk emits whatever the consumer's
DirectX runtime loads (DXBC for DX11, DXIL for DX12) automatically — the consumer never picks
DXBC vs DXIL or SM5 vs SM6"*, with the open design question *"can one artifact serve both, or
must it be auto-detected from the target? Resolve reproduce-first."*

### Area E — the drop-in story under 3.8.5's new Content Builder — ✅ DONE, and the answer is bad news

The drop-in `mgfxc` promise (Tier-1: MGCB `ExternalTool` config / PATH-based override) was
verified against the classic `.mgcb` + `dotnet-mgcb` pipeline. 3.8.5 introduces a **code-centric
Content Builder** (a C# content project). Investigate and document: does the classic
`dotnet-mgcb` path remain fully supported on 3.8.5 (it is still published at 3.8.5, so
presumably yes)? Does the new builder still route `.fx` through an overridable external
`mgfxc`, and if the override mechanism changed, what does a 3.8.5 consumer do instead? Verify
with a scratch consumer (the Phase 37 pack→consume pattern). If the new builder has **no**
external-tool seam, record that honestly and note that [Phase 29](../PHASE-29-mgcb-content-processor-plugin.md)
(the in-process processor plugin) becomes the native integration path for it — that is a finding
to feed Phase 29, not a defect of this phase.

#### Finding (2026-07-28): the Tier-1 PATH override does not work, and never did

The classic path **is** still supported — `dotnet-mgcb` 3.8.5 is published and a scratch `.mgcb`
consumer built `Grayscale.fx` → `.xnb` with it cleanly. But the mechanism ShadowDusk documented as
"the shipping MGCB integration" was measured and **does not fire on any supported MonoGame version.**

**Method.** A real `mgfxc.exe` (a .NET console app named `mgfxc`, not a `.cmd` — .NET's
`Process.Start` with `UseShellExecute=false` resolves `.exe` from `PATH` but not `.cmd`, and using a
`.cmd` shim would have produced a false negative) that appends every invocation to a log and then
delegates to the real pinned mgfxc, placed **first on `PATH`**, then a `.mgcb` content build run
against three versions of `dotnet mgcb`:

| `dotnet mgcb` | shim invocations | result |
|---|---|---|
| **3.8.2.1105** (the product pin) | **0** | `.xnb` built successfully |
| **3.8.4.1** | **0** | `.xnb` built successfully |
| **3.8.5** | **0** | `.xnb` built successfully |

**Corroboration.** No `mgcb` or `MonoGame.Framework.Content.Pipeline` assembly in any of the three
payloads contains the string `mgfxc` or `MGFXC_WINE_PATH` — the only hits anywhere are inside the
standalone `mgfxc.dll` tool itself. `MonoGame.Content.Builder.Task`'s `.targets` (3.8.4.1 and 3.8.5)
only ever `Exec`s `mgcb`. MGCB compiles effects **in-process**: SharpDX `D3DCompiler` through
3.8.4.1, and bundled DXC + MojoShader native tool packages at 3.8.5.

**Scope of the measurement:** Windows only. The Linux/macOS half is inferred from the identical
package payloads, not run. That inference is strong (there is no `mgfxc` string to branch on) but it
is an inference, and the doc updates say so.

**The new Content Builder does not change the answer.** It is the `MonoGame.ContentBuilder.CSharp`
template: an `Exe` project referencing `MonoGame.Framework.Content.Pipeline` 3.8.5 (plus
`MonoGame.Library.MojoShader`, `MonoGame.Tool.Dxc`, …) that subclasses `ContentBuilder` and declares
an `IContentCollection` of include/exclude rules. It drives the same in-process pipeline, so it has
no external-tool seam either — though, being ordinary C# the consumer owns, it is a *better* place to
call ShadowDusk from than the `.mgcb` file ever was.

**Packaging change worth recording:** 3.8.5 moved `mgfxc` out of `dotnet-mgcb` and
`dotnet-mgcb-editor-windows` into its own **`dotnet-mgfxc`** tool package (this is also why the
Area C oracle probe found no 3.8.5 mgfxc where it was looking).

**Consequences, all recorded outside this doc so they are not buried here:**
- `docfx/guides/mgcb-content-pipeline.md`, `docfx/samples/mgcb.md`, `docfx/guides/dropin-mgfxc.md`,
  and `docfx/cli/index.md` corrected — each carried an instruction to do something that does not
  work. They now document the two routes that do: invoke the CLI directly and `/copy:` the `.mgfx`,
  or compile at runtime and hand the bytes to `Effect`.
- `project_decisions.md`'s "MGCB Tier 1, not Tier 2 first" entry marked **superseded by
  measurement** — the reasoning was sound, the premise was false.
- `project_facts.md` and `docs/validation-matrix.md` §7 carry the measured fact.
- **[Phase 29](../PHASE-29-mgcb-content-processor-plugin.md) is promoted from convenience to the only
  route to native MGCB integration.** Its doc opens by calling itself a layer "on top of the
  already-working Tier-1 drop-in"; that framing is now wrong and is flagged in its status line.

**Headline pitch corrected too.** Four top-of-funnel surfaces (`README.md`, `docfx/index.md`,
`docfx/getting-started/overview.md`, `docs/the-purpose.md`) opened with "MonoGame's stock content
pipeline shells out to `mgfxc`". The *substance* survives untouched — compiling shaders through MGCB
really is a Windows-dependent build step (SharpDX `D3DCompiler` through 3.8.4.1), which is exactly
the problem ShadowDusk solves — but the stated *mechanism* was wrong, so each now says MGCB compiles
with the same effect compiler `mgfxc` wraps, which needs `fxc.exe` and only runs on Windows. Same
claim, accurate mechanism; no repositioning of the product.

### Area F — documentation staleness sweep

Every repo statement that 3.8.5 is preview/unreleased is now stale. Known sites (grep for
`3.8.5` to catch stragglers):
- `CLAUDE.md` — "Newer MonoGame exists (3.8.4.1 stable, 3.8.5-preview), but bumping is rejected"
  → update to "(3.8.5 stable)" while keeping the directive itself untouched. **Already correct**
  (found already saying "3.8.5 stable since 2026-07-15" — fixed in an earlier, untracked pass).
- `validation/ForwardCompat/README.md` — "Version landscape" section + the extend-the-matrix
  note; refresh with the Area A results. **Wording fixed 2026-07-24** (status corrected to
  "shipped STABLE 2026-07-15"); the actual re-sweep against the stable build is still Area A2,
  not done here.
- `validation/MonoGameV11/*` — csproj comment + README (Area B). **Wording fixed 2026-07-24**
  (status corrected; the project still pins `-preview.6` and the actual bump-and-re-run is still
  Area B, not done here — see the task list below).
- `docs/validation-matrix.md` — checked 2026-07-24, no stale preview wording found (already
  clean); v10-on-3.8.5-stable and v11-on-3.8.5-stable rows still land once Areas A/B are green;
  DX12 row when Area D lands.
- `README.md` / `src/ShadowDusk.Wasm/README.md` — checked 2026-07-24: the "MonoGame 3.8.5+ →
  `MgfxVersion = 11`" guidance is a version threshold, not a preview/stable claim — not stale,
  no change needed.
- `plan/plan.md` — Phase 51 B1/B2 status notes (B1 → here, B2 → Phase 32 done). **Already
  correct** — both already carry their "gate cleared 2026-07-18" update annotations.
- Optional: `validation/AndroidGl` pins `MonoGame.Framework.Android 3.8.4.1` — check whether a
  3.8.5 stable Android package exists and bump the validation driver if it does (low priority,
  feeds [Phase 50](../PHASE-50-android-runtime-support.md)). Not checked; tracked as F2 below.

## 5. Tasks

Sequenced; A/B/C/F are small and independent, D is the big rung, E is investigation.

- [x] **A1** — add `3.8.5` to the default `-Versions` in `validation/ForwardCompat/run-forwardcompat.ps1`
  (floor stays `3.8.2.1105` first) and to the README examples. **Done 2026-07-28**; also updated
  `compare_forwardcompat.py`'s default/usage and the csproj's bare-build default property.
- [x] **A2** — run the full ForwardCompat sweep on the Windows GPU box; all cells green
  (pixel-identical to floor + within tolerance of mgfxc goldens); record the result table +
  refreshed version landscape in the README. **Done 2026-07-28.**
- [x] **A3 (added by owner directive 2026-07-28: "let's not tie to 3.8.2.1105 specifically … we
  should test against before and after 3.8.5")** — **widened the matrix from an anchor version to
  the full supported range, with the floor measured instead of assumed.** Every stable
  `MonoGame.Framework.DesktopGL` release was probed by running the harness against it:
  **3.8.0.1641 REJECTS** our v10 output (`new Effect()` throws *"This MGFX effect seems to be for a
  newer release of MonoGame"*, 0/10 — its loader predates MGFX v10), and **3.8.1.263 through 3.8.5
  all load and render 10/10.** The default matrix is now those **seven consecutive releases**
  (`3.8.1.263` floor, `3.8.1.303`, `3.8.2.1105`, `3.8.3`, `3.8.4`, `3.8.4.1`, `3.8.5`) and the full
  sweep is green: **70 renders, every cell maxd 0 against the floor, every cell within tolerance of
  the mgfxc goldens** (`Scanlines`/`Dots` at 1/255, the other eight at 0). So the compat claim is
  now *broader* and *evidenced* rather than anchored: **3.8.1.263 is the real floor, not
  3.8.2.1105.** Recorded in the README (including the probe table), `docs/validation-matrix.md`,
  `project_facts.md`, and the `CLAUDE.md` standing directive, which was reworded so the commitment
  is the **output format (MGFX v10)** rather than a MonoGame version.
- [x] **B1** — bump `validation/MonoGameV11/MonoGameV11.csproj` to `3.8.5` stable; update the
  csproj comment (drop the PRE-RELEASE caveat; keep the validation-only / not-the-baseline note).
  **Done 2026-07-28.**
- [x] **B2** — re-run the MonoGameV11 harness: v11 loads + renders, and v11 == v10 render on
  stable; record the result. **Done 2026-07-28: 10/10 load + render on real MonoGame 3.8.5.0,
  maxd 0 vs v10 on all ten, <= 1 vs the mgfxc goldens — the preview.6 result table reproduced cell
  for cell on stable.** The v11 claim no longer rests on a pre-release runtime.
- [x] **C1** — regenerate the golden corpus with `mgfxc` 3.8.5 (scratch dir, goldens untouched),
  structural-compare vs current goldens, and record the bump-or-keep decision for
  `.config/dotnet-tools.json` with the evidence. **Done 2026-07-28 — see the Area C findings below;
  decision: KEEP the pin, and stop letting the oracle float.**
- [x] **D1/D2** — source-inspect first, then the decision gate. **Exercised 2026-07-23:** inspection
  found no `PlatformTarget.DirectX12` at all, so this was new-backend work, not a render rung →
  split to **[Phase 54](PHASE-54-dx12-dxil-backend.md)** (since completed, rung-4 maxd 0), with
  the research committed to
  [`PHASE-54-appendix/dx12-dxil-container-research.md`](PHASE-54-appendix/dx12-dxil-container-research.md)
  and the Phase 51 B1 pointer re-pointed.
- [x] **D3** — n/a in this phase: carried into Phase 54 by the D2 split, and delivered there
  (`validation/BaselineDx12` + `CandidateDx12` + `VsDrivenDx12`, wired into the Windows render gate).
- [x] **E1** — verify the classic `dotnet-mgcb` 3.8.5 pipeline still honors the Tier-1
  `ExternalTool`/PATH mgfxc override with a scratch consumer; investigate the new code-centric
  Content Builder's external-shader-compiler seam; document both outcomes (feed Phase 29 if the
  new builder needs the in-process plugin). **Done 2026-07-28 — and the answer is that the override
  never worked on any version; see the Area E findings below.**
- [x] **F1** — documentation staleness sweep per Area F list (grep `3.8.5` for stragglers);
  includes the CLAUDE.md wording fix and Phase 51 B1/B2 notes. **Done 2026-07-24** — every
  "known site" checked; the only two that were actually stale (`validation/ForwardCompat/README.md`,
  `validation/MonoGameV11/*`) had their status wording corrected. Note: this is the *wording* sweep
  only — the actual version bumps + re-validation (Areas A2/B2) remain open, tracked below.
- [x] **F2** — check `MonoGame.Framework.Android` for a 3.8.5 stable package; bump
  `validation/AndroidGl` if it exists (optional, non-blocking). **Done 2026-07-28: the package
  exists and `validation/AndroidGl` builds + packages an APK against it with no source change, so
  the pin moved to 3.8.5 — BUILD-verified only.** The on-device proof in Phase 50 §6.2 was taken on
  3.8.4.1 and was not repeated, which the csproj comment, the Phase 50 note, and the
  validation-matrix row all say explicitly so the pin is not misread as render evidence. *(One
  transient `XABBA7000` "renaming temporary file failed" APK-zip failure occurred on the first
  attempt and did not reproduce; a 3.8.4.1 control build in between was clean, so it was a local
  file-lock, not a 3.8.5 incompatibility.)* **The build also surfaced a real, previously-unknown
  defect** — `XA0141`: our own NDK-built `libdxcompiler.so` is not 16 KB-page-size aligned, which
  Android 16 will require. Filed into [Phase 50](../PHASE-50-android-runtime-support.md) against the
  open `dxc-android-build.yml` item (needs `-Wl,-z,max-page-size=16384`), affecting both ABIs.
- [x] **Guard** — confirm the product is byte-untouched. **Done 2026-07-28:**
  `Directory.Packages.props` still `3.8.2.1105`; `CompilerOptions.MgfxVersion` still `10`; full
  `dotnet test ShadowDusk.slnx` **2381/2381 green, 0 failed**; **zero churn under `tests/`** (no
  golden, no byte-identity-manifest change). **No `src/` file was touched by this phase at all**, so
  output-byte invariance holds by construction rather than by measurement. The full Windows render
  gate is not required here (the HARD RULE's trigger is a change to shader output / transpilation /
  the writers / render state / matrix handling, and D3 — the one area that could have caused one —
  moved to Phase 54, where it was gated). The three drivers this phase did touch were each run
  green: ForwardCompat (30 cells), MonoGameV11 (both arms), and `ReservedWordGl` (6/6, including the
  mgfxc-equivalence checks that now resolve through the pinned oracle).

## 6. Acceptance Criteria

- [x] ForwardCompat matrix is green **including `3.8.5` stable** — same v10 bytes, pixel-identical
  to the 3.8.2.1105 floor, within tolerance of the mgfxc goldens — with the result table recorded.
  **10/10 × 3 versions, maxd 0 vs floor; table in `validation/ForwardCompat/README.md`.**
- [x] MGFX v11 render proof is green on **stable** 3.8.5 (not just preview.6), recorded.
  **10/10, maxd 0 vs v10; table in `validation/MonoGameV11/README.md`.**
- [x] The mgfxc oracle decision (bump `dotnet-mgcb` or keep 3.8.4.1) is made and recorded with
  corpus evidence, not by default. **Keep 3.8.4.1 + pin the resolution and assert the version byte;
  evidence table in Area C.**
- [x] DX12: the load-path research (D1) is committed; and either the rung-4 DX12 render proof is
  green and wired into the Windows render gate, or Area D is explicitly split to its own phase
  with Phase 51 B1 repointed (decision gate exercised, not ignored). **Gate exercised → split to
  Phase 54, which then delivered the rung-4 proof at maxd 0 and wired it into the gate.**
- [x] The 3.8.5 Content Builder drop-in story is verified and documented (classic path confirmed;
  new-builder seam answered honestly, feeding Phase 29 if needed). **Classic path confirmed
  working; neither it nor the new builder has an external-`mgfxc` seam — measured, documented, and
  fed to Phase 29.**
- [x] No repo doc still describes MonoGame 3.8.5 as preview/unreleased (**done 2026-07-24**, F1);
  `docs/validation-matrix.md` now carries the stable rows for Areas A/B (2026-07-28).
- [x] The product is untouched: pin `3.8.2.1105`, default MGFX v10, full test suite green, zero
  output-byte churn (byte-identity manifest unchanged). **Confirmed — and no `src/` file was
  touched at all.**

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
