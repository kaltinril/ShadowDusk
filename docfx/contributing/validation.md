# Validation & Evidence Ladder

ShadowDusk earns its existence on **two axes, both required**:

1. **Reach `mgfxc` can't** — compile `.fx` on Linux/macOS (no Wine, no Windows SDK) and at runtime / in-browser via WASM.
2. **Output `mgfxc` would** — the compiled `.mgfx`, loaded into the **real** MonoGame/KNI runtime, renders the **same image** as the `mgfxc`-compiled version.

The product is the *combination*: the same result `mgfxc` gives, produced where `mgfxc` can't run.

## The bar: in-engine behavioral equivalence

The measure is **what a player sees in a real MonoGame game**, not "ShadowDusk's own tests pass." Unit tests, structural `.mgfx` tests, and images from ShadowDusk's *own* renderer are necessary **proxies, not the bar** — a proxy can be green while the real goal is unmet.

### Evidence ladder (weakest → strongest)

1. Compiles without error.
2. The `.mgfx` is structurally well-formed.
3. ShadowDusk's GLSL matches `mgfxc`'s GLSL **in our own renderer**.
4. **ShadowDusk's `.mgfx` loads in MonoGame's `Effect` and renders like `mgfxc`'s in the real runtime.** ← only this proves the promise.

Rung 4 is **proven** for:

- the **OpenGL SM3 PS-only corpus** — 10/10 render pixel-equivalent in real MonoGame DesktopGL;
- the **DirectX SM5 PS-only corpus** — 10/10 DX `.mgfx` load in real MonoGame WindowsDX and render pixel-equivalent to `mgfxc`, via **both** the `d3dcompiler_47` oracle and the cross-platform `vkd3d-shader` backend;
- the **KNI WebGL** path — render-equivalent in a real headless KNI WebGL run.

## Compare same-backend, never cross-backend

Validation always compares ShadowDusk vs `mgfxc` on the **same** target (GL↔GL, DX↔DX) — never OpenGL output against DirectX output. Each backend is a separate emitted artifact (OpenGL = GLSL text; DirectX = GPU bytecode) loaded by a different runtime path, so a green OpenGL result says nothing about DirectX. A shipped game runs exactly one backend; each must be produced and validated on its own.

## "Same `.mgfx`" ≠ byte-identical to `mgfxc`

"Same `.mgfx` output" means **behaviorally equivalent and `Effect`-loadable**. ShadowDusk and `mgfxc` are different compilers; byte-equality with `mgfxc` is neither expected nor a goal. The "deterministic / byte-identical" constraint refers only to **ShadowDusk's own** reproducibility: same ShadowDusk version + same source + same target → same bytes.

## Where the harnesses live

- The render-validation harness is under `validation/` in the repository.
- Cross-platform compile reach is exercised by CI (`.github/workflows/ci.yml`) on Linux, macOS, and Windows.
- The forward-compatibility version matrix (v10 across MonoGame versions) lives under `validation/ForwardCompat/`.

See the [test shader corpus](test-shader-corpus.md) for the inputs.
