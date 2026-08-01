# Phase 47 Appendix — ShaderToy Sample + Runtime Helper Migration

**Track:** Delivery shapes / samples (reach, not product).
**Status:** ✅ **DONE (2026-07-31)** as [Phase 51](../../PHASE-51-consolidated-remainder-backlog.md)
**A4** — written 2026-06-20, stayed *Planned* for six weeks. The sample lives at
**`samples/ShaderToyViewer/`** with the `ShaderToyEffect` helper folded in at
`Runtime/ShaderToyEffect.cs`; D1, D2, D4, D5, D6 and D7 landed as written, and the acceptance
criteria below are met (`--smoke` 4/4, `NoMonoGameInProductLibrariesTests` green on both TFMs,
zero compiler-output byte change). **Two departures, both deliberate and recorded in the A4 entry:**
**D3 was deferred under its own R1** (the render-proof driver stays at
`tools/shadertoy2fx/render-proof/`, now source-linking the helper file from the sample exactly as D3
prescribes for that case), and the standalone **PoC CLI stays** — it is the only entry point to the
converter's `--multipass` batch mode, so `tools/shadertoy2fx/` is not empty of source and was not
removed. Appendix to the main Phase 47 plan
([`PHASE-47-shadertoy-frontend-promotion.md`](../PHASE-47-shadertoy-frontend-promotion.md),
authored by a sibling agent). **This appendix covers ONLY the sample + the MonoGame runtime helper**;
the converter-library promotion (`tools/shadertoy2fx/src/ShadowDusk.ShaderToy` →
`src/ShadowDusk.ShaderToy`, the new pure-managed product package) is the main plan's job and is
the **anchor** this appendix builds on.

> **Anchor (from the main plan, restated so this doc stands alone):** the ShaderToy *converter*
> becomes the product library **`src/ShadowDusk.ShaderToy`** — pure managed, **no native dependency,
> no MonoGame dependency** — and ships as a NuGet alongside the existing `ShadowDusk.*` packages.
> Everything in *this* appendix lives **one tier below the product**: it is sample/demo code that
> *consumes* that library plus `ShadowDusk.Compiler` plus MonoGame. **No code in this appendix may
> end up in a shipped `ShadowDusk.*` package.**

---

## Why this is a separate appendix (the load-bearing constraint)

`CLAUDE.md` → THE PURPOSE: *"the browser / WASM shader-fiddle is ONLY a sample of reach — never the
product"* and *"samples are reach, not the product."* The same applies here. The interactive
ShaderToy viewer is a **demonstration that the product works at runtime**, not a product surface.

The hard constraint that forces the split:

- The shipped product packages (`ShadowDusk.{Core,HLSL,GLSL,Compiler,Cli,Wasm}` + the new
  `ShadowDusk.ShaderToy`) **must NOT take a MonoGame dependency.** MonoGame is a *consumer's*
  runtime, not ours; pulling `MonoGame.Framework.*` into a product package would (a) bloat every
  consumer's transitive graph with a graphics framework they may not use (a KNI or FNA consumer, or
  a build-time-only MGCB user, would all inherit it), and (b) pin a MonoGame version into the product
  in violation of the "do not bump / do not couple MonoGame" directive.
- But the **`ShaderToyEffect` runtime helper** in
  `tools/shadertoy2fx/src/ShadowDusk.ShaderToy.Runtime/ShaderToyEffect.cs` is *entirely* built on
  `Microsoft.Xna.Framework.Graphics` (`GraphicsDevice`, `Effect`, `EffectParameter`, `VertexBuffer`,
  `Texture2D`, …). It **cannot** be MonoGame-free.

Therefore the runtime helper and the viewer **stay at the sample tier** — they reference MonoGame,
the product never does. This appendix's whole job is to land that split cleanly under `samples/`.

---

## Current state (what exists today, researched 2026-06-20)

Under `tools/shadertoy2fx/` the experiment has six projects:

