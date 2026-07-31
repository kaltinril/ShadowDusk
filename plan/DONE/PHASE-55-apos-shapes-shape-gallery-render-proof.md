# Phase 55 — Apos.Shapes full shape-gallery render-proof (real `ShapeBatch`, effect substitution)

**Status:** ✅ **Done (2026-07-23).** Implemented same-day: all four backends wired, real
measured results in hand (see §8), full `dotnet test` (2222/2222) green, zero output-byte churn
(no `src/` change).

**Track:** Correctness / drop-in `mgfxc` fidelity — third-party shader corpus depth (breadth was Phase 49; this is depth on the one shader that matters most to a named stakeholder).
**Depends on:** [Phase 49](PHASE-49-apos-shapes-regression-corpus.md) (corpus + provenance) and [Phase 51](../PHASE-51-consolidated-remainder-backlog.md) A3 (the single-shape render-proof this phase supersedes) · the four existing `validation/VsDriven{,Dx,Dx12,Vulkan}` drivers and their `-- apos` mode, which this phase rewrites.
**Owner-context:** Requested directly, 2026-07-23. Only one shape (a hand-built circle quad) is currently render-proven per backend; the owner wants the full Apos.Shapes shape/feature surface exercised, using the real Apos.Shapes NuGet package as the render harness and golden source.

---

## 1. Why this phase exists

`validation/{VsDriven,VsDrivenDx,VsDrivenDx12,VsDrivenVulkan}`'s `-- apos` mode each render exactly **one** shape: a hand-rolled `BuildCircleQuad()` that manually Cantor-pairs colors and hand-packs a 10- or 13-element vertex struct to match one pinned fixture revision (see `AposShapesRenderer.cs` in each driver). That proves the shader loads and renders *a* shape correctly, but Apos.Shapes' real shader dispatches on 11+ shape kinds (circle, rectangle with per-corner radii, line, arbitrary path with joins/caps/dashes, hexagon, triangle, ellipse, arc, ring) times fill/border/gradient/dash permutations. None of that surface is exercised today — a regression in, say, the rectangle corner-radius branch or the dash-style math would not be caught by any current render gate.

## 2. The decision locked in

**Use the real `Apos.Shapes` NuGet package as both the drawing harness and the golden source, via its built-in effect-injection constructor — not a hand-rolled vertex harness.**

Confirmed by reading `Source/ShapeBatch.cs` upstream: `public ShapeBatch(GraphicsDevice graphicsDevice, Effect? effect = null)`. Passing `null` makes it call a private `LoadEmbeddedEffect`, which reads a **raw per-profile `.mgfx`/`.knifx` byte blob embedded as an assembly manifest resource** (`Apos.Shapes.apos-shapes.{ogl,dx11,dx12,vk}.mgfx` / `.knifx`) and does `new Effect(graphicsDevice, bytes)` — the same call every ShadowDusk validation driver already makes with its own compiled bytes. Passing a non-null `Effect` skips that entirely and uses the supplied effect for every draw.

So the harness is:
- **Golden arm:** `new ShapeBatch(graphicsDevice)` — the package's own embedded, pre-existing effect for that graphics backend.
- **Candidate arm:** `new ShapeBatch(graphicsDevice, shadowDuskEffect)` — a ShadowDusk-compiled `Effect` built from the *same* upstream `apos-shapes.fx` source the NuGet's embedded resource was built from (pin the NuGet version, fetch its matching upstream commit, vendor it as usual, compile it, done — no local `mgfxc` invocation needed to produce the golden bytes at all, since the NuGet already ships them precompiled).
- Both arms driven through the **same public draw calls** (`DrawCircle`, `DrawRectangle`, `DrawLine`, `DrawPath`, `DrawHexagon`, `DrawTriangle`/`DrawEquilateralTriangle`, `DrawEllipse`, `DrawArc`, `DrawRing`, their `Fill*`/`Border*` variants, `Gradient`, `DashStyle`, `CornerRadii`), so the real batching/vertex-packing code exercises both effects identically. This replaces the hand-rolled `AposVertex`/`Pack11`/Cantor-pairing code in all four `AposShapesRenderer.cs` files — that code was a workaround for not having the real library to hand, and is now unnecessary.

