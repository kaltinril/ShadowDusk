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

- **macOS shader compilation works.** The upstream `Vortice.Dxc` package ships no macOS
  DXC native, so every OpenGL/WebGL compile on a Mac threw `DllNotFoundException`.
  ShadowDusk now bundles its **own `libdxcompiler.dylib`** for osx-x64 and osx-arm64,
  built from the exact DXC commit the bundled Windows/Linux natives report
  (1.7.2212.40 / `e043f4a1` — same compiler, never a substitute), SHA-256-pinned and
  loaded automatically. The full integration suite is green on macOS in CI.

### Changed

- **DirectX 11 (`.mgfx`) compiles now run end-to-end on Linux and macOS** (Phase 18
  Track A). DXBC reflection no longer P/Invokes Windows-only `D3DReflect`
  (d3dcompiler_47): it is a pure-managed reader of the DXBC container's `RDEF`/`ISGN`/
  `OSGN` chunks (`RdefReader`), proven deeply equal to `D3DReflect`'s output for both
  the d3dcompiler_47 and vkd3d backends, with **zero change to emitted `.mgfx` bytes**
  (full-corpus A/B, DirectX + OpenGL). With the vkd3d backend (which already shipped
  for all four desktop RIDs), no Windows-only native remains on the DX11 path.
- The DXC compiler is now constructed lazily inside the pipeline: DirectX 11 compiles
  never load the DXC native (FNA already did not), so they work on hosts where it is
  unavailable (e.g. macOS, pending the Phase 37 A DXC dylib). OpenGL/Vulkan behavior is
  unchanged.

### Fixed

- **Linux shader compilation no longer fails with `Internal Compiler error`.** Every DXC
  compile on Linux failed (`error X0000`): Vortice.Dxc's managed wrapper marshals DXC's
  `LPCWSTR*` arguments as UTF-16 on every OS, but DXC's non-Windows builds use the
  platform's 4-byte `wchar_t` (UTF-32), so the native compiler read garbage arguments.
  ShadowDusk now invokes `IDxcCompiler3::Compile` with platform-correct argument encoding
  (and an explicit UTF-8 source buffer). The native compiler binary is unchanged; Windows
  output is byte-identical. The same fix is what makes the new macOS dylib work.
- The in-browser render-validation harness (the WebGL-vs-DesktopGL pixel compare behind
  the KNI/WebGL support claims) now runs in CI on every change, on a software-GL baseline
  with documented per-shader tolerances — 10/10 corpus shaders load and render
  equivalently in real KNI WebGL1, and the issue #7 HiDef/WebGL2 guard runs with it.

## [0.3.0] - 2026-06-10

### Added

- **FNA support: the new `PlatformTarget.Fna` output target.** Compiles D3D9-style `.fx`
  to the legacy D3D9 Effects binary (`.fxb`) FNA loads — no `fxc.exe`, no Wine, on every
  desktop OS. Render-validated in real FNA 26.06: the validation corpus (PS-only,
  VS-driven, multi-pass, in-pass render states) draws pixel-equivalent (max Δ ≤ 1/255) to
  `fxc /T fx_2_0` output. Purely additive — existing OpenGL/DirectX output is unchanged.
- **vkd3d-shader natives for all four desktop RIDs now ship inside `ShadowDusk.HLSL`**
  (win-x64, linux-x64, osx-x64, osx-arm64; pinned vkd3d 1.17, SHA-256-verified at
  restore). The FNA target and the opt-in `DxbcBackend.Vkd3d` DirectX backend are
  self-contained from the package — add the package, compile, no manual install.
- `THIRD-PARTY-NOTICES.txt` (vkd3d-shader attribution + LGPL-2.1 text) ships in the
  `ShadowDusk.HLSL` package.
- Docs: new "Choosing a target" guide (OpenGL vs DirectX vs FNA, and why output is raw
  `.mgfx`/`.fxb` rather than `.xnb`).

### Changed