| Project | Role | Refs MonoGame? | Fate (this phase) |
|---|---|---|---|
| `src/ShadowDusk.ShaderToy` | the converter (pure managed) | no | **PROMOTED to `src/`** (main plan, not this appendix) |
| `src/ShadowDusk.ShaderToy.Cli` | converter CLI | no | main plan's call (out of scope here) |
| `tests/ShadowDusk.ShaderToy.Tests` | converter unit tests | no | main plan's call |
| `src/ShadowDusk.ShaderToy.Runtime` | `ShaderToyEffect` MonoGame helper | **YES** | **moves to sample tier** (this appendix) |
| `sample/` (`ShadowDusk.ShaderToy.Sample`) | interactive viewer + `--smoke` | **YES** | **moves to `samples/`** (this appendix) |
| `render-proof/` (`ShadowDusk.ShaderToy.RenderProof`) | fidelity / gallery / multipass render drivers | **YES** | **stays out-of-band as validation** (this appendix decides) |

What the sample does today (all to be preserved):

- **Interactive viewer** (`SampleGame`): runtime `ShaderToyConverter.Convert` (GLSL → `.fx`) →
  `EffectCompiler.Compile` (`.fx` → `.mgfx` **in memory**, OpenGL target, no `mgfxc`) →
  `new Effect(GraphicsDevice, bytes)` → wrapped in `ShaderToyEffect` and drawn over a fullscreen
  quad. Cycles a **bundled catalog** of four animated/interactive shaders; mouse drives `iMouse`;
  ESC quits.
- **Load any file** (`Program.cs` positional arg): point it at any `.glsl/.frag/.fs/.txt` ShaderToy
  image-tab source; it is added as the first, **hot-reloadable** entry (polls last-write-time ~4×/s,
  re-converts + recompiles + reloads live; `R` forces reload).
- **On-screen error overlay**: convert/compile diagnostics drawn with a built-in `PixelFont` (no
  `SpriteFont` content build) so a bad shader never crashes and never shows a silent black screen.
- **`--smoke`** (`SmokeGame`, `Program.cs`): headless one-frame render per bundled shader (or per
  given file) to an offscreen `RenderTarget2D`, writes a PNG to `output/`, asserts the frame is
  non-trivial (not all-black), and returns a process exit code (0 all-pass, 1 any render fail, 2 bad
  path, 3 harness fault). This is the sample's *self-test*.

Wiring confirmed (`SampleCompiler.cs`): `ShaderToyConverter.Convert` → `IShaderCompiler.Compile`
(`new EffectCompiler()`, `PlatformTarget.OpenGL`) → `new Effect` → `ShaderToyEffect`. The sample
already references **`src/ShadowDusk.Compiler`** (so the transitive DXC / SPIRV-Cross / vkd3d natives
already flow in) plus the two in-tree experiment projects (`ShadowDusk.ShaderToy`,
`ShadowDusk.ShaderToy.Runtime`) plus `MonoGame.Framework.DesktopGL`.

**Existing-sample conventions (researched):**

- `samples/` holds `ShaderViewer/`, `ShaderFiddle.Web/`, `mgcb/`. **None of them are in
  `ShadowDusk.slnx`** (the slnx contains only `src/` and `tests/`). Samples are out-of-band,
  built/run directly with `dotnet run --project samples/<name>`.
- `samples/ShaderViewer` references MonoGame via a **package** (`MonoGame.Framework.WindowsDX`) and
  is `net8.0-windows` — i.e. samples are allowed to depend on MonoGame freely (they are consumers).
- MonoGame versions are centrally pinned in `Directory.Packages.props`
  (`MonoGame.Framework.DesktopGL` / `WindowsDX` = `3.8.2.1105`); the migrated sample should rely on
  that central pin (reference the package **without** an inline `Version=`, as the experiment sample
  already does).
- The experiment already commits a handful of PNGs under `sample/output/` as **eyeball evidence**
  (`atan_polar.png`, `mouse_interaction.png`, `neon.png`, `time_animation.png`, `sd_kaleidoscope.png`)
  — consistent with the repo's "commit a few representative PNGs as proof" convention.

---

## Decisions

### D1 — Where the sample lives: `samples/ShaderToyViewer/`