This is a strict upgrade over the current single-shape harness: real vertex-packing code path (no hand-reverse-engineered layout), and every shape/feature is covered for free through the public API instead of one more bespoke vertex struct per shape.

## 3. Does this require generating a full `.xnb`? **No.**

Confirmed by the same source read: `LoadEmbeddedEffect` never touches `ContentManager` or an `.xnb` — it reads a raw byte resource and calls `new Effect(graphicsDevice, bytes)` directly, exactly like ShadowDusk's own drivers already do (`new Effect(GraphicsDevice, job.Bytes)` in every existing `AposShapesRenderer`). The `.xnb` container is a Content Pipeline packaging format `ContentManager.Load<T>` unwraps; `ShapeBatch`'s effect-injection path bypasses it entirely. No `.xnb` writer/reader work is in scope anywhere in this phase.

## 4. Scope

**GL decision (locked in 2026-07-23):** the `Apos.Shapes` package's current shader revision (`a85a31c`, byte-identical to our vendored `apos-shapes-sm6.fx` modulo one comment) is the ONLY shader `ShapeBatch` can drive on any backend — its `VertexShape` struct (13 fixed elements: `Position`, `TextureCoordinate`, Oklab-packed `FillA/FillB/BorderA/BorderB`, `FillCoord`, `BorderCoord`, `Meta1-3`, `ClipDistances`, `ClipRounding`, `ClipAaSize`) is emitted identically regardless of graphics backend, so there is no way to substitute the older, GL-safe `apos-shapes.fx` (3fb73b8d) revision the way the existing single-shape GL proof does — that revision predates this vertex contract. And Phase 51 A3 already found, independently, that `mgfxc`'s own GL compile of this exact shader revision renders **solid black for every non-textured shape** (a confirmed MojoShader `-0.0 >= 0.0` codegen bug, not a ShadowDusk defect) — which is nearly the entire gallery. **Conclusion: there is no trustworthy `mgfxc` GL oracle for this gallery.** GL therefore does NOT get a golden pixel-diff in this phase: render the gallery through ShadowDusk's GL candidate only, and assert it loads, renders without crashing, and produces visible (non-black, non-transparent) output per shape — rung 2-3, not rung 4. The existing single-shape GL proof (against the older `apos-shapes.fx` revision) is UNCHANGED and remains the one pixel-diffed GL data point for Apos.Shapes.

