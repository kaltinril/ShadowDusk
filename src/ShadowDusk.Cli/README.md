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

Flags mirror `mgfxc` (`/Profile`, `/Debug`, `/I`, `/DxbcBackend`, `--mgfx-version`), plus `--target-runtime` to pick the backend + format together by name. Run `ShadowDuskCLI --help` for the full list.

### ShaderToy / GLSL input

In addition to `.fx`, the CLI accepts a single-pass **ShaderToy / GLSL image shader** (`.glsl`, `.frag`, `.fs`) and converts it to a self-contained `.fx` before compiling — so a ShaderToy `mainImage` (or a plain `void main()` fragment shader) compiles straight to a loadable `.mgfx`/`.fxb` for any target:

```
ShadowDuskCLI shader.glsl shader.mgfx /Profile:OpenGL
```

Detection is automatic (by extension, with a content sniff for off-convention files); **`.fx` is never sniffed and behaves exactly as before**, and no flag is ever required for correct output. The non-required escape hatch `--input-format auto|fx|glsl` forces a route for an oddly-named or genuinely-ambiguous file. Unsupported GLSL constructs fail loudly with an MGCB-parseable `file(line,col): error SDxxxx: message` diagnostic pointing at the original `.glsl`. `--print-uniforms` lists the drivable effect parameters (e.g. `iResolution`, `iTime`, custom `uniform`s) you must set each frame at runtime. The converted shader needs a small per-frame harness (set the uniforms, draw a fullscreen triangle) — see the `ShaderToyViewer` sample.

The output container defaults to **MGFX v10**, which loads on every MonoGame 3.8.2+ and KNI runtime — you never need a flag for correct output. For newer runtimes, `--mgfx-version 11` opts into a faithful MonoGame MGFX v11 container (MonoGame 3.8.5+, opt-in/experimental). To pick a whole target in one flag, `--target-runtime <name>` (`monogame-gl`, `monogame-dx`, `monogame-gl-v11`, `kni-knifx`, `fna`) selects the backend and container together — e.g. `--target-runtime kni-knifx` emits KNI's KNIFX v11 container.

### As the MGCB shader compiler

Point MGCB's `ExternalTool` at `ShadowDuskCLI` (or alias it to `mgfxc` on `PATH`) and the MonoGame Content Pipeline uses ShadowDusk transparently — including on Linux/macOS build agents where `mgfxc` cannot run.

Self-contained single-file binaries (no .NET install needed) for win-x64, linux-x64, osx-x64, and osx-arm64 are attached to each [GitHub Release](https://github.com/kaltinril/ShadowDusk/releases).

## Links

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
- Library flavor (compile in-process at runtime): **ShadowDusk.Compiler**
