# CLI Reference (`ShadowDuskCLI`)

ShadowDusk's CLI is a **drop-in replacement** for MonoGame's `mgfxc`: same positional arguments, same `.mgfx` output format, same exit codes, and MGCB-parseable diagnostics on stderr. Install it as a global tool:

```sh
dotnet tool install --global ShadowDusk.Cli
```

## Usage

```text
ShadowDuskCLI <SourceFile> <OutputFile> [options]
```

Arguments are **positional** — `<SourceFile>` then `<OutputFile>`. There is **no** `/Output:` flag (output is the second positional argument).

```sh
ShadowDuskCLI MyShader.fx MyShader.mgfx /Profile:OpenGL
```

## Options

| Option | Description | Default |
|---|---|---|
| `/Profile:<Platform>` | Target platform. Valid: `DirectX_11`, `OpenGL`, `Vulkan`, `FNA` (the D3D9 fx_2_0 `.fxb` target — additive, not an `mgfxc` profile). | **`DirectX_11`** |
| `/Debug` | Include debug information in the output. | off |
| `/I <path>` | Additional include search path (repeatable). Also accepts `/I:<path>`. | none |
| `--mgfx-version <10\|11>` | MGFX container version (opt-in escape hatch). `10` (default) loads on every MonoGame 3.8.2+ and KNI runtime — leave it unset for correct output everywhere. `11` emits a faithful MonoGame **MGFX v11** container (MonoGame 3.8.5+, opt-in/experimental; renders identically to v10). | **`10`** |
| `--target-runtime <name>` | Pick the output target (backend **and** container/version) with one name: `monogame-gl`, `monogame-dx`, `monogame-gl-v11` (MGFX v11), `kni-knifx` (KNI's KNIFX v11), `fna`. Overrides `/Profile` and `--mgfx-version`. Also accepts `/target-runtime:<name>`. | (use `/Profile`) |

Unknown flags are **silently ignored** (not consuming a following value) so that future `mgfxc` flags MGCB may pass don't break existing pipelines.

### Unsupported platforms

`/Profile:` values `PlayStation4`, `XboxOne`, and `Switch` are rejected and exit with code **1** (a portable tool can't produce console bytecode). An unknown profile name also fails loudly.

## The default-profile caveat

The CLI default profile is **`DirectX_11`** (matching MonoGame's `mgfxc`), but the **library** default — <xref:ShadowDusk.Core.CompilerOptions.Target> — is **`OpenGL`**:

| Surface | Default target |
|---|---|
| CLI — `ShadowDuskCLI /Profile` | **`DirectX_11`** |
| Library — `CompilerOptions.Target` | **`OpenGL`** |

So `ShadowDuskCLI MyShader.fx out.mgfx` (no `/Profile`) compiles for **DirectX_11**, while the equivalent library call with no `Target` compiles for **OpenGL**. Always pass the target explicitly. (See the [In-Memory Quickstart](../getting-started/in-memory-quickstart.md).)

## Examples

```sh
# OpenGL / DesktopGL
ShadowDuskCLI effects/Blur.fx Content/Blur.mgfx /Profile:OpenGL

# DirectX 11 (the CLI default — /Profile optional)
ShadowDuskCLI effects/Blur.fx Content/Blur.mgfx /Profile:DirectX_11

# With include paths and debug info
ShadowDuskCLI effects/Lit.fx Content/Lit.mgfx /Profile:OpenGL /I shaders/common /I shaders/lighting /Debug

# Pick backend + format together by name (KNI's KNIFX v11 container)
ShadowDuskCLI effects/Cube.fx Content/Cube.knifx --target-runtime kni-knifx
```

## Exit codes & diagnostics

- **0** — success.
- **non-zero** — failure; diagnostics are written to **stderr** in `mgfxc`-compatible `file(line,col-col): severity CODE: message` form, which MGCB parses.

## Using it from MGCB

MGCB shells out to the executable **named `mgfxc`**, so expose ShadowDusk's CLI under that name (a renamed copy/symlink or a wrapper script forwarding to `ShadowDuskCLI`) first on `PATH`; MGCB then calls it unchanged. See [Drop-in mgfxc](../guides/dropin-mgfxc.md) and [MGCB Content Pipeline (Tier-1)](../guides/mgcb-content-pipeline.md) for the exact steps.
