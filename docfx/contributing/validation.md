# Validation & Evidence Ladder

ShadowDusk earns its existence on **two axes, both required**:

1. **Reach `mgfxc` can't** — compile `.fx` on Linux/macOS (no Wine, no Windows SDK) and at runtime / in-browser via WASM.
2. **Output the reference compiler would** — the compiled effect, loaded into the **real** runtime, renders the **same image** as the reference-compiled version (`mgfxc` for MonoGame/KNI `.mgfx`; `fxc /T fx_2_0` for FNA `.fxb`).

The product is the *combination*: the same result `mgfxc` gives, produced where `mgfxc` can't run.

## The bar: in-engine behavioral equivalence

The measure is **what a player sees in a real MonoGame game**, not "ShadowDusk's own tests pass." Unit tests, structural `.mgfx` tests, and images from ShadowDusk's *own* renderer are necessary **proxies, not the bar** — a proxy can be green while the real goal is unmet.

### Evidence ladder (weakest → strongest)

1. Compiles without error.
2. The `.mgfx` is structurally well-formed.
3. ShadowDusk's GLSL matches `mgfxc`'s GLSL **in our own renderer**.
4. **ShadowDusk's `.mgfx` loads in MonoGame's `Effect` and renders like `mgfxc`'s in the real runtime.** ← only this proves the promise.

Rung 4 is **proven** for:

