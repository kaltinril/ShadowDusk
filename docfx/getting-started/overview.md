# Overview

ShadowDusk is a **cross-platform, self-contained, in-memory HLSL shader compiler** for the **XNA-likes** — [MonoGame](https://monogame.net/), [KNI](https://github.com/kniEngine/kni), and [FNA](https://fna-xna.github.io/) (the XNA-derived runtimes; classic Microsoft XNA 4.0 is out of scope). It compiles `.fx` shaders into the format each runtime loads — `.mgfx` for MonoGame/KNI, the legacy D3D9 fx_2_0 `.fxb` for FNA — on **Linux, macOS, or Windows**, with no Wine, no Windows SDK, no `fxc.exe`, and no native toolchain the user has to install separately.

## The problem it solves

MonoGame's stock content pipeline (`MGCB`) shells out to **`mgfxc`**, which depends on **`fxc.exe`** from the DirectX SDK and therefore only runs on Windows. That makes shader compilation a Windows-only build step: it cannot run on **Linux or macOS**, and it cannot run **at runtime or in a browser** at all. (FNA's equivalent path leans on `fxc` and has the same Windows-only constraint.)

ShadowDusk replaces that step with one **portable, faithful pipeline** whose output a real MonoGame, KNI, or FNA `Effect` loads and **renders like the reference compiler's** (`mgfxc` for MonoGame/KNI, `fxc` for FNA).

## What success means — two axes, both required

1. **Reach `mgfxc` can't.** Compile `.fx` where MonoGame's own toolchain cannot: on Linux/macOS (no Wine, no Windows SDK) and at runtime / in-browser via WASM. Matching `mgfxc` *only* on Windows-at-build-time would be pointless — the reach is the reason to exist.
2. **Output the reference compiler would.** The compiled effect, loaded into the real runtime, renders **the same image** as the reference-compiled version — zero code or content-pipeline changes. For MonoGame/KNI the reference is `mgfxc` (the `.mgfx` container); for FNA it is `fxc /T fx_2_0` (the D3D9 `.fxb`).

> **"Same `.mgfx` as `mgfxc`"** means *behaviorally equivalent and `Effect`-loadable* — the same pixels in the real runtime. Byte-identity is only ShadowDusk's **own** reproducibility (same compiler version + source + target → same bytes); it is **never** byte-equality with `mgfxc` (they are different compilers).

## The product and its delivery shapes

| Shape | Package / Tool | Use |
|---|---|---|
| **Library (the product)** | `ShadowDusk.Compiler` — `EffectCompiler : IShaderCompiler` | Add the package, call `CompileAsync(fx)`, get `.mgfx` bytes in-memory. |
| **CLI tool** | `ShadowDusk.Cli` — `dotnet tool` named `mgfxc` | The same library for build-time use from MGCB, scripts, or a terminal. |
| **WASM library** | `ShadowDusk.Wasm` — `WasmShaderCompiler : IShaderCompiler` | The same pipeline inside .NET WASM for in-browser runtime compilation. |

Every shape implements the same <xref:ShadowDusk.Core.IShaderCompiler> interface and runs the **same faithful pipeline** — no substitute compilers. The in-browser [ShaderFiddle.Web](../samples/shaderfiddle-web.md) is a **sample** of the WASM reach, not a separate product.

## Supported backends

| Backend | Output | Status |
|---|---|---|
| OpenGL / DesktopGL | GLSL | Validated end-to-end in real MonoGame DesktopGL |
| DirectX (Windows, DX11) | DXBC (SM5) via vkd3d-shader | Validated end-to-end in real MonoGame WindowsDX |
| FNA | D3D9 fx_2_0 `.fxb` (SM ≤ 3) via vkd3d-shader | Validated end-to-end in real FNA (renders pixel-equivalent to `fxc /T fx_2_0`, PS-only and VS-driven corpora) |
| WebGL (KNI browser) | GLSL ES | Validated end-to-end in real headless KNI WebGL |
| [Metal (macOS / iOS)](../backends/metal.md) | MSL | Not yet implemented (future) |
| [Vulkan](../backends/vulkan.md) | SPIR-V | Future |

> **Output container.** The default is **MGFX v10**, which loads on every MonoGame 3.8.2+ and KNI
> runtime — you never set a flag for correct output. As of **0.6.0**, opt-in/experimental newer
> containers are additionally available: a faithful MonoGame **MGFX v11** (`MgfxVersion = 11`, MonoGame
> 3.8.5+) and KNI's **KNIFX v11** (`Container = EffectContainer.Knifx`, KNI v4.02+), both render-proven in
> their real engines. See [Parameters & Caveats](../guides/parameters-and-caveats.md).

> **Version note:** use **0.4.0 or later** on macOS and Linux. In the 0.3.0 packages the
> OpenGL/WebGL targets failed to compile on macOS (no DXC native) and Linux (an
> argument-marshalling bug); 0.4.0 fixes both — ShadowDusk bundles its own pinned macOS
> DXC dylibs and the corrected interop, the full test suite runs green on all three OSes
> in CI, and the compiled bytes are machine-verified identical across hosts. The FNA
> target works on every OS in both versions.

## Next steps

- [Installation](installation.md) — add the package / install the tool.
- [In-Memory Quickstart](in-memory-quickstart.md) — compile a shader in C# in a few lines.
- [The Faithful Pipeline](../architecture/the-faithful-pipeline.md) — how a `.fx` becomes a `.mgfx`.