Move `tools/shadertoy2fx/sample/` → **`samples/ShaderToyViewer/`** (assembly/root namespace
`ShadowDusk.ShaderToyViewer`; the experiment's `ShadowDusk.ShaderToy.Sample` namespace collides
confusingly with the now-product `ShadowDusk.ShaderToy` namespace, so rename to `ShaderToyViewer`).

Rationale: it matches the sibling `samples/ShaderViewer` naming (a "viewer" demo), it is discoverable
under `samples/` like every other reach demo, and the rename disambiguates the *sample* from the
*product library* that now owns the `ShadowDusk.ShaderToy` name.

### D2 — Where the `ShaderToyEffect` runtime helper lives: folded INTO the sample

Fold the single `ShaderToyEffect.cs` file **into the `samples/ShaderToyViewer/` project itself**
(e.g. `samples/ShaderToyViewer/Runtime/ShaderToyEffect.cs`), under the sample's namespace. Do **not**
keep it as a separate `ShadowDusk.ShaderToy.Runtime` project.

Rationale:

- It is **one ~170-line file** with a single public type; a whole project + csproj + packages.lock
  for it is overhead with no payoff. The only other consumer is the out-of-band `render-proof`
  driver (see D3), which can reference the file/source directly or carry its own copy — it does not
  justify a shared shipped library.
- Keeping it inside the sample makes the MonoGame-out-of-product boundary **structurally obvious**:
  the *only* projects that reference MonoGame are under `samples/` and `validation/`/`render-proof`,
  never under `src/`. There is no `ShadowDusk.ShaderToy.Runtime` package to accidentally promote or
  for a consumer to mistake for product.
- It honors the user directive "seamless for the end user": a real consumer who wants to drive a
  converted effect copies ~20 lines of "apply this pixel shader over a fullscreen quad" glue (or
  reads the sample) — they do **not** need a ShadowDusk MonoGame package, and we do not ship one that
  would couple us to a MonoGame version.

> **Alternative considered and rejected:** a shared, sample-tier `ShadowDusk.ShaderToy.Runtime`
> project under `samples/` (referenced by both the viewer and the render-proof driver), explicitly
> `IsPackable=false`. Rejected as premature: it buys deduplication of one file across one sample and
> one out-of-band driver, at the cost of a project whose name (`ShadowDusk.ShaderToy.Runtime`) looks
> like a product package and invites exactly the MonoGame-in-product mistake we are trying to make
> impossible. If a *third* MonoGame consumer of the helper appears later, revisit then. (Open
> question O1 leaves this reversible.)

### D3 — Render-proof / fidelity / gallery drivers stay OUT-OF-BAND (not a sample, not product)

`tools/shadertoy2fx/render-proof/` (`FidelityRunner`, `GalleryRunner`, `GlReferenceRenderer`,
`MultipassChain2Proof`, `RenderProofGame`, …) is a **validation driver**, not a user demo: it renders
ShadowDusk's output side-by-side against a raw Silk.NET GL ground-truth reference and asserts
fidelity, exactly like the `validation/*` rung-4 drivers. It is the *proof*, the sample is the
*demo*.

Decision: **move it under `validation/`** (e.g. `validation/ShaderToyRenderProof/`) alongside the
other render-proof drivers, keep it `IsPackable=false`, `TreatWarningsAsErrors=false` (it already is,
for MonoGame/SDL analyzers), and **out of `ShadowDusk.slnx`** — matching how every `validation/*`
driver is handled (`docs/validation-matrix.md` §6). It is NOT a sample (not user-facing) and NOT
product (it renders and compares; it ships nothing).

- It references the runtime helper today. Since the helper is folding into the sample (D2), the
  render-proof driver should **link the single `ShaderToyEffect.cs` source file** from the sample
  (`<Compile Include="..\..\samples\ShaderToyViewer\Runtime\ShaderToyEffect.cs" />`) rather than a
  project reference — a one-file link is lighter than re-introducing the shared project D2 rejected.
  (This is the one place a shared sample-tier helper project would pay off; see O1.)
- This move is **secondary** to the sample migration. If time-boxed, the render-proof driver may stay
  at `tools/shadertoy2fx/render-proof/` for this phase (still out-of-band, still not shipped) and be
  relocated in a follow-up; it changes no product behavior either way. The appendix's *required*
  outcome is the sample under `samples/` + no MonoGame in product. (Risk R3.)

