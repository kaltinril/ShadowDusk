# CLI Reference (`mgfxc`)

ShadowDusk's CLI is a **drop-in replacement** for MonoGame's `mgfxc`: same positional arguments, same `.mgfx` output format, same exit codes, and MGCB-parseable diagnostics on stderr. Install it as a global tool:

```sh
dotnet tool install --global ShadowDusk.Cli
```

## Usage

```text
mgfxc <SourceFile> <OutputFile> [options]
```

Arguments are **positional** — `<SourceFile>` then `<OutputFile>`. There is **no** `/Output:` flag (output is the second positional argument).

```sh
mgfxc MyShader.fx MyShader.mgfx /Profile:OpenGL
```

## Options

| Option | Description | Default |
|---|---|---|
| `/Profile:<Platform>` | Target platform. Valid: `DirectX_11`, `OpenGL`, `Vulkan`. | **`DirectX_11`** |
| `/Debug` | Include debug information in the output. | off |
| `/I <path>` | Additional include search path (repeatable). Also accepts `/I:<path>`. | none |
| `--mgfx-version <10\|11>` | Output `.mgfx` format version. | **`10`** |

Unknown flags are **silently ignored** (not consuming a following value) so that future `mgfxc` flags MGCB may pass don't break existing pipelines.

### Unsupported platforms

`/Profile:` values `PlayStation4`, `XboxOne`, and `Switch` are rejected and exit with code **1** (a portable tool can't produce console bytecode). An unknown profile name also fails loudly.

## The default-profile caveat

The CLI default profile is **`DirectX_11`** (matching MonoGame's `mgfxc`), but the **library** default — <xref:ShadowDusk.Core.CompilerOptions.Target> — is **`OpenGL`**:

| Surface | Default target |
|---|---|
| CLI — `mgfxc /Profile` | **`DirectX_11`** |
| Library — `CompilerOptions.Target` | **`OpenGL`** |

So `mgfxc MyShader.fx out.mgfx` (no `/Profile`) compiles for **DirectX_11**, while the equivalent library call with no `Target` compiles for **OpenGL**. Always pass the target explicitly. (See the [In-Memory Quickstart](../getting-started/in-memory-quickstart.md).)

## Examples

```sh
# OpenGL / DesktopGL
mgfxc effects/Blur.fx Content/Blur.mgfx /Profile:OpenGL

# DirectX 11 (the CLI default — /Profile optional)
mgfxc effects/Blur.fx Content/Blur.mgfx /Profile:DirectX_11

# With include paths and debug info
mgfxc effects/Lit.fx Content/Lit.mgfx /Profile:OpenGL /I shaders/common /I shaders/lighting /Debug
```

## Exit codes & diagnostics

- **0** — success.
- **non-zero** — failure; diagnostics are written to **stderr** in `mgfxc`-compatible `file(line,col-col): severity CODE: message` form, which MGCB parses.

## Using it from MGCB

Put ShadowDusk's `mgfxc` first on `PATH`; MGCB then calls it unchanged. See [Drop-in mgfxc](../guides/dropin-mgfxc.md) and [MGCB Content Pipeline (Tier-1)](../guides/mgcb-content-pipeline.md).
