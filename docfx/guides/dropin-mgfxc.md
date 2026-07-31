# Drop-in `mgfxc` Replacement

ShadowDusk's CLI tool is a **transparent substitute** for MonoGame's `mgfxc`: same positional arguments, the same `.mgfx` output format, the same exit codes, and MGCB-parseable error messages on stderr. A build step that shells out to `mgfxc` can call it instead with **zero downstream changes** — though MGCB itself compiles in-process and cannot be redirected to it (see the warning below).

## Install

```sh
dotnet tool install --global ShadowDusk.Cli
```

This registers a `ShadowDuskCLI` command.

## Usage

```sh
ShadowDuskCLI <SourceFile> <OutputFile> [options]
```

Output is **positional** — `<SourceFile>` then `<OutputFile>`. There is **no** `/Output:` flag. See the [full CLI Reference](../cli/index.md) for every flag.

```sh
# Compile for OpenGL
ShadowDuskCLI MyShader.fx MyShader.mgfx /Profile:OpenGL

# Compile for DirectX 11 (the CLI default profile)
ShadowDuskCLI MyShader.fx MyShader.mgfx /Profile:DirectX_11
```

> **Default profile:** with no `/Profile`, the CLI defaults to **`DirectX_11`** (matching `mgfxc`). Note this differs from the **library** default (`CompilerOptions.Target = OpenGL`). See [Parameters & Caveats](parameters-and-caveats.md).

## Replacing `mgfxc` in a build

1. **Explicit invocation (the one that works everywhere).** Call `ShadowDuskCLI` directly from your build script / Makefile / CI step. Because the flags, output, and exit codes match `mgfxc`'s, nothing downstream needs to know it swapped tools.
2. **PATH override**, for a build step that genuinely launches a process named `mgfxc`: expose ShadowDusk's CLI under that name (a renamed copy/symlink of a published build, or a wrapper script forwarding to `ShadowDuskCLI`) ahead of MonoGame's on `PATH` — such scripts look for the *name* `mgfxc`, not `ShadowDuskCLI`, so the installed tool command alone is not picked up.

> [!WARNING]
> **The PATH override does not work for MGCB.** It was documented as the shipping MGCB integration until
> 2026-07-28, when measurement showed `dotnet mgcb` (3.8.2.1105, 3.8.4.1, and 3.8.5 alike) compiles `.fx`
> **in-process** and never launches an external `mgfxc` — so there is no process for the alias to intercept.
> Use explicit invocation, or compile at runtime. See [MGCB Content Pipeline](mgcb-content-pipeline.md).

## Why it works where `mgfxc` can't

`mgfxc` depends on `fxc.exe` from the DirectX SDK and only runs on Windows. ShadowDusk runs the [faithful pipeline](../architecture/the-faithful-pipeline.md) — DXC → SPIR-V → SPIRV-Cross → GLSL for OpenGL, and `vkd3d-shader` → DXBC for DirectX — on Linux, macOS, and Windows. The DirectX path uses `vkd3d-shader` (cross-platform) rather than DXC, because DXC only emits SM6 DXIL while MonoGame's DX11 runtime loads DXBC (SM ≤ 5); see [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md).

> **Output equivalence.** ShadowDusk's `.mgfx` is *behaviorally equivalent* to `mgfxc`'s — it loads in the same `Effect` and renders the same pixels — not byte-for-byte equal. Determinism is ShadowDusk's own (same version + source + target → same bytes).