### D4 — Reference graph of the migrated sample

`samples/ShaderToyViewer/ShaderToyViewer.csproj` references exactly:

1. `..\..\src\ShadowDusk.ShaderToy\ShadowDusk.ShaderToy.csproj` — the **promoted converter library**
   (replaces the old `..\src\ShadowDusk.ShaderToy` experiment path).
2. `..\..\src\ShadowDusk.Compiler\ShadowDusk.Compiler.csproj` — the in-memory compiler; **its
   transitive native assets (DXC, SPIRV-Cross, vkd3d) flow in through this reference** (unchanged
   from today).
3. `MonoGame.Framework.DesktopGL` (PackageReference, **no inline Version** — central pin from
   `Directory.Packages.props`).

The old `ProjectReference` to `..\src\ShadowDusk.ShaderToy.Runtime` is **removed** (helper folded in,
D2). The `<None Include="shaders\*.glsl" CopyToOutputDirectory="PreserveNewest" />` content item is
preserved (with the path adjusted for the new location).

> Note: the sample depends on the converter library *as a `ProjectReference`* while in-repo. Once
> `ShadowDusk.ShaderToy` is a published NuGet, the sample is still fine as a project reference in the
> monorepo; a *consumer copying the sample out* would swap it for the two `<PackageReference>`s
> (`ShadowDusk.ShaderToy` + `ShadowDusk.Compiler`). The sample README should show that
> package-reference form so it is genuinely copy-pasteable (purpose: "add the package, call the API").

### D5 — The sample stays OUT of `ShadowDusk.slnx` (follow existing samples)

`samples/ShaderViewer`, `samples/ShaderFiddle.Web`, and `samples/mgcb` are **all** absent from
`ShadowDusk.slnx`. Follow suit: do **not** add `samples/ShaderToyViewer` to the slnx. It builds and
runs via `dotnet run --project samples/ShaderToyViewer`. (`dotnet test ShadowDusk.slnx` therefore
does not build it — the sample's own `--smoke` is its gate, run manually / in the optional CI smoke,
exactly as the experiment is today.)

### D6 — What the sample demonstrates (unchanged scope)

Keep **all** of today's behavior: runtime convert → in-memory compile → `new Effect` → render; the
interactive cycle; load-any-file + hot-reload; the on-screen error overlay; and `--smoke` headless
validation. Single-pass **image** shaders only — **multipass** (Buffer-A/B graphs) is the
converter/CLI path, explicitly out of scope for this single-quad viewer (the README already says so;
keep that limitation note). Optionally surface a one-line "paste/point at any ShaderToy image-tab
shader" story prominently in the README (already supported via the positional-path + hot-reload
path; no code change needed).

---

## Migration mechanics

### File moves

```
tools/shadertoy2fx/sample/Program.cs               -> samples/ShaderToyViewer/Program.cs
tools/shadertoy2fx/sample/SampleGame.cs            -> samples/ShaderToyViewer/ShaderToyViewerGame.cs   (rename optional)
tools/shadertoy2fx/sample/SmokeGame.cs             -> samples/ShaderToyViewer/SmokeGame.cs
tools/shadertoy2fx/sample/SampleCompiler.cs        -> samples/ShaderToyViewer/SampleCompiler.cs
tools/shadertoy2fx/sample/ShaderCatalog.cs         -> samples/ShaderToyViewer/ShaderCatalog.cs
tools/shadertoy2fx/sample/ShaderSource.cs          -> samples/ShaderToyViewer/ShaderSource.cs
tools/shadertoy2fx/sample/PixelFont.cs             -> samples/ShaderToyViewer/PixelFont.cs
tools/shadertoy2fx/sample/README.md                -> samples/ShaderToyViewer/README.md   (updated, see below)
tools/shadertoy2fx/sample/shaders/*.glsl           -> samples/ShaderToyViewer/shaders/*.glsl   (4 files)
tools/shadertoy2fx/sample/output/*.png             -> samples/ShaderToyViewer/output/*.png  (eyeball evidence; D7)
tools/shadertoy2fx/src/ShadowDusk.ShaderToy.Runtime/ShaderToyEffect.cs
                                                   -> samples/ShaderToyViewer/Runtime/ShaderToyEffect.cs
```

