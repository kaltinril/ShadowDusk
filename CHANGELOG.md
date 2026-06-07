# Changelog

All notable changes to ShadowDusk are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

ShadowDusk is a cross-platform, in-memory drop-in `mgfxc` replacement: a self-contained
library that compiles `.fx` → `.mgfx` at runtime on Linux, macOS, and Windows, with output
that loads and renders identically to `mgfxc`'s in the real MonoGame/KNI runtime. All six
`ShadowDusk.*` packages share a single version (see `Directory.Build.props` `<Version>`).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.1.0] - 2026-06-07

First public release. A single faithful HLSL → `.mgfx` pipeline
(HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL → managed reflect + MojoShader-dialect rewrite +
MGFX writer, or vkd3d-shader → DXBC for DirectX), delivered as a library, a CLI tool, and a
WASM-capable build — the same pipeline on every host, with no substitute compilers.

### Added

- **Cross-platform in-memory `.fx` → `.mgfx` compile.** `ShadowDusk.Compiler`
  (`EffectCompiler : IShaderCompiler`) compiles HLSL `.fx` shaders to MonoGame `.mgfx`
  bytes in-process on Linux, macOS, and Windows — no `fxc.exe`, no `mgfxc`, no Wine, no
  Windows SDK. `IShaderCompiler.CompileAsync(fx)` returns `.mgfx` bytes; no temp files or
  child process required by the API.
- **OpenGL / DesktopGL backend.** HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL with a managed
  MojoShader-dialect rewriter and MGFX writer. SPIRV-Cross rides inside the package via the
  `Silk.NET.SPIRV.Cross.Native` transitive dependency, and DXC via `Vortice.Dxc` — so
  `dotnet add package ShadowDusk.Compiler` and call the API is the entire setup for the GL
  path on a clean machine.
- **DirectX DXBC backend.** Compiles HLSL → SM5 DXBC in-process (no `fxc.exe`/`mgfxc`)
  behind the `IDxbcShaderCompiler` seam, with two backends chosen by
  `CompilerOptions.DxbcBackend`: the **default** `d3dcompiler_47` (Microsoft's HLSL
  compiler — a system DLL already present on Windows; most `fxc`-faithful) and the **opt-in,
  cross-platform** `vkd3d-shader` (`DxbcBackend.Vkd3d`) for compiling DX shaders on
  Linux/macOS where `mgfxc` cannot run. Both render pixel-equivalent to `mgfxc` (Phase 18).
  DXC is not used for DX11 (it emits DXIL/SM6, not DXBC/SM ≤ 5); its `ps_6_0`/`vs_6_0` output
  is retained for the DX12/KNI path. *(Cross-platform `vkd3d` is not yet packaged in the
  NuGet — see Known limitations.)*
- **`mgfxc`-compatible CLI tool.** `ShadowDusk.Cli` ships as a `dotnet tool` named `mgfxc`
  (`dotnet tool install -g ShadowDusk.Cli`) — same CLI flags, same `.mgfx` output format,
  same exit codes, and MGCB-parseable stderr diagnostics, so existing MonoGame content
  pipelines switch with zero code changes (via `ExternalTool` config or PATH override).
- **WASM / in-browser compile engine.** `ShadowDusk.Wasm` (`WasmShaderCompiler`) targets
  `net8.0-browser` and runs the same faithful pipeline in the browser via `[JSImport]`
  bindings to WASM-compiled DXC and SPIRV-Cross — emitting `.mgfx` bytes identical to the
  CLI/desktop path. A pure-managed `SpirvReflector` reflects SPIR-V without a DXIL oracle.
  The in-browser shader-fiddle (`samples/ShaderFiddle.Web`) is a sample of this reach.
- **KNI HiDef / WebGL2 (GLSL ES 3.00) output.** A single `.mgfx` loads and renders in both
  KNI Reach (WebGL1 / GLSL ES 1.00) and KNI HiDef (WebGL2 / GLSL ES 3.00) — the rewriter
  emits `mgfxc`'s `#define ps_oC0 gl_FragColor` form that KNI's runtime converts to a typed
  `out vec4`, with zero consumer input and no new flag or format.
