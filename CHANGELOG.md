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

## [0.6.0] - 2026-06-14

The seamless default is unchanged: MGFX **v10** is still the default container and you never
set a flag to get correct output. This release adds two **opt-in / experimental** container
writers for newer runtimes (MonoGame MGFX v11 and KNI KNIFX v11), recovers macro-declared
techniques on DirectX, and fixes two OpenGL vertex-shader fidelity bugs.

### Added

- **Faithful MGFX v11 writer (opt-in, experimental).** `CompilerOptions.MgfxVersion = 11`
  (CLI `--mgfx-version 11`) now emits a **correct** MonoGame v11 container — where it was
  previously **corrupt** (a v10 body labeled version 11, which a real v11 reader cannot
  parse). MonoGame 3.8.5's `Effect` loader expects a per-shader `SourceFile` and `Entrypoint`
  string in the shader stream (PR #8813); ShadowDusk now writes them. They are diagnostic-only
  (they appear in shader error messages) and do not affect rendering. **Render-proven in real
  MonoGame 3.8.5**: the corpus loads + renders 10/10, max delta 0 vs the v10 render. **v10
  remains the default and never reads them** — `MgfxVersion` is a non-required escape hatch
  (default 10).
- **KNIFX v11 container target (opt-in, experimental).** New public `EffectContainer` enum
  (`Mgfx` default, `Knifx`) and `CompilerOptions.Container` property (default `Mgfx`). Set
  `Container = EffectContainer.Knifx` to emit KNI's newer KNIFX v11 container for KNI v4.02+
  consumers — signature `KNIF`, a multi-backend directory, a packed-int body, and the GL
  GLSL-version directory KNI's runtime requires. **Render-proven in real KNI v4.2.9001 desktop
  GL**: the corpus loads + renders 10/10, max delta 0 vs the v10 render. Additive, not a
  replacement for the v10 default; `MgfxVersion` is ignored when `Container == Knifx`, and
  `Container` is ignored for `PlatformTarget.Fna` (always D3D9 fx_2_0). `CompiledShaderBlob`
  gained three init-only properties with mgfxc's own safe fallbacks (`ShaderModel = (3,0)`,
  `SourceFile`/`Entrypoint = "<unknown>"`).

### Changed

- **DirectX: macro-declared techniques are now recovered.** Stock-MonoGame-style effects that
  declare their technique via the `TECHNIQUE(...)` macro (e.g. `BasicEffect.fx`) now compile
  on DirectX/Vulkan through a gated zero-technique fallback (DXC-preprocess then re-parse).
  OpenGL and FNA explicitly decline this path; existing behavior is otherwise unchanged.
- **vkd3d: include-heavy effects compile without noise** — `#line` directives are blanked so
  they no longer surface as diagnostics.
- **`ShadowDusk.HLSL` package: removed dead public types** as dead-code cleanup
  (`RenderStateMapper`, `MappedRenderState`, the empty `FxFileParser` stub,
  `ReflectionInput.SpirVBlob`, `ReflectionPipeline.ReflectAsync`; `IDxcShaderCompiler` gained a
  `Preprocess` method). Behavior-neutral — no emitted bytes change. The product surface
  (`IShaderCompiler` in `ShadowDusk.Compiler`) is unaffected; this only matters if you
  referenced `ShadowDusk.HLSL` internals directly.

### Fixed