Use `git mv` so history is preserved. Delete the now-empty
`tools/shadertoy2fx/src/ShadowDusk.ShaderToy.Runtime/` project (csproj + packages.lock + obj/bin).

### csproj / namespace / reference rewrites

- New `samples/ShaderToyViewer/ShaderToyViewer.csproj`: `OutputType=Exe`, `IsPackable=false`,
  `AssemblyName`/`RootNamespace` = `ShadowDusk.ShaderToyViewer`; the three references in D4; the
  `shaders\*.glsl` copy item. Keep warnings-as-errors **on** (the experiment sample held the
  zero-warning bar; preserve it).
- Update the namespace in all moved `.cs` from `ShadowDusk.ShaderToy.Sample` →
  `ShadowDusk.ShaderToyViewer`, and the helper from `ShadowDusk.ShaderToy.Runtime` →
  `ShadowDusk.ShaderToyViewer` (or a `.Runtime` sub-namespace). Update the `using` in
  `SampleCompiler.cs`/`SmokeGame.cs` that referenced `ShadowDusk.ShaderToy.Runtime`.
- `SampleCompiler.cs` already `using ShadowDusk.ShaderToy;` (the converter) and
  `using ShadowDusk.Compiler;` — these are **unchanged** because the converter keeps its
  `ShadowDusk.ShaderToy` namespace through the promotion (the main plan preserves the public API/
  namespace). Confirm with the main plan that `ShaderToyConverter`, `ConvertOptions`,
  `ConvertResult`, `ConvertDiagnostic` keep their namespace; if the main plan renames anything,
  mirror it here. (Dependency D-main.)

### Content / assets

- The four bundled `.glsl` move with the project; the `<None Include="shaders\*.glsl"
  CopyToOutputDirectory="PreserveNewest" />` item keeps them next to the binary at
  `AppContext.BaseDirectory/shaders` (which `SampleCompiler.ShadersDirectory` resolves) — no code
  change needed.
- **CC0 attribution must travel with `neon.glsl`.** Its in-file `// License CC0` header is intact
  (confirmed); keep it. The README's provenance line currently points at
  `tools/shadertoy2fx/tests/.../corpus/cc0/LICENSES.md` — repoint it to wherever that LICENSES.md
  lands after the main plan moves the tests (Dependency D-main), or copy the relevant CC0 stanza into
  the sample README so attribution is self-contained even if the test corpus path changes.

### README rewrite

Update `samples/ShaderToyViewer/README.md`: new run commands
(`dotnet run --project samples/ShaderToyViewer [-- <file>] [-- --smoke [<file>]]`), the
"not in slnx / sample of reach, not the product" framing, the copy-out package-reference snippet
(D4), the preserved multipass/`iChannel` limitations, and the CC0 attribution (above).

### What gets committed

- All moved source + csproj + the 4 `.glsl` + README.
- A **few representative PNGs** under `samples/ShaderToyViewer/output/` as eyeball evidence (the
  experiment commits `atan_polar/mouse_interaction/neon/time_animation/sd_kaleidoscope.png`; carry
  these over). (D7.)
- Regenerable `*.fx` / `*.mgfx` that `--smoke` might write are **not** committed; ensure a
  `.gitignore` (or the existing repo ignore) covers `samples/ShaderToyViewer/output/*.fx` and
  `*.mgfx` while keeping the committed `*.png`. (D7.)

### D7 — output/ handling

Mirror today's convention: commit the handful of `*.png` as proof; gitignore regenerable
`*.fx`/`*.mgfx`. If the experiment relied on an ignore under `tools/shadertoy2fx/`, add the
equivalent scoped ignore for `samples/ShaderToyViewer/output/` (none was found scoped to the
experiment, so the PNGs are simply tracked and the `.fx/.mgfx` should get an ignore line).

### Decommission the experiment tree

