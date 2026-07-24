# Phase 55 — Apos.Shapes full shape-gallery render-proof (real `ShapeBatch`, effect substitution)

**Status:** 🔵 Planned (created 2026-07-23).

**Track:** Correctness / drop-in `mgfxc` fidelity — third-party shader corpus depth (breadth was Phase 49; this is depth on the one shader that matters most to a named stakeholder).
**Depends on:** [Phase 49](DONE/PHASE-49-apos-shapes-regression-corpus.md) (corpus + provenance) and [Phase 51](PHASE-51-consolidated-remainder-backlog.md) A3 (the single-shape render-proof this phase supersedes) · the four existing `validation/VsDriven{,Dx,Dx12,Vulkan}` drivers and their `-- apos` mode, which this phase rewrites.
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

**In scope:**
- Add a `PackageReference` to `Apos.Shapes` (the plain MonoGame build, not the KNI variant) in `validation/VsDriven`, `VsDrivenDx`, `VsDrivenDx12`, `VsDrivenVulkan`.
- Vendor the upstream `apos-shapes.fx` revision matching the pinned `Apos.Shapes` NuGet version (new pin, new `NOTICE.md` row — same verbatim-vendoring rule as Phase 49) if it differs from the existing pinned fixtures.
- Build a shape-gallery scene: one `ShapeBatch.Draw*` call per shape kind × {solid fill+border, gradient fill, dashed border, rotated, non-default `aaSize`} — enough permutations to hit every branch in the shader's shape dispatch and style dispatch, not an exhaustive cross-product.
- Render the gallery through both arms (golden embedded effect, ShadowDusk-compiled effect) on GL, DirectX_11, DirectX_12, and Vulkan; pixel-diff per the existing rung-4 pattern (`maxd`, non-vacuity check).
- Retire the hand-rolled `AposVertex`/`BuildCircleQuad`/`Pack11`/`Unpair` code in all four `AposShapesRenderer.cs` once the `ShapeBatch`-driven gallery covers the same ground.
- Update `docs/validation-matrix.md`, `docs/test-shader-corpus.md`, `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md`, and `plan/plan.md` per `CLAUDE.md`'s support-surface rule.

**Out of scope (explicit, not silently dropped):**
- **FNA** — unchanged, permanently excluded (real SM3 instruction-ceiling rejection, already documented; `ShapeBatch` doesn't target FNA anyway).
- **KNI** (`Apos.Shapes.KNI` + `KniVsDriven`/`KniDesktopGL`) — a natural follow-on since ShadowDusk already proves KNI separately, but not committed here; decide as a follow-up once the MonoGame-family gallery is green, so this phase doesn't grow two dimensions (shape breadth × runtime breadth) at once.
- Any change to shipped `ShadowDusk.*` library code. This is validation-only depth; `Apos.Shapes` is a dev dependency of `validation/*` projects, never a product dependency (no license/shipping concern — MIT, validation-only, same posture as the existing test-fixture vendoring).

## 5. Tasks

- [ ] 5.1 Resolve the exact `Apos.Shapes` NuGet version to pin, fetch its matching upstream `Source/Content/apos-shapes.fx` commit, vendor it (provenance header, `LICENSE`, `NOTICE.md` update) if it's a new revision vs. the existing three pinned fixtures.
- [ ] 5.2 Confirm the embedded per-profile resources really are `mgfxc`-produced bytes (not some other tool) before trusting them as goldens — a quick `decode_mgfx.py`-style header/profile-byte check is enough.
- [ ] 5.3 Add the `Apos.Shapes` `PackageReference` to the four `validation/VsDriven*` projects.
- [ ] 5.4 Build the shared shape-gallery scene builder (likely belongs in `validation/Shared`/`SharedDx` given it's identical across backends) driving `ShapeBatch` through both a golden and a candidate instance.
- [ ] 5.5 Wire each backend's `-- apos` mode to render the gallery through both arms and pixel-diff, replacing the single-circle draw.
- [ ] 5.6 Delete the now-unnecessary hand-rolled vertex/packing code from each `AposShapesRenderer.cs`.
- [ ] 5.7 Run `dotnet test ShadowDusk.slnx` (this doesn't touch `src/`, so no output-byte change is expected, but confirm) and `./validation/run-windows-render-gates.ps1`.
- [ ] 5.8 Update the support-surface docs listed in §4 per `CLAUDE.md`.

## 6. Acceptance criteria

- All four backends (GL, DirectX_11, DirectX_12, Vulkan) render the full shape gallery through a real `ShapeBatch` at `maxd` within each backend's already-established tolerance (0 for DX/DX12/Vulkan, the documented ≤2/255 transcendental drift for GL), with the existing non-vacuity check.
- Every `ShapeBatch.Draw*`/`Fill*`/`Border*` shape method is exercised at least once, with gradient, dash, rotation, and corner-radius variants each hit at least once somewhere in the gallery.
- No hand-rolled vertex-packing code remains in any `AposShapesRenderer.cs`.
- Support-surface docs updated; `plan/plan.md` gets a Phase 55 index row.

## 7. Notes / risks

- The embedded-resource golden is a **new kind of golden** for this repo (previously, goldens are always a locally-invoked `mgfxc`/`fxc` compile). Task 5.2 exists specifically to not silently trust an unverified assumption here.
- If the pinned `Apos.Shapes` NuGet version's upstream shader has drifted further (e.g. past the `sm6`/`Pack11` revision already on file), re-run the same GL-divergence investigation Phase 51 A3 did before assuming a clean `maxd 0` — that phase found a genuine `mgfxc`/MojoShader bug in one specific fxc-optimized revision's GL output, not a ShadowDusk defect, but it had to be diagnosed, not assumed.
