# Overview

ShadowDusk is a self-contained, in-memory HLSL shader compiler for [MonoGame](https://monogame.net/), [KNI](https://github.com/kniEngine/kni), and [FNA](https://fna-xna.github.io/). It compiles `.fx` shaders into the format each runtime loads (`.mgfx` for MonoGame/KNI, the D3D9 `.fxb` for FNA) on Linux, macOS, or Windows. Nothing extra to install: no Wine, no Windows SDK, no fxc.exe, no separate toolchain. (Classic Microsoft XNA 4.0 is out of scope.)

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
| **CLI tool** | `ShadowDusk.Cli` — `dotnet tool` named `ShadowDuskCLI` | The same library for build-time use from MGCB, scripts, or a terminal. For MGCB, expose it on `PATH` under the name `mgfxc`. |
| **WASM library** | `ShadowDusk.Wasm` — `WasmShaderCompiler : IShaderCompiler` | The same pipeline inside .NET WASM for in-browser runtime compilation. |

Every shape implements the same <xref:ShadowDusk.Core.IShaderCompiler> interface and runs the **same faithful pipeline** — no substitute compilers. The in-browser [ShaderFiddle.Web](../samples/shaderfiddle-web.md) is a **sample** of the WASM reach, not a separate product.

## Supported backends

| Backend | Output | Status |
|---|---|---|
| OpenGL / DesktopGL | GLSL `.mgfx` | Supported |
| DirectX 11 | DXBC `.mgfx` | Supported |
| WebGL (KNI browser) | GLSL `.mgfx` | Supported |
| Android (on-device) | GLSL `.mgfx` | Supported (byte-identical to the desktop build) |
| FNA | D3D9 `.fxb` | Supported |
| [Metal (macOS / iOS)](../backends/metal.md) | MSL | Not yet |
| [Vulkan](../backends/vulkan.md) | SPIR-V `.mgfx` | Supported (MonoGame `DesktopVK` only — KNI has no Vulkan platform) |
| [DirectX 12](../backends/directx12.md) (MonoGame `WindowsDX12`) | DXIL `.mgfx` | Supported (MonoGame `WindowsDX12` only — KNI has no DirectX 12 platform) |

Supported targets are tested end-to-end against the reference compiler and render identically (on-device Android via byte-identity: its output is byte-identical to the desktop build, whose renders are proven — the on-device pixel diff is a tracked follow-up). See [Validation](../contributing/validation.md) for how that's proven, and [Choosing a Target](../guides/choosing-a-target.md) to pick one.

> **Output format.** The default is **MGFX v10**, which loads on MonoGame 3.8.2 and every newer
> MonoGame, plus KNI — you never set a flag for correct output. Targeting a newer runtime?
> `MgfxVersion = 11` (MonoGame 3.8.5+) and `Container = EffectContainer.Knifx` (KNI v4.02+) are optional
> and load and render just like v10. See [Parameters & Caveats](../guides/parameters-and-caveats.md).

> **Cross-platform.** Every target compiles on Windows, macOS, and Linux and produces the same
> bytes on every OS — ShadowDusk bundles its own native pieces, and the full test suite runs green
> on all three in CI.

## Next steps

- [Installation](installation.md) — add the package / install the tool.
- [In-Memory Quickstart](in-memory-quickstart.md) — compile a shader in C# in a few lines.
- [The Faithful Pipeline](../architecture/the-faithful-pipeline.md) — how a `.fx` becomes a `.mgfx`.
- [Glossary](../glossary.md) — quick definitions of the shader and compiler terms.