After the sample + (optionally) render-proof move and the main plan promotes the converter/CLI/tests,
`tools/shadertoy2fx/` should be **empty of source** and removed. **Do not delete it until every
durable artifact is moved** (CLAUDE.md directive: never destroy uncommitted/durable agent output —
the real value is the source, not bin/obj). Coordinate the final `tools/shadertoy2fx/` removal with
the main plan; this appendix only removes the `sample/` and `src/ShadowDusk.ShaderToy.Runtime/`
subtrees (and, if D3 is done now, `render-proof/`).

---

## Tasks (sequenced)

1. [x] **Pre-flight:** confirm with the main Phase 47 plan that `src/ShadowDusk.ShaderToy` exists and
       keeps the `ShadowDusk.ShaderToy` namespace + `ShaderToyConverter` public API. (Blocks all
       reference rewrites; Dependency D-main.)
2. [x] `git mv` the 7 `.cs` + README + `shaders/*.glsl` from `tools/shadertoy2fx/sample/` to
       `samples/ShaderToyViewer/`.
3. [x] `git mv` `ShaderToyEffect.cs` into `samples/ShaderToyViewer/Runtime/`; delete the
       `src/ShadowDusk.ShaderToy.Runtime` project (csproj + lock + obj/bin).
4. [x] Author `samples/ShaderToyViewer/ShaderToyViewer.csproj` (D4 refs, `IsPackable=false`,
       warnings-as-errors on, `shaders\*.glsl` copy item).
5. [x] Update namespaces (`ShadowDusk.ShaderToy.Sample`/`.Runtime` → `ShadowDusk.ShaderToyViewer`)
       and the `using`s; fix the helper reference in `SampleCompiler.cs`/`SmokeGame.cs`.
6. [x] Carry over the representative `output/*.png`; add the `*.fx`/`*.mgfx` gitignore for the new
       `output/` (D7).
7. [x] Rewrite `samples/ShaderToyViewer/README.md` (run commands, slnx note, copy-out package
       snippet, CC0 attribution, limitations).
