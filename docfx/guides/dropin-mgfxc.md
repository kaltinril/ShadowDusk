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

### Replacing the content pipeline entirely: `.xnb` output

Name an `.xnb` output path and ShadowDusk writes the whole content-pipeline file itself — the container `Content.Load<Effect>("MyShader")` reads — so your game's loading code does not change at all and MGCB never runs:

```sh
ShadowDuskCLI MyShader.fx Content/MyShader.xnb /Profile:OpenGL
```

No flag selects this: the output extension is the whole switch, and any other extension gets the raw effect bytes exactly as before. The effect payload inside the container is byte-for-byte the `.mgfx` the same invocation would write. Drop the file where your `mgfxc`-built `.xnb` used to sit; the asset name is the file name, and no companion file is needed. (From the library, the same thing is `result.Value.ToXnb()`.)

> [!WARNING]
> **Name the profile.** With no `/Profile:` the CLI defaults to **`DirectX_11`** (mgfxc parity), so `ShadowDuskCLI MyShader.fx Content/MyShader.xnb` writes a WindowsDX effect, and a DesktopGL game fails at `Content.Load<Effect>` with *"This MGFX effect was built for a different platform!"* (KNI: *"Effect profile 'DirectX_11' is not compatible with the graphics backend 'OpenGL'."*). The CLI prints `warning SD0029` when an `.xnb` is written with the implicit default; passing any `/Profile:` (or `--target-runtime`) silences it.

The XNB platform identifier is derived from the profile you already chose:

| `/Profile:` (or `--target-runtime`) | XNB platform byte | Runtimes that accept it |
|---|---|---|
| `OpenGL` (`monogame-gl`, `monogame-gl-v11`, `kni-knifx`) | `'d'` (DesktopGL) | MonoGame DesktopGL / Android / iOS / macOS / Web, KNI (every GL platform) |
| `DirectX_11` (`monogame-dx`) — **the CLI default** | `'w'` (Windows) | MonoGame WindowsDX, KNI WinForms.DX11 |
| `DirectX_12` | `'G'` | MonoGame WindowsDX12 (3.8.5+) |
| `Vulkan` | `'V'` | MonoGame DesktopVK (3.8.5+) |
| `FNA` (`fna`) | `'w'` | FNA (the only byte in FNA's list that MonoGame also accepts; the payload is the `.fxb`) |

Every runtime checks the byte only for **membership in its whitelist**, never against the platform actually running (measured on MonoGame, KNI and FNA), so one `/Profile:OpenGL` `.xnb` serves DesktopGL, Android, iOS, macOS and Web alike — an mgcb Android build would say `'a'` where ShadowDusk says `'d'`, and it does not matter.

If the file loads on the wrong runtime, the message names neither the profile nor ShadowDusk. What each one means:

| `Content.Load<Effect>` says | What happened |
|---|---|
| `This MGFX effect was built for a different platform!` (MonoGame) | a `DirectX_11` `.xnb` (the CLI default) in a DesktopGL / Android / iOS / macOS game — recompile with `/Profile:OpenGL` |
| `Effect profile 'DirectX_11' is not compatible with the graphics backend 'OpenGL'.` (KNI 4.3) | the same, on KNI |
| `MOJOSHADER_compileEffect Error: Not an Effects Framework binary` (FNA) | a MonoGame `.mgfx` `.xnb` in an FNA game — FNA loads the fx_2_0 `.fxb`, so use `/Profile:FNA` |
| `This effect seems to be for a newer version of KNI.` / `This effect is an unsupported effect format.` (KNI) | an MGFX **v11** `.xnb` (`--target-runtime monogame-gl-v11`) on KNI, which reads v10 and KNIFX only |
| `FileLoadException: The given assembly name was invalid.` (KNI ≤ 4.2.9001) | a **stock MonoGame-mgcb** `.xnb`, not a ShadowDusk one: KNI 4.2's reader-name resolver rejects mgcb's type-reader manifest; ShadowDusk's `.xnb` carries the XNA-4.0 name and loads |

Proven at rung 4 on **MonoGame** (WindowsDX and DesktopGL), **KNI** (4.2.9001 and 4.3.9001, MGFX v10 and KNIFX payloads) and **FNA** (26.06): a real `ContentManager.Load<Effect>` on the ShadowDusk-written file renders pixel-identical to the reference build on every one of them (`validation/XnbContentLoad`, `XnbContentLoadGl`, `KniXnbContentLoad`, and the `.xnb` arm of `FnaValidation`).

## Replacing `mgfxc` in a build

1. **Explicit invocation (the one that works everywhere).** Call `ShadowDuskCLI` directly from your build script / Makefile / CI step. Because the flags, output, and exit codes match `mgfxc`'s, nothing downstream needs to know it swapped tools.
2. **PATH override**, for a build step that genuinely launches a process named `mgfxc`: expose ShadowDusk's CLI under that name (a renamed copy/symlink of a published build, or a wrapper script forwarding to `ShadowDuskCLI`) ahead of MonoGame's on `PATH` — such scripts look for the *name* `mgfxc`, not `ShadowDuskCLI`, so the installed tool command alone is not picked up.

> [!WARNING]
> **The PATH override does not work for MGCB.** It was documented as the shipping MGCB integration until
> 2026-07-28, when measurement showed `dotnet mgcb` (3.8.2.1105, 3.8.4.1, and 3.8.5 alike) compiles `.fx`
> **in-process** and never launches an external `mgfxc` — so there is no process for the alias to intercept.
> **For MGCB, use the [content-processor plugin](mgcb-content-pipeline.md)** (`ShadowDusk.MgcbPlugin`),
> which MGCB loads via `/reference:` and runs in its own process. Explicit CLI invocation and runtime
> compilation still work too.

## Why it works where `mgfxc` can't

`mgfxc` depends on `fxc.exe` from the DirectX SDK and only runs on Windows. ShadowDusk runs the [faithful pipeline](../architecture/the-faithful-pipeline.md) — DXC → SPIR-V → SPIRV-Cross → GLSL for OpenGL, and `vkd3d-shader` → DXBC for DirectX — on Linux, macOS, and Windows. The DirectX path uses `vkd3d-shader` (cross-platform) rather than DXC, because DXC only emits SM6 DXIL while MonoGame's DX11 runtime loads DXBC (SM ≤ 5); see [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md).

> **Output equivalence.** ShadowDusk's `.mgfx` is *behaviorally equivalent* to `mgfxc`'s — it loads in the same `Effect` and renders the same pixels — not byte-for-byte equal. Determinism is ShadowDusk's own (same version + source + target → same bytes).
