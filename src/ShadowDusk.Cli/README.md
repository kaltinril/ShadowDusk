# ShadowDusk.Cli (`ShadowDuskCLI`)

**Cross-platform drop-in replacement for MonoGame's `mgfxc` shader compiler**, as a `dotnet tool`: compiles `.fx` to MonoGame/KNI `.mgfx` and FNA D3D9 fx_2_0 `.fxb` on Linux, macOS, and Windows — no `fxc.exe`, no Wine, no Windows SDK. Same flags, same output format, same exit codes, and MGCB-parseable stderr diagnostics, so an existing MonoGame content pipeline can switch with zero code changes.

## Install

```
dotnet tool install --global ShadowDusk.Cli
```

## Use

```
ShadowDuskCLI <input.fx> <output.mgfx> /Profile:OpenGL
ShadowDuskCLI <input.fx> <output.mgfx> /Profile:DirectX_11
ShadowDuskCLI <input.fx> <output.fxb>  /Profile:FNA
```

Flags mirror `mgfxc` (`/Profile`, `/Defines`, `/Debug`, …). Run `ShadowDuskCLI --help` for the full list.

### As the MGCB shader compiler

Point MGCB's `ExternalTool` at `ShadowDuskCLI` (or alias it to `mgfxc` on `PATH`) and the MonoGame Content Pipeline uses ShadowDusk transparently — including on Linux/macOS build agents where `mgfxc` cannot run.

Self-contained single-file binaries (no .NET install needed) for win-x64, linux-x64, osx-x64, and osx-arm64 are attached to each [GitHub Release](https://github.com/kaltinril/ShadowDusk/releases).

## Links

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
- Library flavor (compile in-process at runtime): **ShadowDusk.Compiler**
