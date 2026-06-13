---
title: ShadowDusk
---

# ShadowDusk

**A cross-platform HLSL shader compiler for [MonoGame](https://monogame.net/), [KNI](https://github.com/kniEngine/kni), and [FNA](https://fna-xna.github.io/).** Compile `.fx` shaders to `.mgfx` (MonoGame/KNI) or `.fxb` (FNA) on Linux, macOS, or Windows — no Wine, no Windows SDK, no `fxc.exe`, no DirectX install required.

> **The "XNA-likes."** Throughout these docs, **MonoGame, KNI, and FNA** — the XNA-derived runtimes ShadowDusk targets — are collectively the **XNA-likes**. Classic Microsoft XNA 4.0 itself is **out of scope** (a different, abandoned, Windows-only toolchain). The three share a heritage but **not** one effect format: MonoGame and KNI load the `.mgfx` (MGFX) container, while FNA loads the legacy D3D9 fx_2_0 `.fxb` — so where the output format matters, the docs name the runtime explicitly rather than say "XNA-likes."

> The product is the in-memory `IShaderCompiler` library (the `ShadowDusk.Compiler` NuGet package): add the package and call `CompileAsync(fx)` to get the compiled effect bytes (`.mgfx`, or `.fxb` for FNA) — nothing else to install. The **CLI** (`mgfxc` dotnet tool) is a delivery shape of the same library for build-time use (an **MGCB plugin** is a future scaffold, not yet shipped). The **in-browser shader fiddle is only a sample of reach — not a separate product.**

## What it does

MonoGame's stock content pipeline shells out to `mgfxc`, a Windows-only tool that depends on `fxc.exe` from the DirectX SDK. ShadowDusk replaces that step with one portable pipeline whose output a real XNA-like `Effect` — MonoGame, KNI, or FNA — loads and **renders like the reference compiler's** (`mgfxc` for MonoGame/KNI, `fxc` for FNA):

```text
OpenGL / WebGL  (MonoGame, KNI):
  HLSL (.fx)
    → DXC (via Vortice.Dxc)  →  SPIR-V
    → SPIRV-Cross             →  GLSL (+ MojoShader-dialect rewrite)
    → .mgfx binary            →  MonoGame / KNI Effect loader

DirectX (DX11)  (MonoGame, KNI):
  HLSL (.fx)
    → vkd3d-shader            →  DXBC (SM5)
    → .mgfx binary            →  MonoGame / KNI Effect loader

FNA  (D3D9 fx_2_0):
  HLSL (.fx)
    → vkd3d-shader            →  D3D9 fx_2_0 bytecode (SM ≤ 3)
    → .fxb binary             →  FNA Effect loader (MojoShader)
```

**OpenGL / WebGL is fully cross-platform and self-contained** (DXC + SPIRV-Cross ride inside the package). For **DirectX (DX11)**, ShadowDusk produces DXBC in-process via two backends behind `IDxbcShaderCompiler`, chosen by `CompilerOptions.DxbcBackend`: the **default, cross-platform** `vkd3d-shader` (`DxbcBackend.Vkd3d`), whose per-RID natives ship inside the package and run on Linux/macOS/Windows, and the **opt-in** `d3dcompiler_47` (Microsoft's HLSL compiler — a Windows-only system DLL; the most `fxc`-faithful correctness oracle). DXC is **not** used for DX11 (it emits DXIL/SM6, not the DXBC/SM ≤ 5 the DX11 runtime loads). See [The Faithful Pipeline](architecture/the-faithful-pipeline.md) and [DirectX DXBC (vkd3d) Path](architecture/directx-dxbc-vkd3d.md).

## Supported backends

| Backend | Output | Status |
|---|---|---|
| OpenGL / DesktopGL | GLSL | Validated end-to-end (10/10 in real MonoGame DesktopGL) |
| DirectX (DX11) | DXBC (SM5) via `vkd3d-shader` (default, cross-platform) / `d3dcompiler_47` (Windows-only oracle, opt-in) | Validated end-to-end (10/10 in real MonoGame WindowsDX) |
| WebGL (XNA Fiddle / KNI browser) | GLSL ES | Validated end-to-end (10/10 in real headless KNI WebGL) |
| FNA (`/Profile:FNA` → `.fxb`) | D3D9 fx_2_0 via vkd3d-shader | Validated end-to-end (pixel-identical to `fxc /T fx_2_0` in real FNA — PS-only and custom-vertex-shader effects, incl. multi-pass + in-pass render states) |
| [Metal (macOS / iOS)](backends/metal.md) | MSL | **Not yet implemented (future)** |
| [Vulkan](backends/vulkan.md) | SPIR-V | **Future** |

The table above is the **graphics-backend** axis — the one that decides the output bytes for MonoGame/KNI. **Framework** is a separate axis: **MonoGame and KNI** read the same MGFX format (both supported); **FNA** is also supported, but via a different effect path — ShadowDusk emits the legacy D3D9 fx_2_0 `.fxb` (MojoShader) FNA loads, not the MGFX container; classic Microsoft **XNA 4.0** is **out of scope**. New to picking a target, or building a shader-download feature? See **[Choosing a Target](guides/choosing-a-target.md)** — it covers the framework / backend / `GraphicsProfile` axes and the `.mgfx`-vs-`.xnb` distinction.

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