- the **OpenGL SM3 PS-only corpus** — 10/10 render pixel-equivalent in real MonoGame DesktopGL; **Apos.Shapes (Gum's SDF shape renderer)** also render-proven on real MonoGame DesktopGL (max Δ 2/255 — documented transcendental-math GLSL-dialect drift on the shader's OkLab round-trip), via `apos-shapes.fx` — a different, older vendored revision than the DX/Vulkan Apos.Shapes proof uses, because the later revision's real `mgfxc` GL compile is confirmed to render solid black (a MojoShader/fxc codegen bug, not a ShadowDusk defect). The full 30-cell `ShapeBatch` shape gallery also renders through ShadowDusk's GL compile as a candidate-only visibility check — all 30 shapes render visible content; no trustworthy `mgfxc` GL oracle exists for that revision;
- the **DirectX SM5 PS-only corpus** — 10/10 DX `.mgfx` load in real MonoGame WindowsDX and render pixel-equivalent to `mgfxc`, via **both** the `d3dcompiler_47` oracle and the cross-platform `vkd3d-shader` backend; the same is now also true for the real-world **`Apos.Shapes`** SDF shape renderer, driven through the real package's own `ShapeBatch` across its full 30-cell shape gallery (**max Δ 0**, both DXBC backends — the `d3dcompiler_47` arm against the real `mgfxc` golden, the `vkd3d-shader` arm against the package's own vkd3d-compiled embedded effect);
- the **KNI WebGL** path — render-equivalent in a real headless KNI WebGL run;
- the **FNA** target — the PS-only and custom-vertex-shader corpora render pixel-equivalent (max Δ ≤ 1/255, an imperceptible difference) to `fxc /T fx_2_0` in real FNA, including multi-pass effects and in-pass render states;
- the **Vulkan SPIR-V** path — on two arms. Effects with **explicit registers** are pixel-diffed against the `mgfxc` 3.8.5 golden in a real MonoGame 3.8.5 **DesktopVK** `Effect` and match exactly (**max Δ 0**), covering a VS-driven non-identity transform and the upstream `Apos.Shapes` effect — since expanded to the package's full 30-cell `ShapeBatch` shape gallery (**max Δ 0** against the package's own DXC-family embedded effect). The 10/10 PS corpus (profile byte 80) is instead in-engine render-proven on its own output, because `mgfxc`'s Vulkan output crashes in real DesktopVK for **auto-numbered** (non-explicit-register) resources — a confirmed MonoGame-side `SlotOffset` bug — so no reference render exists for that corpus to diff against;
- the **DirectX 12 DXIL** path — pixel-diffed against a real `mgfxc /platform:WindowsDX12` golden (MonoGame 3.8.5's own content pipeline) in a real MonoGame **WindowsDX12** `Effect` and matches exactly (**max Δ 0**) across the 10/10 PS/SpriteBatch corpus, a VS-driven rig, and the `Apos.Shapes` SDF shape renderer (custom vertex shader); the full 30-cell `ShapeBatch` shape gallery lands within 1/255 against the same golden, differing on 11 pixels out of 402,984 — **root-caused to the pinned DXC build, not a ShadowDusk defect**: ShadowDusk's DXIL comes from `dxcoob 1.7.2212.40` (the `Vortice.Dxc` pin) and the golden's from MonoGame 3.8.5's bundled `dxcoob 1.8.2505.32`, and putting ShadowDusk's own preprocessed HLSL and own flags through a DXC 1.8 build reproduces the golden's DXIL instruction-for-instruction and renders at max Δ 0;
- **multiple render targets** — `DeferredSpriteMrtGl` draws a two-output shader on real MonoGame DesktopGL with **two targets bound**, reads **both** attachments back, and pixel-diffs each against the `mgfxc` OpenGL golden (**max Δ 0** on both). Every other GL gate binds a single target, so none of them (and no structural check — that output lives in the emitted GLSL, not the `.mgfx` record tables) could distinguish "the second output reached attachment 1" from "the second output went nowhere";
- the **ShaderToy / `.glsl` frontend route** — `ShaderToyRouteGl` converts `GradientToy.glsl` in process with the real converter and pixel-diffs ShadowDusk's OpenGL build against **`mgfxc`'s build of the same converted `.fx`** on real MonoGame DesktopGL (**max Δ 0**). An `mgfxc` oracle exists here because "no oracle" is true only of ShaderToy *input*: the converter's *output* is ordinary HLSL. The gate asserts the converter still emits the committed `.fx` the golden was built from before it renders, so converter drift turns it red. A **DirectX arm** (`ShaderToyRouteDx`, real MonoGame `WindowsDX`) exists since 2026-07-31 and is default-ON in the Windows gate script; it became possible only once the converter started emitting a DirectX-valid profile header, because `mgfxc` had been refusing the converter's own output for `/Profile:DirectX_11`;
- **per-(texture, sampler)-pair sampler records** — `SamplerPairsGl` renders an *asymmetric* function of two samplers (a symmetric `diffuse * light` would render identically under a swap and prove nothing) so a mis-binding changes the picture, covering both the shared-`SamplerState` and the reverse-first-use shapes that Phase 51 A7 fixed.
- **OpenGL sampler slot allocation** — `SamplerRegisterOrderGl` (issue #189) proves texture units are allocated in HLSL **declaration** order like `fxc`/`mgfxc`, and that an explicit `register(sN)` on a legacy `sampler` pins the unit. It is the **only** GL gate that does not bind every texture through `effect.Parameters[...]`: it leaves unit 0 to `SpriteBatch`, which is what makes the numbering visible in the rendered picture at all. Both arms pixel-diff against real `mgfxc` OpenGL goldens (**max Δ 0**; each measured **max Δ 255** before the fix). Every other GL gate was green throughout the defect, because a first-use-numbered table is internally consistent when the effect binds every unit itself.

## Compare same-backend, never cross-backend

Validation always compares ShadowDusk vs `mgfxc` on the **same** target (GL↔GL, DX↔DX) — never OpenGL output against DirectX output. Each backend is a separate emitted artifact (OpenGL = GLSL text; DirectX = GPU bytecode) loaded by a different runtime path, so a green OpenGL result says nothing about DirectX. A shipped game runs exactly one backend; each must be produced and validated on its own.

## "Same `.mgfx`" ≠ byte-identical to `mgfxc`

"Same `.mgfx` output" means **behaviorally equivalent and `Effect`-loadable**. ShadowDusk and `mgfxc` are different compilers; byte-equality with `mgfxc` is neither expected nor a goal. The "deterministic / byte-identical" constraint refers only to **ShadowDusk's own** reproducibility: same ShadowDusk version + same source + same target → same bytes.

**One carve-out: `DirectX12`.** DXIL signing is Windows-only (`dxil.dll`), so DX12 output *is* host-dependent — a Linux/macOS compile emits unsigned DXIL that retail D3D12 rejects, warned as `SD0214`. The byte-identity manifest covers `DirectX_Vkd3d`, `FNA`, and `OpenGL` only.

## Where the harnesses live

- The render-validation harness is under `validation/` in the repository.
- **Eight in-process OpenGL render gates run in CI** on Mesa llvmpipe (`.github/workflows/validation-render.yml`): `StateFidelity`, `CbufferModel`, `TextureBreadthValidation`, `ReservedWordGl`, `SamplerPairsGl`, `SamplerRegisterOrderGl`, `DeferredSpriteMrtGl`, and `ShaderToyRouteGl`.
- The **DirectX / DX12 / FNA / KNI / Vulkan / browser-ANGLE** gates have no headless CI driver — they run from `validation/run-windows-render-gates.ps1` on a Windows box with a GPU, which is the pre-merge and pre-release bar for anything touching shader output.
- Cross-platform compile reach is exercised by CI (`.github/workflows/ci.yml`) on Linux, macOS, and Windows.
- The forward-compatibility version matrix (v10 across MonoGame versions) lives under `validation/ForwardCompat/`.
- **One driver there is not a render gate:** `validation/MgcbPlugin` runs a real `dotnet mgcb` content build through the [MGCB content-processor plugin](../guides/mgcb-content-pipeline.md) and asserts the `.mgfx` inside the produced `.xnb` is byte-for-byte the CLI's, that the `.xnb` envelope matches MGCB's own stock build, and that the payload differs from stock. It needs no GPU, but it does need `dotnet tool restore` (CI has no `dotnet-mgcb`), so it runs from the same Windows gate script.

See the [test shader corpus](test-shader-corpus.md) for the inputs.