8. [~] (Secondary, D3) Move `render-proof/` → `validation/ShaderToyRenderProof/`, switch its helper
       dependency to a `<Compile Include=…ShaderToyEffect.cs />` link, update
       `docs/validation-matrix.md` §6 and `docs/repository-layout.md`. (May defer to a follow-up.)
       — **PARTLY DONE, relocation deferred under R1.** The driver stays at
       `tools/shadertoy2fx/render-proof/`; its helper dependency **is** now the one-file
       `<Compile Include=…ShaderToyEffect.cs />` source link, and its exact run commands were added
       to `docs/validation-matrix.md` §8 (alongside the sample's `--smoke`) so neither goes missing.
9. [x] Update `docs/repository-layout.md`: add `samples/ShaderToyViewer/`; remove the
       `tools/shadertoy2fx/` experiment subtree entries this phase removes.
10. [x] **MonoGame-leak check** (acceptance gate): assert no `src/*.csproj` references any
        `MonoGame.Framework.*` (a grep / a tiny test). (See Acceptance.)
11. [x] Build + run + `--smoke` the migrated sample on this machine; confirm green and a non-trivial
        frame per bundled shader; eyeball the committed PNGs.
12. [x] `dotnet test ShadowDusk.slnx` stays green (the sample is not in the slnx, so this only proves
        the converter promotion didn't regress — necessary, not sufficient). Run on the integrated
        result; the migration commit itself was verified with a full `dotnet build ShadowDusk.slnx`
        (0 warnings) plus the targeted `NoMonoGameInProductLibrariesTests` on both TFMs, since
        nothing under `src/` or `tests/` changed.

---

## Acceptance criteria

- [x] `dotnet build samples/ShaderToyViewer` and `dotnet run --project samples/ShaderToyViewer`
      succeed against the **promoted** `src/ShadowDusk.ShaderToy` + `src/ShadowDusk.Compiler` +
      `MonoGame.Framework.DesktopGL` (central pin) — the interactive viewer renders and cycles.
- [x] `dotnet run --project samples/ShaderToyViewer -- --smoke` is **green** (every bundled shader
      converts + compiles in-memory + loads + renders a non-trivial frame; exit 0); and
      `--smoke <file>` works for an arbitrary external file.
- [x] Load-any-file + hot-reload + the on-screen error overlay all still work (manual check; documented
      in the README).
- [x] **No MonoGame dependency in any shipped `ShadowDusk.*` package**: no project under `src/`
      references `MonoGame.Framework.*` (grep/test passes), and `src/ShadowDusk.ShaderToy` in
      particular is pure-managed with no MonoGame. The `ShaderToyEffect` helper lives only under
      `samples/` (and, if moved, `validation/`).
- [x] The sample is **discoverable under `samples/`** and **absent from `ShadowDusk.slnx`** (matching
      the other three samples).
- [x] CC0 attribution for `neon.glsl` is intact and the README provenance link resolves.
- [x] `tools/shadertoy2fx/sample/` and `…/src/ShadowDusk.ShaderToy.Runtime/` are removed; no source
      is orphaned (the experiment tree's remaining removal is coordinated with the main plan).

---

## Open questions / risks

- **O1 — shared helper vs. folded-in (D2/D3 coupling).** This appendix folds `ShaderToyEffect.cs`
  into the sample and source-links it into the render-proof driver. If a *third* MonoGame consumer of
  the helper appears (or the owner prefers one home), a sample-tier `ShadowDusk.ShaderToy.Runtime`
  project (explicitly `IsPackable=false`, under `samples/`) is the fallback. Reversible; owner's
  call. **Recommendation: fold-in now** (simplest, makes the MonoGame boundary structural).
- **O2 — sample name.** `ShaderToyViewer` vs. `ShaderToySample` vs. `ShaderToyFiddle`. Chose
  `ShaderToyViewer` to mirror `samples/ShaderViewer`. Owner may prefer another; trivial to change.
- **O3 — converter namespace stability.** This appendix assumes the promotion keeps the
  `ShadowDusk.ShaderToy` namespace and `ShaderToyConverter`/`ConvertOptions`/`ConvertResult`/
  `ConvertDiagnostic` public API unchanged (so `SampleCompiler.cs` needs no logic edit). If the main
  plan renames the package/namespace, mirror it here. **Dependency on the main plan (D-main).**
- **R1 — render-proof relocation is secondary.** D3 (moving render-proof to `validation/`) is not
  required for the sample to land; if time-boxed, leave it out-of-band at its current path this phase
  and relocate later. It ships nothing either way, so deferring it has no product impact.
- **R2 — CI smoke (optional).** Today nothing in CI runs the experiment `--smoke`. Migrating doesn't
  change that. If desired, a *separate* follow-up could add an optional non-blocking GL smoke job
  (Linux Mesa llvmpipe, like the `validation-render.yml` GL gate) — but the sample's bar is a manual
  local run, not a CI gate (it is reach, not product), so this is explicitly *not* required by this
  appendix.
- **R3 — leak regression guard.** The "no MonoGame in `src/`" check (Task 10) should be a *standing*
  guard (a tiny unit test or a CI grep), not a one-time manual check, so a future edit can't quietly
  add a MonoGame reference to a product package. Recommend a `Core.Tests` assertion that scans
  `src/*/*.csproj` for `MonoGame.Framework`.

---

## Definition of done

The interactive ShaderToy viewer + its `ShaderToyEffect` MonoGame runtime helper live under
**`samples/ShaderToyViewer/`** (helper folded in), built/run out-of-band like the other samples and
absent from `ShadowDusk.slnx`. The sample consumes the **promoted product** `src/ShadowDusk.ShaderToy`
(converter) + `src/ShadowDusk.Compiler` (in-memory compile, transitive natives) + MonoGame, and
demonstrates the full runtime convert → in-memory compile → `new Effect` → render path, the load-any-
file + hot-reload cycle, the on-screen error overlay, and a green `--smoke`. **No shipped
`ShadowDusk.*` package gains a MonoGame dependency** — guarded by a standing check. The fidelity /
gallery / multipass render-proof driver remains **out-of-band validation** (relocated to
`validation/` or left at its experiment path), never a sample and never product. The
`tools/shadertoy2fx/sample/` and `…/ShaderToy.Runtime/` subtrees are removed.