- **GL texture breadth.** Cube maps work on every GL target; 3D textures and explicit
  LOD / gradient sampling work on Desktop and HiDef (WebGL1 cannot, by platform limit). The
  MGFX sampler `Type` byte now carries the reflected texture dimension (2D / Cube / 3D), and
  the rewriter emits per-dimension sampling builtins.
- **VS-driven effects (custom vertex shaders).** Effects that ship their own vertex shader
  (a `float4x4` transform with `POSITION` / `COLOR0` / `TEXCOORD0` attributes) compile
  faithfully on the GL path — the `MonoGameGlslRewriter` emits the symmetric
  `vs_uniforms_vec4` block, the legacy `attribute`/`varying` stage I/O, and the full
  matrix-uniform expansion — not just pixel-shader-only post-process effects.
- **Forward-compatibility with newer MonoGame.** ShadowDusk's default MGFX **v10** output
  loads and renders correctly in MonoGame **3.8.4.1** (the latest stable 3.8.x) as well as
  the pinned **3.8.2.1105** baseline — pixel-identical on the same bytes, within tolerance
  of the `mgfxc` goldens — so a consumer's existing `.mgfx` keeps working forward with no
  action required. A forward-compat regression guard backs this.
- **Self-contained single-file CLI.** `dotnet publish -r <rid> --self-contained` produces a
  working `mgfxc` binary that bundles the native dependencies it needs.

### Validated

- **OpenGL fidelity in the real MonoGame runtime (Phase 17).** All 10/10 shaders of the SM3
  PS-only corpus load in a real MonoGame DesktopGL `Effect` and render pixel-equivalent to
  `mgfxc` — the strongest rung of the evidence ladder: in-engine behavioral equivalence.
- **DirectX fidelity in the real MonoGame runtime (Phase 18).** All 10/10 DX `.mgfx` of the
  SM5 PS-only corpus load in real MonoGame WindowsDX and render pixel-equivalent to `mgfxc`,
  via both the `d3dcompiler_47` oracle and the cross-platform vkd3d-shader backend.
- **VS-driven fidelity in the real MonoGame runtime (Phase 28).** A VS-driven `.fx` (custom
  vertex shader + `float4x4` transform) compiled by ShadowDusk loads in real MonoGame
  DesktopGL **and** WindowsDX and renders pixel-identical (max delta 0) to its `mgfxc`
  golden, on both the `d3dcompiler_47` oracle and the cross-platform vkd3d backend for DX.
- **In-browser render proof (Phases 22–24).** Corpus `.mgfx` load and render in real
  headless KNI WebGL (Reach and HiDef/WebGL2), and the faithful in-browser DXC → WASM path
  emits `.mgfx` byte-identical to the CLI for the corpus.
- **Deterministic, byte-identical output across hosts.** Same ShadowDusk version + same
  source + same target produces the same `.mgfx` bytes on desktop, CLI, and WASM.

### Known limitations

- **DirectX from a pure NuGet add is not yet fully self-contained.** The GL + DXC in-memory
  path ships self-contained from NuGet today; the DirectX vkd3d-shader native is a restored,
  non-redistributed artifact not yet packaged as a `runtimes/<rid>/native/` asset. The
  0.1.0 line advertises GL-from-NuGet as the self-contained path.
- **VS-driven effects** are covered for the SpriteBatch-compatible attribute set
  (`POSITION` / `COLOR0` / `TEXCOORD0`); additional vertex semantics (`NORMAL` / `TANGENT` /
  skinning) and Metal/Vulkan vertex-shader paths are follow-ons.
- **Metal (MSL) and Vulkan backends** are not yet implemented (stubs only).
- **The MGCB content-processor plugin** is a scaffold; the PATH-based `mgfxc` override is the
  shipping MGCB integration path.

[Unreleased]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/kaltinril/ShadowDusk/releases/tag/v0.1.0
