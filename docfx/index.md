---
title: ShadowDusk
---

# ShadowDusk

**A cross-platform HLSL shader compiler for [MonoGame](https://monogame.net/) and [KNI](https://github.com/kniEngine/kni).** Compile `.fx` shaders to `.mgfx` on Linux, macOS, or Windows — no Wine, no Windows SDK, no `fxc.exe`, no DirectX install required.

> The product is the in-memory `IShaderCompiler` library (the `ShadowDusk.Compiler` NuGet package): add the package and call `CompileAsync(fx)` to get `.mgfx` bytes — nothing else to install. The **CLI** (`mgfxc` dotnet tool) and the **MGCB plugin** are delivery shapes of the same library for build-time use. The **in-browser shader fiddle is only a sample of reach — not a separate product.**

## What it does

MonoGame's stock content pipeline shells out to `mgfxc`, a Windows-only tool that depends on `fxc.exe` from the DirectX SDK. ShadowDusk replaces that step with one portable pipeline whose output a real MonoGame/KNI `Effect` loads and **renders like `mgfxc`'s**:

```text
OpenGL / WebGL:
  HLSL (.fx)
    → DXC (via Vortice.Dxc)  →  SPIR-V
    → SPIRV-Cross             →  GLSL (+ MojoShader-dialect rewrite)
    → .mgfx binary            →  MonoGame Effect loader

DirectX (DX11):
  HLSL (.fx)
    → vkd3d-shader            →  DXBC (SM5)
    → .mgfx binary            →  MonoGame Effect loader
```

**OpenGL / WebGL is fully cross-platform and self-contained** (DXC + SPIRV-Cross ride inside the package). For **DirectX (DX11)**, ShadowDusk produces DXBC in-process via two backends behind `IDxbcShaderCompiler`, chosen by `CompilerOptions.DxbcBackend`: the **default** `d3dcompiler_47` (Microsoft's HLSL compiler — a system DLL already present on Windows; most `fxc`-faithful) and the **opt-in, cross-platform** `vkd3d-shader` (`DxbcBackend.Vkd3d`) for Linux/macOS. DXC is **not** used for DX11 (it emits DXIL/SM6, not the DXBC/SM ≤ 5 the DX11 runtime loads). See [The Faithful Pipeline](architecture/the-faithful-pipeline.md) and [DirectX DXBC (vkd3d) Path](architecture/directx-dxbc-vkd3d.md).

## Supported backends

| Backend | Output | Status |
|---|---|---|
| OpenGL / DesktopGL | GLSL | Validated end-to-end (10/10 in real MonoGame DesktopGL) |
| DirectX (Windows, DX11) | DXBC (SM5) via vkd3d-shader | Validated end-to-end (10/10 in real MonoGame WindowsDX) |
| WebGL (XNA Fiddle / KNI browser) | GLSL ES | Validated end-to-end (10/10 in real headless KNI WebGL) |
| [Metal (macOS / iOS)](backends/metal.md) | MSL | **Not yet implemented (future)** |
| [Vulkan](backends/vulkan.md) | SPIR-V | **Future** |

> **"Same `.mgfx` as `mgfxc`"** means behaviorally equivalent and `Effect`-loadable — it renders the same pixels in the real runtime. Byte-identity is only ShadowDusk's *own* reproducibility (same version + source + target → same bytes), **never** byte-equality with `mgfxc`.

## Quick links

- **New here?** Start with [Overview](getting-started/overview.md) → [Installation](getting-started/installation.md) → [In-Memory Quickstart](getting-started/in-memory-quickstart.md).
- **Using the content pipeline?** [Drop-in `mgfxc`](guides/dropin-mgfxc.md) and [MGCB Content Pipeline](guides/mgcb-content-pipeline.md).
- **Compiling in the browser?** [In-Browser (KNI/Blazor WASM)](guides/in-browser-kni-blazor.md).
- **How it works:** the [Architecture](architecture/the-faithful-pipeline.md) section.
- **API:** the [API Reference](../api/index.md) (generated from the code's own XML doc-comments).
- **CLI:** the [`mgfxc` CLI Reference](cli/index.md).

## In-memory in five lines

```csharp
using ShadowDusk.Compiler;
using ShadowDusk.Core;

var compiler = new EffectCompiler();
Result<CompiledShader, ShaderError[]> result =
    await compiler.CompileAsync(hlslSource, new CompilerOptions { Target = PlatformTarget.OpenGL });

if (result.IsSuccess)
{
    byte[] mgfx = result.Value.Data;   // .mgfx, ready for new Effect(graphicsDevice, mgfx)
}
```

See the [In-Memory Quickstart](getting-started/in-memory-quickstart.md) for the full walkthrough (and the default-target caveat between the library and the CLI).

## Design principles

- **No Windows / Wine requirement.** Every native binary has Linux + macOS builds; the pieces the pipeline needs ride inside the package.
- **Drop-in replacement.** Same CLI flags, same `.mgfx` output format, same exit codes and error format as `mgfxc`. Zero changes to existing content pipelines.
- **Deterministic output.** Same source + same target = byte-identical `.mgfx`, given the same compiler version (ShadowDusk's own reproducibility).
- **Fail loudly.** Shader errors surface the source file, line, column, and message exactly as the underlying compiler emitted them.
- **Result-typed errors.** No exceptions for expected shader failures — the API returns `Result<CompiledShader, ShaderError[]>`.

---

The repository [`README.md`](https://github.com/kaltinril/ShadowDusk/blob/main/README.md) is the front door and stays the source of truth for the project summary, the backend matrix, and acknowledgements; this site documents the product, its delivery shapes, samples, and API in depth.
