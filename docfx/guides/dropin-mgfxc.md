# Drop-in `mgfxc` Replacement

ShadowDusk's CLI tool is a **transparent substitute** for MonoGame's `mgfxc`: same positional arguments, the same `.mgfx` output format, the same exit codes, and MGCB-parseable error messages on stderr. Games using the MonoGame Content Pipeline require **zero code changes** to switch.

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

Two common patterns:

1. **PATH override (Tier-1).** Expose ShadowDusk's CLI **under the name `mgfxc`** (a renamed copy/symlink of a published build, or a wrapper script named `mgfxc` that forwards to `ShadowDuskCLI`) ahead of MonoGame's `mgfxc` on `PATH` — MGCB and `mgfxc`-shelling scripts look for the *name* `mgfxc`, not `ShadowDuskCLI`, so the tool command alone is not picked up. This is the shipping MGCB integration path — see [MGCB Content Pipeline](mgcb-content-pipeline.md) for the exact steps.
2. **Explicit invocation.** Call `ShadowDuskCLI` directly from your build script / Makefile / CI step.

Because the flags, output, and exit codes match, nothing downstream needs to know it swapped tools.

## Why it works where `mgfxc` can't

`mgfxc` depends on `fxc.exe` from the DirectX SDK and only runs on Windows. ShadowDusk runs the [faithful pipeline](../architecture/the-faithful-pipeline.md) — DXC → SPIR-V → SPIRV-Cross → GLSL for OpenGL, and `vkd3d-shader` → DXBC for DirectX — on Linux, macOS, and Windows. The DirectX path uses `vkd3d-shader` (cross-platform) rather than DXC, because DXC only emits SM6 DXIL while MonoGame's DX11 runtime loads DXBC (SM ≤ 5); see [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md).

> **Output equivalence.** ShadowDusk's `.mgfx` is *behaviorally equivalent* to `mgfxc`'s — it loads in the same `Effect` and renders the same pixels — not byte-for-byte equal. Determinism is ShadowDusk's own (same version + source + target → same bytes).