**In scope:**
- Add a `PackageReference` to `Apos.Shapes` (the plain MonoGame build, not the KNI variant) in `validation/VsDriven`, `VsDrivenDx`, `VsDrivenDx12`, `VsDrivenVulkan`.
- Vendor the upstream `apos-shapes.fx` revision matching the pinned `Apos.Shapes` NuGet version (new pin, new `NOTICE.md` row — same verbatim-vendoring rule as Phase 49) if it differs from the existing pinned fixtures. (Already checked 2026-07-23: NuGet 0.7.7 pins commit `a85a31c`, which IS `apos-shapes-sm6.fx` modulo one comment line — no new fixture needed.)
- Build a shape-gallery scene: one `ShapeBatch.Draw*` call per shape kind × {solid fill+border, gradient fill, dashed border, rotated, non-default `aaSize`} — enough permutations to hit every branch in the shader's shape dispatch and style dispatch, not an exhaustive cross-product.
- Render the gallery through both arms (golden embedded effect, ShadowDusk-compiled effect) on DirectX_11, DirectX_12, and Vulkan; pixel-diff per the existing rung-4 pattern (`maxd`, non-vacuity check). On GL, render through ShadowDusk only (no golden arm) per the decision above.
- Retire the hand-rolled `AposVertex`/`BuildCircleQuad`/`Pack11`/`Unpair` code in all four `AposShapesRenderer.cs` once the `ShapeBatch`-driven gallery covers the same ground (GL's existing single-shape pixel-diff harness is kept as its own thing, since it targets the different, older fixture revision).
- Update `docs/validation-matrix.md`, `docs/test-shader-corpus.md`, `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md`, and `plan/plan.md` per `CLAUDE.md`'s support-surface rule.

**Out of scope (explicit, not silently dropped):**
- **FNA** — unchanged, permanently excluded (real SM3 instruction-ceiling rejection, already documented; `ShapeBatch` doesn't target FNA anyway).
- **KNI** (`Apos.Shapes.KNI` + `KniVsDriven`/`KniDesktopGL`) — a natural follow-on since ShadowDusk already proves KNI separately, but not committed here; decide as a follow-up once the MonoGame-family gallery is green, so this phase doesn't grow two dimensions (shape breadth × runtime breadth) at once.
- Any change to shipped `ShadowDusk.*` library code. This is validation-only depth; `Apos.Shapes` is a dev dependency of `validation/*` projects, never a product dependency (no license/shipping concern — MIT, validation-only, same posture as the existing test-fixture vendoring).

## 5. Tasks

- [x] 5.1 Resolve the exact `Apos.Shapes` NuGet version to pin, fetch its matching upstream `Source/Content/apos-shapes.fx` commit, vendor it if new. **Done 2026-07-23:** latest is 0.7.7, nuspec pins commit `a85a31ca4ccbdcb4a5cf2321ea039d5352e5edcd` — diffed against the vendored `apos-shapes-sm6.fx` (commit `ea38c6d8`) and it is identical except one comment line (`"Vulkan compiles"` → `"Vulkan and DirectX 12 compile"`). No new fixture needed; `NOTICE.md` records the 0.7.7/`a85a31c` confirmation.
- [x] 5.2 Confirm the embedded per-profile resources really are `mgfxc`-produced bytes before trusting them as goldens. **Originally marked done on a flawed inference (that maxd-0 agreement implies mgfxc provenance) — corrected same day.** Actually disassembling the DX11 embedded resource found it is a `vkd3d-shader`-compiled artifact, NOT `mgfxc`/`d3dcompiler_47`. It remains the correct baseline for the DX11 `vkd3d-shader` candidate (same toolchain family) but is NOT a valid oracle for the `d3dcompiler_47` candidate — see §8. This is exactly the failure mode this task existed to prevent; it was skipped rather than actually done, and the gap wasn't caught until asked to investigate the resulting "divergence" further.
- [x] 5.3 Add the `Apos.Shapes` `PackageReference` to all four `validation/VsDriven*` projects (feasibility-checked first — clean restore/build on GL, DX11, DX12, Vulkan; the flagged MonoGame-flavor incompatibility risk did not materialize).
- [x] 5.4 Built `validation/SharedDx/AposGalleryRenderer.cs` — ONE file, linked unmodified into all four projects, driving `ShapeBatch` through a golden + candidate arm (DX11/DX12/Vulkan) or a candidate-only arm (GL).
- [x] 5.5 Wired: DX11/DX12/Vulkan's `-- apos` mode now renders the gallery through both arms and pixel-diffs; GL got a NEW `-- apos-gallery` mode (candidate-only, per-shape visibility). GL's existing `-- apos` single-circle harness is untouched and reconfirmed green (maxd 2/255, unchanged).
- [x] 5.6 Deleted the hand-rolled `AposVertex`/`BuildCircleQuad`/`Pack11`/`Unpair` code from the DX11, DX12, and Vulkan `AposShapesRenderer.cs` files.
- [x] 5.7 Full `dotnet test ShadowDusk.slnx`: **2222/2222 passed**, confirming no output-byte churn (expected — no `src/` change). The Windows render gate script was updated with the new gate descriptions and measured tolerances (§8); the individual new gate commands were run directly and manually, not via a full `run-windows-render-gates.ps1` pass (not required — no shader-output-affecting change).
- [x] 5.8 Updated `docs/validation-matrix.md`, `docs/test-shader-corpus.md`, `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md`, `validation/run-windows-render-gates.ps1`, and `plan/plan.md`.

## 6. Acceptance criteria

- [x] DirectX_11 (both `d3dcompiler_47` oracle and `vkd3d-shader` arms) and Vulkan render the full shape gallery at `maxd 0`, with the existing non-vacuity check. (The oracle arm's baseline is the real, locally-generated `mgfxc` golden, not the package's embedded effect — see §8 correction.)
- [x] ~~DirectX_12 at maxd 0~~ — **revised, see §8**: lands at `maxd 1` on a handful of pixels, confirmed against the REAL local `mgfxc` golden (not a methodology artifact). Tolerance adjusted to 1 for this arm. **Root-caused 2026-07-31 to the pinned DXC build** (ours 1.7.2212.40, the reference's 1.8.2505.32); replaying ShadowDusk's own HLSL and flags through a DXC 1.8 build reproduces the golden's DXIL instruction-for-instruction and renders at maxd 0.
- [x] GL renders the full shape gallery through a real `ShapeBatch` with ShadowDusk's compiled effect and every shape produces visible (non-black, non-transparent) output (30/30); no golden comparison claimed or implied.
- [x] Every `ShapeBatch.Draw*`/`Fill*`/`Border*` shape method is exercised at least once, with gradient, dash, rotation, and corner-radius variants each hit at least once somewhere in the gallery (30 cells: 10 shape kinds × Draw/Fill/Border). **Was only true at the draw-call level until 2026-07-31** — 10 of the 30 cells were clipped off the render target and contributed no pixels to any comparison; see §8.2.
- [x] No hand-rolled vertex-packing code remains in the DX11/DX12/Vulkan `AposShapesRenderer.cs` files (deleted). GL's existing single-shape harness is untouched.
- [x] Support-surface docs updated; `plan/plan.md` gets a Phase 55 index row.

## 8. As-built (2026-07-23) — measured results

| Backend | Golden source | Result |
|---|---|---|
| Vulkan | `Apos.Shapes`' own embedded Vulkan effect | **maxd 0**, all 30 cells |
| DirectX_11 (`vkd3d-shader`) | `Apos.Shapes`' own embedded DX11 effect | **maxd 0**, all 30 cells |
| DirectX_11 (`d3dcompiler_47` oracle) | the real, locally-generated `mgfxc` golden | **maxd 0**, all 30 cells |
| DirectX_12 | the real, locally-generated `mgfxc` golden | **maxd 1** on a handful of pixels — root-caused 2026-07-31 to the pinned DXC build, see below |
| OpenGL | none (candidate-only) | 30/30 shapes visible; no pixel-diff attempted |

**Correction, same day: the original "DX11 oracle maxd 1 on 14/30 cells" finding was a
methodology bug, not a fidelity gap — found by doing exactly what this phase's own §7 warned
against (trusting the embedded resource as a golden without verifying it).** Disassembling
Apos.Shapes' embedded DX11 effect (via `Vortice.D3DCompiler`'s `D3DDisassemble`) found its
header reads `// Generated by vkd3d-shader 1.17` — it is a `vkd3d-shader` artifact, not an
`mgfxc`/`d3dcompiler_47` one. So the original comparison pitted the `d3dcompiler_47` oracle
against an independent compiler implementation's output; the DX11 vkd3d-shader arm's maxd-0
"match" against the same resource was likewise vkd3d-vs-vkd3d agreement, not evidence of
`mgfxc` fidelity. A prior attempted fix (adding `ShaderFlags.OptimizationLevel3` to
`D3DCompilerShaderCompiler`, hypothesizing a missing mgfxc-matching flag) was tried, measured
to make zero difference to the compiled bytes, and reverted — consistent with the real
explanation being "wrong reference," not "wrong flag." `validation/VsDrivenDx`'s `-- apos`
mode now compares the oracle candidate against the real, already-checked-in
`tests/fixtures/golden/DirectX_11/apos-shapes-sm6.mgfx` (the same golden Phase 51 A3's
single-shape proof used) instead of the embedded resource, and gets **maxd 0 across the full
gallery**. No ShadowDusk defect. No follow-up needed for DX11.

**DX12's `maxd 1` is real, and as of 2026-07-31 it is ROOT-CAUSED: it is the pinned DXC build,
not a ShadowDusk defect.** The earlier "independent-DXC-build drift" hypothesis was right; what
follows is the measurement that turned it into a finding. Two errors in the original write-up are
corrected at the same time — see §8.1 and §8.2.

The two sides compile the same HLSL to DXIL with **different DXC binaries**, and each blob says so
in its own `!llvm.ident` metadata:

| | DXC build | validator |
|---|---|---|
| `mgfxc` golden (MonoGame 3.8.5's bundled DXC) | `dxcoob 1.8.2505.32 (b106a961d)` | 1.9 |
| ShadowDusk (`Vortice.Dxc` 3.3.4, the pinned DXC `e043f4a1`) | `dxcoob 1.7.2212.40 (e043f4a12)` | 1.7 |

**The evidence chain, in the order it eliminates alternatives:**

1. **The vertex shader is already instruction-identical.** Disassembling both `.mgfx`'s VS DXIL
   (`dxc -dumpbin`) and diffing gives zero instruction differences — only the shader hash, the
   `!llvm.ident` string, `!dx.valver`, and comment lines. So the HLSL our pre-parser produces and
   the flags we pass are not obviously wrong; whatever differs is in the pixel shader's codegen.
2. **Our HLSL input and our flags are exactly right.** `validation/DumpPreprocessedHlsl` (added by
   this investigation) dumps the exact text `CompilationPipeline` hands to DXC. Feeding **that
   text**, with **ShadowDusk's own DXC flags** (`-E <entry> -T ps_6_0 -WX -D MGFX=1 -D HLSL=1
   -D SM6=1`), to a **DXC 1.8** build (the Windows SDK 10.0.26100 `dxc.exe`, `1.8.2502.11`)
   reproduces the golden's pixel-shader DXIL **instruction-for-instruction** — the disassembly
   diff is three lines (shader hash, `!llvm.ident`, `!dx.valver`) and zero instructions. Add
   `-Qstrip_reflect` and the whole container comes out the same **41876 bytes** with the same six
   parts (`SFI0`/`ISG1`/`OSG1`/`PSV0`/`HASH`/`DXIL`) at identical offsets and sizes. There is no
   source difference and no flag difference left to blame.
3. **Swapping only the DXIL makes the render exact.** `validation/VsDrivenDx12 -- apos` grew an
   opt-in third arm (`SHADOWDUSK_DX12_PROBE_MGFX`) that renders an arbitrary `.mgfx`. Take
   ShadowDusk's own candidate `.mgfx`, replace **only** the DXIL payload inside the `0xB00B00`
   wrapper with the DXC-1.8 build of the same source, and render it: **maxd 0, zero differing
   pixels of 402,984**, against the same golden that our DXC-1.7 build misses by 1 on eleven.
4. **What the two DXC builds actually disagree about is not math, it is scheduling.** Their DXIL
   *intrinsic* histograms are identical — every `Sample`, `Sqrt`, `Log`, `Exp`, `Sin`, `Cos`,
   `FAbs` matches one for one. They differ only in rewrites `fast` math licenses: 1.7 if-converts
   more aggressively (87 fewer branches, 22 fewer phis, 34 more selects), reorders commutative
   `fmul` operands, and folds eight `x - y*c` into `x + y*(-c)`.
5. **Why any of that reaches a pixel at all.** The shader's last act before quantization is
   `result.rgb += (DitherNoise(p.Pos.xy) - 0.5) * dither_scale`, where `dither_scale` is
   `DitherStrength / 255` — i.e. it deliberately nudges every pixel by up to ±half an 8-bit LSB.
   A sub-ULP float difference upstream therefore flips exactly the pixels sitting on the rounding
   boundary and nothing else. Measured: **11 pixels of 402,984, every one exactly ±1 in a single
   channel.** That is also why the *identity* of the affected pixels moves between builds and
   re-runs, which is what made this look shape-specific when it never was.

**Verdict: not a ShadowDusk defect.** `maxd 1` is the honest tolerance for DX12 for as long as the
two DXC pins differ. **What would make it a defect** (so nobody re-diagnoses this a fourth time):
evidence that our HLSL input or DXC flag list differs from `mgfxc`'s in a way that changes codegen
— re-run step 2 above and look for a *non-comment, non-`!llvm.ident`* line in the disassembly diff.
As long as that diff is empty, the pin is the only variable left. **Closing it means bumping the
DXC pin**, which is a deliberate, cross-target re-baseline (`Vortice.*` is capped *by* the DXC pin;
the same DXC feeds OpenGL/Vulkan SPIR-V and DX11 reflection, and our own macOS/Android/WASM natives
are built from the same commit) — a scoped follow-up, not a bug fix. Tracked in
`docs/validation-matrix.md` §7.

**Also found, and NOT fixed here: `mgfxc` strips reflection from its DX12 DXIL and ShadowDusk does
not.** The golden's container has six parts; ours has seven, carrying an extra `STAT` part (~2.5 KB
per shader) plus the HLSL identifier names (`$Globals`, `TextureSampler`, `BlueNoiseTex`, …) in the
DXIL metadata. Phase 54's note claiming the "spurious `RTS0`/`STAT` parts" had been removed is
half-right: `RTS0` went, `STAT` never did. It is behaviorally inert (the probe arm rendered maxd 0
*with* `STAT` present), so it is a size/name-leak divergence, not a correctness one, and adding
`-Qstrip_reflect` for DirectX12 would move DX12 bytes — deliberately left as its own change.

### 8.1 Correction: the failing "cells" were never `DrawCircle` and `FillArc`

`AposGalleryRenderer.Cells` built its per-cell rectangles from the **untransformed** gallery layout
while `RenderArm` draws through `Matrix.CreateScale(1.15f) * Matrix.CreateTranslation(6, 4)`. A
shape drawn in layout cell (3,3) lands in screen cell (4,4), so the per-cell breakdown named
whichever cell the pixels *landed* in, not the shape that produced them. That is why this delta was
recorded as `DrawCircle`/`FillArc` in this document and as `FillRing` on a later re-run, and why
"what do those two shapes share?" had no answer: they were not the shapes involved. The pixels are
`DrawEllipse`'s (`Color.LightGreen`, verified by centroid: measured (408.0, 406.0), predicted
`1.15 * 350 + 6 = 408.5`). Fixed 2026-07-31 — `Cells` now transforms through the view matrix.

### 8.2 Correction: a third of the gallery was not being compared at all

The same transform, against a render target sized to the *untransformed* 600x500 layout, pushed the
entire last column off the right edge (layout x=550 → screen 638) and the last row down to a ~10px
sliver (layout y=450 → screen 521). So `BorderRectangle`, `BorderPath`, `BorderEquilateralTriangle`,
`BorderEllipse` and `BorderRing` contributed **no pixels to any comparison**, and the five arc/ring
entries contributed only slivers — while the GL visibility check still reported a clean 30/30,
because it was measuring the untransformed rectangles, i.e. whatever the neighbouring column spilled
into them. The acceptance criterion "every shape method is exercised" held at the draw-call level
but not at the pixel level. Fixed 2026-07-31 by sizing the render target to the *transformed* extent
(696x579); the gallery has no stored reference images (every arm renders live in one process), so
this needed no golden regeneration. Re-verified after the resize: DX11 `maxd 0` (both arms),
Vulkan `maxd 0`, GL 30/30 genuinely visible, DX12 `maxd 1` on 11 pixels (up from 5 on the smaller
target, same cause), DXC-1.8 probe `maxd 0`.

**Files touched:** `Directory.Packages.props` (Apos.Shapes 0.7.7 pin), all four
`validation/VsDriven*/*.csproj` (package reference + `Compile Include`), new
`validation/SharedDx/AposGalleryRenderer.cs`, all four `validation/VsDriven*/Program.cs`
(gallery wiring; GL got a new `apos-gallery` mode, others rewired their existing `apos` mode),
deleted `AposShapesRenderer.cs` from DX11/DX12/Vulkan, `validation/run-windows-render-gates.ps1`,
and the support-surface docs listed in §5.8. No `src/` change.

## 7. Notes / risks

- The embedded-resource golden is a **new kind of golden** for this repo (previously, goldens are always a locally-invoked `mgfxc`/`fxc` compile). Task 5.2 exists specifically to not silently trust an unverified assumption here.
- If the pinned `Apos.Shapes` NuGet version's upstream shader has drifted further (e.g. past the `sm6`/`Pack11` revision already on file), re-run the same GL-divergence investigation Phase 51 A3 did before assuming a clean `maxd 0` — that phase found a genuine `mgfxc`/MojoShader bug in one specific fxc-optimized revision's GL output, not a ShadowDusk defect, but it had to be diagnosed, not assumed.