- The release pipeline now refuses to publish if the packed `ShadowDusk.HLSL` package is
  missing any of the four vkd3d natives or the license notice — a stopped release beats
  shipping the FNA target broken.

### Fixed

- **FNA: brace-form sampler blocks (`sampler s = sampler_state { Texture = (tex); … };`)
  now bind their texture correctly** — previously the binding was silently lost and the
  effect rendered wrong with no diagnostic.
- **FNA: all render states FNA honors are now emitted into the `.fxb`** (11 previously
  missing states), and states FNA would throw on are rejected loudly at compile time
  (`SD0303`) instead of failing at runtime.
- FNA: matrix parameters now carry the same parameter class `fxc` emits (column-major
  fidelity, pinned by a new golden); shader-model/stage mismatches are caught at compile
  time; SM1 profiles are rejected with a clear error.

## [0.2.0] - 2026-06-07

### Added

- **`ShaderError` is now the diagnostics contract on every host.** A failed
  `IShaderCompiler.CompileAsync` returns `ShaderError[]` with `File`, `Line`, `Column`,
  and the compiler's `Message` verbatim — usable as a `.fx` validator (ignore the bytes,
  read the errors). This already worked on desktop; the **in-browser (WASM) path now carries
  the same line/column** (see Fixed), so a KNI/Blazor tool can highlight the offending line
  with no API change.

### Changed

- **Sample `ShaderFiddle.Web` highlights compile errors.** Bad shader lines get a wavy
  underline, the line-number gutter shows the message on hover, and each diagnostic is
  clickable to jump to its line — a demonstration of the line/column diagnostics above.

### Fixed

- **In-browser (WASM) compile errors now report the source line and column.** Previously a
  failed in-browser compile surfaced a single opaque error (`[object WebAssembly.Exception]`)
  with no location, while desktop reported file/line/column. DXC captured the diagnostics, but
  the WASM module *threw* them and `-fwasm-exceptions` made the text unreadable in JS. The
  faithful DXC→WASM module now **returns** its diagnostics, so the in-browser path runs them
  through the same reformatter as desktop and yields `ShaderError`s with real `Line`/`Column`.
  Compiled output is byte-identical to before (success-path SPIR-V unchanged, 10/10).

## [0.1.1] - 2026-06-07

Maintenance release: the CLI is rebranded to `ShadowDuskCLI`, plus release-pipeline and CI
reliability fixes. The product libraries (`Core` / `HLSL` / `GLSL` / `Compiler` / `Wasm`)
are functionally identical to 0.1.0 — they remain platform-agnostic .NET packages that work
for Linux, macOS, and Windows consumers from a single install.

### Changed

- **CLI renamed `mgfxc` → `ShadowDuskCLI`.** The `dotnet tool` command and the self-contained
  binary now ship under ShadowDusk's own brand rather than the name of the tool they replace.
  The NuGet package id is unchanged (`ShadowDusk.Cli`). To use it as a drop-in for MonoGame's
  content pipeline, point MGCB's `ExternalTool` at `ShadowDuskCLI` (or alias it to `mgfxc`).

### Fixed

- **Release now produces the per-RID self-contained CLI binaries.** The single-file publish
  names the apphost after the assembly, so the GitHub Release verify/archive steps now target
  `ShadowDuskCLI`; 0.1.0's `Publish CLI` jobs failed looking for a `mgfxc` binary.
- **macOS CI no longer hangs.** The ImageTests GL fixture initialized GLFW on macOS, leaving a
  non-background Cocoa thread that kept the test host from exiting after a green run. The GL
  render proxy is now correctly treated as N/A on macOS (Apple deprecated OpenGL; the proxy is
  covered on Linux + Windows), so macOS is back in the release gate and completes in seconds.
- **Quieter, tighter CI.** Doc-only pushes skip the build matrix, the WASM/browser workflow is
  on-demand, and CI job timeouts were tightened from 25–30 min to 10–12 min.

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

[Unreleased]: https://github.com/kaltinril/ShadowDusk/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/kaltinril/ShadowDusk/releases/tag/v0.1.0