- **OpenGL vertex-shader geometry fidelity ([#70](https://github.com/kaltinril/ShadowDusk/issues/70)).**
  Two silent GL bugs in custom-vertex-shader effects are corrected, moving the default v10 GL
  output toward `mgfxc`-equivalence: a `float4x4` uniform was reconstructed **transposed** (so
  a non-identity `mul(v, M)` rendered an exploded/garbled mesh), and legacy `: POSITION` vertex
  outputs were not mapped to `gl_Position` (silently broken geometry). Both are now
  render-proven **max delta 0** against the `mgfxc` golden in real MonoGame. This intentionally
  changes the v10 GL bytes for VS-driven effects (12 OpenGL byte-identity fixtures updated;
  **zero** DirectX/FNA fixtures changed) — previously broken output is now correct.

> MGFX v11 and KNIFX v11 are opt-in and experimental; the seamless default remains MGFX v10,
> which loads on every MonoGame 3.8.2+ and KNI runtime with no consumer action. FNA (fx_2_0
> `.fxb`) output is byte-identical to the previous release.

## [0.5.1] - 2026-06-12

### Added

- **Every package now ships a README** on its nuget.org page (previously only
  `ShadowDusk.Wasm` had one — the other five showed nuget.org's "missing a README"
  banner).

### Fixed

- **macOS: native-library resolution now keys on the process architecture, not the OS
  architecture.** Under Rosetta 2 (an x64 build running on an Apple-silicon Mac) the
  resolvers for all three natives (DXC, vkd3d-shader, SPIRV-Cross) probed the arm64
  binaries — which can never load into an x64 process — instead of the x64 ones sitting
  beside them, so compiles failed with `X0099`. Caught by the release pipeline's new
  smoke-run gate during the 0.5.0 publish: the run stopped before creating the GitHub
  Release, so the broken self-contained osx-x64 CLI binary never shipped. The 0.5.0
  NuGet packages remain fine for typical consumers (NuGet's own native layout sidesteps
  the buggy probe); 0.5.1 makes the self-contained osx-x64 CLI work on Apple-silicon
  Macs and completes the GitHub Release that 0.5.0 never got.

## [0.5.0] - 2026-06-12

### Added

- **`InitializeAsync()` + synchronous `Compile()`** on the compiler surface
  (`IShaderCompiler` / `EffectCompiler` / `WasmShaderCompiler`) — issue
  [#28](https://github.com/kaltinril/ShadowDusk/issues/28): compile `.fx` from a
  **synchronous** call site (e.g. MonoGame/KNI `Content.Load<Effect>`) after a one-time
  async warm-up, with no sync-over-async deadlock on single-threaded Blazor WASM.
  `await compiler.InitializeAsync()` once (on WASM it loads all the compiler WASM
  modules; on desktop it is a documented no-op), then `compiler.Compile(source, options)`
  runs the entire pipeline on the calling thread. Sync and async share **one** pipeline
  core, so their output is byte-identical (asserted over the full fixture corpus for
  OpenGL, DirectX, and FNA, on desktop and in a real browser). Calling the synchronous
  `Compile` on WASM before `InitializeAsync` returns a clear `SD1903` error telling you
  to initialize first — never an opaque runtime abort. `CompileAsync` is unchanged for
  existing consumers. The backend interfaces (`IDxcShaderCompiler`,
  `IDxbcShaderCompiler`) and reflection pipelines gained matching synchronous entries.
- **In-browser DirectX and FNA compilation.** `WasmShaderCompiler` now compiles
  `PlatformTarget.DirectX` (SM5 DXBC `.mgfx`) and `PlatformTarget.Fna` (D3D9 `.fxb`) in
  the browser, so every shipping target (OpenGL, DirectX, FNA) works on every host. The
  browser runs the **same pinned vkd3d-shader 1.17** the desktop packages bundle,
  compiled to WebAssembly (0.43 MB gzipped) — never a substitute compiler — and the
  emitted bytes are identical to desktop output, asserted over the full DirectX + FNA
  fixture corpus both in Node and in a real headless browser against the committed
  cross-host manifest.

### Changed

- **DirectX compiles default to the cross-platform vkd3d-shader backend on every OS.**
  A bare DirectX compile (including the CLI's default `DirectX_11` profile with no
  backend flag) previously defaulted to the Windows-only `d3dcompiler_47` and
  hard-failed `SD0210` on Linux and macOS. The default is now host-independent — the
  same vkd3d backend everywhere, so default DX output is byte-identical across OSes.
  `d3dcompiler_47` remains fully supported as the opt-in correctness oracle (CLI escape
  hatch `/DxbcBackend:<vkd3d|d3dcompiler>`), and vkd3d's stderr debug chatter is
  suppressed so the CLI keeps `mgfxc`'s silent-success contract.
- **Vertex-stage texture sampling on the GL target now fails at compile time with a
  clear diagnostic** instead of emitting GLSL that MonoGame's GL runtime cannot bind
  (it was silently broken at runtime in two independent ways).
- Sample: `ShaderFiddle.Web` gained an export station — compile once in the browser and
  download the compiled artifact for each target (OpenGL/DirectX `.mgfx`, FNA `.fxb`).

### Fixed

- **GL: effects with a custom vertex shader rendered upside-down when drawing to the
  backbuffer** (the normal game case — only render-target rendering was correct), and
  `UseHalfPixelOffset` was ignored. ShadowDusk baked a static Y-flip into the vertex
  shader where MonoGame expects `mgfxc`'s dynamic `posFixup` uniform (the runtime flips
  the sign for backbuffer vs render target and applies the half-pixel offset).
  ShadowDusk now emits the exact `posFixup` contract, validated pixel-identical
  (max delta 0) to `mgfxc` in real MonoGame 3.8.2 in **both** backbuffer and
  render-target modes.
- **MGFX: pass render states, annotations, and `sampler_state` filter/address states
  are now written in MonoGame 3.8.2's exact wire format.** A pass carrying render
  states (e.g. `AlphaBlendEnable = TRUE;`) or annotations could desync or fail the real
  `Effect` reader, and sampler filter/address modes were silently dropped on MGFX
  targets. All three are now byte-faithful to the real reader, golden-validated and
  render-validated in real MonoGame.
- **GL: effects with multiple cbuffers, a cbuffer shared by VS and PS, or uniform
  arrays now get a correct uniform/parameter model.** Same-stage cbuffers merge into
  one register space, per-stage records bind correctly (a buffer shared by VS and PS is
  no longer deduped into an unbindable record), and array parameters carry per-element
  records so `Effect.Parameters` behaves as with `mgfxc`. Shapes the GL model does not
  yet cover (int/bool/mat3/struct uniform members) now fail loudly at compile time
  (`SD0210`/`SD0012`) instead of emitting wrong GLSL.
- **GL on Mesa (Linux): explicit-LOD/gradient sampling** (`SampleLevel`, `SampleGrad`,
  projective forms) failed on strict drivers because the rewriter emitted generic
  `textureLod`/`textureGrad` in versionless GLSL. These now lower to the legacy builtin
  names under MojoShader's guarded `GL_ARB_shader_texture_lod` header, matching
  `mgfxc`.
- **A first-use race in all three native-library loaders** (DXC, vkd3d-shader,
  SPIRV-Cross): a concurrent first compile could P/Invoke before the import resolver
  was registered, surfacing as an intermittent `DllNotFoundException` under test
  parallelism. Also revived the SPIRV-Cross resolver, which matched the wrong library
  name and never fired (the library had loaded only via default probing).
- Preprocessor/lexer robustness on real-world `.fx`: `#include` diamonds (the same
  header reachable via two paths) no longer error; directives inside comments are
  ignored; the HLSL lexer no longer silently swallows minus signs or unknown
  characters. SPIR-V reflection now populates struct `Members` (parity with the DXIL
  oracle), and colliding `SDxxxx` diagnostic codes were renumbered behind a registry
  test.
- WASM: the DXC module load retries after a transient fetch failure, and the vkd3d
  shim is hardened (allocation null-checks, bounded string reads, clean retry after a
  failed init) — a flaky first fetch no longer wedges the in-browser compiler.

### Verified

- **The CLI and the in-process library emit byte-identical output** — proven over the
  fixture corpus by a parameterized suite that runs every fixture through both
  invocation modes (the CLI is a delivery shape of the library, now machine-checked).
- **The pre-1.0 verification sweep closed every deferred verify item from the
  foundation phases** with 32+ new tests (negative diagnostics coverage, golden
  parameter-table matches against the `mgfxc` goldens, include-resolver and GLSL
  Y-flip checks), plus scripted pack / global-install / self-contained-publish
  verification of the CLI.

## [0.4.0] - 2026-06-11

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

### Verified

- **Cross-host determinism is machine-verified.** CI now asserts a committed SHA-256
  manifest of compiled output (102 fixture×target entries: OpenGL, DirectX via vkd3d, FNA)
  independently on Windows, Linux, and macOS — the emitted bytes are identical on every
  OS, so the Windows render-validation results apply byte-for-byte everywhere.
- **The consumer experience is machine-verified.** A CI job on all three OSes packs the
  packages, installs `ShadowDusk.Compiler` into a scratch project from a local feed, and
  compiles real shaders through it (including the bundled-natives check that a 0.2.0-style
  empty package can never ship again). `THIRD-PARTY-NOTICES.txt` now also covers the
  bundled DXC dylibs (LLVM Release License).

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

[Unreleased]: https://github.com/kaltinril/ShadowDusk/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/kaltinril/ShadowDusk/releases/tag/v0.1.0
