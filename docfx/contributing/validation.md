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
- the **DirectX 12 DXIL** path — pixel-diffed against a real `mgfxc /platform:WindowsDX12` golden (MonoGame 3.8.5's own content pipeline) in a real MonoGame **WindowsDX12** `Effect` and matches exactly (**max Δ 0**) across the 10/10 PS/SpriteBatch corpus, a VS-driven rig, and the `Apos.Shapes` SDF shape renderer (custom vertex shader); the full 30-cell `ShapeBatch` shape gallery lands at 28/30 cells max Δ 0 against the same golden, with 2 cells within 1/255 (an open, not-yet-root-caused follow-up).

## Compare same-backend, never cross-backend

Validation always compares ShadowDusk vs `mgfxc` on the **same** target (GL↔GL, DX↔DX) — never OpenGL output against DirectX output. Each backend is a separate emitted artifact (OpenGL = GLSL text; DirectX = GPU bytecode) loaded by a different runtime path, so a green OpenGL result says nothing about DirectX. A shipped game runs exactly one backend; each must be produced and validated on its own.

## "Same `.mgfx`" ≠ byte-identical to `mgfxc`

"Same `.mgfx` output" means **behaviorally equivalent and `Effect`-loadable**. ShadowDusk and `mgfxc` are different compilers; byte-equality with `mgfxc` is neither expected nor a goal. The "deterministic / byte-identical" constraint refers only to **ShadowDusk's own** reproducibility: same ShadowDusk version + same source + same target → same bytes.

**One carve-out: `DirectX12`.** DXIL signing is Windows-only (`dxil.dll`), so DX12 output *is* host-dependent — a Linux/macOS compile emits unsigned DXIL that retail D3D12 rejects, warned as `SD0214`. The byte-identity manifest covers `DirectX_Vkd3d`, `FNA`, and `OpenGL` only.

## Where the harnesses live

- The render-validation harness is under `validation/` in the repository.
- Cross-platform compile reach is exercised by CI (`.github/workflows/ci.yml`) on Linux, macOS, and Windows.
- The forward-compatibility version matrix (v10 across MonoGame versions) lives under `validation/ForwardCompat/`.

See the [test shader corpus](test-shader-corpus.md) for the inputs.
