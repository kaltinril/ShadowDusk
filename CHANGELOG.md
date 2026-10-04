# Changelog

All notable changes to ShadowDusk are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

ShadowDusk is a cross-platform, in-memory drop-in `mgfxc` replacement: a self-contained
library that compiles `.fx` → `.mgfx` at runtime on Linux, macOS, and Windows, with output
that loads and renders identically to `mgfxc`'s in the real MonoGame/KNI runtime. All ten
`ShadowDusk.*` packages share a single version (see `Directory.Build.props` `<Version>`).

## [Unreleased]

### Added

- **The Android emulator lane checks the OpenGL corpus on the device, stage by stage (issue #304
  follow-up).** Every OpenGL fixture of the byte-identity manifest is compiled on the device and its
  SPIR-V (DXC), GLSL (SPIRV-Cross) and `.mgfx` must equal the desktop's, with a positive control.
  The desktop half, `OpenGlIntermediatesByteIdentityTests`, pins the new
  `intermediates-manifest.json` on every OS.
- **The Android on-device checks run in CI (issue #304).** The new `android-emulator.yml` boots an
  API-34 x86_64 emulator on ubuntu and runs `validation/AndroidGl/run-dxc-identity-checks.ps1`: an
  HLSL string compiled on the device and loaded into a live MonoGame `Effect` (now on MonoGame
  Android 3.8.5), a foreign and a missing DXC refused with `SD0219`, and a foreign and a missing
  SPIRV-Cross refused with `SD0103`. Any wrong or missing verdict fails the job. It runs on PRs
  labelled `run-android`, weekly, on manual dispatch and on pushes to main that touch the loaders,
  the harness or the restore pins. The x86_64 emulator natives it needs are now hosted on the
  `native-dxc-1.7.2212.40` release (`libdxcompiler.android-x64.so`,
  `libspirv-cross.android-x64.so`) and restored with SHA-256 verification by `tools/restore.*`,
  like the android-arm64 pair; no package ships them. Integration tests now tie the Android DXC
  and SPIRV-Cross build-id pins to the restored files, and CI's integration job requires all four
  Android natives. The identity script runs under `pwsh` on Linux and macOS too.
- **`SkslConverter` can run in the browser (issue #349).** `SkslConverter.Convert` has an overload taking
  DXC / SPIRV-Cross factories (like `EffectCompiler`), and `WasmShaderCompiler.ConvertToSksl` is the
  synchronous entry point after `InitializeAsync()`. The default desktop path is unchanged.

- **Build-time warning `SD0220` when a consumer's graph lifts Vortice.Dxc (issue #282).**
  ShadowDusk.HLSL now ships `buildTransitive/ShadowDusk.HLSL.targets`, so a project that references
  it directly or through ShadowDusk.Compiler / ShadowDusk.ContentPipeline is told AT BUILD TIME when
  it resolves a Vortice.Dxc other than 3.3.4 (another package raising it, measured with
  Evergine.DirectX12, which pulls 3.8.3; or the consumer's own reference). The warning names the
  resolved version, says DirectX 12 / OpenGL / Vulkan will fail at runtime with `SD0219`, and gives
  the fix (pin `Vortice.Dxc` 3.3.4). A warning, never an error: DirectX 11 and FNA do not use DXC
  and keep building; `NoWarn` silences it. Before this the only build-time signal was NuGet's
  generic `NU1608`. Proven end to end by `tools/verify-vortice-dxc-conflict.sh` (a cold consumer of
  the packed feed with Vortice.Dxc 3.8.3, then pinned to 3.3.4, then with no reference), which
  `Pack & Consume` now runs on all three OSes and both TFMs.
- **Android checks the identity of the DXC it loaded (issue #289).** The APK holds no separate file
  to read a build id from, so `DxcLoader` reads the GNU build id from the image the dynamic linker
  mapped (`dl_iterate_phdr`, the PT_NOTE segment in memory) and refuses anything but the pinned
  android-arm64 build with `SD0219`: another package bundling its own `libdxcompiler.so` for the
  same ABI was previously used without a check. Linux runs the same mapped-image check after its
  absolute-path load. Measured on a pixel_7 API-34 x86_64 emulator by the new
  `validation/AndroidGl/run-dxc-identity-checks.ps1`: the pinned build compiles, a copy with one
  build-id byte changed is refused, and an APK without the library gets `SD0219`, never a raw
  `DllNotFoundException`.

- **`CompilerOptions.EmbeddedSourceFileName` (issue #274).** The source-file string an MGFX v11
  container stores per shader can now be set independently of `SourceFileName`, which keeps
  feeding diagnostics and `#include` resolution. Unset (the default) nothing changes: the string
  is `SourceFileName` as passed, like `mgfxc`. A build tool that compiles from absolute paths can
  set it to keep those paths out of DirectX 12 / Vulkan output; ShadowDusk's own content processor
  sets it to `<unknown>`. Ignored by MGFX v10, KNIFX and FNA, which store no source name.
- **Full Slang input in the browser (issue #257, Phase 67).** A browser cannot spawn `slangc`, so the
  pinned slangc v2026.14.1 now also runs inside the page as WebAssembly: the new
  `src/ShadowDusk.Slang.Wasm` project (`WasmSlangCompiler`, not published as a package yet) loads it
  and drives `SlangCompiler` through an internal in-process seam, so the argument list, register
  strip, per-entry merge, `.fx` assembly and the downstream DXC/vkd3d pipeline are the desktop route's
  own code. Slang stays an input language only: the module emits HLSL and never replaces DXC. The
  module is linked from upstream's own prebuilt wasm libraries behind a glue that replays slangc's
  command line (`.wasm-build/slang-wasm/`); upstream's own `slang-wasm.js` was measured unusable for
  this (its API takes no flags or macros; 0/20 corpus entries matched). Measured: 235/235 slangc runs
  byte-identical to native slangc (every corpus, entry point and target, failures included), and in
  real headless Chromium 42/42 corpus artifacts byte-identical to `slang-manifest.json` plus 17/17
  KNI WebGL renders at maxd 0 against the subset frontend. Two new gates in `wasm.yml`
  (`node-test-slangc-wasm.mjs`, `browser-slang-gate.mjs`). The ShaderFiddle sample compiles Slang
  source through it (new "Slang generic blend" example). New codes `SD1904` (slangc module failed to load)
  and `SD1905` (slangc module trapped; the instance is discarded and reloads), and `SD1903` now also covers a sync compile before the slangc module is loaded. The module links with an 8 MB stack (emscripten's 64 KB default trapped on valid nested shaders native slangc compiles). No emitted byte changes on any existing route.
- **Android full-Slang measurement (issue #257).** On an API-34 emulator an app can spawn a packaged
  executable only when native libraries are extracted, which a default Release build does not do,
  and upstream ships no Android slangc, so full Slang on Android will take the in-process route
  (designed in Phase 67, not built). `docs/validation-matrix.md` §7 tracks it.

- **Real-engine render gates for `ShadowDusk.Slang` on DirectX 12, Vulkan and FNA (issue #230).**
  The 21-shader real-slangc corpus goes through `SlangCompiler` and is rendered next to the
  reference compiler's build of the same assembled `.fx`, in the real engine on the same device:
  `validation/SlangFullCorpusDx12` (mgfxc 3.8.5 `/Profile:DirectX_12`, MonoGame 3.8.5 WindowsDX12,
  21/21, max delta 0/255), `validation/SlangFullCorpusVulkan` (mgfxc 3.8.5 `/Profile:Vulkan`, DesktopVK,
  21/21, max delta 1/255) and `validation/FnaValidation -- slang` (`fxc /T fx_2_0`, FNA 26.06, 21/21,
  max delta 1/255, plus the `.xnb` Content.Load arm at delta 0). Each runs two positive controls (a
  swapped-channel pixel shader and a transposed vertex transform) that must diverge, and each now has
  a slot in `validation/run-windows-render-gates.ps1` (FNA under `-IncludeFna`).
- **Linux and Slang evidence for the DXC concurrency fix (issue #256).** No emitted byte changes.
  The fork probe in `DxcConcurrencyStressTests` now also makes real libc `fork()` calls on Linux
  (`Process.Start` there is `vfork()`, which never exercises the macOS mechanism) and reports what
  it raced; on ubuntu CI 16 runs raced about 8,200 DXC compiles against 8,100 process starts and 10,000
  forks with no hang, so `DxcForkGate` stays macOS-only. A new fresh-process `setlocale` audit
  checks every DXC entry point ShadowDusk uses and fails if any call outside the fork gate starts
  calling `setlocale` (reflection was measured not to). A probe pinning which hosts' `Process.Start`
  really forks, and `SlangDxcConcurrencyTests` (slangc spawns racing `.fx` compiles), join it.
  A hung probe child now leaves native stacks (gdb on Linux, `sample` on macOS) and a
  `createdump` core in the CI hang-dump artifact; the Linux integration lane lowers
  `ptrace_scope` so they can attach.

- **The Vulkan render gates run in CI.** `validation-render.yml` gains a `vulkan-render-gates` job
  (ubuntu, label-gated like the GL and DX jobs) that renders `VsDrivenVulkan` (VS-driven fixture vs the
  `mgfxc` golden, then the Apos.Shapes gallery) and the `CandidateVulkan` corpus on real MonoGame
  DesktopVK through Mesa lavapipe. `validation/run-with-vk-validation.sh` forces the Khronos validation
  layer on and fails the gate on any layer error. Two positive controls in the same job (a wrong shader;
  SPIR-V stamped 1.3 on MonoGame's Vulkan 1.0 instance) must turn it red, and do. The Vulkan validation
  drivers now also reference `MonoGame.Runtime.Linux.Vulkan`.

- **Doc-consistency test (issue #218).** `DocConsistencyTests` checks that `plan/plan.md`'s phase index
  agrees with each phase doc's `**Status:**` glyph, that nothing in `plan/DONE/` claims to be open,
  that every linked doc exists, and that `docfx/images/pipeline-overview.svg` carries every note
  from `docs/pipeline-overview.puml`. Unrecognized status glyphs fail, and every `validation/*` driver
  must appear in `docs/validation-matrix.md` section 6 (added the missing `CandidateDx12` and
  `CandidateVkd3d` paths). Also corrects Phase 50's status glyph to match its index row.
- **HLSL → raylib fragment shaders for Raylib-cs (Phase 59, the fragment-only slice).**
  `ShadowDusk.Compiler.Raylib.RaylibConverter.Convert(fx)` turns a single-pass, pixel-only `.fx`
  into a `#version 330` fragment shader for `Raylib.LoadShaderFromMemory(null, fs)`, so one
  post-process source runs on MonoGame and on raylib. Same faithful front half as the OpenGL
  target (DXC, SPIRV-Cross), branching before the MonoGame rewriter; a convention mapper renames
  the interface to raylib's fixed names (`TEXCOORD0` → `fragTexCoord`, `COLOR0` → `fragColor`,
  the output → `finalColor`, the unit-0 sampler → `texture0`) and flattens cbuffers to loose
  uniforms raylib binds by name. The result is a `RaylibShader` with the binding contract
  (uniform names and types, samplers, baked sampler state), deliberately not a `CompiledShader`.
  Anything raylib's model cannot hold is refused by name (`SD0630`–`SD0636`): multi-pass,
  render states, vertex shaders, interpolants other than `TEXCOORD0`/`COLOR0`, Y-orientation
  dependent builtins (`SV_Position`, `ddy`), MRT, non-2D textures, matrix/struct uniforms, and
  names that would collide or bind nothing. `.slang` input works through the existing Slang
  frontend with no raylib-specific code. **Evidence model: rendered-image fidelity, not
  `mgfxc`-equivalence** (raylib has no reference compiler): the new `validation/RaylibRoute`
  gate renders each conversion in real Raylib-cs 8.1.0 (raylib 6.0) and pixel-diffs it against
  the same `.fx` built for OpenGL in real MonoGame DesktopGL; 13/13 shaders (the 10-shader GL
  corpus, a CRT and a handheld-LCD effect, Gum's Grayscale) at maxd 0, with three positive
  controls that must diverge. Runs in the Linux GL CI lane. `glsl100` (web) is not emitted yet.

- **New package: `ShadowDusk.Slang`, a real-slangc compile route for genuine Slang (Phase 66,
  opt-in; win-x64, linux-x64, osx-x64, osx-arm64).** A consumer who needs real Slang — `import`, generics, `interface`
  conformances, everything real slangc accepts, none of which `ShadowDusk.Compiler`'s built-in
  HLSL-compatible-subset `.slang` frontend can compile — adds this separate package; a consumer
  who does not is completely unaffected (zero size, zero dependency, zero behavior change).
  `SlangCompiler` drives the packaged real `slangc` as `-target hlsl`,
  one process invocation per discovered `[shader(...)]` entry point (source piped over
  stdin), merges the per-entry HLSL translation units (deduplicating slangc's redeclared
  shared types/cbuffers), and hands the result to the existing, unchanged `EffectCompiler`
  pipeline — proving genuine Slang (`import`, generics, `interface`s) can compile end to end
  through OpenGL and DirectX_11 without ever substituting for DXC. `-no-mangle` keeps a
  consumer's parameter names (cbuffer members, texture/sampler declarations) intact in the
  compiled effect's reflected parameter table, and stripping slangc's redundant
  `#pragma pack_matrix(column_major)` fixed the OpenGL `layout(row_major)` gap on
  `float4x4` cbuffer members — corpus now **21/21 on both DirectX_11 and OpenGL**. Not yet
  wired into any CLI/MGCB delivery surface; the free default `.slang` support remains
  `ShadowDusk.Compiler`'s HLSL-compatible-subset frontend, untouched. See
  `plan/PHASE-66-full-slang-input-implementation.md`'s A4 write-up for the full mechanism.
- **`ShadowDusk.Slang`'s real three-band accept/reject rule (Phase 66 A5).** `SlangCompiler`
  now rejects an SM6-only wave/quad intrinsic (`WaveActiveSum`, `QuadReadAcrossX`, …) with a new
  registered diagnostic, `SD0624`, naming the intrinsic and the target, on OpenGL, DirectX
  (DX11), and FNA — the three targets architecturally capped below Shader Model 6 that can never
  represent it, detected via a static scan of the finite HLSL SM6 Wave/Quad intrinsics
  vocabulary (a `SlangSm6ConstructGuard` regex scan against the raw Slang source, run before
  slangc is even spawned), chosen over relying on each backend's own inconsistent downstream
  error. Compute/mesh entry points were already rejected loudly by name (`SD0602`, reused
  unchanged from `SlangEntryScanner`) and slangc's own syntax errors already surfaced verbatim
  (`SlangDiagnosticReformatter`) — both verified, not rebuilt. See
  `plan/PHASE-66-full-slang-input-implementation.md`'s A5 write-up.
- **`validation/SlangFullCorpus` (Phase 66 A7), a render gate for `ShadowDusk.Slang`'s
  real-slangc route, distinct from the existing `validation/SlangCorpus` (which keeps
  validating only the subset frontend).** Three gates, default-ON in
  `run-windows-render-gates.ps1`, all measured green: the 21-shader corpus compiles through
  the real `SlangCompiler` on all four reachable targets (**84/84**,
  OpenGL/DirectX_11/DirectX_12/Vulkan); the 8-shader uniform-free procedural subset renders
  pixel-identical (**maxd 0**) on OpenGL between ShadowDusk's `.fx`-wrapped route and the exact
  same slangc invocation `SlangCompiler` uses internally, fed straight to DXC with no wrapping;
  and all 21 shaders load into a real `MonoGame.Framework.WindowsDX` `Effect` and render
  (**21/21**). See `plan/PHASE-66-full-slang-input-implementation.md`'s A7 write-up for what
  is left open (a real-`Effect`-load gate for DirectX_12/Vulkan; the FNA arm).

- **Docs: link to the [FlatRedBall Discord](https://discord.gg/Rr9SMBrPck)** for questions and
  feedback, from the README (new *Community* section), the documentation site's home page and
  footer, and the Contributing guide.

- **`ShadowDusk.Slang` runs on Linux and macOS (issue #227).** The package bundles slangc for
  win-x64, linux-x64, osx-x64 and osx-arm64 (only the slangc executable + its compiler library
  per RID, hash-pinned in `tools/restore.*`), so adding the package is the whole setup on every
  desktop OS. Host floors come from the upstream binaries: Linux needs a GCC 11+ `libstdc++`
  (Ubuntu 22.04+), macOS needs macOS 26+ (follow-up #237 to lift it); any other host gets
  `SD0620` naming the reason. A cross-host byte-identity manifest pins the route's output.
  linux-arm64 and win-arm64 are deliberately not bundled even though upstream publishes them:
  the core pipeline is not complete on either RID (linux-arm64 has no DXC and no vkd3d native;
  win-arm64 has DXC and SPIRV-Cross natives but no vkd3d, so no DirectX or FNA, and no CI lane
  proves it). They follow when the core pipeline does.
- **`ShadowDusk.Slang` is proven to work from a cold NuGet install (issue #225).**
  `tools/verify-slang-packaging.sh`, run by `pack-consume.yml` on all three OSes, packs the
  package, consumes it from a scratch project outside the repo, and compiles real Slang in both
  the framework-dependent and the self-contained publish shape. Direct `SlangToolPath` tests
  cover every probe, the repository-root walk-up, and `ResolveOrThrow`'s failures.
- **The release now publishes `ShadowDusk.Slang` and fails red if any slangc native is missing
  from it (issue #226).**

### Changed

- **Android DXC is now 16 KB page aligned, built reproducibly in CI.** Google Play has required 16 KB
  page support for new apps and updates targeting Android 15+ since November 2025; the
  `libdxcompiler.so` ShadowDusk.HLSL packed for android-arm64 used 4 KB pages (the Android SDK's
  `XA0141` warning), so an app compiling shaders on-device could be rejected. It is rebuilt at the same
  pinned DXC commit (e043f4a1) by `tools/build-dxc-android.sh` (run by `dxc-android-build.yml`: NDK
  r27c, API 24, 16 KB pages, no debug info, stripped; two independent builds per ABI must be
  byte-identical and equal the shipped pins), hosted on the new `native-dxc-android-1.7.2212.40-16k`
  release, with the SHA-256 pins in `tools/restore.*` and the build-id pins in `DxcNativeIdentity`
  updated. No output changed: on an emulator, the 50-shader OpenGL corpus compiled on the device is
  byte-identical to the desktop at every stage (SPIR-V, GLSL, `.mgfx`) with the new DXC. The package's
  arm64 DXC shrinks from 33.4 MB to 25.1 MB. The Android emulator lane now fails on any `XA0141`.
- **Android SPIRV-Cross is now the desktop's SPIRV-Cross (issue #304 follow-up).** The
  `libspirv-cross.so` ShadowDusk.GLSL packs for android-arm64 (and the emulator's android-x64 copy)
  is rebuilt at SPIRV-Cross `d8e3e2b1`, the commit the desktop natives (Silk.NET.SPIRV.Cross.Native
  2.23.0) contain, by a byte-reproducible recipe (`tools/build-spirv-cross-android.sh`, run in CI by
  `spirv-cross-android-build.yml`: NDK r27c, API 21, 16 KB page aligned). The previous file was a
  local build of a newer, modified SPIRV-Cross. Hosted on the new `native-spirv-cross-android-d8e3e2b1`
  release, SHA-256 pinned in `tools/restore.*`, build ids pinned in `SpvcLoader`. No GLSL changed on
  the fixture corpus (measured on an emulator, before and after), so this is a guarantee by
  construction rather than a fix of an observed difference. The new file carries no debug info, so
  the android-arm64 SPIRV-Cross in the ShadowDusk.GLSL package shrinks from about 41.6 MB to 4.6 MB.

- **BREAKING (output names): a legacy sampler with no texture of its own now gets `mgfxc`'s
  parameter name, `X`, instead of `X_SDTexture`.** For `sampler2D X;`, `sampler X;`,
  `sampler2D X : register(sN);`, SpriteBatch's `sampler s0;`, or a `sampler_state` block that names
  no texture, `mgfxc` names the effect parameter `X` (Texture2D) on OpenGL and DirectX 11, and
  ShadowDusk named it `X_SDTexture`, the texture its SM4 rewrite synthesizes. So
  `effect.Parameters["X"]` was `null` on DirectX, and on OpenGL and Vulkan it found a standalone
  sampler parameter that no sampler record points at, so `SetValue(texture)` drew nothing (real
  MonoGame DesktopGL: blue instead of white, maxd 255). The parameter is now `X` on OpenGL,
  DirectX 11, DirectX 12, Vulkan and KNI, and the duplicate sampler parameter of that name is gone;
  shader bytecode is unchanged (the 26 byte-identity entries that moved differ only in the table).
  FNA never synthesized the texture. `mgfxc` 3.8.5 gives no reference for DirectX 12 (empty table)
  or Vulkan (rejects the legacy types), so those targets follow the name `mgfxc` uses everywhere it
  compiles the shape.
  **Migration:** code calling `effect.Parameters["X_SDTexture"]` now gets `null` on every target,
  KNI included, and must use `effect.Parameters["X"]` (which also works with `mgfxc`'s output).
  The raylib and SkSL converters are NOT changed and still expose `X_SDTexture` as the uniform /
  child-shader name.
- **BREAKING (refusal): two legacy samplers on one register are refused (`SD0227`), as `mgfxc`
  refuses them.** `sampler2D A : register(s0); sampler2D B : register(s0);` with both read compiled
  and gave B the next unit on every target, while fxc (so `mgfxc` 3.8.4.1 on OpenGL and
  DirectX_11) stops with `X4500: overlapping register semantics`. OpenGL, DirectX 11, DirectX 12
  and Vulkan now fail with `SD0227` at the second declaration, naming both samplers. Decided on the
  preprocessed source and only for samplers the compiled shader reads, like fxc: a second
  declaration nothing reads, a clash in an inactive `#if` branch, and a sampler sharing its number
  with a `Texture2D : register(tN)` or a constant `register(cN)` all still compile. FNA was already
  refused by vkd3d. **Migration:** an effect that compiled before with this shape now fails, as it
  does under `mgfxc`; give each sampler its own register.
- **A cancelled compile says where its time went (issue #373).** `EffectCompiler.Compile` and
  `CompileAsync` now throw an `OperationCanceledException` whose message gives the elapsed time, how
  long an async compile waited for a thread before it started, how many native compiler calls
  (DXC, SPIRV-Cross, vkd3d-shader, d3dcompiler_47) completed, which was the longest and which the
  last, how many native calls other compiles had in flight and the oldest of them, and the worker
  and thread-pool load. A token cannot interrupt a native call, so this is what tells a stalled
  native call from a starved process. A cancelled `CompileAsync` task is still CANCELED, never
  faulted (`IsCanceled`, `OnlyOnCanceled` continuations and `WhenAll` behave as before); what changes
  is that a compile cancelled while it waited for a thread now also starts and throws the traced
  exception, where it used to end with a bare `TaskCanceledException` (both are an
  `OperationCanceledException`). Output is unchanged.
- **CI's integration lane runs at most two test hosts at once (issues #373, #316, #351).** On the
  4-vCPU `windows-latest` runner, all hosts at once kept the run queue at 20 to 60, and one host at
  a time made no progress for 40 to 176 s (measured from every `.trx` of 74 CI runs); a blame dump
  of one caught it blocked starting a new thread, with no native compiler busy. Every 30 and 60 s
  test budget that expired there was a test inside such a stall. `dotnet test -m:2` keeps the
  step's wall time and removes the stalls (over 5 CI runs per lane the longest no-progress gap was
  22 s on Windows, 28 s on ubuntu, 34 s on macOS, against the 3-minute blame-hang timeout); the per-test compile budgets became one measured
  `TestBudget.Compile` (3 minutes, the blame-hang timeout), and a Windows blame-hang dump is now
  turned into managed stacks in the job.
- **A child process that fails without writing anything is explained (issue #321).** The test
  helper's output for such an exit names the exit code in hex and what it means (for `-1`: the
  process was terminated from outside, `TerminateProcess(-1)` / `Process.Kill`, or called
  `exit(-1)` itself, since the .NET host and runtime print a message for every failure they
  report), how long it ran, its CPU time and its command line, instead of "(no stdout, no stderr)".
- **CI executes `ShadowDusk.Slang`'s osx-x64 slangc (issue #352).** CI's macOS runners are arm64,
  so the bundled osx-x64 slangc was only checked for presence. `pack-consume.yml`'s macOS lane now
  extracts it from the packed nupkg, runs it under Rosetta 2, and fails unless its HLSL is
  byte-identical to the osx-arm64 slangc's (`tools/verify-slang-osx-x64-rosetta.sh`). No package
  change.

- **SkSL converter: `COLOR0` (SpriteBatch's vertex color) now converts by default instead of
  refusing with `SD0611` (issue #368).** A shader that reads `input.Color` (every XnaFiddle
  example, and every `.fx` written for SpriteBatch) converts with the `float4` uniform
  `ShadowDusk_Color` in its place. Set it each draw to the sprite's tint, white when untinted;
  never leave it unset (SkiaSharp does not zero a runtime effect's uniform buffer, so an
  unset value is undefined, not black). It is listed in `SkslConversion.SynthesizedUniforms` with an
  `SD0614` warning, like `ShadowDusk_Resolution`. `TreatVaryingsAsUniforms` still governs every
  other interpolant (still refused by name otherwise). Callers that listed `"COLOR0"` in it keep
  working and get the same output, but the uniform is now named `ShadowDusk_Color`, not
  `in_var_COLOR0`: rename the uniform in your draw code. A `COLOR0` read narrower than `float4`
  is still refused (`SD0611`).

- **SkSL converter: sampling at a computed coordinate now converts instead of refusing with
  `SD0612` (issue #371).** `tex2D(s, uv * 2)` becomes `s.eval((uv * 2) * ShadowDusk_Resolution)`:
  HLSL coordinates are normalized, `.eval()` takes child pixels. `ShadowDusk_Resolution` is the
  existing `float2` uniform, now documented as the pixel size of the element being drawn, which is
  also the size of the bound child textures (one value for Gum to set). A shader that used only
  computed sampling now gets this uniform and its `SD0614` warning too: set it each draw or every
  sample reads the top-left texel. XnaFiddle's Pixelated converts. HLSL's ties-to-even `round`
  (`roundEven`, absent in SkSL) is emitted as an exact `_sd_roundEven` helper. A sampling bias or
  extra argument, or a sampler that is not one of the shader's textures, is still refused
  `SD0612`.

- **`SD0620` on macOS older than 26 now says no self-built slangc is planned (issue #237).**
  The message asks the consumer to open an issue if they need one and points at the built-in
  `.slang` subset frontend, which runs on any macOS. No behavior change.

- **CI dumps a stalled integration test host from outside the process (issue #312).**
  `tools/ci/hang-watchdog.sh` runs as a sibling shell process on the macOS and Linux integration
  lanes: after five minutes it captures lldb or gdb stacks of every thread, a `sample`, the Linux
  wait channels and a `createdump --withheap` of every test host still running, then a second stack
  snapshot, into the `integration-hang-dumps-<os>` artifact, and analyzes the dump on the runner
  with `dotnet-dump` (`threads`, `clrstack -all`, `syncblk`). It exists because the blame hang dump
  cannot see this stall shape (a few frozen tests among a suite that keeps running never trip its
  inactivity timer) and, when it does fire, dumps through a `fork()` inside the hung process. The
  test host also writes a once-a-minute hint of its own after three minutes (thread counts and the
  fork gate's reader/writer state, nothing spawned). It found the issue #312 deadlock on its first
  stalled run.
- **Test child processes leave evidence when they stall (issues #316, #312).** Every test that
  spawns a process (the CLI, the `dotnet exec` DXC probes, `codesign`, `slangc`, the fallback
  `dotnet publish`) now goes through one helper, `tests/ShadowDusk.Integration.Tests/ChildProcess.cs`:
  stdout and stderr are drained concurrently (six of the old per-class copies read stdout to the
  end before stderr, which deadlocks on a child that fills the stderr pipe first), a timeout kills
  the whole process tree, and the failure carries the elapsed time, the command line, the child's
  CPU time and thread states, the output so far and, for .NET children, a dump taken before the
  kill (`MiniDumpWriteDump` on Windows, `createdump` elsewhere). CI's integration step caps a test
  session at 10 minutes instead of 5, so `--blame-hang-timeout 3m` (an inactivity timer that every
  finishing test resets) can still dump a stall that begins late in the run; `CiHangGuardTests`
  fails if the two numbers drift apart again, and xUnit now names every test running over a
  minute. Test budgets were sized from measurement: every CLI compile of a minimal fixture measured on Windows at 0.21 s idle, under 1 s with 20 compiles racing on 16 cores and 7.7 s at worst under 4x CPU oversubscription (6,280 runs, no hang), so the two 30 s CLI budgets become 60 s like the rest (a single-spawn test reached 19 s under that load) and the 60 s and 120 s ones stay.
- **`validation/MgcbPlugin` covers issue #280.** Every case now also has the CLI write an `.xnb`
  and requires its payload to equal the plugin's MGCB payload, and every `DesktopVK` /
  `WindowsDX12` case rebuilds with `/config:Debug` (default `DebugMode=Auto`) and requires the
  same `.xnb` as the default build; where MGCB's stock processor runs, its own `/config:Debug`
  build is checked against its default build too, so the stock premise is re-measured on every
  run. Measured red before the fix (6 of 15 cases) and green after (15 of 15).
- **`validation/MgcbPlugin` and `validation/ContentBuilder` now pin the MGFX v11 source-file string
  (issue #274).** The MGCB gate grew from 13 to 15 cases: the `DesktopVK` / `WindowsDX12` cases
  compare the string with MGCB's own stock build's (now possible for the two fixtures with an
  `#if SM6` branch), require the payload to be the CLI's with only that string replaced, and
  rebuild the effect from a second directory for a byte-identical `.xnb`. The Content Builder gate
  gained real `DesktopVK` and `WindowsDX12` Builder passes (7 to 11 assets) with the same
  assertions and a second Builder run from a different source directory. Both measured red before
  the fix and green after.
- **Every release and pack-consume nupkg gate now matches exact entry names** instead of a
  substring of the listing. A substring match could not tell `runtimes/<rid>/native/slangc`
  from the mis-packed `runtimes/<rid>/native/slangc/slangc`.
- **The `ShadowDusk.Slang` nupkg gate is one script, runnable locally (issue #226).**
  `tools/verify-slang-nupkg.sh <nupkg>` holds the only list of the eight slangc natives (slangc
  plus its slang-compiler library for win-x64, linux-x64, osx-x64 and osx-arm64) and the
  Apache-2.0 notice. `release.yml` and `tools/verify-slang-packaging.sh` (pack-consume.yml) both
  call it, so the two gates can no longer drift apart, and it fails red when the package is
  missing any entry or was not produced at all.

### Fixed

- **OpenGL: a legacy sampler beside modern `Texture2D` + `SamplerState` pairs now gets `mgfxc`'s
  texture unit.** fxc gives every modern pair its unit before any legacy sampler's; ShadowDusk used
  plain declaration order. So `sampler2D Mask; Texture2D SpriteTexture; SamplerState SpriteSampler;`
  put `Mask` on unit 0, where SpriteBatch binds the sprite, and the sprite never reached
  `SpriteTexture` (measured in real MonoGame DesktopGL: black instead of yellow). Matched against
  `mgfxc` 3.8.4.1 on 24 mixed shapes. **Consumer-visible:** an effect that mixes the two styles
  now binds the sprite to the modern texture, as with `mgfxc`; code that worked around the old
  order by binding the legacy sampler's texture to the sprite should stop doing so. Effects that
  use only one style are unchanged.
- **DirectX 11: a legacy `sampler A : register(sN)` binds its sampler at `sN`**, as `mgfxc` does,
  and the effect's sampler record carries `sN`. The rewrite dropped the clause, so the sampler sat
  at `s0` and a state set on `GraphicsDevice.SamplerStates[N]` never reached it (measured in real
  MonoGame WindowsDX: clamped instead of wrapped). **Consumer-visible:** such a shader now reads
  the sampler state at slot N, as with `mgfxc`, instead of slot 0. DirectX 12 and Vulkan are
  unchanged (no `mgfxc` reference for legacy samplers there). DirectX 11 texture SLOTS still differ
  from `mgfxc` when legacy and modern textures are mixed or legacy textures are sampled out of
  declaration order (recorded in `project_facts.md`).
- **`sampler2D A = sampler_state { Texture = <Tex>; };` compiles when nothing declares `Tex`.**
  `mgfxc` reads the name off the state block and emits a `Tex` parameter; ShadowDusk handed DXC an
  undeclared identifier. The compiler now declares `Texture2D Tex;` only when the PREPROCESSED source
  (includes inlined, macros expanded) declares no `Tex`, so a texture declared in an `#include`d
  header or by a macro keeps compiling with its own declaration. Any texture type counts as a declaration, template arguments included (`Texture2D<float4>`, as in MonoGame's `Macros.fxh`), and the added declaration sits ahead of all conditional code, so a sampler declared once per `#if` branch compiles on both sides.
- **The consumer's include resolver is called once per `#include` per compile.** A compile
  flattens the source more than once (the compile itself, the preprocessed sampler views, the
  legacy-sampler recovery); every pass now reuses the resolver's first answer, so a resolver that
  counts, logs, or throws on a repeat call sees one call.
- **`ShadowDusk.Slang` on OpenGL: a combined sampler's register is its texture unit, as `mgfxc` gives
  the legacy `sampler2D X : register(sN)` (refs issue #252).** `Sampler2D SpriteTexture : register(s0)`
  landed on unit 1, so SpriteBatch's unit-0 texture never reached the shader (it rendered white),
  while DirectX 11 sampled `t0`: slangc splits the combined sampler into a texture and a
  `SamplerState : register(s0)`, which the GL allocator read as a modern reservation. The sampler
  register of a combined sampler now pins its texture's unit instead. Measured with `mgfxc` 3.8.4.1
  `/Profile:OpenGL` and matched exactly: `s0`/`s1`/`s2` give units 0/1/2, `A : s1` + `B : s0` give
  1/0, `A : s2` + an unregistered `B` give 2/0, an unregistered `A` + `B : s0` give 1/0. An
  author's split `Texture2D` + `SamplerState : register(sN)` pair keeps the reservation rule, and
  DirectX, Vulkan and FNA output is unchanged. Rendered in real MonoGame DesktopGL by a new
  `validation/SlangTexturedGl` row (maxd 0 vs the `mgfxc` golden; it rendered white before).
  Two combined samplers declaring one register (`Sampler2D A : register(s0); Sampler2D B :
  register(s0);`) are now refused as the new `SD0644` instead of the second moving silently (fxc
  refuses the legacy pair, `X4500`, and the DirectX targets refuse it); `register(sN, spaceM)` pins
  unit N too. Also fixed: the GL units follow the author's DECLARATION order, as `mgfxc` fills them,
  not slangc's first-use order (`Texture2D T; SamplerState S : register(s0); Sampler2D A;` sampled A
  first gave A unit 1 and T unit 2; `mgfxc` and the `.fx` route give T 1, A 2), when every sampled
  texture is declared in the entry text slangc compiled: its preprocessed text when the register pass
  read it, else the raw text outside every `#if` block when no `#define`, `#include`, paste, splice
  or spelled `-D` can change it (a declaration in an inactive branch never places one); otherwise
  slangc's own order, as before. No extra slangc run.
- **vkd3d-shader and SPIRV-Cross are loaded only as ShadowDusk's pinned builds, by absolute path
  (issue #350, the counterpart of #270's DXC fix).** `Vkd3dLoader` (DirectX 11, FNA) and
  `SpvcLoader` (OpenGL) used to fall back to a bare-name load and then to the runtime's default
  probing, both of which are the OS search (`PATH`, `LD_LIBRARY_PATH`, `DYLD_LIBRARY_PATH` and the
  macOS working directory). Whenever the app-local probe missed (a RID-specific publish for
  SPIRV-Cross; every MGCB plugin build for both) a `libvkd3d-shader` or `spirv-cross` found there
  was loaded instead: measured before the fix, a CLI with its pinned native removed and a copy on
  `PATH` compiled DirectX 11, FNA and OpenGL with the `PATH` copy (Windows, measured). Both loaders now probe beside the
  ShadowDusk assemblies, the application directory and the host's native search directories by
  absolute path, check each file's SHA-256 against the pin for the process RID BEFORE loading it
  (vkd3d: the `tools/restore.*` pins; SPIRV-Cross: Silk.NET.SPIRV.Cross.Native 2.23.0's files), check
  on macOS that dyld really mapped that file, and otherwise refuse with `SD0211` / `SD0103` naming
  what was found and where they looked. A foreign library is never loaded. The MGCB plugin's
  last-resort `PluginNativeLibraryResolver` is gone: the loaders find the plugin directory
  themselves. **Behavior change:** an application whose graph resolves a `Silk.NET.SPIRV.Cross.Native`
  other than 2.23.0 now gets `SD0103` for OpenGL (a different SPIRV-Cross) instead of silently different
  GLSL; pin it to 2.23.0. The same build now warns **`SD0226`** at build time
  (`buildTransitive/ShadowDusk.GLSL.targets`, the counterpart of `SD0220`; a warning, never an error,
  `NoWarn`-able), proven cold by `tools/verify-spirv-cross-conflict.sh` in Pack & Consume on all three
  OSes. **Android:** SPIRV-Cross still loads by SONAME from the APK, and the image the linker mapped
  must now carry the pinned GNU build id for its ABI (the mapped-image reader moved from `DxcLoader`
  to `ShadowDusk.Core.ElfImages`, shared by both); another package's `libspirv-cross.so` in the APK
  is refused with `SD0103`, measured on an x86_64 emulator by `validation/AndroidGl/run-dxc-identity-checks.ps1`.
  Proven by `CliNativeSearchPathHijackTest` (all three desktop OSes; on Windows it fails 5/8 against
  the previous loaders), `SpvcLoaderPinTests`, `SpvcLoaderAndroidIdentityTests`, `Vkd3dLoaderTests`
  and `PinnedNativeLibraryTests`. A host that loads the packages in place from the NuGet global
  packages folder (`dotnet fsi` `#r "nuget: ..."`, .NET Interactive notebooks) finds SPIRV-Cross in
  its own `silk.net.spirv.cross.native/<version>` folder beside `shadowdusk.glsl/<version>`, still
  SHA-256 checked; vkd3d and DXC ship in the packages of the assemblies that load them and were
  already found there. `tools/verify-fsi-consumer.sh` proves it with a real `dotnet fsi` script in
  Pack & Consume on all three OSes (OpenGL failed `SD0103` there before this was added). A failed
  load is retried on the next compile (only a success is cached), so a transient failure such as a
  briefly locked file no longer refuses every compile for the life of the process.
  **Also changed by the absolute-path rule:** on linux-arm64 and win-arm64 (and any RID ShadowDusk
  ships no vkd3d-shader for) DirectX 11 and FNA now refuse with `SD0211`, where a distro-installed
  or PATH `libvkd3d-shader` used to be picked up by bare name. ShadowDusk packs vkd3d-shader for
  win-x64, linux-x64, osx-x64 and osx-arm64 only; an x64 process (emulated on Windows on Arm) still
  works.

- **A full `dotnet test` no longer rewrites a tracked file (issue #361).**
  `Phase41StructuralDivergenceMatrixTests` regenerated
  `plan/PHASE-41-appendix/structural-divergence-matrix.md` on every run, so the tree went dirty
  whenever a fixture landed without the appendix being regenerated in the same commit (the census
  count is two cells per non-golden fixture; the content itself is deterministic). It now compares and fails on drift, listing the
  lines that changed and how to regenerate (`SHADOWDUSK_REGENERATE_PHASE41_APPENDIX=1`, see
  `project_rules.md`). The report no longer stamps the assembly version, which moved on every
  release bump without any cell changing, so releases no longer carry a regenerated appendix.
- **Shell and Python scripts check out with LF on Windows (issue #357).** `.gitattributes` pins
  `*.sh`, `*.bash` and `*.py` to `eol=lf` (bash rejected `set -euo pipefail\r`, and a CRLF shebang
  names no interpreter). `VerifySlangNupkgScriptTests` runs the checked-out
  `tools/verify-slang-nupkg.sh` instead of an LF-converted copy, and `ScriptLineEndingTests` pins
  the rules and the checkout. A checkout made before the rules keeps CRLF copies until they are
  deleted and re-checked-out; the test failure says how.
- **A custom NuGet global-packages folder no longer breaks the foreign-DXC tests or the mgfxc
  lookups (issue #362).** The one `$(NuGetPackageRoot)` concatenation (fixed in #354) is now the
  only shape `NuGetPackageRootUsageTests` allows, across every project file. A missing DXC 1.9
  fixture names the file and the NuGet directory the build copied it from.
  `tools/compile-fixtures.ps1` and `validation/ReservedWordGl` read `NUGET_PACKAGES` before
  `~/.nuget/packages`, like the other drivers.
- **Memo and no-memo DirectX / FNA compiles are compared byte for byte (issue #358).** An internal
  `CompilerOptions.BypassDxbcMemo` seam (not a consumer setting) skips `MemoizingDxbcCompiler`, and
  every MonoGame stock effect is compiled both ways on DirectX and FNA with identical container
  bytes and warnings (or identical errors). The bypass arm reaches vkd3d once per pass (64 calls for
  `BasicEffect`), so the two arms are not the memo twice; a deliberately broken memo key fails 9 of
  the 12 cases.
- **`ShadowDusk.ImageTests` no longer fails with `WGL: Failed to make context current: The handle is
  invalid` and then hangs the test host on Windows (issue #345).** The GL fixture created its hidden
  window on a thread-pool thread, and Windows destroys a window when its creating thread exits: once
  the pool retired that thread (20 s idle, reached only under a loaded full-solution run) the context
  was gone. The window now lives on a thread the fixture owns. The failure also hung the host, because
  the fixture threw out of make-current while holding its lock; a make-current failure now fails that
  test and every later GL test at once, a claim of the context waits at most 2 minutes, and a GL call
  that never returns ends the host after 3 minutes instead of hanging the run. The net8.0 and net10.0
  hosts that a solution `dotnet test` runs together are serialized around GL by a named mutex. The
  test project retires idle pool threads after 100 ms, which reproduced the old failure on every run,
  so every ImageTests run now re-proves the fix. Test infrastructure only; no shipped package changes.
- **`ShadowDusk.Slang` on OpenGL: an array of combined samplers (`Sampler2D Comb[3]`) is `SD0217`,
  located at the author's declaration and saying what to do, like the `Texture2D Tex[3]` array
  (issue #356).** It used to surface as SPIRV-Cross's bare `SD0100` ("arrays or structs of separate
  samplers"), which is kept verbatim at the end of the new message and in `RawDiagnostics`. An
  author-written `SamplerState S[N]` is unchanged (`SD0100`, as on the `.fx` route), and so is one named
  like slangc's lowering (`SamplerState My_sampler_0[2]`): the rewrite needs the author's source to
  declare the combined sampler as an array.
- **`ShadowDusk.Slang`: a slangc that Windows cannot start no longer opens a modal system dialog.**
  On an interactive Windows desktop, launching a damaged or wrong-architecture `slangc.exe` raised
  an "Unsupported 16-Bit Application" message box that blocked the compiling thread until someone
  clicked OK. The launch now runs with the calling thread's hard-error dialogs off
  (`SetThreadErrorMode`, restored afterwards, nothing else in the process changes), so it fails at
  once as `SD0622` with the OS's reason.
- **`ShadowDusk.Slang` on DirectX: `Sampler2D A : register(s2)` binding texture `t0` and sampler `s2`
  is confirmed correct and pinned (issue #355).** slangc binds `register(sN)` on a combined sampler
  to its sampler half only, and real `mgfxc` 3.8.4.1 compiles the hand-written legacy
  `sampler2D A : register(s2)` to the same bindings (`fxc /dumpbin`: `dcl_resource_texture2d t0`,
  `dcl_sampler s2`), so `GraphicsDevice.Textures[2]` does not reach that texture under `mgfxc`
  either. `Sampler2D A : register(t2) : register(s2)` binds both halves to slot 2. No output change.

- **Debug output no longer depends on the files in the working directory or on the host (issue
  #343).** With `Debug` set, DXC's SPIR-V emitter (OpenGL, Vulkan) fills each `OpSource` by
  reading the file it names wherever its leaf-name self-load succeeds (Windows, macOS 14+, Linux
  with a copy of the pinned build on `LD_LIBRARY_PATH`): the main input, DXC's default `hlsl.hlsl`
  in the working directory, and, found while fixing this, every file a `#line` directive names,
  which includes a relative or absolute `SourceFileName` and the files it includes. So a
  `hlsl.hlsl` in the working directory replaced the compiled source in the debug Vulkan module, and
  any file at the `SourceFileName` path was embedded verbatim (measured on Windows with decoys:
  both texts in the output); a plain Linux host embedded neither, so the same compile gave
  different debug bytes per host. Debug SPIR-V compiles now name their input
  `/dev/null/<shadowdusk-in-memory>/hlsl.hlsl`, which no host can open (not a directory on
  Linux/macOS, an illegal Win32 name on Windows), so the main `OpSource` is always the in-memory
  text; and every other `OpSource` drops any text DXC read (`DxcDebugSpirvSource`), which is what
  it carries when the file does not exist. The browser build runs the same step (its DXC never
  reads a file; its debug SPIR-V for the new arguments was measured byte-identical to desktop's).
  Debug Vulkan bytes change once (the main file name, and no disk text); release output, DirectX
  12 debug output and OpenGL debug output are byte-identical to before (measured over all 174
  corpus fixtures, four targets, release and debug). A module the step cannot walk is `SD0225`.
  Guarded by `DxcDebugSourceWorkingDirectoryTests` (every OS: decoys for `hlsl.hlsl` and the
  `SourceFileName` in the child's working directory, outputs hashed against a clean directory, a
  pre-fix positive control that must read both decoys, and the debug Vulkan hash pinned to
  win-x64's so every lane proves cross-host identity) and `DxcDebugSpirvSourceTests`.

- **The raylib and SkSL converters compile a legacy sampler declared in an `#include` or through a
  macro (issue #327).** `RaylibConverter.Convert` and `SkslConverter.Convert` pre-parse the raw main
  file and hand its text to the shared DXC seam, so every shape issue #308 fixed on the OpenGL route
  (a `sampler` / `sampler2D` in an `#include`d file, a register clause or whole declaration that
  comes out of a macro such as MonoGame's `DECLARE_TEXTURE`, a `tex2D` inside a macro body or an
  include) was still refused with DXC's own error ("unknown type name 'sampler2D'", "deprecated
  tex2D intrinsic"). Both converters now run the same recovery `CompilationPipeline.Run` uses
  (`LegacySamplerRecovery`, one copy of the outcome handling shared by all three callers): after
  the DXC compile has failed, and only then, the pre-parse is repeated on the preprocessed source
  and the converter's own validations, seam, sampler-register reading and mapper run again from it.
  Measured: every one of the 16 include / macro shapes converts to EXACTLY what its directly
  written twin converts to (fragment shader, uniforms, samplers, warnings; SkSL text, children,
  synthesized uniforms), and the whole 164-file fixture corpus converted with the fix off and on
  is byte-identical for everything that converted before (raylib 79/79; SkSL 33/33 by default and
  69/69 with the varyings opt-in). Eleven fixtures move from "does not convert" to converting
  (`SamplerLegacyInclude`, `SamplerLegacyMacroDecl`, the nine vendored MonoGame `Include.fxh`
  effects; on SkSL, `Bevels` now reaches its real `SD0612` refusal instead of DXC's error). The
  shapes the recovery cannot model are the same `SD0016` as on the OpenGL route, appended after
  the compiler's own diagnostics. Rung 4 for raylib: `validation/RaylibRoute` gains the
  `LegacyInclude` and `LegacyMacroDecl` arms (both masks bound by name on units 2 and 3), 15/15 at
  maxd 0 in real Raylib-cs vs real MonoGame DesktopGL. Cost: nothing for an effect that converts at
  once (3.6 vs 3.3 ms in-process, noise); a recovered effect pays its failed first DXC compile plus
  the preprocessed re-parse (about 6 ms against its direct twin's 3.6 ms).
- **DirectX 11 reflected a texture array as one parameter per element where `mgfxc` reflects one
  (issue #339).** `Texture2D Tex[2]` came out as parameters `Tex[0]` and `Tex[1]` with one sampler
  record each, so `effect.Parameters["Tex"]` was null and, measured in real MonoGame WindowsDX, the
  `Tex[1]` record nulled texture slot 1 at `Apply` (maxd 128 against `mgfxc`'s build). Root cause,
  measured with d3dcompiler_47 on 2026-10-02: fxc at Shader Model 4 (the author's `ps_4_0`, which
  `mgfxc` compiles as written) reflects the array as ONE binding `Tex` with `BindCount` N at the
  array's base register whichever elements are read, while at Shader Model 5 (ShadowDusk's DirectX
  11 compile model, and vkd3d-shader's convention) the RDEF stores one record per element, which
  `D3DReflect` reports as stored. `mgfxc` makes one record per texture binding and one parameter per
  distinct name, hence its single `Tex`. The DXBC reflection extractor now folds the per-element
  records back into that view (`ResourceArrayBindings`; `RdefReader` stays a faithful view of the
  RDEF, its D3DReflect parity contract). Measured against `mgfxc` 3.8.4.1 (the pinned v10 oracle)
  record for record on both DXBC backends for N = 1, 2 and 4, with and without a register, an array
  declared after another texture, and an array with only element 1 read: one `Tex` parameter, one
  record at the base slot. In real MonoGame WindowsDX (`validation/VsDrivenDx -- texarr`, new gate
  row, RTX 3080) both backends render pixel-identical to `mgfxc`'s build (maxd 0), and element `[1]`
  bound through `GraphicsDevice.Textures[1]` IS read there (unlike WindowsDX12, see `SD0222`), so
  DirectX 11 emits no array diagnostic. Committed `mgfxc` 3.8.4.1 DirectX_11 goldens for both
  texture-array fixtures. The whole fixture corpus compiled byte-identical before and after on all
  four profiles (668 cells); only the two texture-array fixtures moved on DirectX_11, as intended.
- **A sampler array compiled on DirectX 11 and 12 where `mgfxc` refuses it (issue #340).**
  `SamplerState S[N]` is refused by `mgfxc` 3.8.4.1 and 3.8.5 on EVERY profile in its own effect
  parser (`Unexpected token '[' found. Expected Semicolon, Comma, or CloseParenthesis.`), so no
  reference output exists for the shape. ShadowDusk compiled it anyway, with sampler records keyed on the
  texture bindings and no record naming a sampler element; it now refuses it with the new error
  **`SD0224`**, located at the declaration, on both targets (the one DirectX 12 case DXIL cannot
  distinguish, a 1-element array, is found from the declaration text). Measured first, as
  evidence (`validation/VsDrivenDx -- samparr <mgfx>`, `VsDrivenDx12 -- samparr <mgfx>`, RTX
  3080): the pre-fix DirectX 11 and DirectX 12 effects of the new fixture loaded into real
  WindowsDX and WindowsDX12 and drew with both textures sampled, so the refusal is for parity with
  the reference compiler (which builds nothing for the shape), not because the engine rejected it. New
  expect-diagnostic fixture `texture-arrays/SamplerArray2.fx` (no golden: `mgfxc` builds none).
  `SD0222` now covers texture arrays only. On the real-slangc route an author-written
  `SamplerState S[N]` is refused the same way, relocated to the author's Slang line, while slangc's
  own lowering of a combined-sampler array (`Sampler2D T[N]`, a texture array plus
  `SamplerState T_sampler_0[N]`) stays exempt (an internal seam, `CompilerOptions.
  SamplerArraysFromCombinedSamplers`): it is one author resource, and its table is the
  hand-written `Texture2D T[N]; SamplerState S;` one on DirectX 11 (one `T` parameter, now that
  issue #339 collapsed the per-element records) as it already was on DirectX 12.
- **A debug SPIR-V compile can no longer hand DXC a `libdxcompiler` that is not ShadowDusk's
  pinned build (issue #332).** Every OpenGL or Vulkan compile with `Debug` set (`-Zi`) makes DXC's
  SPIR-V emitter load `libdxcompiler` a second time, by LEAF name, from inside the compile
  (`clang::spirv::ReadSourceCode` -> `DxcDllSupport::Initialize`, to read the source for
  `OpSource`): a name search, which `DxcLoader` otherwise never allows, and whose answer is the
  dynamic linker's. Measured: Windows and dyld on macOS 14+ answer with the already-loaded pinned
  image (by base name; by its `@rpath/libdxcompiler.dylib` install name), glibc never does (the
  pinned `SONAME` is `libdxcompiler.so.3.7`) and would load a `libdxcompiler.so` from
  `LD_LIBRARY_PATH` (where the Vulkan SDK's `setup-env.sh` puts one) and run its initializer
  inside the compile; dyld on macOS 12/13 would do the same from the working directory or
  `DYLD_FALLBACK_LIBRARY_PATH`. `DxcLoader` now asks the linker right after the pinned load,
  without loading anything (`dlopen(leaf, RTLD_NOLOAD)` plus glibc's "found but not loaded"
  report; `GetModuleHandleW` on Windows; a stat of the dyld-940/1042 directories when dyld does not
  match), and the debug SPIR-V compiles alone are refused with the new `SD0223`, naming the library
  the linker would hand DXC and the fix, when it is not the pinned build; a byte copy of the pinned
  build is the same compiler and is accepted, as at load time. Release compiles, DXIL debug compiles,
  DirectX 11 and FNA never make the load and are untouched. No emitted byte moves: the probe loads
  nothing and what DXC's own load returns is unchanged (every scenario's output is hashed against the
  no-decoy compile in `DxcDebugSpirvLeafNameTests`, which runs the linker's own trace,
  `LD_DEBUG=libs` / `DYLD_PRINT_SEARCHING`, on the ubuntu and macOS lanes). One-time cost per
  process: 1 to 1.5 ms on Windows, 2 to 22 ms on the macOS lane, 18 to 92 ms on the ubuntu lane
  (glibc opens each candidate); nothing per compile beyond an argument check.
- **Texture arrays reflected a wrong parameter table on Vulkan (issue #324).** `Texture2D Tex[N]`
  is a pointer to an array of image types in SPIR-V, and the pure-managed reflector matched only
  a bare image or sampler type, so the texture vanished from the Vulkan effect while its sampler
  survived: the table held `TexSampler` and nothing a game could set the texture through. Measured
  against the reference first (`dotnet-mgfxc` 3.8.5, N = 1, 2 and 4, with and without an explicit
  register, identical for every shape): `/Profile:Vulkan` reflects the array as **no parameter, no
  sampler record and no descriptor binding at all** (the SPIR-V still samples it), so the effect
  cannot draw; `/Profile:DirectX_12` reflects **one `Tex` parameter bound to slot 0**, which is what
  ShadowDusk already emitted there (the issue's "one element" on DirectX 12 is the reference's own
  table, and the other elements are reachable through `GraphicsDevice.Textures[i]`); `mgfxc` refuses
  a sampler array (`SamplerState S[N]`) on every profile in its own parser. Both reflectors now
  report an array with its element count (`TextureReflection.ArrayLength`,
  `SamplerReflection.ArrayLength`: the `OpTypeArray` length from SPIR-V, `BindCount` from DXIL and
  RDEF), and the Vulkan target refuses an array of textures or samplers with the new **`SD0221`**,
  located at the declaration, naming the resource and the reference compiler's measured behavior,
  instead of emitting that effect. On DirectX 12 the output is unchanged and a new **warning
  `SD0222`** says what that table means in the engine: measured in real MonoGame 3.8.5 WindowsDX12,
  element `[1]` reads as zero for `mgfxc`'s build and ShadowDusk's alike, even with
  `GraphicsDevice.Textures[1]` set, because the shader header sizes the descriptor range for one
  texture. The real-slangc `.slang` route gets the same codes for a `Sampler2D T[N]` (slangc emits a
  texture array plus a sampler array), relocated from slangc's core module to the author's
  declaration; on DirectX 12 it keeps the single-parameter table the `.fx` route and `mgfxc`
  produce. New fixtures `tests/fixtures/shaders/texture-arrays/TextureArray2.fx` and
  `TextureArray4NoRegister.fx` with `mgfxc` 3.8.5 goldens for DirectX_12 and Vulkan; new render
  rows `validation/VsDrivenDx12 -- texarr` (both arms in real MonoGame 3.8.5 WindowsDX12: table
  equal record for record, maxd 0) and `validation/VsDrivenVulkan -- texarr` (the rejection, plus
  the golden's empty table; `-- texarr-reference` draws `mgfxc`'s own effect in real DesktopVK:
  it loads with no `Tex` parameter and draws nothing). No emitted byte changes for any shader
  without a texture or sampler array (the whole fixture corpus, 656 cells on four profiles,
  hashed identical before and after). OpenGL already failed on the shape (`SD0217`, as `mgfxc` does
  with `Sequence contains no matching element`) but called the array "not declared as a separate
  texture"; the message now names the array. DirectX 11 is untouched:
  there vkd3d/fxc reflect the elements as separate `Tex[0]`, `Tex[1]` bindings and ShadowDusk emits
  one parameter per element where `mgfxc` 3.8.4.1 emits a single `Tex`, a separate divergence
  tracked as issue #339; a sampler array, which `mgfxc` refuses on every profile, still compiles on
  DirectX 11 and 12 (issue #340).
- **`ShadowDusk.Slang`: a module's author register survives an entry global spelled like the name's
  prefix (issue #325).** The issue #292 register pass read a module's `public Texture2D tex_layer_0 :
  register(t3);` as a resource slangc might have hoisted out of the entry's `Texture2D tex;` (the
  name has the hoisted shape `<global>_<field>_<n>`), judged it from the entry text and stripped the
  author's `t3`, silently moving the texture to another slot (measured through real slangc v2026.14.1
  on DirectX and OpenGL). The hoisted shape now counts as a hoist only for a name NO read text
  spells: slangc locates an author's global at its own declaration and a hoisted resource in its
  core module (measured), so a name spelled by the text of the file slangc locates it in is matched
  verbatim and only verbatim, by that file's own text; an unread file is claimed by nobody's prefix
  (its verdict waits for the file, or fails as `SD0628`). Found on the way and fixed: a module
  imported by a RELATIVE path is located by a bare name (`#line 2 "m.slang"`, measured), which the
  pass mistook for slangc's core module; slangc's core-module file names are now matched by name;
  and a registered resource declared inside a `namespace` block in a module (or the entry) is a
  global declaration like any other, where it used to fail as `SD0628`. No extra slangc run for any
  shape (`SlangRegisterPassCostTests`, plus the same count through real slangc); the Slang corpus
  bytes are unchanged.
- **`ShadowDusk.Slang`: two globals of one name in different namespaces fail as the new `SD0643`
  instead of compiling to a silently merged program or crashing slangc (issue #323).** Measured,
  slangc v2026.14.1 with `-no-mangle` (adopted in Phase 66 A4 so parameter names reach the effect as
  written): `namespace A { Texture2D T; } namespace B { Texture2D T; }` is emitted as ONE `T` both
  reads use; the same for a namespaced global beside a plain one, nested, dotted and `::`
  namespaces, two combined `Sampler2D`, two samplers, two struct- or `ParameterBlock`-typed globals,
  two `cbuffer` blocks of one name, two `static`/`static const` (the first initializer wins), a
  module's `namespace A { T }` beside the entry's `namespace B { T }`, and two modules each declaring
  a plain `public Texture2D T` used inside themselves; two same-named implicit or `uniform`
  constant-buffer members crash slangc (`0xC0000005`, empty stderr), which the route could only
  report as a blank `SD0622`. ShadowDusk cannot fix slangc; it now refuses the pair by name, with
  both declarations and their file, line and column: from the raw entry text before slangc runs
  (so the crashing shape never reaches it) when no directive or `-D` value can rewrite that text,
  confirmed by slangc's own `-E` output otherwise (one run, paid only by a source whose raw text
  shows a candidate; a pair in mutually exclusive `#if` branches is cleared by it), and on every
  text the issue #292 register pass read (the entry's `-E` text and the modules reached by
  quoted-path import), which catches a macro-formed or cross-module pair at no extra run. Same-named
  functions and types are left alone (slangc prefixes those with the namespace). A slangc crash
  the texts could not foresee is still loud: `SD0622` names the crash, its exit code and this
  trigger, keeps slangc's own output verbatim after it, and no longer promotes slangc's warnings to
  the failure. What no read text can show stays open as issue #337. No existing shape pays an extra
  slangc run (pinned by `SlangRegisterPassCostTests`); corpus bytes unchanged.
- **The browser dropped vkd3d's non-fatal diagnostics on a successful DirectX or FNA compile, so
  `CompiledShader.Warnings` was empty where the desktop's was not (issue #335).** vkd3d's message
  buffer is populated on success too (both hosts compile at `LOG_WARNING`): the desktop parses it,
  relocates each entry onto the author's line and returns it as `PlatformBlob.Warnings`, but the
  browser shim read `out_messages` and used it only in its failure branch, returning the bytes
  alone. Measured on the new fixture `ImplicitTruncationWarning.fx` (a `float4` assigned to a
  `float3`): the desktop reports `ImplicitTruncationWarning.fx(47,12): warning W5300: Implicit
  truncation of vector type.` on DirectX and on FNA; the shim returned 148 bytes and nothing else
  for the same compile, and the browser `Warnings` list was empty. Identical bytes on both hosts, so
  no byte-identity gate could see it. The shim's `compile()` now returns `{ code, messages }`,
  verbatim and unfiltered, and `WasmVkd3dShaderCompiler` runs the same shared path the desktop runs:
  `Vkd3dCompileContract.MapCompileWarnings` (the one place a successful compile's text becomes
  warnings) plus `Vkd3dSourceLocator.Relocate`. No module rebuild: every pinned wrapper already set
  `out_messages` on success. Pinned three ways: `CrossHostByteIdentityTests` now records
  `CompiledShader.Warnings` per fixture and target in `warnings-manifest.json` beside the hashes
  (asserted on every CI OS); the node gate requires the shim to hand back the exact message text the
  desktop got from every one of the 93 corpus compiles (two non-empty, with a control that fails
  when no corpus compile warns); the real-browser gate compares the relocated `Warnings` from the
  real `WasmShaderCompiler` against the warnings manifest, sync and async. No byte moved on either
  host (91/93 + the two issue-#295 expected differences before and after; the three manifest
  additions are the new fixture). Cost: a string copy per compile; in node the 91 corpus compiles
  took 129 ms through the new shim against 129 ms before.
- **Browser DirectX compiles handed vkd3d no compile options, so a shader with SM1-3 semantics on
  struct fields compiled differently from the desktop, or not at all (issue #295).** Since 0.20.0 the
  desktop vkd3d backend passes `BACKWARD_COMPATIBILITY`/`MAP_SEMANTIC_NAMES` on the SM4+ target, but
  the browser module's C wrapper still passed none: a second copy of the option list, and it had
  drifted. Measured on the new fixture `Sm3SemanticStructs.fx` (a `POSITION0` vertex output and pixel
  input and a `COLOR0` pixel output, all on struct fields): the desktop names the position
  `SV_Position` (system value 1) and compiles the pixel shader, as `mgfxc` does; the browser named
  it `POSITION` (system value 0, so the rasterizer is given no position; 1932 bytes against 1936) and
  refused the pixel shader with `E5013: Invalid semantic 'COLOR'`. The list now has one owner,
  `Vkd3dCompileContract.ResolveCompileOptions`: the desktop marshals it, the browser sends the same
  list through its shim, and the WASM wrapper (`sdw_vkd3d_compile_options`, replacing
  `sdw_vkd3d_compile`) only forwards what it is handed. The corpus gate could not see the drift
  because no corpus shader changed a byte with the option; `Sm3SemanticStructs.fx` does, and is now
  in the node gate (91 compiles, replayed with the options the desktop really passed to the native
  call, plus a control that the fixture differs without them), in the cross-host byte-identity
  manifest (so `browser-vkd3d-gate.mjs` compiles it through the real `WasmShaderCompiler`) and
  checked against `mgfxc` goldens on both profiles. A new test pins the options the desktop hands the
  native call to the contract's list. **The browser now ships the rebuilt module** (release
  `native-vkd3d-wasm-2.1-r2`, pinned in `tools/restore.*`; one module carries this fix and the #271
  stack fix below), and the shim requires its `sdw_vkd3d_compile_options` export: a module without it
  fails to load (`SD1902`) instead of compiling without options. The gates enforce every entry, with
  no expected difference: node corpus 93/93, real-browser 80/80 in headless Chromium. No emitted byte
  changes on the desktop.
- **The browser vkd3d module printed one `vkd3d:NNNN:fixme:vkd3d:preproc_yyparse #line directive.`
  line to the console per `#line` directive (issue #319).** vkd3d-shader's preprocessor ignores
  `#line` and reports each one as a fixme on stderr, which emscripten routes to the browser console.
  The desktop backend blanked every directive line before the native call; the browser host handed
  vkd3d the directives, a second copy of the source preparation that simply did not exist there.
  Measured on the 91-compile DirectX + FNA corpus (every fixture carries at least the macro prelude's
  directive; an include-heavy effect carries one per include boundary): exactly one fixme line per
  directive (93 on one node gate run), and the output bytes and the diagnostics' positions are the
  same with and without the directives, so it was console noise, not an output defect. The
  preparation now has one owner, `Vkd3dCompileContract.PrepareSource` (blank, never delete, so
  `Vkd3dSourceLocator`'s line alignment holds): the desktop marshals its result and the browser sends
  its result through the shim; neither host transforms the source itself. Pinned by unit tests on
  the include-flattened shape, a desktop test that reads the source back out of the marshalled
  native call (`Vkd3dShaderCompiler.NativeSourceObserver`), the node gate (which now replays the
  bytes the desktop really handed vkd3d, must see zero fixme lines on an intercepted stderr over the
  corpus, and replays the directive-carrying text as a control that must give the same bytes and one
  fixme per directive) and the real-browser gate (zero such console lines over the sync and async
  corpus passes, with a listener control). No desktop byte moved (91/91 corpus blobs identical before
  and after) and no browser byte either (they never depended on the directives). Compile time:
  unchanged on the desktop (the same regex, moved); in node the shim took 131 ms with the directives
  against 129 ms without over the 89 replayable compiles.
- **`ShadowDusk.Slang`: a combined `Sampler2D Comb` now reflects as `Comb`, so
  `effect.Parameters["Comb"]` finds the texture (issue #302).** slangc hoists every resource out of
  an aggregate under a name it generates, even with `-no-mangle`: `Sampler2D Comb` is emitted as
  `Comb_texture_0` plus `Comb_sampler_0`, and that generated name was the compiled effect's
  parameter, so a consumer had to know slangc's suffix to set their own texture. The texture half
  of a combined sampler declared as a global (every `Sampler1D`/`2D`/`3D`/`Cube`, the `Array` and
  `Shadow` forms, arrays of them, a global in an imported module) is now renamed to the global's
  name in slangc's HLSL before the `.fx` is assembled, which is the table the hand-written
  `Texture2D Comb; SamplerState ...` produces through the `.fx` route on every target and the name
  `mgfxc` gives the equivalent legacy `sampler2D Comb` (measured, 3.8.4.1). The sampler half keeps
  slangc's name; nothing sets a sampler by name. A texture hoisted out of anything else, a struct
  global's field (`gM_t_0`), a texture inside a `cbuffer`/`ParameterBlock`, or an entry-point
  `uniform Texture2D`, has no name the author wrote and the reference compiler rejects the shape
  outright (fxc `X3090`), so it now fails as the new `SD0640` at the aggregate's declaration instead
  of reaching the effect under a generated name; a hoisted SAMPLER alone still compiles. slangc
  emits an author global spelled like a hoisted name (`Sampler2D Comb; Texture2D Comb_texture_0;`)
  as two declarations of one name: `SD0641`, as is an author name slangc's output already uses. A
  name is treated as generated only when the source provably never spells it (the raw text, or
  slangc's `#line` for an author's own global, or slangc's own `-E` text, reusing what the register
  pass read); what no read text can explain fails as `SD0642`. No slangc run is added for any shape
  pinned before (a combined sampler declared plainly still costs one run), and no byte moves on the
  Slang corpus. Proven in real MonoGame DesktopGL by a new `validation/SlangTexturedGl` row that
  sets a second texture through `effect.Parameters["Comb"]` and renders (maxd 1), and pinned on
  DirectX, OpenGL, Vulkan, DirectX12 and FNA by `SlangHoistedTextureNameTests`.
- **Desktop: an extremely deep but valid shader no longer kills the host process (issue #306).** The
  native compilers recurse once per nesting level of the source, and they ran on whatever stack the
  calling thread had: 1.5 MB for a .NET thread on Windows, where an additive chain of 2,168 terms or
  1,620 `else if` branches (DirectX 12) died inside `dxcompiler.dll` with `0xC00000FD`, and a chain of
  6,400 functions each calling the next died inside `vkd3d-shader`; the CLI, the MGCB plugin or the game
  calling `CompileAsync` simply exited, with no diagnostic. Every native compiler call (DXC compile,
  preprocess and reflection, vkd3d, SPIRV-Cross, the fxc oracle) now runs on a pool of ShadowDusk-owned
  worker threads with an explicit 64 MB stack (address space, not memory: pages are committed only as a
  compile actually recurses). Measured ceilings on Windows x64 moved from 2,167 to 94,400 additive
  terms (OpenGL and DirectX 12; 122,336 on Vulkan), and the else-if and call chains compile at every
  depth tried (51,200 branches on DirectX 12 in 329 s, 25,600 calls on DirectX in 74 s) with compile
  time, not the stack, as the practical limit. Source deeper still can still exhaust the 64 MB and the process; a source-level
  pre-check was measured unreliable (stack per level varies about 5x between shapes, and macro expansion
  hides the depth), so the ceiling is documented in `project_facts.md` instead. No emitted byte changes:
  the whole fixture corpus is byte-identical on OpenGL, DirectX, DirectX 12, Vulkan and FNA with the
  worker on and off, and concurrent compiles stay concurrent. Cost, measured on a 10 ms pixel-shader effect: none measurable through `CompileAsync` (the pool thread
  hands the whole pipeline to one worker; the Release CLI, one compile per process, gains 2 to 3 ms of
  one-time thread start and JIT, about 1%), about
  0.4 ms per effect through the synchronous `Compile` (the pipeline stays on the calling thread and each
  of its four to six native calls is handed to a worker, about 0.1 ms each). Workers are reused and exit
  after 5 s idle.
  New child-process regression tests (`DeepShaderStackTests`) compile the crashing shapes and keep a
  positive control that still overflows on the caller's stack.
- **OpenGL, Vulkan and DirectX 12: a legacy sampler declared in an `#include`d file or through a
  macro now compiles (issue #308).** The pre-parser's SM4 rewrite of D3D9 sampler syntax
  (`sampler2D` / `sampler_state` / `tex2D` into `Texture2D` + `SamplerState` + `.Sample`) reads the
  raw tokens of the main file, so a declaration in an include, one whose register clause or whole
  declaration comes out of a macro (`sampler S SLOT(s1)`, `sampler2D S : SREG(1)`,
  `DECLARE_TEXTURE(S, 1)`, MonoGame's `Macros.fxh` / `Include.fxh` idiom), a `tex2D` inside a
  macro body (`SAMPLE_TEXTURE`) or inside an include, and an unused bare `sampler2D U;` all reached
  DXC unrewritten and were rejected ("unknown type name 'sampler2D'", "Unsupported intrinsic").
  `mgfxc` 3.8.4.1 compiles every one of them (`ps_s1` on all nine measured shapes). Now, when a DXC
  shader compile fails and the text DXC was given still holds legacy sampler syntax once
  preprocessed, the pre-parse is repeated on the PREPROCESSED source (the managed
  `FxMacroPreprocessor` that already decides the OpenGL sampler registers, in a compiler-input form
  that keeps one line per source line and passes `#line` / `#pragma` / `#error` through) and the
  compile runs once more on that text, which is how `mgfxc` itself works (preprocess first, parse
  second). It runs only after a failure, so an effect that compiled before is compiled from exactly
  the same text: the whole 160-file corpus for OpenGL and DirectX_11 is byte-identical with the fix
  off and on (117 + 125 compiled, 34 + 35 fail with the same diagnostics), and the nine vendored
  MonoGame test effects that use `Include.fxh` (`Bevels`, `BlackOut`, `ColorFlip`,
  `CustomSpriteBatchEffect`, `Grayscale`, `HighContrast`, `Invert`, `NoEffect`, `RainbowH`) now
  compile for OpenGL on `mgfxc`'s units. The recovered compile is byte-identical to the directly
  written declaration on OpenGL, Vulkan and DirectX 12 (pinned by a test); DirectX 11 compiles the
  syntax natively through vkd3d and never takes the recovery. The managed view was measured
  token-for-token identical to DXC `-P` on every corpus fixture
  (`Issue308PreprocessorFidelityCorpusTests`). What the rewrite still cannot model is loud: legacy
  syntax left after the recovery (a `sampler2D` function parameter), a conditional on a
  compiler-predefined macro (`__HLSL_VERSION`, `__hlsl_dx_compiler`, …) whose value the managed
  preprocessor cannot know, or a view that cannot be built is the new `SD0016`, appended after the
  compiler's own verbatim diagnostics, and a legacy intrinsic with no rewrite hidden in a macro
  (`texCUBE`) is its own `FX0012` at the author's file and line. Diagnostics on the recovered path
  keep the author's file and line through the flattener's `#line` directives. New `mgfxc` 3.8.4.1
  goldens `SamplerLegacyInclude` (+ `SamplerLegacyInclude.fxh`) and `SamplerLegacyMacroDecl`, and
  two new `validation/SamplerRegisterOrderGl` arms ("legacy-include", "legacy-macro-decl"): red
  before (the candidate did not compile), maxd 0 against the golden after, in real MonoGame
  DesktopGL. Cost (Release CLI, `/Profile:OpenGL`, median of 5 after a warm-up, before vs after):
  unchanged for an effect that compiles at once (`apos-shapes.fx` 437 to 443 ms, `Grayscale.fx`
  224 to 223 ms, `SamplerLegacyRegisterMacro.fx` 220 to 216 ms); a recovered effect pays the failed
  first compile plus the view on top of its own compile (`SamplerLegacyInclude.fx` 232 ms against
  216 ms for its directly written twin).
- **OpenGL: every sampler type keyword with an explicit `register(sN)` now reserves that unit, not
  only the exact keyword `SamplerState` (issue #309).** On OpenGL a sampler's explicit register is
  kept out of circulation and the combined samplers fxc synthesizes for each (texture, sampler)
  pair read through `Texture.Sample` are allocated around it (issue #189). The reservation matcher
  only recognised `SamplerState`, so `Texture2D Tex; sampler S : register(s0);` + `Tex.Sample(S, uv)`
  gave `ps_s0`, the texture on SpriteBatch's unit, where `mgfxc` 3.8.4.1 emits `ps_s1`. Measured
  against the pinned `mgfxc` (`/Profile:OpenGL`): fxc reserves for every sampler type keyword
  (`sampler`, `sampler2D`, `samplerCUBE`, `SamplerComparisonState`), bare or with a
  `sampler_state` block, whether or not anything reads the sampler (an unused
  `sampler2D U : register(s0)` still moves the one real pair to `ps_s1`), and in every entry point
  of the effect (a legacy `sampler X : register(s0)` one pixel shader reads through `tex2D` keeps
  `s0` reserved in a second pixel shader that never reads it: `ps_s1` for that shader's own
  sampler, where ShadowDusk gave `ps_s0`). The matcher now accepts every sampler type keyword, on
  the preprocessed view as before, and a legacy sampler a `tex2D` reads is both pinned (its own
  pair) and reserved (every other pair). A `sampler2D` read through `Texture.Sample`, which `mgfxc`
  rejects (X3013), still fails here. No existing fixture's bytes moved (the same 160-file sweep).
  New `mgfxc` golden `SamplerReservationKeywords` and a new `validation/SamplerRegisterOrderGl` arm
  ("keyword-reservation"): `ps_s0`/`ps_s1` before, `ps_s2`/`ps_s3` and maxd 0 against the golden
  after.
- **macOS: a `Process.Start` could deadlock forever against a debug SPIR-V compile (issue #312).**
  The `DxcForkGate` added earlier in this release (PR #246) made every `fork()` wait, in its
  `pthread_atfork` prepare handler, for the DXC compiles in flight. libSystem takes dyld's dlopen lock before it runs those
  handlers, and a `-Zi` compile for the OpenGL or Vulkan target `dlopen`s `libdxcompiler` from
  inside DXC (its SPIR-V emitter reads the source for `OpSource` through `DxcDllSupport`), so the
  compile waited for the fork and the fork for the compile. In CI this froze the macOS integration
  job about one run in two (measured with lldb stacks of the hung host, taken by the new sidecar
  below; the blame hang dump could not help, because the runtime dumps itself by forking and that
  fork blocked on the same lock). The gate now covers only DXC's one locale-changing call, a `-P`
  preprocess `DxcForkGate.SettleLocale` runs before a compile whenever the process locale is not
  the one DXC leaves behind; ordinary compiles run ungated, which is safe because Apple's
  `setlocale` allocates under the locale lock only when the locale actually changes, and after
  that first change every `setlocale` DXC makes is a same-name call. No emitted byte changes (the
  change is in the locking around the native call, no flag moves; the cross-host byte-identity
  manifest and goldens pass on all three OSes). `DxcSetlocaleAudit` now also measures that no DXC
  call changes the locale once it has settled, and the fork probe compiles debug SPIR-V on half
  its threads, the shape that deadlocked the old gate within seconds.
- **Browser compiles of nested shaders hung, crashed or miscompiled (issue #271).** The in-browser
  DXC, SPIRV-Cross and vkd3d WebAssembly modules were linked with emscripten's 64 KB default stack
  (the desktop natives get 1 MB on Windows, 8 MB on Linux/macOS), and with emscripten's layout an
  overflow silently corrupts the module instead of trapping. Measured against the desktop pipeline:
  SPIRV-Cross failed on about a dozen nested `if`s or `else if`s (an ordinary OpenGL shader), DXC
  hung on a 100-term expression and gave a wrong diagnostic on 200 `else if`s, vkd3d trapped at 75
  `else if`s. All three now link an 8 MB stack placed below static data, and match the desktop
  byte-for-byte at every measured depth the desktop compiles, except an 800-branch `else if` chain
  in SPIRV-Cross, which still exhausts the JS engine's own stack. A module that traps is now
  discarded and reloaded instead of being reused corrupted, and the compile reports the new code
  `SD1907` (a synchronous `Compile()` before the reload reports `SD1903`; `CompileAsync` reloads by
  itself). All three rebuilt modules ship in this release; the vkd3d one (DirectX/FNA in the browser)
  is pinned from release `native-vkd3d-wasm-2.1-r2`, and the depth gate runs its vkd3d arm on every
  depth case (18/18 byte-identical). No emitted byte changes on any corpus. New gate
  `node-test-wasm-depth.mjs` and a trap scenario in `browser-vkd3d-gate.mjs` (`wasm.yml`); details in
  `.wasm-build/WASM-STACK-DEPTH.md`. Also: `tools/restore.*` now refresh the packaged
  `dxcompiler.wasm` by hash instead of size (a relink can change the module and keep its size).
- **Browser: asking for DirectX 12 now fails with a registered code up front (issue #272).** The browser
  host has no DX12 path, but a `PlatformTarget.DirectX12` request ran DXC and then failed in the JS shim
  with an unregistered `X0000: DXC output is not a SPIR-V module (bad magic word)`, on both the `.fx` and
  the full-Slang route. `WasmShaderCompiler` and `WasmSlangCompiler` now refuse it before any module
  loads with the new `SD1906`, which names the target, the host and the targets the browser does export
  (OpenGL, Vulkan, DirectX, FNA). `Metal` keeps the `SD0200` it gets on every host.
- **Browser builds no longer carry desktop and Android natives (issue #273).** A browser project that
  reached `ShadowDusk.HLSL`/`ShadowDusk.GLSL` by project reference (the ShaderFiddle sample,
  `ShadowDusk.Wasm`, `ShadowDusk.Slang.Wasm`) copied vkd3d, DXC and SPIRV-Cross natives for Windows,
  Linux, macOS and Android (about 130 MB) into its build and publish output, where no browser can load
  them. A root `Directory.Build.targets` drops them for browser projects only; desktop builds still get
  every native. NuGet consumers were measured unaffected. `wasm.yml` now fails if one reappears.
- **OpenGL: which `SamplerState X : register(sN)` declarations reserve a sampler register is now
  decided on the preprocessed source, like `mgfxc` (issue #283).** On OpenGL a modern
  `SamplerState` register is a reservation: the combined sampler fxc synthesizes for each texture is
  allocated around it. The `.fx` route read those registers off the raw source, so a register written
  only in an inactive `#if` branch still counted (one texture landed on `ps_s1`, off SpriteBatch's
  unit 0, where `mgfxc` emits `ps_s0`), and registers spelled through a macro
  (`#define SLOT(n) : register(n)`) or written in an `#include`d file were not seen (`ps_s0`/`ps_s1`
  where `mgfxc` emits `ps_s2`/`ps_s3`; `ps_s0` where it emits `ps_s1`). The reservation is now read
  from a preprocessed view built by a small managed C preprocessor (`#if`/`#elif` expressions,
  object- and function-like macros, `#`, `##`, `__VA_ARGS__`) with the compile's own platform and
  user macros. It is plain C#, so the answer is the same on every host including the browser, whose
  DXC build has no preprocess-only export. The raylib converter uses the same view. A directive or
  expression the view cannot evaluate fails as the new **`SD0009`**, raised only after DXC has
  accepted the source, so malformed shaders still report DXC's own error. New committed `mgfxc`
  goldens `SamplerReservationIfBranch` and `SamplerReservationMacro`, and two new arms
  ("ifbranch", "macro") in `validation/SamplerRegisterOrderGl`, measured RED with the old reading
  (maxd 255, 4096 px each) and maxd 0 after. A corpus sweep builds the view for all 153 parseable
  fixtures; no other shader's output moved. The sibling map, an explicit register on a LEGACY
  `sampler` declaration, is the next entry (issue #299).
- **OpenGL: an explicit `register(sN)` on a legacy `sampler` declaration is now read from the
  preprocessed source too, like `mgfxc` (issue #299).** A legacy `sampler X : register(sN)` pins its
  texture unit (issue #189), and that clause was still read off the raw source after #283. So a
  register written only in the branch OpenGL does not compile pinned the unit anyway
  (`#if OPENGL` / `sampler S = sampler_state {…};` / `#else` /
  `sampler S : register(s1) = sampler_state {…};` gave `ps_s1`, off SpriteBatch's unit 0, where
  `mgfxc` emits `ps_s0`), the wrong branch's number won when both branches had one (`ps_s1` where
  `mgfxc` emits the OpenGL branch's `ps_s2`), and a register number spelled through a macro or a
  `/Defines` value (`#define REG s1` / `sampler S : register(REG);`) was missed (`ps_s0` where
  `mgfxc` emits `ps_s1`). Measured against the pinned `mgfxc` 3.8.4.1 for `sampler` and `sampler2D`,
  with a `sampler_state` block, the brace form and the bare form; all now match. A sampler or
  texture whose NAME is a macro keeps its pin (the names are resolved through the same
  preprocessor), which also fixes `#define TEX RealTex` / `Texture = <TEX>` (`ps_s0` where `mgfxc`
  emits `ps_s1`). Both maps are read
  off the same preprocessed view (`FxPreParser.CollectGlSamplerSlots`), on the OpenGL target and in
  the raylib converter; an unbuildable view is still `SD0009`. New committed `mgfxc` goldens
  `SamplerLegacyRegisterIfBranch` and `SamplerLegacyRegisterMacro`, and two new arms
  ("legacy-ifbranch", "legacy-macro") in `validation/SamplerRegisterOrderGl`, measured RED with the
  old reading (maxd 255, 4096 px each) and maxd 0 after. Every other fixture is unchanged: the whole
  160-file OpenGL corpus was compiled with the fix off and on and only the two new fixtures differ
  (115 byte-identical, 43 fail identically either way), including `VsTransformColorTexture`,
  `VsWaveQuadIntrinsics` and `apos-shapes-sm6`, whose explicit-slot map does change (their register
  lived in the dead `#if SM6` arm) but whose units and bytes do not. Two adjacent defects found by
  the measurement, #308 and #309, are fixed by the two entries at the top of this section.
- **`ShadowDusk.Slang`: author registers in an `import`ed module and on a combined `Sampler2D` are
  kept (issue #292).** Both were stripped silently on DirectX and OpenGL. (1) slangc splits
  `Sampler2D Comb : register(t2)` into `Comb_texture_0 : register(t2)` and a `Comb_sampler_0` it
  numbers itself, and the strip matched by name, so the author's `t2` went. The split halves now map
  back to `Comb` together with the register class (`register(t2)` binds the texture half,
  `register(s3)` the sampler half, `: register(t2) : register(s3)` both), measured for
  Sampler1D/2D/3D/Cube, the `Array` forms and arrays. (2) `slangc -E` does not expand `import` or
  `__include`, so a register declared in an imported module was invisible to the pass (and an
  import-only source skipped it). slangc auto-numbers an imported resource that has no author
  register (measured), so "keep every register from another file" is no fix either. Each
  declaration from another file is now judged from slangc's own `-E` output for the MODULE it is
  in, with the compile's macros (slangc applies `-D` to imported modules too, and a module's
  macros do not cross an `import`, both measured): a file counts as a module when it is reached
  through quoted-path imports or opens with a `module`/`implementing` declaration. An `#include`d
  fragment is read through the module that includes it, never on its own (its includer's macros
  decide what slangc compiled), and a combined sampler's halves, whose `#line` is slangc's core
  module, are found through the imports. A declaration no trusted text decides (a file reached
  only by module name, `import foo;`, that has no `module` declaration; a register spelled through
  a macro no module defines; modules that disagree; a file the pass cannot open) now fails as
  the new `SD0628`, naming the declaration and its file and line, instead of being guessed. Same code
  on both transports through the shared seam (the browser's slangc has no file system, so there an
  import already fails the compile with slangc's own `E00001`); the node gate gains the
  combined-`Sampler2D` shape and the missing-file `-E` shapes. Found on the way and fixed the same
  way: slangc hoists a struct global's resource fields too (`M gM : register(t5)` emits
  `gM_t_0 : register(t5)`), and that register was also stripped. No corpus byte moves.
  **Cost, measured and pinned** (win-x64, Release, median of 9; one slangc spawn is about 150 ms):
  no shape that compiled correctly before pays an extra slangc run (untextured 1 run, registers in
  the entry file 2, combined `Sampler2D` 2, all as before), and a shader with no texture/sampler
  register in slangc's output now skips the `-E` pass even when it writes `register(b0)`. An entry
  that imports modules with registered resources goes from 1 run to 2 (about 165 ms to 310 ms for
  one import or a chain of three): the entry source and every file slangc names are preprocessed
  in ONE `slangc -E` invocation. `SlangRegisterPassCostTests` pins the run count per shape on both
  transports, and a real-slangc test pins the same counts.
- **A Vortice.Dxc other than 3.3.4 in the process is now `SD0219` on every OS, before any native
  is touched (issue #282).** Measured: Vortice.Dxc 3.8.3 is not only a different DXC (1.9.2602.17)
  but a binary-incompatible managed API; with the pinned natives put back in place every DXC call
  failed with `MissingMethodException` (`IDxcUtils.CreateBlobFromPinned`), reported as a
  misleading `SD0102` "Reflection failed". On macOS and Android, where ShadowDusk ships its own
  DXC, that was the ONLY symptom. `DxcLoader` now compares the bound Vortice.Dxc assembly with the
  pin first and returns `SD0219` naming the resolved version and the fix. The unsupported-OS branch
  of the loader is now classified by a pure, unit-tested function (issue #289).
- **The macOS `DYLD_LIBRARY_PATH` decoy test is decisive (issue #289).** With a restamped
  (different `LC_UUID`) decoy it could not tell "dyld mapped the foreign build and ShadowDusk
  refused it" from "dyld never mapped it" (the edit invalidates the ad-hoc signature, so Apple
  silicon refuses to map it). The decoy is now re-signed, the probe reports the image `dladdr`
  names for ShadowDusk's own handle, and the test requires it to be the decoy and every DXC-backed
  target to fail with the "dyld mapped" `SD0219`.
- **FNA / DirectX (vkd3d) errors: the compiler output printed under the summary line still used
  vkd3d's own line numbers** (issue #202 follow-up). 0.19.0 moved the summary line
  (`file(line,col)`) onto the author's source, but whenever vkd3d said more than one line, the
  CLI and the validation report also print vkd3d's complete output under it,
  and that block kept vkd3d's drifted coordinates. On the reporter's Apos.Shapes file the summary
  said line 983 while the block under it said 1115 for the same diagnostic, and later lines in
  the block went up to 3804 in a 3235-line file: the symptom the issue reported. Each
  `file:line:col:` prefix in that block is now relocated the same way as the summary; vkd3d's
  code and message text after the prefix are unchanged, and a line that names another file
  stays as vkd3d wrote it. It is all or nothing: if any line cannot be placed the block is left
  exactly as vkd3d wrote it, never a mix of the two numberings. The block is not bisected line
  by line. One extra parse-only compile carries a marker on every statement start (an empty
  `if` with an attribute vkd3d does not know, which it answers with a located warning that
  names it), so one compile measures the whole file, and only the few lines the markers cannot
  pin are bisected. Every probe now also ends with a terminator line, so a probe whose sentinel
  landed in a skipped `#if` arm stops at the parse instead of running the whole failing compile
  again. **Measured on the reporter's file** (`tests/fixtures/issues/202/apos-shapes.fx`,
  `--target-runtime fna`, Release CLI, one Windows desktop, four runs each): 4.9 to 5.3 s
  before this change (38 of the 4 165 raw lines past the end of the file), 5.1 to 6.1 s after
  (none), which is 9 more vkd3d calls of a few milliseconds each (22 against 13). A first cut
  that bisected each of the 934 distinct locations took about twice as long, almost all of it
  one probe swallowed by the file's `#if VULKAN` arm; `FnaDiagnosticLocationTests` now pins the
  call count so that cannot come back unnoticed. No emitted byte moves: the relocation only
  runs on vkd3d's diagnostics, never on the source a real compile receives.
  `ShaderError.RawDiagnostics` for vkd3d therefore carries relocated prefixes.

- **Stale lock files outside the solution (issues #291, #290).** PR #279's `Vortice.Dxc` `[3.3.4]`
  pin missed the lock files of `Vkd3dCorpusProbe` (which turned Browser render smoke red on main),
  `slang-probe`, `dxc-corpus-probe` and `KniXnbContentLoad`'s 4.3.9001 lock (still at 0.18.0), and
  `FnaValidation`'s lock had lost its `FNA` project entry. All regenerated. New
  `tools/check-lock-files.sh`, run by a new `Lock files` CI job on every PR, restores every tracked
  lock file in locked mode, so this class of miss fails on its own PR. The release lock-file rewrite
  now matches versioned names (`*packages*.lock.json`).

- **`ShadowDusk.Slang`: which registers "the author wrote" is now decided after preprocessing
  (issue #252 follow-up).** The register strip kept a texture/sampler register only when the Slang
  source text spelled `register(...)` on that name, and it read the text before the preprocessor
  ran. Two shapes went wrong. A register that exists only in an inactive branch
  (`#if OPENGL` / `SamplerState S;` / `#else` / `SamplerState S : register(s0);` / `#endif`,
  compiled for OpenGL) counted as the author's, so slangc's own invented `register(s0)` survived
  and the texture landed on `ps_s1` again, off SpriteBatch's unit 0. A register written through a
  macro (`#define SLOT(n) : register(n)`, or a `-D` value) was not recognised and was stripped: on
  DirectX the texture moved from slot 1 to slot 0, and on OpenGL two such textures landed on units
  0/1 instead of 2/3 (a regression from the original #252 fix, which had passed slangc's emission
  through for those). `SlangCompiler` now asks slangc itself: one extra preprocess-only run
  (`slangc -E`, the compile's own macros) whose token stream is what gets scanned, so an inactive
  branch is gone and a macro is expanded. It runs on both transports with the same argument list
  (`SlangcArguments.BuildPreprocess`; the WebAssembly slangc answers it byte for byte like native,
  200/200 corpus runs plus both shapes on all five targets), only after every entry point compiled,
  and only when the source, an include, a `##` paste, a line splice or a `-D` value could spell
  `register` at all, so a shader that writes none pays nothing. A pass that exits 0 with empty output, or
  output missing an entry point the compile found, now fails as `SD0629` instead of silently stripping every
  author register. (A register in an `import`ed module or on a combined `Sampler2D` was still stripped;
  fixed by issue #292, the entry above.) `mgfxc` 3.8.4.1 was measured on the
  same two shapes in a `.fx` file and agrees with the preprocessed reading (`ps_s0`; `ps_s2`+`ps_s3`).
  No corpus byte moves: `slang-manifest.json` is unchanged and the native-vs-WebAssembly identity
  stays 235/235. `validation/SlangTexturedGl` gains an `Invert#if` row that renders the
  inactive-branch shape in real MonoGame DesktopGL: `ps_s1` and a white picture (maxd 254) with the
  old reading, unit 0 and maxd 0 against the CPU expectation and the `mgfxc` golden with the new one.
  Found on the way and recorded as a known gap, not fixed here: the `.fx` route's own OpenGL
  sampler-register reservation also scans unpreprocessed tokens and diverges from `mgfxc` on the
  same two shapes (`docs/validation-matrix.md` §7).
- **`ShadowDusk.Slang` on FNA: a user type whose name starts with "Texture" is no longer mistaken
  for a texture (issue #230 follow-up).** The DX9 respelling matched texture types as `Texture\w*`,
  so a texture-free shader with `struct TextureRegion` passed to a helper failed as `SD0627` ("a
  texture or sampler passed as a function parameter"), and a local `TextureSlot slots[2]` as "the
  texture array 'slots_0[...]'". Both compiled on OpenGL and DirectX all along. Every pattern in the
  respeller and in the register strip now matches only the real resource types (`Texture1D`,
  `Texture2D`, `Texture3D`, `TextureCube`, their `Array`/`MS`/`MSArray` forms, the `RW` variants,
  `SamplerState`, `SamplerComparisonState`) as whole tokens.
- **A content build no longer writes your build machine's path into DirectX 12 and Vulkan effects
  (issue #274).** The MGFX v11 container, which DirectX 12 and Vulkan always use, stores a
  source-file string per shader. MGCB and the MonoGame 3.8.5 Content Builder hand a processor the
  effect's absolute path, and `ShadowDuskEffectProcessor` recorded it, so every such `.xnb` carried
  the builder's directory (user name included) and its bytes changed with the checkout location.
  The processor (both `ShadowDusk.MgcbPlugin` and `ShadowDusk.ContentPipeline`) now writes
  `<unknown>` there, exactly what MonoGame's stock `EffectProcessor` writes. Measured on a real
  `dotnet-mgcb` 3.8.5 for `DesktopVK` and `WindowsDX12`: the same effect built from two different
  directories is now a byte-identical `.xnb`, and the string equals the stock build's. Build errors
  and warnings still name the real file, line and column. OpenGL and DirectX 11 output is untouched
  (MGFX v10 has no such string). **What moves:** DirectX 12 and Vulkan `.xnb` files built through
  the plugin or the Content Builder processor change once (the string, and the 4-byte effect key
  derived from the body); rendering is unaffected.
  - **The CLI's `.mgfx` output is unchanged**: it keeps writing the source path exactly as passed,
    which is what the `mgfxc` CLI does, so the plugin's DirectX 12 / Vulkan payload differs from the
    CLI's `.mgfx` in that one string (and the key). Both content-pipeline gates assert "the CLI's
    bytes with only that string replaced". (The CLI's `.xnb` output follows MGCB instead; see
    issue #280 below.)
  - A build with debug information on (`DebugMode=Debug`) still records the source path inside
    the compiler's own SPIR-V / DXIL debug information, as the CLI's and `mgfxc`'s `/Debug` do.
- **The CLI's `.xnb` output no longer carries your build machine's path on DirectX 12 and Vulkan
  (issue #280).** `ShadowDuskCLI <abs>\Effect.fx Effect.xnb /Profile:Vulkan` (or `DirectX_12`, or
  any target with `--mgfx-version 11`) wrote the absolute source path into every shader record of
  the MGFX v11 payload. `mgfxc` has no `.xnb` mode, so the reference for an `.xnb` is MGCB, whose
  stock `EffectProcessor` writes `<unknown>`: the CLI now writes `<unknown>` when the output is an
  `.xnb`, and its `.xnb` payload equals the MGCB plugin's byte for byte. The same effect built from
  two directories gives the same `.xnb`. **`.mgfx` output is unchanged** (still the path as passed,
  `mgfxc` CLI parity), as are OpenGL / DirectX 11 / FNA `.xnb` files (no such string). Diagnostics
  still name the real file.
- **The MGCB plugin and Content Builder processor no longer turn debug information on for
  `DebugMode=Auto` under `/config:Debug` (issue #280).** Stock MGCB never does: MonoGame 3.8.5's
  `EffectProcessor` sets `Debug = DebugMode == EffectProcessorDebugMode.Debug` (3.8.2 adds `/Debug`
  under the same condition) and never reads the build configuration, and a real `dotnet-mgcb` 3.8.5
  `/config:Debug` build is byte-identical to its release build. ShadowDusk's processor did, which
  made its output diverge from stock and put the source path into DXC's debug information on
  DirectX 12 / Vulkan. `Auto` now optimizes everywhere, as in stock; set `DebugMode=Debug` for debug
  information. **What moves:** only a `.mgcb` that sets `/config:Debug` and leaves `DebugMode` at
  `Auto`; its effects are now the release bytes (MonoGame.Content.Builder.Task's own targets pass no
  `/config`, and the Content Builder has no configuration, so the default routes do not move).
- **`CompilerOptions.EmbeddedSourceFileName`'s XML doc now states that only `null` falls back to
  `SourceFileName`**: an empty string is stored as an empty string (issue #280; pinned by a test).
- **A `dxil.dll` on `PATH` no longer hijacks DXC's DXIL validator on Windows.** With the Windows
  SDK's `bin` directory on `PATH` (every VS Developer Command Prompt), every DirectX 12 compile
  failed with `DXIL container mismatch for 'PSVRuntimeInfoSize'`; with any other `dxil.dll` there,
  DirectX 12 output could come out unsigned without a word. The cause was ShadowDusk's own call
  to Vortice's `Dxc.LoadDxil()`, a bare `LoadLibrary("dxil.dll")` made before DXC loaded: our
  `dxil.dll` sits in `runtimes/<rid>/native`, not the application directory, so the bare load
  walked down to `PATH`, and `dxcompiler.dll` then bound the module already loaded under that
  name. ShadowDusk now loads its pinned `dxil.dll` and then `dxcompiler.dll` by absolute path
  before any DXC call (on Linux, `libdxcompiler.so`), and checks that the `dxil.dll` DXC binds is
  its own (by the build version stamped into it, so a copy of the pinned file is accepted). Its resolver now runs ahead of
  Vortice.Dxc's own, so a bare-name fallback can no longer pick a different DXC (on Linux, from
  `LD_LIBRARY_PATH`). When the pinned natives are missing, every DXC-backed compile fails with the
  new `SD0219` instead of running on whatever the OS search found. When a foreign validator was
  loaded into the process first (by a host tool, or on macOS any `libdxil` image, since that build
  ships none), only DirectX 12 compiles, whose validated and signed DXIL it would decide, fail with
  `SD0219`; OpenGL, Vulkan and DirectX 11 never call the validator and keep compiling. Output bytes
  are unchanged on a clean `PATH`. `CliDxcPathHijackTest` runs the CLI with decoy
  `dxil.dll`/`dxcompiler.dll` (and, where installed, the Windows SDK's `bin`) first on `PATH` for
  DirectX 12, DirectX 11, OpenGL and Vulkan, and requires output byte-identical to a clean-`PATH`
  compile, with DirectX 12 signed; it failed before the fix. `DxcForeignValidatorTests` preloads a
  decoy, the Windows SDK's, a byte-identical copy, and a `\\?\`-path `dxil.dll` in fresh processes
  and pins which targets compile. The MGCB gate's decoy directory now carries a `dxil.dll` too.
- **ShadowDusk now compiles only with its own pinned DXC build, on every OS and in every host
  layout (issue #270).** Three gaps were left after the fix above. On macOS, ShadowDusk's resolver
  still ran after Vortice.Dxc's own, which loads `libdxil` and `libdxcompiler` by bare name, so a
  pair reachable through `DYLD_LIBRARY_PATH`, the working directory or `/usr/local/lib` silently
  replaced our DXC for OpenGL and Vulkan; ShadowDusk now loads its macOS `libdxcompiler.dylib` by
  absolute path and answers ahead of Vortice there too (and on Android). The loader probed the
  host application's `runtimes/<rid>/native` before the directories beside the ShadowDusk
  assemblies and took the first file with the right name: the Windows SDK's 1.8 pair placed in an
  MGCB install's `runtimes\win-x64\native` compiled DirectX 12 with that foreign DXC without a
  word. The natives that ship beside ShadowDusk now come first, and every candidate is checked
  against the pinned build before it is loaded (the PE file version on Windows, the ELF GNU build
  id on Linux, the Mach-O `LC_UUID` on macOS: identities that code signing and `strip` leave
  alone). On macOS, dyld resolves even an absolute-path load against `DYLD_LIBRARY_PATH` first, so
  the image dyld actually mapped is checked after loading as well: a byte copy of the pinned build
  is accepted, a different build is refused. A candidate that is not the pinned build is skipped
  and named; if no pinned build is
  found, every DXC-backed compile fails with `SD0219`, never with a different DXC. DirectX 11 and
  FNA do not use DXC and keep compiling. Output bytes are unchanged. New tests:
  `CliDxcNativeLayoutTests` (the real CLI from a private copy of its output with the natives
  removed, replaced by a foreign build, or shadowed by one; 4 of 6 failed before the fix),
  `DxcLibraryPathDecoyTests` (Linux and macOS: a decoy pair first on the library path and as the
  working directory, with and without the host knowing the natives; the mapped `libdxcompiler`
  must be the pinned one, read from `/proc/self/maps` or dyld's image list; it fails with the old
  subscription order; a macOS case puts a copy and a restamped build on `DYLD_LIBRARY_PATH`),
  `DxcPinnedNativeIdentityTests` and `DxcNativeIdentityTests`.
  The case a consumer really hits is measured: ShadowDusk.HLSL plus Evergine.DirectX12 resolves
  Vortice.Dxc 3.8.3, whose natives are DXC 1.9.2602.17, with no NuGet warning. It now fails with an
  `SD0219` that names the build found, the pinned build (on Windows the file version and the
  source commit, `1.7.2212.40 (e043f4a12)`), the Vortice.Dxc version the process resolved, and the
  fix: pin Vortice.Dxc to 3.3.4. The packed dependency is now the exact range `Vortice.Dxc
  [3.3.4]`, so such a graph also gets NuGet `NU1608` at restore. A pinned-version file of the other
  architecture probed first is skipped instead of ending the search. Android loads its DXC up
  front, so a missing one is `SD0219` rather than a raw `DllNotFoundException`, and any OS without
  a bundled DXC (iOS, Mac Catalyst, FreeBSD) gets `SD0219` too. The MGCB plugin's own DXC hook,
  which `DxcLoader` had made unreachable and which checked no identity, is removed.
- **`ShadowDusk.Slang`: textured shaders no longer crash real FNA (issue #230).** slangc emits
  texture objects (`Texture2D T; SamplerState S; T.Sample(S, uv)`). On the FNA target that compiled,
  but vkd3d folds the pair into one texture-typed sampler named `S+T`, so the `.fxb` held a texture
  where MojoShader expects a sampler and FNA threw `NotImplementedException: Unhandled sampler
  state!` on the first draw (all 12 textured shaders of the 21-shader corpus; `fxc /T fx_2_0` refuses
  the same text). `SlangCompiler` now respells them for FNA in DX9 effect syntax (`texture2D T;
  sampler2D S = sampler_state { Texture = <T>; }; tex2D(S, uv)`), the same SM3 `texld`, with every
  texture declared before any sampler. Texture shapes it does not model (a texture or sampler passed
  as a function parameter, a subscript load `T[...]`, `SampleLevel`/`SampleGrad`/`Load`, a non-2D
  texture, one sampler for two textures, a non-zero register space) fail as the new `SD0627`, at the
  Slang source line.
- **FNA `.fx` (and the built-in `.slang` subset frontend): a DX10-style texture object now fails at
  compile time instead of crashing FNA at the first draw (issue #230). Behavior change.** Any
  `Texture2D`/`Texture3D`/`TextureCube` sampled through a `SamplerState` (`Sample`, `SampleLevel`,
  `SampleGrad`, ...) at a `ps_2_0`/`ps_3_0`/`vs_*` profile used to compile for FNA into the same
  texture-typed `S+T` entry, and FNA throws `Unhandled sampler state!` as soon as a pass that
  samples it is applied (measured in real FNA 26.06 on the D3D11 and OpenGL drivers). `fxc
  /T fx_2_0` refuses this source too. It now fails with `SD0303`, located at the sampling call, with
  DX9 advice for the actual shape (`texture2D`/`texture3D`/`textureCUBE`, and
  `tex2D`/`tex2Dlod`/`tex2Dgrad`/`tex3D`/`texCUBE`). Twelve test fixtures that used to "compile"
  for FNA move to this rejection: `PenumbraLight`, `PenumbraTexture`, `SharedSamplerPair`,
  `ExCubeSamplerHidef`, `ExModernSample`, `ExMultiSamplerHidef`, `ExPhantomTexLodUniform`,
  `ExSampleGradHidef`, `ExSampleLevelHidef`, `ExTextureNamedTexture`, `ExVolumeTextureHidef`,
  `ExVsTextureFetch`. No pass that samples a texture object could render in FNA. One shape did
  work before and is now a compile error, as it is for `fxc`: a multi-technique effect where
  only some techniques sample a texture object (`PenumbraLight.fx`'s three untextured techniques
  rendered; only `TexturedLight` crashed). Respelling the texture in DX9 syntax, as `SD0303`
  advises, restores the whole file. DX9-style `texture` + `sampler_state` + `tex2D` source is
  unaffected and byte-identical.
- **OpenGL `sin`/`cos`/`tan` on large arguments no longer depends on the driver's range reduction (issue #215).** SPIRV-Cross passed the raw argument to the GLSL builtin, so a shader feeding hundreds of radians into `sin` (`Dots.fx` reaches ~792) rendered 19/255 off the `mgfxc` golden on Intel UHD while llvmpipe and NVIDIA matched. The GLSL rewriter now reduces every non-literal `sin`/`cos`/`tan` argument into [-pi, pi] first (new rewriter Rule 16, a Cody-Waite split of 2pi through an `sd_reduce_angle` helper), as fxc does before every D3D9 `sincos`, with constants more accurate than `mgfxc`'s (max phase error 1.3e-7 rad at |x| <= 1000, measured in fp32, against 3.8e-4 for `mgfxc`'s six-decimal ones). `tan` is included because fxc reduces it too: D3D9 has no `tan` instruction, so `fxc /T ps_3_0` emits `mad / frc / mad / sincos / rcp / mul` for it (the same reduction, then sin/cos), and 2pi is two of `tan`'s periods so the same helper is exact. `atan`, `atan2`, `tanh` and the other non-angle builtins are untouched. **Every OpenGL/WebGL shader that calls `sin`, `cos` or `tan` changes bytes**; shaders without them, and every DirectX, DirectX 12, Vulkan and FNA output, are byte-unchanged.

- **DirectX and FNA compiles no longer run vkd3d again for an entry point another pass already compiled (issue #255).** An effect whose techniques share entry points made one vkd3d call per pass. MonoGame's stock `BasicEffect.fx` made 64 calls for 30 distinct shaders. Each distinct request now compiles once per `Compile` call, which roughly halves the vkd3d time of the stock effects: `BasicEffect` about 490 to 250 ms, `SkinnedEffect` (DirectX) about 770 to 350 ms, `EnvironmentMapEffect` about 300 to 90 ms. Output bytes are unchanged. vkd3d is deterministic, the cache key is every field of the request, and the stock effects were byte-compared before and after. A cancelled token now also stops vkd3d diagnostic relocation before each of its parse-only probe compiles, through one check in the shared locator that the desktop and the browser backend both pass their token to. A native call that has already started still cannot be interrupted.
  This does not speed up one very large pixel shader, such as the current upstream Apos.Shapes file (about 5 s on a Ryzen 7 5800X). That time is spent inside a single vkd3d call. vkd3d's HLSL optimizer is roughly quadratic in the size of the fully inlined shader, and vkd3d 2.1 is about 1.6x slower than 1.17 on that file and about 2x slower on branchy code. vkd3d has no option that reduces optimizer work and the faithful pipeline rules out another compiler, so the fix has to come from upstream vkd3d (a report is being verified before it is filed). The measurements are in `project_facts.md`.
- **`ShadowDusk.Slang`: textured shaders sample SpriteBatch's texture on OpenGL (issue #252).** slangc
  numbers every texture and sampler itself (`SamplerState S : register(s0)`), and the OpenGL sampler
  allocator reads a `SamplerState` register as an author reservation (mgfxc's own rule), so a
  single-texture Slang shader landed on sampler slot 1 while SpriteBatch binds the draw texture to
  unit 0, and the shader never saw it. `SlangCompiler` now strips slangc's own texture/sampler
  registers and keeps every `register(...)` the author wrote, so the route matches what the `.fx`
  route gives the equivalent hand-written HLSL. New render gate `validation/SlangTexturedGl` (real
  MonoGame DesktopGL, texture on unit 0 via SpriteBatch, Invert compared against the `mgfxc` golden)
  measured red before the fix and green after; `slang-manifest.json` regenerated.
- **Slang follow-ups (issue #258).** `*.slang` files are now pinned to LF in the checkout
  (`.gitattributes`), like `.fx`/`.fxh`; every tracked `.slang` was already LF in the repo, so no
  bytes change. `SlangCompiler`'s `SD0625` rejection (two entry points emitting different
  declarations of one cbuffer/resource name) is now tested end to end through `SlangCompiler`
  with a fake slangc, via an internal seam; the public API is unchanged. Every committed
  `packages.lock.json` now records the `ShadowDusk.*` project references at 0.20.0 (they still
  said 0.18.0, which no restore flags), and the release runbook rewrites them on each bump.
- **Intermittent 60 s timeout in `Issue202_AposShapesCurrentUpstream_LandsOnTheRegisterLimit` on CI.** The test compiled the 3235-line shader twice, and each vkd3d compile costs about 3.3 s of CPU that a loaded runner stretched past the test's 60 s token. It now compiles once with no wall-clock token, since a token cannot interrupt a native compile; the CI integration step's `--blame-hang-timeout 3m` guards hangs and uploads a thread dump.
- **`.fx` wave/quad intrinsics now fail loudly and consistently on every target that cannot hold them.** On OpenGL, DirectX 11 and FNA they are rejected with `SD0624` (the code the `.slang` route already used), instead of DXC's `Vulkan 1.1 is required` (OpenGL) or vkd3d's `Function "WaveActiveSum" is not defined` (DX11, FNA). The message names the intrinsic and target, keeps the compiler's own line and column, and appends its text; `.fx` and `.slang` share one message. A user function that shares an intrinsic's name on those targets still compiles. DirectX12 still compiles them; Vulkan stays `SD0218`.
- **Host-independent generated text.** The Slang frontend `.fx`, the SkSL uniform rewrite, the ShaderToy `.fx` and harness, and the multipass manifest/WIRING.md used `AppendLine` (CRLF on Windows, LF elsewhere); they now emit `\n` everywhere. `HostNewlineBanTests` fails if `AppendLine`/`Environment.NewLine`/`WriteLine` reappears in a generator project. Compiled output bytes are unchanged.

- **`SlangCompiler` mis-merged entry points that instantiate the same generic differently
  (issue #228).** `-no-mangle` is collision-free inside one entry (measured: `Box_0`/`Box_1`/
  `Box_2` for three `Box<T>`), but slangc numbers symbols per run in first-use order, so a
  vertex and a pixel entry using `helper<float>` and `helper<float2>` in opposite order both
  called theirs `helper_0`. The merged effect failed with a location-less redefinition error,
  or silently bound the wrong overload when only the signatures differed. The merge now splits
  by top-level declaration (it used to split at every `#line`, including mid-function, and could
  drop a function's closing fragment as a "duplicate") and renames a later unit's colliding
  structs, functions and statics (`helper_0_e1`), iterated to a fixed point. A colliding
  cbuffer or resource, which is a reflected parameter name and cannot be renamed, is rejected as
  `SD0625`.
- **HLSL Effect (`.fx`) input to `SlangCompiler` is rejected as `SD0626` (issue #231).**
  Measured against MonoGame v3.8.5's own effects (`Macros.fxh`, BasicEffect, SkinnedEffect, ...):
  0 of 66 entries compile through real slangc, because of the `technique` block and then the
  legacy `sampler` type; with both removed all 66 compile. Those effects belong on the `.fx`
  route; previously the author got `SD0603`, "add `[shader]` attributes", a dead end.
- **Slang: `[shader(...)]` or an SM6 intrinsic name inside a comment or string literal is no longer read as code (#222).** A doc comment quoting `[shader("fragment")]` could produce a phantom entry point and a false `SD0604`; the same blindness let a commented `WaveActiveSum` trigger `SD0624`. The entry scanner, the SM6 guard, and the `SD0600` construct scan now share one comment/string mask, and attribute stripping only removes real attributes.
- **Vulkan: HLSL wave/quad intrinsics (`WaveActiveSum`, `QuadReadAcrossX`, ...) are rejected
  loudly with a new diagnostic, `SD0218`, instead of DXC's confusing `Vulkan 1.1 is required for
  Wave Operation`** ([#229](https://github.com/kaltinril/ShadowDusk/issues/229)). They are not
  supported on Vulkan: MonoGame's DesktopVK creates a Vulkan 1.0 instance with no subgroup
  support. Measured in the CI Vulkan lane: a SPIR-V 1.3 wave shader rendered correctly on Mesa
  lavapipe, but the Khronos validation layer reported 10 spec errors, so a GPU driver is free to
  refuse or miscompile it. The message names the intrinsic and the reason, keeps DXC's location,
  and appends DXC's own text. The real-slangc `.slang` route rejects the same intrinsics on
  Vulkan with the same code and message. Non-wave Vulkan output is unchanged (no target-env flag
  is ever passed).

- **`ShadowDusk.Slang` packed its Unix slangc at `runtimes/<rid>/native/slangc/slangc`,** away
  from its library, because NuGet treats an extension-less `PackagePath` as a folder. Found by
  the new cold-consumer run before any release shipped it.
- **`SlangCompiler`'s source-only rejections (`SD0602`, `SD0603`, `SD0624`) no longer depend on
  the host.** They ran after the platform check, so a host without slangc reported `SD0620`
  instead; they now run first and are tested on every OS. A slangc the OS refuses to start
  surfaces as `SD0622` with the OS's reason instead of an exception, a missing compiler library
  is reported up front (`SD0623`), and slangc's stdin is written as UTF-8 on every host.
- **`SlangCompiler`'s assembled `.fx` text is LF-only on every host.** The synthesized wrapper
  used the host newline, so Windows got mixed line endings around slangc's LF body. The
  compiled bytes were already identical across hosts; the intermediate text now is too.
- **Compiling on macOS (and DXC's other non-Windows builds) can no longer crash or hang the
  host process when compiles run concurrently or alongside `Process.Start`.** Three native
  failures, all measured on macOS arm64 and all inside DXC's Unix support code, not in
  ShadowDusk's output (no emitted byte changes):
  (1) every DXIL compile and preprocess runs LLVM's `RegisterHandlers()`, which installs LLVM's
  own signal handlers over the .NET runtime's (`SIGSEGV`, `SIGBUS`, and on macOS `SIGUSR1`,
  CoreCLR's thread-suspension signal); test hosts died by `SIGUSR1` (exit code 158);
  (2) concurrent first calls race that registration and overflow its fixed 17-slot table into
  `TargetRegistry` state, so the next DXIL compile segfaults in
  `llvm::TargetRegistry::lookupTarget`;
  (3) DXC's `WideCharToMultiByte` shim calls `setlocale` about 180 times per compile, which
  deadlocks permanently against a concurrent `fork()`.
  ShadowDusk now performs DXC's signal registration once, serialized, and restores the
  runtime's handlers (`DxcSignalIsolation`; LLVM never registers again), and on macOS a
  `pthread_atfork` gate keeps `fork()` out of in-flight native compiles (`DxcForkGate`).
  Compiles still run in parallel. This was the integration suite's intermittent "Test host
  process crashed" (3 of 4 local macOS runs before; 11 of 11 clean on both TFMs after) and is
  guarded by fresh-process probes plus a deterministic signal-ownership check in
  `DxcConcurrencyStressTests`. CI's macOS-only `xUnit.MaxParallelThreads=1` workaround, which
  hid the crash rather than fixing it, is removed.

- **`ShadowDusk.Slang`'s real-slangc route now forwards the same per-target platform macros
  (`OPENGL`/`SM4`/`VULKAN`/`SM6`/`HLSL`/`GLSL`/`MGFX`/`FNA`/`SM3`, `__KNIFX__` for the KNIFX
  container) the ordinary `.fx` route already defines for DXC, closing a silent divergence
  found by Phase 66 A6's residue sweep.** `SlangCompiler` previously forwarded only the user's
  own `CompilerOptions.Defines` to slangc's preprocessor, never `PlatformMacros.For` — so a
  Slang author's `#if OPENGL` / `#if VULKAN` / `#if SM4` / `#ifdef __KNIFX__` branch (the exact
  idiom common across the rest of this project's shaders, e.g. a real MonoGame `Instancing.fx`
  vertex shader choosing whether to `transpose()` an instancing matrix) resolved to the SAME
  branch on every target, since slangc never saw any of these macros defined. Measured directly:
  before the fix, Vulkan and DirectX produced IDENTICAL HLSL on the point that mattered (both
  took the non-Vulkan `transpose()` branch); after the fix they diverge correctly. See
  `plan/PHASE-66-full-slang-input-implementation.md`'s A6 write-up.

- **`SD0600`'s Slang-only-construct scan now catches bare `interface` and generic
  type-parameter-constraint syntax** (`ShadowDusk.Compiler.Slang.SlangFrontend`, Phase 65 §5's
  finding, closed by Phase 66 A5). Previously a real Slang file using either (e.g. an `interface`
  a struct conforms to, plus a generic free function constrained to it) fell through this
  courtesy scan and reached DXC's own confusing raw diagnostic (`X0000: expected ';' after
  __interface`) instead of a clean, named rejection. Two patterns added:
  a line-anchored `interface` keyword and a colon-constrained generic angle-bracket shape
  (`Name<T : IConstraint>(`) that does not false-positive on ordinary HLSL templated resource
  types (`StructuredBuffer<float4>`, `Texture2D<float4>`, …).

## [0.20.0] - 2026-09-10

### Added

- **vkd3d-shader natives are now built for every desktop RID in CI, from a version input.**
  `build-vkd3d-natives.yml` and `vkd3d-wasm-build.yml` take `version` + `tarball_sha256` as
  workflow-dispatch inputs, so a future pin bump needs no workflow edit, and **win-x64 is CI-built
  for the first time** (MSYS2 MINGW64, libgcc/winpthread linked statically, gated on an `objdump`
  check that the DLL depends on system DLLs only) rather than being a local build a maintainer
  uploaded by hand. Every RID's build smoke now compiles a `[loop]` with a data-dependent break at
  `ps_3_0`, so a native that cannot do SM3 loops can never be published by accident.
- **DX12 render gate joins the headless CI lane** (issue #209, follow-up to #204). The
  `BaselineDx12`+`CandidateDx12`+`compare_dx12.py` DX12 corpus was measured RED in #204's new
  `windows-latest` WARP job: `MonoGame.Framework.Native` compiles `SDL_WINDOW_VULKAN` into any
  DX12 build's window creation, independent of the WARP pin, and the runner ships no Vulkan
  loader or ICD. Fixed by installing Google SwiftShader (a CPU Vulkan ICD + loader) via
  `jakoch/install-vulkan-sdk-action` in the same job - WARP still does the actual D3D12
  rendering, SwiftShader only satisfies SDL's window-creation probe. Measured green on a real
  `windows-latest` run: maxd 0, 10/10.

### Changed

- **The pinned vkd3d-shader native moves 1.17 to 2.1** (issue #212), for all four desktop RIDs and
  the WASM build. This is the compiler that produces every DirectX and FNA byte, so **all DirectX
  and FNA output changes** - a newer compiler selects different instructions. OpenGL, Vulkan and
  DirectX 12 bytes are provably unchanged (those go through DXC, not vkd3d). The output format, the
  default MGFX version, the public API and the MonoGame pin are all untouched.
- **Ten fixtures that could not compile for FNA now do, with no regressions.** `BasicEffect` and
  `EnvironmentMapEffect` (SM2 register pressure, the `SD0305` class), `DeferredSprite`,
  `ForwardLighting` and Nez `Reflection` (int-typed ternary in `clip()`, if/else flattening), and
  all three Apos.Shapes revisions plus `Sd0402UniformBoundedLoop` (loops with a runtime trip
  count). `SkinnedEffect` still exceeds the `vs_2_0` register file and still says so loudly.

### Fixed

- **`PlatformTarget.Fna`: a shader with a loop the compiler cannot unroll now compiles**
  (issue #212). vkd3d 1.17 had no SM2-3 lowering for the `HLSL_IR_LOOP` node, so a `for` with a
  runtime trip count, or any loop marked `[loop]`, failed with
  `E5017: Aborting due to not yet implemented feature: Instruction type HLSL_IR_LOOP` - even though
  `ps_3_0` supports dynamic looping and the same source compiled fine for OpenGL. vkd3d 2.0
  implemented SM3 loops. One shape is still rejected upstream: a loop whose trip count is a
  user-declared `int` **uniform**.
- **DirectX 11: a pixel shader returning a struct with `COLOR0`/`COLOR1` fields compiles again.**
  vkd3d 2.1 rejects a user-defined semantic on an SM4/5 pixel-shader output (`E5013`), which `fxc`
  accepts and which is how MonoGame `.fx` files written against SM3 still build at `ps_4_0`.
  ShadowDusk now passes vkd3d's `BACKWARD_COMPATIBILITY`/`MAP_SEMANTIC_NAMES` option on the SM4+
  target, which covers the struct-field case the source rewrite deliberately cannot touch (the same
  struct may be a vertex-shader output, where `COLOR` is legal).
- **FNA: sampler parameters are typed from their declaration, matching `fxc`.** A bare
  `sampler s0` read with `tex2D` was recorded as `D3DXPT_SAMPLER2D` where `fxc` records
  `D3DXPT_SAMPLER`, and FNA binds off that table.

## [0.19.0] - 2026-09-10

### Added

- **Headless DirectX render gate in CI** (issue #204). A new `windows-latest` job in
  `validation-render.yml` runs three DX render gates pinned to **WARP** (Windows' bundled
  software D3D rasterizer, no GPU needed on the runner) instead of a hardware adapter -
  `DxModernFeatures`, `KniWinFormsDX`, and the `BaselineDx`+`CandidateDx` DX11 corpus - all
  three measured green in real CI, closing most of the "DX render gates are
  Windows-box-only" gap `docs/validation-matrix.md` and `CLAUDE.md` called out. The pin is
  opt-in via `SHADOWDUSK_DX_WARP=1` (`validation/SharedDx/DxHeadlessRasterizer.cs` for
  MonoGame's static `GraphicsAdapter.UseDriverType`; a local `PreparingDeviceSettings` hook
  in `KniWinFormsDX/Program.cs` for KNI's per-device `PresentationParameters.UseDriverType`),
  so a developer's local `dotnet run` is unaffected and keeps rendering on the real GPU. The
  DX12 corpus (`BaselineDx12`+`CandidateDx12`) was also tried and measured **RED**:
  `MonoGame.Framework.Native`'s SDL2 window creation needs a Vulkan surface regardless of the
  WARP pin, and `windows-latest` has no GPU or Vulkan ICD at all - tracked as issue #209. The
  Apos.Shapes DX/DX12 gallery, the ShaderToy DX route, and FNA stay manual-only for now (not a
  WARP limitation for those, just not the smallest first step); Vulkan needs Mesa lavapipe,
  not WARP, and is out of scope here.

- **`ShadowDusk.ContentPipeline`: the importer/processor pair as a library, for MonoGame 3.8.5's
  Content Builder project** (Phase 63, issue #203, requested by aitorciki). 3.8.5's
  template-default content story is a C# `ContentBuilder` the consumer owns, which needs
  `new ShadowDuskEffectImporter()` / `new ShadowDuskEffectProcessor()` at compile time - and the
  tools-only `ShadowDusk.MgcbPlugin` has no `lib/` to reference. The ninth package is a
  `lib/net8.0` library compiled from the **same five source files** as the plugin (pinned by
  test), with a real `MonoGame.Framework.Content.Pipeline >= 3.8.2.1105` dependency (the floor,
  kept: the 3.8.2.1105-compiled pair binds inside a 3.8.5 Builder, measured) and
  `ShadowDusk.Compiler` transitive so the natives flow into the Builder's bin. Usage is one
  `PackageReference` plus passing the two instances:
  `content.Include<WildcardRule>("Effects/*.fx", new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor());`
  - **pass the instances**: with both pairs loaded, extension auto-discovery silently picks
  MonoGame's own. The target follows `-p` / `$(MonoGamePlatform)`, `DesktopVK` and `WindowsDX12`
  included. The pair's C# namespace is now `ShadowDusk.ContentPipeline` in both packages (the
  plugin's assembly name and `tools/net8.0/any/` path are unchanged; MGCB resolves importers by
  simple type name, so no `.mgcb` changes). Rung 4: `validation/ContentBuilder` builds the
  fixtures through a real 3.8.5 `ContentBuilder` with both the stock and the ShadowDusk pair,
  asserts the ShadowDusk payload is byte-identical to the CLI's and the envelope to the stock
  build's, and loads both through a real `ContentManager.Load<Effect>` on MonoGame 3.8.5 with
  pixel-identical renders; `pack-consume.yml` consumes the packed package cold in a scratch
  Builder. No existing output byte moves for any route.

- **HLSL → SkSL conversion for SkiaSharp** (issue #197).
  `ShadowDusk.Compiler.Sksl.SkslConverter.Convert(fx)` turns a pixel-only `.fx` into an SkSL
  runtime effect for `SKRuntimeEffect.CreateShader`, via the same faithful front half as every
  ShadowDusk compile (`HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL`) plus a convention mapper —
  `half4 main(float2 coord)` entry, combined samplers as `uniform shader` children named after
  the HLSL textures and sampled with `.eval(coord)`, cbuffers flattened to loose uniforms.

  **The limits are enforced loudly, never silently** (`SD0610`–`SD0615`), because SkSL runtime
  effects have no vertex stage and **no varyings at all** — a pixel shader that reads an
  interpolated input does not convert *even though it is purely a pixel shader*. The canonical
  case is real: Gum maintains this same grayscale effect as both `.fx` and hand-written `.sksl`,
  and the hand port silently drops the `* input.Color` tint the `.fx` applies. ShadowDusk's
  converter refuses that shader by default, naming `COLOR0`, and offers
  `TreatVaryingsAsUniforms` as the documented opt-in — under which the emission keeps the tint.
  Derivatives, `gl_*` builtins, computed-UV sampling, vertex-shader passes, and multi-pass
  effects are likewise refused by name.

  Evidence (real SkiaSharp, CPU raster, test-only dependency — no shipped library references
  it): Skia's own compiler accepts the emissions with zero errors, and renders match the
  original HLSL's analytically computed math at ±2/255 (SkSL evaluates at `half` precision).
  This is a **rendered-image-fidelity claim, never `mgfxc`-equivalence** — Skia has no reference
  compiler to be equivalent to, and `.sksl` is not a validation-matrix backend.

- **Slang as an input language — the HLSL-compatible subset, with nothing to install on any
  platform** (issue #198). ShadowDusk now accepts `.slang` source: on the CLI by extension
  (`ShadowDuskCLI MyShader.slang out.mgfx /Profile:OpenGL`, or `--input-format slang`), and from
  the library via `ShadowDusk.Compiler.Slang.SlangFrontend.ConvertToFx`.

  The frontend is a **pure managed text transform** — ShadowDusk is a multi-input compiler
  (`.fx`, ShaderToy GLSL, now Slang) over one faithful pipeline, and this input follows that
  shape exactly. Entry points are declared Slang's own way, with `[shader("vertex")]` /
  `[shader("fragment")]` attributes, and the technique block is synthesized from them (Slang has
  no technique/pass concept). The attributes are stripped and the body — HLSL-compatible Slang
  is near-HLSL by Slang's own design — compiles through the **same pipeline as every `.fx`**, so
  the author's names are the effect parameter names verbatim, and `.slang` input works on every
  host and every target the pipeline works on, browser/WASM included. No Slang binary is
  shipped, downloaded, or invoked, anywhere.

  The deliberate trade: **Slang-only language features** (`import` modules, generics,
  `extension`s, `associatedtype`) are rejected with a clear error naming the construct
  (`SD0600`), never approximated — matching the issue as filed (*"write slang, but still go
  through the normal compilation pipe… features not supported in HLSL are likely also not
  supported in MonoGame"*). An entry point whose stage no `Effect` can load — compute, mesh,
  raytracing — is likewise rejected loudly by name (`SD0602`). New registered diagnostics
  `SD0600`, `SD0602`–`SD0604`.

  No route through Slang is `mgfxc`-equivalent — `mgfxc` cannot read Slang at all; the claim is
  that the generated `.fx` compiles through the same faithful pipeline as any other, to the same
  proven targets.

  **The subset claim is measured against the real Slang compiler, not assumed.** A 17-shader
  corpus (`tests/fixtures/shaders/slang/`: procedural gradients/SDF/plasma, textured effects,
  cbuffer-driven parameters, and VS+PS pairs) is cross-validated by `validation/SlangCorpus`:
  every shader is accepted by the **real pinned `slangc`** (a SHA-256-verified test-time oracle,
  like `fxc` — never shipped, never invoked by the product), and the uniform-free procedural
  subset renders **pixel-identical (maxd 0)** through ShadowDusk's route vs through slangc's own
  HLSL emission fed to the same DXC + SPIRV-Cross. In-suite, all 17 convert and compile on
  OpenGL and DirectX.

- **Direct `.xnb` output: replace your content pipeline with ShadowDusk and change no lines of
  code** (Phase 60, issue #199). ShadowDusk now writes the content-pipeline `.xnb` itself, so a
  consumer drops the file where their `mgfxc`-built one sat and keeps calling
  `Content.Load<Effect>("MyShader")` unchanged, with MGCB out of the picture entirely. Two
  surfaces, one writer: `CompiledShader.ToXnb()` on the library, and an `.xnb` output path on the
  CLI (extension-driven, because a ShadowDusk-specific flag needed to get correct output is
  exactly what the seamlessness rule forbids).

  **A container only.** The payload is the same `.mgfx` / `.fxb` bytes ShadowDusk already emitted
  and already had render-proven, byte-identical to what the same invocation writes without the
  wrapper, so no shader-compilation behaviour changes and no existing output byte moves. Pure
  managed, no native dependency, so it works on every host including WASM and Android.

  **The XNB platform byte is derived from the target you already picked** and is never something
  you select. That was settled by measurement, not assumption: both MonoGame's and FNA's
  `ContentManager` validate the byte only for membership in a whitelist, never against the
  platform actually running. FNA's whitelist is the binding constraint (it has no `'V'` for
  DesktopVK and no `'G'` for DirectX 12), which is why the FNA target maps to `'w'`.

  **The type-reader manifest is the XNA-4.0 name**
  (`Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0,
  Culture=neutral, PublicKeyToken=842cf8be1de50553`), what KNI's own content pipeline and XNA
  itself write, and the only name measured to load on every consumer runtime (Phase 64,
  2026-09-09: MonoGame 3.8.1.263 / 3.8.2.1105 / 3.8.5, KNI 4.2.9001 / 4.3.9001, FNA 26.06). Phase
  60 first emitted the mgcb-shaped name (`…, MonoGame.Framework, Version=3.8.4.1, …`), reasoning
  that what every shipped MonoGame game already carries cannot be wrong; **it was wrong for KNI**:
  KNI 4.2.9001's reader-name resolver throws `FileLoadException: The given assembly name was
  invalid.` on it (stock `dotnet mgcb` `.xnb` files fail on KNI 4.2 identically; 4.3.9001 catches
  the exception). The version and token values are inert on every runtime — MonoGame only strips
  the version when the name contains `PublicKeyToken`, FNA requires the full
  `, <assembly>, Version=…, Culture=…, PublicKeyToken=…` triple — but the shape is load-bearing,
  and the XNA-4.0 shape is the intersection of the three resolvers. The mgcb-shaped string never
  shipped in a release, so nothing a consumer holds changes.

  Evidence: the envelope is byte-for-byte stock MGCB's field for field except the reader name
  (asserted equal to the XNA-4.0 constant); the payload is byte-for-byte the CLI's; and
  **rung 4 on every consumer family, all maxd 0** — `validation/XnbContentLoad` (real MonoGame
  WindowsDX `ContentManager.Load<Effect>` vs stock mgcb's `.xnb`, 4/4 fixtures, 1,230,720 px
  identical each), `validation/XnbContentLoadGl` (the same on MonoGame DesktopGL, the most common
  consumer, 4/4), `validation/KniXnbContentLoad` (real KNI SDL2.GL, built against **both**
  4.2.9001 and 4.3.9001, MGFX v10 **and** KNIFX payloads vs the mgcb payload, 4/4 each — and it
  pins that a stock mgcb `.xnb` is *rejected* on 4.2, the positive control for the manifest
  choice), and the `.xnb` arm of `validation/FnaValidation` (every gate shader's `.fxb` through a
  real FNA 26.06 `ContentManager`, 17/17 within 4/255 of the `fxc` oracle and maxd 0 of the
  raw-bytes arm). The MonoGame and KNI gates are default-ON in
  `validation/run-windows-render-gates.ps1`; the FNA arm rides `-IncludeFna`. The MGCB plugin is
  unaffected and stays: it serves teams who *want* MGCB in their build. (On KNI ≤ 4.2.9001 the
  direct route is the one that works: MGCB, stock or through the plugin, writes the mgcb-shaped
  manifest KNI 4.2 rejects.)

  Two CLI touches ride with it: the usage text now documents that an `.xnb` output extension
  selects the container, and **`SD0029`**, a warning (never an error, never a required flag)
  when an `.xnb` is written with **no `/Profile:` and no `--target-runtime`** — the implicit
  `DirectX_11` default is correct for a WindowsDX game but a DesktopGL / Android / iOS / macOS /
  KNI-GL game rejects it at `Content.Load` with a message that names neither the profile nor
  this tool. Naming any profile keeps stderr empty, so the MGCB empty-stderr contract for
  explicit invocations holds. The consumer docs now state the platform-byte table, restate the
  default beside every `.xnb` example, and tabulate the wrong-runtime failure texts.

- **`FX0014`, a registered diagnostic for the shader stages the consumer runtime cannot load**
  (Phase 58 Area C). A pass assigning `HullShader`, `DomainShader`, `GeometryShader`, or
  `ComputeShader` now fails at the stage keyword with the stage named and the permanent reason
  stated, instead of falling through to render-state parsing and reporting
  `FX0008: Expected ';' after render-state 'HullShader = compile'` on a file whose punctuation is
  correct. MonoGame's and KNI's `Effect` model exactly two shader stages, so no compiler can
  produce a loadable effect containing one; the message says that, and points at MonoGame's two
  open-but-undesigned upstream issues and at the fork that does support them.

  **The reject set did not move, and no output byte moved.** These inputs already failed; only the
  message changed. The set was measured against the pinned `mgfxc` 3.8.2.1105 rather than assumed:
  it refuses all four stages in **both** the `= compile <profile> Entry();` and the `= NULL;` form.
  The `NULL` arm was worth measuring, because `VertexShader = NULL;` **is** accepted (fxc parity),
  so it was a real branch that could have let these through silently. Verified on the three real
  `cpt-max/MonoGame-Shader-Samples` shaders that prompted the phase.

### Changed

- **`Vortice.Direct3D12` (and `Vortice.DXGI`) no longer ship in ShadowDusk's dependency graph**
  (Phase 63 Area A, issue #203). `ShadowDusk.HLSL` referenced it for one reason, the managed
  wrapper types for the reflection object `IDxcUtils::CreateReflection` returns; those are now
  ShadowDusk-owned declarations over `SharpGen.Runtime` (already present through `Vortice.Dxc`),
  transcribed from `d3d12shader.h`. Same pinned DXC, same call, same vtable slots, **zero byte
  change**: every golden and the cross-host byte-identity manifest are untouched, and the full
  suite passes on both TFMs. It had to go because that assembly carries one type the CLR refuses
  to load (`VersionedDeviceRemovedExtendedData+Union`, identical in 3.5.0 and 3.8.3), and
  MonoGame 3.8.5's Content Builder scans every consumer dependency with an unguarded
  `Assembly.GetTypes()` - any consumer whose Builder referenced ShadowDusk died before touching
  a shader. `DependencyGraphScanTests` reproduces that scan so it cannot come back. A consumer
  pinning `Vortice.Direct3D12` themselves is unaffected; nothing in ShadowDusk's public API
  exposed a Vortice.Direct3D12 type.

- **`docs/validation-matrix.md` §7 records that geometry / hull / domain / compute are not
  supportable on stock MonoGame or KNI**, with the source-level evidence re-measured 2026-08-05, so
  the question is not investigated a third time. The decisive new measurement is that the
  `cpt-max/MonoGame` fork's effect format is a **second container, not a superset**: it writes
  `(int)ShaderStage` where stock writes a 1-byte bool, so every shader record diverges from its
  first byte and stock MonoGame would misparse fork output rather than ignore it.

- **Phase 58 is closed.** Two decisions are now recorded rather than left open:

  - **The `cpt-max/MonoGame` fork is not an in-scope consumer runtime** (owner decision), so
    ShadowDusk gains no fork target for these stages. It is a second output container, not a
    superset, so supporting it would mean a second writer, reference compiler, pin, and
    render-gate family, for a fork last pushed 2024-05-20 whose packages stop at 3.8.3 against
    stock's 3.8.5. Recorded with its revisit condition in `project_decisions.md`.

  - **No compute-to-pixel-shader converter will be built** (Phase 58 Area D). The hand-conversion
    probe *passed* — cpt-max's hue-sort compute kernel, ported by hand to a render-target pixel
    shader, reproduces the original kernel's defined result at **maxd 0** in real MonoGame
    DesktopGL (`validation/ComputeConversionProbe`, mutation-checked three ways) — and that is
    precisely what settled the question. The convertible set turns on whether every output is a
    pure function of its own coordinate, which is a judgement about the *algorithm* rather than
    anything a converter could detect, and every converted kernel needs host C# no converter can
    write. The method, the convertible/not-convertible rule, and the per-shape host recipes are
    written down instead, for anyone porting a kernel by hand.

### Fixed

- **FNA / vkd3d diagnostics named a line past the end of the file** (issue #202, reported by uwx:
  `apos-shapes.fx:3586:26: E5017 …` in a 3236-line file). Every diagnostic from vkd3d-shader —
  the FNA `fx_2_0` target and the default DirectX 11 DXBC backend — was reported in vkd3d's
  own coordinates, which drift three ways: ShadowDusk blanks the `#line` directives vkd3d never
  honoured (so the 5-line macro prelude counted), vkd3d's preprocessor drops skipped `#if` arms
  and block-comment interiors from its count, and vkd3d 1.17 lexes the internal HLSL templates
  behind `atan`/`atan2` (+20 lines each), `asin`/`acos` (+11), `tanh`/`lit` (+7), `refract` (+6),
  `sincos`/`smoothstep`/`sinh`/`cosh`/`dst`/`faceforward`/`modf` (+4), `fwidth`/`determinant`
  (+3) against the user file's line counter, per call site. The reporter's construct is line
  **3009** (`if (isGlyph ? glyphFade <= 0.0 : d >= …)`); even the template-header corpus
  shaders were off by two or three lines. `Vkd3dSourceLocator` now asks vkd3d itself where each
  diagnostic sits (a sentinel parse-abort bisection of the same request, about 0.2 s against
  the failing compile's 3 s), maps the physical line back through the `#line` directives to the
  author's file and line (flattened includes too), and maps vkd3d's re-spaced column to the
  author's; message, code and raw text stay verbatim. The desktop P/Invoke and browser
  `[JSImport]` hosts share it, and it holds on every host: the sentinel's diagnostic is spelled
  by the bison that generated vkd3d's parser, not by vkd3d (`unexpected invalid token` from
  bison 3.6+, the Windows and macOS natives; `unexpected $undefined` from bison 3.5, the
  linux-x64 native built in an Ubuntu 20.04 container for its glibc baseline), and the locator
  recognises both, so a Linux consumer gets the same relocated coordinates as a Windows one
  (the first cut recognised only the first spelling, and Linux fell back to vkd3d's raw
  coordinates after burning the probe budget). **No emitted byte moves** (every FNA corpus output hashed
  identical before and after; the real compile's input is untouched). The reporter's file
  itself cannot become an FNA effect under *any* compiler — `fxc /T fx_2_0` rejects it too
  (`X3506` as written, `X4505` maximum temp register index with its `ps_3_0` arm forced) — so
  the correct outcome, now delivered, is vkd3d's loud diagnostic at the right place; the
  report's `error X0000: <file>:3586:26: E5017: …` shape was the pre-0.15.0 formatter and is
  already the MSBuild-parseable `<file>(3009,29-29): error E5017: …`. Pinned by pure locator
  tests against a fake vkd3d, real-vkd3d tests with a known construct behind known drift, and
  `FnaDiagnosticLocationTests` on the corpus fixtures plus the reporter's exact file
  (`tests/fixtures/issues/202/`, kept outside the corpus sweeps). Record:
  `plan/DONE/ISSUE-202-fna-error-line-numbers.md`.

- **MGCB plugin: `/platform:DesktopVK` on MonoGame 3.8.5 silently built an OpenGL effect, and
  `/platform:Web` was refused** (Phase 63 Area B, issue #203). MonoGame renumbered
  `TargetPlatform` in 3.8.5 (`Stadia=12, Web=13` became `Web=12, DesktopVK=13, WindowsDX12=14,
  XboxSeries=15`) and `MgcbPlatformMap` switched on the enum members it was compiled against
  (3.8.2.1105), so on a real `dotnet-mgcb` 3.8.5 value 13 read as `Web` and produced a GLSL
  payload under the Vulkan platform byte, value 12 read as `Stadia` and refused `Web` with an
  `SD0501` whose text listed Web as supported, and `WindowsDX12` was refused although DirectX 12
  is a rung-4 target. The map now keys on the platform's **name** (`platform.ToString()`, which
  the host's own enum spells), so `DesktopVK` → Vulkan and `WindowsDX12` → DirectX 12 derive
  seamlessly on 3.8.5 with no `ShaderProfile` needed (it stays as the escape hatch for MGCB
  before 3.8.5), `XboxSeries` joins the refused consoles, and `SD0501`'s supported list is built
  from the map so it can never name the platform it refuses. `validation/MgcbPlugin` gained a
  real `dotnet-mgcb` 3.8.5 arm (`DesktopGL`, `Web`, `DesktopVK`, `WindowsDX12`; payload
  byte-identical to the CLI's for the target the platform actually is, plus the negative that a
  `V`-byte `.xnb` never carries OpenGL bytes again); `MgcbPlatformMapTests` pins both numberings.
- **MGCB plugin: inside MGCB, DXC could be picked off the OS `PATH`, and DirectX 12 output was
  unsigned** (found by the new 3.8.5 gate arm). Vortice.Dxc probes the host's base directory
  (MGCB's, a miss) and then falls back to a bare-name load, so on a machine with a
  `dxcompiler.dll` on `PATH` (the Vulkan SDK installs one) every MGCB build through the plugin
  compiled with **that** DXC rather than the pinned one, a silent substitute compiler; and DXC's
  own internal `LoadLibrary("dxil.dll")` never reaches a .NET hook, so DX12 through MGCB came
  out unsigned (retail D3D12 rejects it). `PluginNativeLibraryResolver` now also subscribes
  `Dxc.ResolveLibrary` (polled before the bare-name fallback), pre-loads the plugin directory's
  `dxil.dll`, and returns its `dxcompiler.dll`. The gate runs the plugin-arm MGCB with a decoy
  `dxcompiler.dll` first on the child's `PATH` and requires DX12 payloads to equal the CLI's
  signed bytes, so neither can come back silently.

- **The DirectX 11 `d3dcompiler_47` oracle backend (Windows-only diagnostic, never ships) now
  compiles with the same fxc flags mgfxc itself uses.** Decompiling `mgfxc.dll`
  (MonoGame.Framework.Content.Pipeline 3.8.2.1105) showed its real `DirectX_11` compile sets
  `EnableBackwardsCompatibility` and `OptimizationLevel3`; `D3DCompilerShaderCompiler` set
  neither. Both load the same system `d3dcompiler_47.dll`, so this was a genuine flag
  mismatch. This narrows, but does not fully close, the Apos.Shapes gallery gate's maxd 1 —
  the residual is a machine-dependent sub-ULP fast-math scheduling difference, the same
  non-defect class already root-caused for DX12's own gate; see `docs/validation-matrix.md`.

## [0.18.0] - 2026-08-03

### Added

- **`GlSamplerSlotCorpusTests`, a corpus-wide OpenGL sampler-slot sweep against the `mgfxc`
  goldens.** It **discovers** every `tests/fixtures/golden/OpenGL/*.mgfx` from disk and asserts,
  per sampler record, that the (uniform name, texture unit, bound texture parameter) triple matches
  the reference compiler — the exact triple MonoGame's GL runtime binds with. 52/52 fixtures pass,
  10 of them as named known-gap skips (`SD0010` macro-defined techniques, Phase 41 GAP-1's GL half;
  listed individually so a *new* compile failure fails the test rather than joining the skip set).
  Discovery is the point: a golden added later is covered automatically, unlike the hand-maintained
  13-entry array the closest existing golden comparison uses. Issue #189 was fixed twice, and both
  intermediate rules looked correct against hand-picked probes before failing on an untried shape.

- **`validation/SamplerRegisterOrderGl`, the rung-4 gate for OpenGL sampler slot allocation**
  (issue #189). It is the only GL render gate that does **not** bind every texture through
  `effect.Parameters[...]` — it leaves texture unit 0 to `SpriteBatch`, which is exactly what
  makes slot numbering observable in the rendered picture. Wired into `validation-render.yml`
  alongside the other in-process GL gates.

### Changed

- **CI workflows now declare explicit `GITHUB_TOKEN` permissions** (CodeQL
  `actions/missing-workflow-permissions`, 18 alerts). Every job that only checks out, builds,
  tests, and moves run artifacts is now `contents: read`; `release.yml`'s `github-release` job
  keeps its own `contents: write`, which is the only GitHub write in the repo. No behaviour
  change to any workflow.

- **The retracted "arm B render-proved it" claim was found in a fourth place and corrected: a
  public XML doc-comment on the new `ResolveSlots`**, which renders into the published API
  reference. It asserted that `validation/SamplerPairsGl` arm B render-proved the slot numbering
  for two pairs sharing one texture. It did not, and could not: arm B's fixture uses two
  *distinct* textures, and that shape is **un-renderable as a visible distinction** in MonoGame
  3.8.2's GL backend, which has no sampler objects and applies filtering with `glTexParameteri`
  on the bound texture (one object on two units collapses to whichever filter ran last). The
  guarantee is held at record level by `OpenGl_OneTextureTwoSamplers_BakesEachPairsOwnSamplerState`
  and `OpenGl_OneTextureTwoSamplers_NamesEverySamplerUniformTheGlslDeclares` instead. Without
  this, 0.18.0 would have published the retraction and the retracted claim in the same release.
  Found by the release docs audit, not by a test.

- **Support-surface docs corrected to match what actually shipped.** The CI OpenGL gate count
  said **seven** in three places (`docs/validation-matrix.md` ×2 and the public
  `docfx/contributing/validation.md`) while `validation-render.yml` has built and run **eight**
  since `SamplerRegisterOrderGl` landed; the site's rung-4 driver list never mentioned that gate
  at all; `docs/test-shader-corpus.md`'s header counted 151 `.fx` against 153 on disk; the
  Windows gate-command lists in `CLAUDE.md`, `RELEASING.md`, and the release skill all omitted
  `ShaderToyRouteDx`; `GlSamplerSlotCorpusTests` had no §6 row; the two transcluded rewriter-rule
  tables said slots "honour `register(sN)`" without the legacy-pins / modern-reserves split, so a
  reader would predict the wrong slot for the modern spelling; issue #187 had no validation-matrix
  footprint; the DirectX 11 `register(sN)` residual had no §7 row; retired `SD0304` had no
  do-not-reuse row; and `validation-render.yml` never uploaded the new gate's debug output, so a
  CI failure landed with no diff images.

### Fixed

- **OpenGL: sampler texture units are now allocated in HLSL declaration order, the way fxc and
  therefore `mgfxc` allocate them, instead of SPIRV-Cross's first-use order** (issue #189,
  reported by Apostolique against Apos.Shapes). A `sampler : register(s0)` that was not *also*
  the first one sampled was assigned texture unit 1. Since `SpriteBatch` forces the sprite
  texture onto unit 0 immediately after `EffectPass.Apply()`, the sampler ShadowDusk had placed
  on unit 0 read the **sprite**, and the one it placed on unit 1 read whatever nothing had
  bound: a silent wrong-texture render in the most common MonoGame custom-effect idiom, with a
  clean compile and no diagnostic. Against the pinned `mgfxc` 3.8.4.1 on the reporter's repro,
  `mgfxc` mapped `ps_s0` to the first-declared sampler while ShadowDusk mapped it to the
  first-*sampled* one. The rule was **measured rather than assumed**, on three shapes including
  one with every `register` annotation stripped: fxc allocates in declaration order in all of
  them, so this is not only about honouring `register(sN)`. Slots now come from
  `SpirvCombinedSamplerPairs.ResolveSlots` — the pair's texture declaration index, ranked by the
  raw SPIR-V `Binding` decoration DXC allocates in declaration order — which is used by **both**
  `MonoGameGlslRewriter` (naming the uniforms `ps_s{slot}`) and the `.mgfx` sampler table, so the
  table and the GLSL it describes cannot drift apart; records are additionally emitted sorted by
  slot, matching how `mgfxc`'s goldens lay the table out. Where two pairs share one texture (one
  texture through two `SamplerState`s) the declaration indices collide and no `mgfxc` golden
  exists for the shape, so `ResolveSlots` falls back to the previous positional numbering rather
  than inventing an answer. **No existing output moved:** all 48 OpenGL entries of the cross-host
  byte-identity manifest are byte-for-byte unchanged and the golden corpus is untouched, because
  only shaders whose declaration order differs from their sampling order are affected and none in
  the corpus were. DirectX, DirectX 12, Vulkan, and FNA are untouched. Proven by the new
  `validation/SamplerRegisterOrderGl` gate, which measured **maxd 255 across all 4096 pixels
  before the fix and maxd 0 after**, against a real `mgfxc` golden rendered in the same scene;
  full suite 3123/3123 and Windows render gates 15/15 green.
- **OpenGL: an explicit `register(sN)` on a legacy `sampler` declaration now pins the texture
  unit** (issue #189, sparse half). Samplers at `s2`/`s3` with nothing at `s0`/`s1` were compacted
  to units 0/1. That is order-preserving and internally self-consistent, and still wrong, because
  unit 0 is not the effect's to allocate: `SpriteBatch` overwrites it with the sprite right after
  `EffectPass.Apply()`, so the sampler pushed onto unit 0 read the sprite. `FxPreParser` now
  **records** the register index it already parsed (`FxParseResult.ExplicitGlSamplerSlots`,
  texture-keyed) before its SM4 rewrite drops the clause, and `ResolveSlots` prefers it.
  The clause is **recorded, not re-emitted**: putting it back into the rewritten HLSL would change
  what DXC compiles and move the DirectX, DX12, Vulkan and FNA bytes, none of which have a
  reported defect — so this changes OpenGL allocation only.
  **What it actually models is fxc's allocator, not a declaration form.** In texture-declaration
  order, a pair whose sampler declared an explicit register takes it; every other pair takes the
  lowest register neither already taken nor **reserved** by a modern `SamplerState : register(sN)`.
  One rule, verified against the pinned `mgfxc` on **ten shapes**, legacy and modern, annotated and
  not — all match. It also explains what looked like an arbitrary asymmetry: at `ps_3_0` a texture
  and a sampler are ONE object in ONE register namespace, so a legacy `sampler X : register(sN)`
  *is* the combined object and lands on `N`, while a modern `SamplerState` is a sampler-only object
  that occupies its register and pushes the synthesized combined samplers around it — one texture
  plus `SamplerState S : register(s0)` yields `ps_s1`, not `ps_s0`, and `S : register(s1)` with two
  textures yields `ps_s0` + `ps_s2` (skipped, not shifted).
  Fixing this also surfaced a **pre-existing** divergence in the same area: the declaration rank was
  taken from the SPIR-V `Binding` decoration, which equals declaration order only while DXC
  auto-allocates and equals *register* order once the source is annotated. It now comes from module
  order, so `Texture2D TexA : register(t3); Texture2D TexB : register(t2);` puts TexA on unit 0 like
  `mgfxc`, where before it put TexB there. Proven by a second arm on
  `validation/SamplerRegisterOrderGl` (`SamplerRegisterSparse.fx`), measured **(0,255,0) vs
  `mgfxc`'s (255,255,0), maxd 255 before and maxd 0 after**.

  **One residual is recorded rather than papered over.** DirectX 11 ignores `register(sN)` for its
  sampler slot (we emit 0/1 where `mgfxc` emits the declared 2/3), but `fxc` itself auto-assigns DX
  *texture* registers by first use, so matching the annotation there would move *away* from the
  reference compiler; left measured and unchanged (`project_decisions.md`).
  **The validation-gap lesson is the durable part:** all 30-plus existing GL render gates bind
  their textures through `effect.Parameters[...]`, under which a first-use-numbered table is
  internally consistent and renders *correctly*, so not one of them could see this. The
  `validation/SamplerPairsGl` arm B gate was even documented as proving the numbering divergence
  was "benign" — it could not, because its two textures hold identical pixels by design, which is
  precisely what makes it blind to which unit each pair lands on. That claim has been retracted in
  `docs/validation-matrix.md`, `project_facts.md`, and the driver itself.

- **OpenGL: a numeric parameter that reflection reports but the shipped GLSL never declares now
  gets synthesized register backing instead of shipping as a phantom** (issue #187, found on
  `GradientToy.fx`'s `iResolution`). DXC's `-spirv` backend folds the shader's
  `(uv * iResolution.xy) / iResolution.xy` identity and drops the then-unused `$Globals` cbuffer
  from the SPIR-V, while the DXIL companion compile that sources desktop-GL reflection — like real
  fxc/mgfxc — keeps it; the .mgfx therefore carried `Parameters["iResolution"]` with **zero**
  cbuffer records behind it, so `SetValue` wrote into CPU-side parameter data nothing ever
  consumed. The pipeline's reflection→backing join was one-directional (GLSL layout → parameter,
  never the reverse); it now appends a register slot, cbuffer membership, and a covering
  `uniform vec4 {vs,ps}_uniforms_vec4[N];` declaration for each reflected Scalar/Vector/Matrix
  parameter the rewriter's layout missed. For `GradientToy` the output now carries exactly the
  mgfxc golden's structure (one `ps_uniforms_vec4` cbuffer, 16 bytes, parameter 0 at offset 0,
  referenced by the pixel shader); rendering is unchanged — the GL driver either link-strips the
  unread array (a silent, spec-sanctioned skip) or uploads data nothing reads, the same shape real
  mgfxc's DirectX profile ships for any declared-but-unused uniform. A 147-fixture corpus sweep
  confirmed `GradientToy` is the sole pre-existing affected output. Two rounds of adversarial
  review hardened four synthesis sub-shapes before shipping, each pinned by a purpose-built
  fixture: a **non-square matrix** phantom must be sized by the runtime's transposed write model
  (Columns registers — MonoGame uploads `ColumnCount` 16-byte rows, so sizing by Rows crashes the
  first `EffectPass.Apply`; `ExPhantomNonSquareMatrix.fx`); the synthesized declaration must be
  inserted after the `#extension` derivatives header + `#ifdef GL_ES` precision block
  (`ExPhantomDerivativeUniform.fx`) **and** after the balanced `#if __VERSION__ >= 300 … #endif`
  TexLod header, whose `#extension` directives live inside branches — Mesa hard-errors on a
  mid-shader `#extension`, so `GlPrologueEnd` consumes balanced preprocessor blocks
  (`ExPhantomTexLodUniform.fx`); and a stage with live uniforms plus a phantom must resize its
  existing declaration rather than insert a second one (`ExPhantomSecondCbufferFold.fx`).
  `GlPhantomParameterTests` was rewritten to
  assert **structural backing** and un-skipped — its original `ShouldContain(name)` assertion
  could never pass, even against the golden's own GLSL (GL packs uniforms into register arrays, so
  parameter names never appear literally) — and a new corpus-wide backing sweep guards the whole
  class. Residual divergences (SPIR-V-reflecting paths — the WASM host and desktop Vulkan —
  still cannot see the folded-away parameter, so their parameter lists lack it; the fold itself
  diverges from mgfxc only at degenerate values like an unset `iResolution`, where ShadowDusk's
  build is the more forgiving one, and no lever closes that half) and the DXC 1.8
  `-fspv-preserve-bindings` follow-up for the reflector half are recorded in
  `plan/DONE/ISSUE-187-gl-phantom-parameter-compile-fidelity.md`.

## [0.17.0] - 2026-08-01

### Added

- **`validation/DumpPreprocessedHlsl`, a no-GPU diagnostic** that dumps the exact HLSL text the
  compilation pipeline hands to DXC for a given `.fx` + target, plus the `-D` macro flags. It exists
  so a divergence on any DXC-fed target can be *attributed*: replay the identical input through a
  different `dxc.exe` and diff the disassembly. An empty instruction diff means our source and flags
  are right and only the pinned DXC build differs.
- **`SD0104`, the mgfxc-parity warning for an unrecognized vertex-input semantic** (closes the
  remaining half of bug-hunt 2026-07-27 N5). An HLSL vertex semantic ShadowDusk does not model
  has always defaulted to `VertexElementUsage.TextureCoordinate`, which is correct — real `mgfxc`
  defaults the same way — but `mgfxc` also *prints a warning when it defaults* and ShadowDusk did
  not, so a typo such as `TEXCORD0` for `TEXCOORD0` silently minted a phantom TextureCoordinate
  attribute that MonoGame's `VertexInputLayout` then demanded from the vertex declaration, with a
  failed draw as the only symptom. The Vulkan (SPIR-V) and DirectX12 (DXIL) attribute-table paths
  now surface it through `CompiledShader.Warnings`, so it reaches CLI stderr, MGCB, and
  `ValidateAsync` like every other warning. It is a **warning, never an error** (`mgfxc` accepts
  and defaults, so a drop-in replacement must too), and **no emitted byte moves**: the fallback
  usage/index values are unchanged and warnings never gate output.
- **`SD0008`, a warning for an `#include` that only resolves because your file system ignores
  case.** `#include "shared/macros.fxh"` against a file really named `Shared/Macros.fxh`
  compiles on Windows and on a default macOS volume, and then fails with `SD0001` on Android,
  on Linux, and on a case-sensitive APFS volume — a break the author cannot see locally. It is
  a warning rather than an error because the include genuinely did resolve and `mgfxc` accepts
  it too; the message names the on-disk spelling so the fix is one edit. Only the path segments
  the directive itself spells are checked, since the absolute prefix above them is your own
  machine layout and never ships.
- **`ShaderToyRouteDx`, the DirectX arm of the ShaderToy / `.glsl` route render gate.** It converts
  `GradientToy.glsl` in process with the real converter and pixel-diffs ShadowDusk's `DirectX_11`
  build against **`mgfxc`'s `DirectX_11` build of the same converted `.fx`** on real MonoGame
  `WindowsDX`. This arm was previously impossible, not merely missing: mgfxc refused the converter's
  own output on DirectX (see below), so no golden could exist. Default-ON in
  `validation/run-windows-render-gates.ps1`; there is no CI lane, because no GitHub runner has a
  headless D3D driver (the same bucket as every other DX gate).
- **A `DirectX_11` golden for the pinned ShaderToy fixture**, so `tests/fixtures/shaders/shadertoy/GradientToy.fx`
  is golden-backed on both profiles like the rest of the corpus. `tools/compile-fixtures.ps1` now
  includes the `shadertoy/` subdirectory by default, so a future regeneration cannot silently skip it.
- **`ShadowDusk.MgcbPlugin` — MonoGame Content Builder integration, for real** (Phase 29). The
  project went from a `.csproj` with zero `.cs` files to a shipping content-processor plugin:
  `/reference:` it in a `.mgcb`, select `ShadowDuskEffectImporter` / `ShadowDuskEffectProcessor`,
  and MGCB compiles `.fx → .xnb` through ShadowDusk **in its own process** — no `mgfxc`, no
  `fxc.exe`, no Wine, no PATH plumbing. This is the native MGCB route, and the only one: MGCB
  compiles effects in-process and launches no external effect compiler, so the previously
  documented "put ShadowDusk on PATH as `mgfxc`" override never fired.
  - **The target comes from the content project's own `/platform:` line** (`Windows` → DirectX 11,
    the GL-family platforms → OpenGL, consoles → a loud `SD0501`). No ShadowDusk-specific flag is
    ever required for correct output. Optional processor parameters: `DebugMode`, `Defines`,
    `IncludeDirs`, and the escape hatches `ShaderProfile` (reaches DirectX 12 / Vulkan, which
    MGCB's platform list cannot name), `MgfxVersion`, `DxbcBackend`.
  - **The `.mgfx` inside the `.xnb` is byte-for-byte the ShadowDusk CLI's** output for the same
    source and target, because the plugin is an adapter onto the same `EffectCompiler` and adds no
    compilation logic. Proven, not asserted: `MgcbPluginByteIdentityTests` (14/14, under
    `dotnet test`, compared against the real CLI binary as a separate process) and the new
    `validation/MgcbPlugin` driver (7/7 through a real `dotnet mgcb`, which additionally checks the
    `.xnb` envelope equals MGCB's own stock output and the payload differs from it). Same payload
    out of `dotnet mgcb` 3.8.2.1105, 3.8.3, 3.8.4, 3.8.4.1 and 3.8.5, and out of the packed
    `.nupkg` extracted into a bare directory.
  - Shader errors surface through MGCB in the canonical `file(line,col-col): error CODE: message`
    form, from the CLI's own formatter (source-linked, so the two cannot drift), with the
    underlying compiler's words verbatim beneath. `#include`d files are registered as build
    dependencies.
  - The package is **tools-only** (no `lib/`; everything under `tools/net8.0/any/`), because MGCB
    resolves a referenced plugin's dependencies — managed and native — from the plugin's own
    directory. It is a `DevelopmentDependency` and contributes nothing to a consumer's shipped game
    assembly. `release.yml` fails the release red if any native is missing from it, or if it ships
    MonoGame's content-pipeline assembly.
  - `samples/mgcb` gained `Content/Content.ShadowDusk.mgcb`, the same corpus built through the
    plugin alongside the stock one.
- **`ShadowDusk.MgcbPlugin` is now a published package**, making it the **eighth** `ShadowDusk.*`
  NuGet. `release.yml`, `RELEASING.md`, the `/release` skill, and the package-count mentions in
  `CLAUDE.md` / `Brand/README.md` were updated together.

### Changed

- An opt-in third arm on the DirectX 12 Apos.Shapes gate (`SHADOWDUSK_DX12_PROBE_MGFX`) that renders
  an arbitrary supplied `.mgfx` alongside the golden and candidate. Off unless the variable is set,
  and its result is reported, never asserted.
- **Root-caused the DirectX 12 Apos.Shapes gallery's `maxd 1`: it is the pinned DXC build, not a
  ShadowDusk defect.** ShadowDusk compiles DXIL with `dxcoob 1.7.2212.40` (the `Vortice.Dxc` 3.3.4
  pin); the `mgfxc` `DirectX_12` golden was built with MonoGame 3.8.5's bundled `dxcoob 1.8.2505.32`.
  Feeding ShadowDusk's own pre-parsed HLSL and own DXC flags to a DXC 1.8 build reproduces the
  golden's DXIL instruction-for-instruction, and rendering that payload in ShadowDusk's own container
  gives maxd 0 with zero differing pixels of 402,984. The delta reaches a pixel at all only because
  the shader adds half an 8-bit LSB of dither immediately before quantization. `maxd 1` stays the
  honest DX12 tolerance until the DXC pin moves. No compiler behavior changed.
- **The interactive ShaderToy viewer and its MonoGame runtime helper moved out of the Phase-46
  experiment tree into `samples/ShaderToyViewer/`** (Phase 51 A4, closing the Phase 47
  sample-migration appendix that had stayed *Planned*). The `ShaderToyEffect` helper is folded into
  the sample as `Runtime/ShaderToyEffect.cs` rather than kept as a separate
  `ShadowDusk.ShaderToy.Runtime` project: it is one file with one public type, and folding it in
  makes the MonoGame boundary structural, since the only projects that reference MonoGame are now
  under `samples/`, `validation/`, and the out-of-band render-proof driver, never under `src/`.
  Namespaces moved with it (`ShadowDusk.ShaderToy.Sample` and `ShadowDusk.ShaderToy.Runtime` became
  `ShadowDusk.ShaderToyViewer` and `ShadowDusk.ShaderToyViewer.Runtime`), which disambiguates the
  sample from the `ShadowDusk.ShaderToy` product library that now owns that name. Like every other
  sample it stays out of `ShadowDusk.slnx`; run it with
  `dotnet run --project samples/ShaderToyViewer` (add `-- --smoke` for the headless self-test, which
  is green 4/4 and regenerates the committed eyeball PNGs byte-identically). **Relocation only: no
  compiler code, no shipped package, and no output byte changed**, and
  `NoMonoGameInProductLibrariesTests` stays green on both TFMs. `tools/shadertoy2fx/` keeps the
  standalone PoC CLI (still the only entry point to the converter's `--multipass` batch mode) and
  the out-of-band fidelity/gallery render-proof driver, which now source-links the single helper
  file from the sample.
- **The ShaderToy converter emits a DirectX-valid compile-profile header.** It used to write
  `vs_3_0`/`ps_3_0` in *both* arms of its `#if OPENGL … #else … #endif`, so the DirectX arm asked for
  a profile MonoGame's `DirectX_11` shader profile refuses — real `mgfxc /Profile:DirectX_11` failed
  every converted shader with *"Invalid profile 'vs_3_0'. Vertex shader 'VSMain' must be SM 4.0 level
  9.1 or higher!"*. The header is now gated on **`SM4`** (the macro MonoGame's own DirectX_11 profile
  defines): DirectX gets `vs_4_0_level_9_1`/`ps_4_0_level_9_1`, while OpenGL **and FNA** keep
  `vs_3_0`/`ps_3_0`. `SM4` rather than the stock `#if OPENGL … #else` split precisely because that
  `#else` arm also catches the FNA target, whose `fx_2_0` output is capped at Shader Model 3.
  **If you regenerate a `.fx` from a `.glsl`, its header text changes** — 81 converter goldens and 2
  multipass goldens moved with it. **No compiled output moved:** ShadowDusk's OpenGL `.mgfx` for the
  pinned fixture is byte-identical before and after, and mgfxc's OpenGL golden regenerated
  byte-for-byte identical.
- **The DirectX target now rejects compile profiles below MonoGame's floor, matching `mgfxc`
  (new diagnostic `SD0015`).** A profile can be perfectly recognized — so the Phase 48 `SD0013`
  check passes — and still be one the reference compiler refuses for this target. `mgfxc`'s
  `DirectX_11` profile accepts **only** `{vs,ps}_4_0_level_9_1`, `_4_0_level_9_3`, `_4_0`, `_4_1`,
  and `_5_0`; the accepted set was established by sweeping every recognized profile through the
  pinned `mgfxc` rather than inferred from the names, which matters because `_4_0_level_9_0` **and
  every SM6 profile** are refused too. **This turns previously-succeeding DirectX compiles into
  loud rejections**, for shaders real `mgfxc` was already refusing: 20 corpus fixtures flipped,
  including 14 vendored Nez post-process shaders (which name `ps_2_0`/`ps_3_0` outright — Nez
  targets DesktopGL), `FnaMultiPassStates.fx`, and `examples/Ex{Int,Mat3}UniformMember.fx`. The fix
  a consumer applies is the standard MonoGame `#if OPENGL … #else …` header, which the diagnostic
  names. OpenGL, Vulkan, DirectX 12, and FNA are **unaffected**: their floors are different
  (measured and recorded in `docs/validation-matrix.md` §8.1) and enforcing them is separate work.
  No output bytes changed on any target.
- `FnaMultiPassStates.fx` was dropped from the **DirectX** arm of the cross-host byte-identity
  manifest **and from the DirectX arm of `Vkd3dCorpusProbe`**, which captures the desktop ground
  truth for the WASM vkd3d byte-identity gate (it stays in the OpenGL and FNA arms of both). It
  compiles `vs_2_0`/`ps_2_0`, so its DirectX row was pinning bytes the reference compiler cannot
  produce. The probe keeps its own corpus list in step with `CrossHostByteIdentityTests`, and
  missing it there turned the WASM gate red on the first CI run that exercised it, which is the
  reason that gate is not skipped on a PR that changes the reject set. The node gate's corpus is
  now **94** stage compiles rather than 98, the four removed being this fixture's one vertex and
  three pixel entries on the DirectX arm.
- **`NoMonoGameInProductLibrariesTests` gained a narrow, named exemption** for
  `ShadowDusk.MgcbPlugin` — an MGCB plugin cannot exist without the
  `ContentImporter`/`ContentProcessor` contract — plus a second test pinning that the reference
  stays `IncludeAssets="compile" PrivateAssets="all"`, which is what keeps it harmless. No other
  `src/` project may name MonoGame, and none does.
- The MGCB documentation across the site (`guides/mgcb-content-pipeline.md`,
  `samples/mgcb.md`, `index.md`, `getting-started/overview.md`, `contributing/index.md`,
  `api/index.md`, `README.md`, `docs/the-purpose.md`) now documents the plugin as the MGCB route
  instead of describing it as an unimplemented scaffold.

### Fixed

- **The Apos.Shapes gallery harness named the wrong shape in every divergence it reported.** Its
  per-cell rectangles came from the untransformed layout while the scene renders through a 1.15x
  scale plus a (6,4) translate, so a shape drawn in layout cell (3,3) lands in screen cell (4,4).
  That is why the DX12 delta above was recorded first against `DrawCircle`/`FillArc` and later
  against `FillRing`; the pixels are `DrawEllipse`'s. Cell rectangles now go through the view matrix.
- **A third of the Apos.Shapes gallery was being drawn but never compared.** The render target was
  sized to the untransformed 600x500 layout, so the same 1.15x scale pushed the entire last column
  off the right edge and the last row down to a ten-pixel sliver: 10 of the 30 cells contributed no
  pixels to any comparison, while the OpenGL visibility check still reported 30/30 because it was
  measuring those same untransformed rectangles. The target is now sized to the transformed extent.
  The gallery has no stored reference images, so no goldens needed regenerating; re-verified after
  the change at DX11 maxd 0 (both arms), Vulkan maxd 0, OpenGL 30/30 genuinely visible, DX12 maxd 1.
- **`#include` de-duplication and cycle detection no longer guess whether the file system is
  case-sensitive from the operating system** (bug-hunt 2026-07-27 N17). The rule was
  "Linux is case-sensitive, everything else is not", which is wrong on two hosts ShadowDusk
  ships to: **Android's file system is case-sensitive** (and .NET's `OperatingSystem.IsLinux()`
  is false there), and **APFS can be formatted case-sensitive**. On those hosts two genuinely
  distinct headers whose names differed only by case were treated as one file, so a
  `#pragma once` in the first silently suppressed the second, and a legal include chain through
  a case twin was rejected as a false circular-include error. Resolved paths are now compared
  the way the storage they came from spells them: ordinal by default, with two case-only
  variants merged only when the file system confirms they are one file. Output bytes are
  unchanged for every input that resolved the same way before.
- **OpenGL-target compiles could crash on hosted CI with a DXIL validation error** (e.g.
  GitHub Actions `windows-latest`, `dotnet test`): `error: DXIL container mismatch for
  'PSVRuntimeInfoSize' ... Validation failed`, while the identical source compiled fine for
  DirectX on the same runner and on a real dev machine (issue #185). The OpenGL path compiles
  the shader twice: once targeting OpenGL (SPIR-V, shipped) and once targeting DirectX (SM6
  DXIL) solely to reflect parameters from the native DXIL oracle. Hosted runners can carry
  their own preinstalled Windows SDK `dxil.dll`/`dxcompiler.dll` on PATH, version-skewed
  against the ones this library is pinned to; when the mismatched `dxil.dll` wins native
  resolution, DXC's validator rejects an otherwise-correct module. The reflection-only
  companion compile now passes DXC's `-Vd` (skip validation) — its bytes are discarded after
  reflection and never shipped, so skipping validation there cannot ship an invalid module.
  Every shipped compile (OpenGL SPIR-V, DirectX DXBC, DirectX 12 DXIL) still validates fully.

### Known issue (found, filed, deliberately not fixed here)

- **A pixel shader whose DXC compile cancels an algebraic identity that mgfxc's fxc compile
  does not can carry a phantom effect parameter** (issue #187, split out from #185 while
  investigating the DXIL-validation fix above). `GradientToy.fx` computes `fragCoord = uv * iResolution.xy`
  then `uv2 = fragCoord / iResolution.xy`; DXC's `-spirv` backend cancels the identity
  entirely, so the shipped GLSL never references `iResolution`, while `fxc`/`mgfxc` do not
  perform the same cancellation and the committed `mgfxc` golden's GLSL still reads it.
  ShadowDusk's OpenGL reflection sources from a separate DXIL companion compile that also
  does not cancel it, so `Parameters["iResolution"]` exists but is inert (`SetValue` writes
  nowhere). Reflecting from the SPIR-V that actually ships instead was tried and reverted: it
  removes the phantom but makes the parameter list diverge from the mgfxc golden **by name**,
  which is the project's primary compatibility bar — trading one divergence for a different
  one, not a fix. The real root cause is upstream of reflection (ShadowDusk's DXC compile and
  mgfxc's fxc compile produce non-equivalent GLSL for this shader) and needs its own scoped
  fix. Pinned by `GlPhantomParameterTests` (`Skip`-marked pending that fix, not deleted).

## [0.16.0] - 2026-07-30

> The MonoGame pin stays 3.8.2.1105 and the default output stays MGFX v10. The golden corpus and
> the byte-identity manifest are **untouched**: the OpenGL and DirectX 12 sampler-table fix below
> changes output only for shapes that previously failed to compile or were silently mis-bound, and
> every 1:1 texture/sampler shader (which is the whole golden corpus) is byte-identical.

### Added

- **`DeferredSpriteMrtGl`, the first render gate in the repo that binds more than one render
  target**, closing the multiple-render-target render rung that had been open since the
  `DeferredSprite.fx` compile fix in June. It draws the shader on real MonoGame DesktopGL with
  two targets bound, reads **both** attachments back, and pixel-diffs each against the real
  `mgfxc` OpenGL golden: maxd 0 on both. Every other GL gate binds one target, so none of them
  could tell "the second output reached attachment 1" from "the second output went nowhere",
  and a structural match cannot either — that output lives in the emitted GLSL, not in the
  `.mgfx` record tables. Wired into `validation-render.yml` so it runs in CI on Mesa llvmpipe.
  Alongside the mgfxc diff it asserts the exact values the HLSL implies and names which failure
  mode a wrong picture is, because the mutation check (binding one target instead of two) leaves
  the diff arm reporting maxd 0 — both sides broken identically — and only the absolute arm
  catches it.
- **`ShaderToyRouteGl`, a render gate for the ShaderToy / `.glsl` frontend route**, which had been
  compile-proven and fidelity-proven but never held to the reference compiler. It converts
  `GradientToy.glsl` in process with the real converter and pixel-diffs ShadowDusk's OpenGL build
  against **`mgfxc`'s build of the same converted `.fx`** on real MonoGame DesktopGL: maxd 0, in CI.
  An `mgfxc` oracle exists here despite the docs saying otherwise, because "no oracle" is true of
  ShaderToy *input* — the converter's *output* is ordinary HLSL that `mgfxc` compiles like any other,
  so the downstream half of the route can be held to the real product bar. The gate asserts the
  converter still emits the committed `.fx` the golden was built from before it renders, so converter
  drift turns it red instead of leaving the golden describing a different shader.
- **`SamplerPairsGl`, a new OpenGL rung-4 render gate** for per-(texture, sampler)-pair sampler
  records, wired into `validation-render.yml` so it runs in CI on Mesa llvmpipe. Both of its arms
  render an *asymmetric* function of two samplers so that a mis-binding changes the picture (a
  symmetric `diffuse * light` would render identically under a swap and prove nothing), and arm A
  reports *which* failure mode a wrong colour corresponds to rather than just "wrong".

### Known issue (found, filed, deliberately not fixed here)

- **A converted ShaderToy `.fx` compiles for DirectX in ShadowDusk and is rejected by `mgfxc`.** The
  converter emits `vs_3_0`/`ps_3_0` in *both* arms of its `#if OPENGL` header, so the DirectX arm asks
  for a profile below MonoGame's DirectX floor; real `mgfxc /Profile:DirectX_11` refuses it (*"must be
  SM 4.0 level 9.1 or higher"*) while ShadowDusk compiles the identical file successfully. Surfaced
  while adding the gate above, and only because the new fixture joined the auto-globbed corpus. Two
  separable defects (the converter's emitted profile header, and a reject-fidelity gap of the Phase 48
  class that `SD0013`/`SD0014` do not cover), each with its own blast radius: one moves every converter
  golden, the other turns a currently-succeeding compile into a rejection. Filed as Phase 51 A10 with
  the ordering constraint that fixing the reject side alone would break the route's own output.
  **Both halves are fixed in `[Unreleased]`** — see the entries there; this note stays as the record
  of when the divergence shipped.

### Fixed (compiler)

- **OpenGL now compiles several textures read through one shared `SamplerState`** — the classic
  diffuse+lightmap shape, ordinary HLSL that `mgfxc` has always compiled (its own golden for the
  shape carries two sampler records). ShadowDusk rejected it outright with `SD0216`. The GL sampler
  table is now keyed on the **(texture, sampler) pairs** SPIRV-Cross folds into combined samplers
  rather than on the reflected samplers, which is the only list that can be right: the GL runtime
  looks each record up by GLSL uniform name, and there is one uniform per pair.
- **A second, silent OpenGL mis-binding that the `SD0216` guard could not see.** SPIRV-Cross
  declares combined samplers in **first-use** order, not declaration order, so a shader sampling
  two textures through two samplers in reverse order produced matching counts — two uniforms, two
  records, guard satisfied — while both the texture parameter and the sampler-type byte came out
  **swapped**. Probed with a `Texture2D` + `TextureCube` pair: the emitted GLSL declared `ps_s0` as
  `samplerCube` while the record claimed 2D, so MonoGame bound a 2D texture to a cube sampler unit.
  It compiled cleanly with no diagnostic. The same mis-numbering affected any shader mixing legacy
  `sampler2D` with modern `Texture2D` + `SamplerState` declarations.
- **DirectX 12 had the shared-`SamplerState` bug too**, and silently: it was never included in the
  texture-keyed branch, so the diffuse+lightmap shape emitted one record and the second texture was
  never bound, with no diagnostic. DirectX 11 was always correct. Found while closing the OpenGL
  work.
- The pair list is derived in **pure managed code** from the SPIR-V (`SpirvCombinedSamplerPairs`),
  not by calling SPIRV-Cross's own `spvc_compiler_get_combined_image_samplers`, because the browser
  host's `spirv-cross.wasm` does not export that function — a native call would have fixed desktop
  only and broken the guarantee that the CLI and the browser emit identical bytes.

### Fixed (CI / evidence)

- **Every workflow now installs the .NET 10 SDK alongside .NET 8.** All seven pinned
  `dotnet-version: '8.0.x'` while six `src/` libraries multi-target `net8.0;net10.0`; the
  `net10.0` leg was being satisfied only by whatever the runner image happened to preinstall.
  That is an undeclared dependency in `release.yml` — the workflow that publishes — so a runner
  image change could have broken a release with no prior signal. `pack-consume.yml` also gained a
  `tfm` matrix dimension, so the scratch consumer now restores and runs against **both** shipped
  TFMs rather than `net8.0` alone; a broken `net10.0` asset previously had no end-to-end gate.
- **The retracted MGCB "expose ShadowDusk as `mgfxc` on `PATH`" claim is now corrected
  everywhere it appeared**, not only on the four docfx pages fixed earlier. It survived in
  `src/ShadowDusk.Cli/README.md` — which ships *inside the NuGet package* — and in the site's
  Overview delivery-shapes table, `README.md`, and `docfx/index.md`. All now point at the routes
  that work: invoke the CLI directly and `/copy:` the `.mgfx`, or compile at runtime.
- **The GLSL rewriter-rule docs no longer describe the retired sampler-slot model.**
  `docs/glsl-uniform-naming.md` and `docs/references/compilation-pipeline.md` (both transcluded
  into the published site) still said samplers were `ps_s{slot}` "looked up by slot"; they now
  document the per-(texture, sampler)-pair, first-use-order model this release shipped, including
  what `SD0217` cross-checks and why the pair list is derived in managed code.
- Assorted support-surface drift corrected in the same pass: the rung-4 list gained the three new
  render gates; the CI GL-gate count went from three to **seven** in both `docs/validation-matrix.md`
  and the gate script's own help text; the MGFX v10 floor reads **3.8.1.263** (the measured floor)
  rather than 3.8.2 in nine places; fixture counts in `docs/repository-layout.md` (144 `.fx`);
  `docs/test-shader-corpus.md` gained the sampler-pair and ShaderToy-route fixtures plus a
  last-updated line; `project_facts.md` no longer records the *reverted* `Apos.Shapes` 0.7.12 bump
  as shipped; and two stale references to the retired `SD0215`/`SD0216` are gone.

- **Test-results artifacts stopped discarding 13 of every 14 assemblies' results.** Every
  `dotnet test` invocation in `ci.yml` and `release.yml` passed a fixed
  `--logger "trx;LogFileName=…"` while running 14 assemblies (7 projects across `net8.0` and
  `net10.0`) concurrently, so they all wrote the same file — the logs were full of
  `WARNING: Overwriting results file` — and the uploaded artifact held whichever assembly
  happened to finish last. All five invocations now use `LogFilePrefix`, which emits one
  `<prefix>_<tfm>_<timestamp>.trx` per assembly (measured on the real solution: 14 files, zero
  overwrite warnings). A guard step fails the integration job if only one `.trx` lands, so a
  revert cannot silently re-lose the results. This matters beyond tidiness: the ubuntu
  integration lane intermittently crashes a test host, and the crashed assembly's `.trx` was
  precisely the one guaranteed to be overwritten, which is why that crash had been re-derived
  from log adjacency three times — and mis-attributed each time. No product code is affected.

### Changed

- **The test suite moved off `FluentAssertions` onto `Shouldly` 4.3.0, and FluentAssertions is now
  banned** (issue #171). This is a licence obligation, not a preference: FluentAssertions 8.x
  relicensed to the Xceed "Community License Agreement (for Non-Commercial Use)", which requires a
  paid commercial licence for any organisation that earns revenue. We had been capped at 7.2.2 (the
  last Apache-2.0 release), but that line receives no further fixes, so the cap only deferred the
  work onto a frozen dependency. Shouldly is BSD-3-Clause at every version with no commercial gate.
  All 7 test projects and ~4,000 assertion sites across 132 files were converted; the suite is green
  at the same 2,394 tests per target framework as before the migration. Nothing shipped changes:
  no `ShadowDusk.*` package ever referenced FluentAssertions, so consumer output and dependency
  graphs are untouched.
  - Two Shouldly differences were **not** mechanical and are worth knowing when writing new tests.
    String `ShouldContain`/`ShouldNotContain` default to **case-insensitive** where FA's
    `Contain`/`NotContain` were case-sensitive, so every string-receiver site now passes
    `Case.Sensitive` explicitly — without it roughly 900 assertions over generated GLSL/HLSL would
    have silently weakened, and no test failure would have revealed it. And FA's `BeEquivalentTo`
    compared structurally where Shouldly's `ShouldBe(…, ignoreOrder: true)` compares with `Equals`,
    so collections of reference types without value equality use `ShouldBeEquivalentTo`.
  - FluentAssertions is now **banned** by standing rule, recorded in `project_facts.md`,
    `project_rules.md`, `CLAUDE.md`, and the `Directory.Packages.props` comment. Deliberately a
    written rule rather than a repo-scanning test: the thing being prevented is an author
    reaching for the familiar `.Should()` API, which the rule addresses where authors read.
- **`SD0215` and `SD0216` are retired**, and their numbers are marked do-not-reuse in
  `docs/error-codes.md`. Both existed only because of the old sampler-keyed GL table: `SD0216`
  rejected the shared-`SamplerState` shape, and `SD0215` rejected sampler registers that were not
  contiguous from `s0` (the record used to be named after the sampler's bind slot). Neither
  restriction applies now — a `register(s3)`-only shader compiles correctly. The new **`SD0217`**
  covers input shapes the declaration-order model does not cover, plus an internal cross-check of
  the derived pair count against the sampler uniforms the emitted GLSL actually declares; ordinary
  HLSL never raises it.
- OpenGL texture parameters **keep the plain texture name** (`DiffuseMap`) rather than adopting
  `mgfxc`'s MojoShader `<sampler>+<texture>` spelling (`TextureSampler+DiffuseMap`), which its
  OpenGL goldens use for every modern-syntax shader. This is a deliberate, recorded decision:
  MonoGame resolves a sampler's texture through the record's parameter *index* and never its name,
  so the two spellings behave identically; renaming would break every existing consumer's
  `Parameters["DiffuseMap"]` lookup; and ours is the same name the DirectX, DirectX 12, Vulkan, and
  FNA targets use, whereas `mgfxc`'s is OpenGL-only and makes an effect's parameter names depend on
  the backend.

- **The shipped libraries now multi-target `net8.0` and `net10.0`.** `ShadowDusk.Core`, `.HLSL`,
  `.GLSL`, `.Compiler`, `.Metal`, and `.ShaderToy` build for both, and all seven test projects run
  against both — **4762 tests green (2381 on each)** — with the compiler's output verified
  byte-identical across them. This is deliberately multi-targeting rather than a move to .NET 10:
  a `net10.0`-only package cannot be referenced from a `net8.0` project, which is what most
  MonoGame/KNI games still target, so bumping would have broken existing consumers. .NET 8 reaching
  end of support in November 2026 no longer strands the packages. `ShadowDusk.Cli` (a dotnet tool,
  which rolls forward onto newer runtimes), `ShadowDusk.MgcbPlugin` (a stub), and
  `ShadowDusk.Wasm` (`net8.0-browser`) stay single-TFM for now.
- The forward-compatibility matrix now covers **every MonoGame release that can load ShadowDusk's
  output, not one anchor version**. One unchanged **v10** build renders **pixel-identically (max
  delta 0) across seven consecutive releases — 3.8.1.263, 3.8.1.303, 3.8.2.1105, 3.8.3, 3.8.4,
  3.8.4.1, and 3.8.5 stable** — 70 renders in total, all within tolerance of the mgfxc goldens
  (`validation/ForwardCompat`). The **floor is now measured rather than assumed**: every stable
  `MonoGame.Framework.DesktopGL` release was probed, and 3.8.0.1641 is the one that rejects our
  output (*"This MGFX effect seems to be for a newer release of MonoGame"* — its loader predates
  MGFX v10), which makes **3.8.1.263** the true floor. Nothing about the product changed to earn
  this; it is the same compiler, the same default options, and the same bytes.
- The opt-in **MGFX v11** output is re-proven against **3.8.5 stable** instead of
  `3.8.5-preview.6`, with the result table unchanged cell for cell (`validation/MonoGameV11`).
- `validation/AndroidGl` moved to `MonoGame.Framework.Android` 3.8.5. Build-verified only: the
  on-device proof was taken on 3.8.4.1 and has not been repeated, which the csproj, the Phase 50
  notes, and the validation matrix all state so the pin is not misread as render evidence.

### Changed

- Dependency currency pass. **Nothing a consumer downloads changed**: the only packages the shipped
  `ShadowDusk.*` libraries reference are `Silk.NET.SPIRV.Cross.Native` (already latest) and
  `Vortice.*` (the DXC pin, deliberately held — see below). Everything updated here is
  test/validation/build-only: `xunit` 2.9.2 → 2.9.3, `xunit.runner.visualstudio` 2.8.2 → 3.1.5,
  `Microsoft.NET.Test.Sdk` 17.11.1 → 18.8.1, `coverlet.collector` 6.0.3 → 10.0.1,
  `FluentAssertions` 6.12.2 → 7.2.2, `docfx` 2.78.3 → 2.78.5.
  No vulnerable or security-deprecated package was found anywhere before or after.
- **Two dependencies are now explicitly capped for licensing reasons, with the reason recorded at
  the pin** so a future currency sweep does not undo it. `FluentAssertions` stays on the **7.x**
  line because 7.2.2 is the last Apache-2.0 release and **8.x** relicensed to an Xceed
  non-commercial community licence that requires payment for commercial use.
  `SixLabors.ImageSharp` stays on **3.1.12** because **4.0.0 refuses to build at all** without a
  paid Six Labors licence key. `Vortice.*` stays at 3.3.4/3.5.0 because that version *is* the
  pinned DXC commit (`e043f4a1`) our macOS/Android/WASM natives are built from; moving it is an
  output-affecting change, not a routine bump.
- **`Apos.Shapes` stays at 0.7.7 — it is an evidence pin, and the reason is now recorded at the
  pin.** A bump to 0.7.12 was attempted and reverted: the Phase 55 shape-gallery proof uses the
  package's own embedded effect as its baseline arm, which is only a valid comparison because
  0.7.7's shader *is* the vendored `apos-shapes-sm6.fx` (upstream `a85a31c`). 0.7.12 pins a
  different upstream commit (`b69bd73`) whose shader differs by ~1150 lines and adds a **fourth
  sampler** (`ArcTex` at `t2`, displacing `BlueNoiseTex` to `t3`) — so the bump would have
  silently pointed the baseline arm at a different shader than the candidate. Re-pinning it means
  vendoring the new upstream revision and re-running that phase's proof.
- The `mgfxc` used to generate the golden corpus is now **pinned and version-checked** rather than
  discovered as "the newest `mgfxc.exe` in the NuGet cache". `tools/compile-fixtures.ps1` resolves
  it from the `dotnet-mgcb` version in `.config/dotnet-tools.json`, invokes it through the `dotnet`
  host so it behaves the same on every OS, and **asserts the MGFX version byte of every file it
  writes**, refusing to overwrite the v10 corpus with a different container version.
  `validation/ReservedWordGl` uses the same pin for its reference-compiler arm. Verified: mgfxc
  3.8.2.1105 and 3.8.4.1 each reproduce all 46 committed goldens byte-for-byte on both OpenGL and
  DirectX_11.
- Documentation correction across `docfx/guides/mgcb-content-pipeline.md`, `docfx/samples/mgcb.md`,
  `docfx/guides/dropin-mgfxc.md`, and `docfx/cli/index.md`: the **"expose ShadowDusk as `mgfxc` on
  `PATH` and MGCB will use it" integration does not work**, and these pages had documented it as the
  shipping path. Measured against `dotnet mgcb` 3.8.2.1105, 3.8.4.1, and 3.8.5 with a real logging
  `mgfxc.exe` first on `PATH`: zero invocations in all three, and a valid `.xnb` produced each time.
  MGCB compiles `.fx` in-process and launches no external effect compiler; MonoGame 3.8.5's new
  code-centric Content Builder has no external-tool seam either. The pages now document the routes
  that do work: invoke the CLI directly and `/copy:` the resulting `.mgfx`, or compile at runtime and
  hand the bytes to `Effect`. Compiling with the ShadowDusk CLI or library is unaffected.

### Fixed

- The out-of-band ShaderToy render-proof driver (`tools/shadertoy2fx/render-proof`) had been dead
  since Phase 47, in two stacked ways. **(1)** That phase promoted the converter and its corpus
  in-solution to `tests/ShadowDusk.ShaderToy.Tests/`, but the driver kept looking under
  `tools/shadertoy2fx/tests/…` and exited with "authored corpus not found" before rendering
  anything; it now probes the current location first and falls back to the legacy one.
  **(2)** With that fixed, it then hung indefinitely. All four of its child-process helpers drained
  the CLI's stdout to EOF *before* reading stderr, which deadlocks as soon as the child writes more
  to stderr than the pipe buffer holds: the child blocks writing, so it never exits, so stdout never
  reaches EOF. Latent until Phase 53 made warnings print by default — a corpus shader that trips
  `SD0402` on a dozen loops now emits well past the buffer. **The compiler was never implicated**
  (the shader that hung it compiles in 0.38 s when run directly); all four call sites now drain both
  pipes concurrently through a shared `ProcessCapture` helper. Nothing caught either failure because
  the driver is deliberately not in `ShadowDusk.slnx`. With both fixed the gate is green again and
  broader than when it last ran: **53/53 shaders match the original GLSL within tolerance, 0
  diverged, 0 errored** (Phase 47 recorded 46/46; the extra seven are corpus growth and they pass
  too, so the converter never regressed).
- `tools/compile-fixtures.ps1` could not regenerate the golden corpus at all, in two independent
  ways. Its mgfxc probe selected the highest version in the NuGet cache, which resolved to
  `dotnet-mgcb-editor-windows` 3.8.4.1's `mgfxc.exe` - a binary that throws
  `Could not load file or assembly 'SharpDX.D3DCompiler'` on every shader because that package ships
  it without its dependency, so a regeneration would have compiled 0 of 46 and reported them all as
  failures. Separately, a bare no-argument run globbed **0 shader files**: a `[string]` parameter
  defaulted to `$null` arrives as an empty string, so the `$ShaderDir ?? (default)` fallback never
  fired and the script only worked when every path was passed explicitly.

## [0.15.1] - 2026-07-28

### Added

- New `SD0216` diagnostic: on the OpenGL target, the emitted GLSL declares a different number of
  sampler uniforms than the effect's sampler table has records, so some `ps_s{k}` would never be
  assigned a texture unit and would silently sample unit 0. It fires for several textures read
  through one shared `SamplerState`, which SPIRV-Cross expands into a combined sampler per
  (texture, sampler) pair while the GL table is keyed on samplers. Previously that shipped a
  table that could not bind; now it is a compile error naming the fix.
- New `SD0006` / `SD0007` diagnostics for the ShaderToy/GLSL front end. Its convert errors and
  warnings were emitted under `SD0010` and `SD0001`, which are already allocated to "effect
  source contains no techniques" and "`#include` file not found" — so a converter failure
  printed a code whose published meaning was unrelated and unactionable. Registered in
  `docs/error-codes.md`.
- `SD0403` now also flags the integer bitwise and modulo operators (`&`, `|`, `^`, `~`, `%`).
  They are reserved below GLSL 1.30 / ES 3.00 by the same specification sentence as the shifts
  it already flagged, and SPIRV-Cross emits them verbatim for signed-`int` operands, where no
  `uint` token appears for the existing unsigned check to catch — so an ordinary
  checkerboard/hash/mask shader shipped with no signal and failed `Effect`-load on Mesa, macOS
  OpenGL, and WebGL1.

### Changed

- Security: the vkd3d-shader loader's dev-convenience `tools/vkd3d/` probe now runs **after**
  the packaged-native probe and is bounded to a directory that actually looks like a ShadowDusk
  checkout. It previously walked to the filesystem root ahead of the NuGet
  `runtimes/<rid>/native` lookup, so on Windows — where the volume root is add-subdirectory
  writable by ordinary users — a planted `C:\tools\vkd3d\libvkd3d-shader-1.dll` would have been
  loaded and executed inside a framework-dependent consumer's process, and any unrelated
  `tools/vkd3d` on the path could silently displace the pinned, hash-verified native.
- `RuntimeProfileDetector.Recommend` now refuses a target no `CapabilityProfile` models instead
  of falling through to the OpenGL profile. Because a set `Profile` overrides
  `CompilerOptions.Target`, `Vulkan` and `DirectX12` silently compiled to a MojoShader-GLSL
  `.mgfx` the consumer's runtime cannot load, and `Metal` bypassed the pipeline's own `SD0200`
  rejection. Setting `Target` directly with `Profile` left null is unaffected and remains the
  supported path for both.
- The release workflow's published-CLI smoke now also compiles `/Profile:DirectX_11` (and runs
  from outside the checkout on Windows too, as it already did elsewhere). It only ever compiled
  `/Profile:OpenGL`, which drives DXC and SPIRV-Cross but never vkd3d-shader — so half the
  single-file bundle's native surface had no guard on any platform.
- `wasm.yml` now fails hard when the DXC→WASM module is missing, instead of warning and silently
  skipping the entire `ShadowDusk.Wasm` build and pack. The module is force-committed, so its
  absence is a repo regression; the old guard let a green run cover nothing.

### Fixed

- **DirectX / DirectX 12: two textures sharing one `SamplerState` now emit one `.mgfx` sampler
  record per texture.** The table was built from the reflected samplers, so the classic
  diffuse+lightmap shape got a single record: MonoGame's `ApplySamplers` only binds the slots it
  is handed, so every texture after the first was never bound and
  `Parameters["Lightmap"].SetValue(tex)` silently did nothing, with exit 0. `mgfxc` keys its own
  DX table on the reflected textures and its golden carries one record per texture; this closes
  the last "sampler slot / baked-state" divergence in the Phase-41 structural matrix.
  The OpenGL table stays keyed on samplers, because there a record must NAME a `ps_s{k}` uniform
  the emitted GLSL actually declares, and SPIRV-Cross declares one combined sampler per
  (texture, sampler) **pair** — a texture-keyed GL table would drop the mirror shape (one texture
  read through two `SamplerState`s, the linear+point idiom), leaving `ps_s1` on texture unit 0.
  The new `SD0216` makes the residual GL case loud instead of silent (see Added).
- `--target-runtime=<name>` (the `=` form) is now parsed. It fell through to the silent
  unknown-flag branch, so `--target-runtime=monogame-gl` compiled with the default profile and
  exit 0: the wrong artifact, with no diagnostic. The space and `:` forms were unaffected, and
  every other long option already accepted all three spellings.
- OpenGL: `trunc()` lowering is now fully parenthesized. `trunc(x)` is a primary expression, so
  splicing the bare product `sign(x) * floor(abs(x))` over it re-associated wherever the
  surrounding operator bound at least as tightly — `1.0 / trunc(x)` became
  `(1.0 / sign(x)) * floor(abs(x))`, valid GLSL with a silently wrong value.
- OpenGL: an omitted HLSL semantic index is now correctly treated as index 0 when naming
  varyings (`: COLOR` ≡ `COLOR0`, `: TEXCOORD` ≡ `TEXCOORD0`, as fxc/mgfxc treat them). DXC
  passes the author's spelling through verbatim, so a SpriteBatch-style pixel-only pass
  declaring `: COLOR` emitted `var_COLOR` instead of `vFrontColor` — a hard link failure against
  MonoGame's built-in SpriteEffect on strict drivers, and garbage on lenient ones.
- OpenGL: the Rule 13 bounded-loop rewrite now also proves the loop bound is **invariant**. It
  derived the ceiling from the bound's initializer without checking that the bound never
  changes, so a body that raised it made the synthesized header exit before the terminal `else`
  finalizer ever ran, leaving its output undefined — issue #160's failure mode, reachable
  through the one property the rewrite's correctness rested on. Those shapes now decline to the
  honest `SD0402` warning.
- `ColorWriteEnable = None;` (and `true` / `false`) now compiles instead of failing `SD0011`.
  `None` is the idiomatic depth-only or stencil-only pass and `mgfxc` accepts all three.
- Render-state and FNA sampler-state values now accept the HLSL float suffix and float-spelled
  integers, via one shared mgfxc-parity numeric parse. `DepthBias = 0.0001f;` hard-failed the
  compile on every target, and `MipMapLodBias = -2.0f;` / `MaxAnisotropy = 4.0;` compiled for
  OpenGL and DirectX but failed for FNA — source `fxc /T fx_2_0` itself accepts.
- `CompilerOptions.WithGraphicsTarget` no longer drops `Defines`, despite documenting that it
  preserves every other setting. The pipeline calls it whenever a `Profile` implies a different
  backend and `Validate`/`ValidateAsync` call it once per target, so
  `--target-runtime monogame-gl /Defines:HIGH_QUALITY=1` compiled with the macro undefined and
  wrote the wrong artifact with exit 0.
- An `#include` resolver's own diagnostic is no longer overwritten with a synthesized `SD0001`
  "cannot find include". A present-but-unreadable header (locked, ACL-denied, or deleted mid-read)
  reported a missing file, making the registered `SD0004` unreachable, and any diagnostic from a
  consumer-supplied `IIncludeResolver` was silently discarded.
- A dropped `#pragma once` line, and a skipped duplicate `#include`, are now blanked rather than
  deleted, so every later line of the enclosing file keeps its `#line`-relative number. DXC
  reported the whole file's diagnostics one line too low, and the CLI, MGCB, and IDE
  jump-to-line all trust that location verbatim.
- DXIL reflection no longer converts a cancellation into an `SD0102` "Reflection failed" error.
  The CLI's watchdog never reported `X0007` "Compilation timed out", and a library consumer's own
  `CancellationToken` stopped behaving per the .NET contract.
- ShaderToy: a parenthesized assignment or comma sequence used as a sub-expression keeps its
  parentheses. The parser drops the source's grouping parens, so the common raymarching idiom
  `if ((d = map(p)) < 0.001)` emitted `if (d = map(p) < 0.001)` — HLSL binds `<` tighter than
  `=`, so `d` received a bool 0/1 instead of the distance and the shader rendered a different
  image with no diagnostic.
- ShaderToy: a `#if` / `#ifdef` / `#ifndef` inside a **skipped** conditional group is no longer
  evaluated, per C11 6.10.1p6 (which the GLSL preprocessor inherits). An expression the evaluator
  could not handle inside a dead `#if 0` branch aborted conversion of a shader every real GLSL
  compiler accepts.
- `samples/mgcb` and `samples/ShaderViewer` can be restored and built again. Both declared
  `Version` on `PackageReference` while inheriting Central Package Management, so `dotnet
  restore` failed `NU1008` and neither sample — including the repo's only demonstration of the
  Tier-1 drop-in delivery shape — could run at all, contrary to their published instructions.
  `ShaderViewer`'s floating `3.8.*` MonoGame reference is also now pinned to 3.8.2.1105.

## [0.15.0] - 2026-07-27

### Added

- CLI: mgfxc's `/Defines:<name=value;...>` flag is now implemented (previously the flag was
  silently ignored and `#ifdef` branches compiled out with exit 0). Library consumers get the
  same via the new `CompilerOptions.Defines` property; the macros ride through both the
  `#define` prepend and the DXC `-D` flags on every backend, including FNA.
- New `SD0403` portability warning: a GLSL-1.30+/ES-3.00-only construct that survived into the
  versionless emitted GL source (`transpose`, `sinh`-family, `isnan`/`isinf`, `texelFetch`,
  bit-casts, `switch`, `uint`, non-square matrices, integer shifts) is now flagged at compile
  time instead of failing at Effect-load only on strict drivers (macOS/Mesa/WebGL1) — the
  class behind issues #149 and #163, made loud up front. Also backstops the round/trunc
  lowerings by flagging any call a rewrite missed.
- FX parser accepts more real-world fxc/mgfxc syntax: `VertexShader = NULL;` /
  `PixelShader = NULL;`, `Texture = NULL;` in `sampler_state` blocks (binds the synthesized
  runtime texture instead of emitting `NULL.Sample(...)`), `technique10` blocks, numeric
  booleans (`AlphaBlendEnable = 1;`), and hex stencil values (`StencilMask = 0xFF;`).
- New diagnostics, all registered in `docs/error-codes.md`: `SD0004` (unreadable include),
  `SD0005` (undetectable input format — was mis-filed under `SD0002`), `SD0028` (Vulkan
  shared-sampler co-location, located at the offending `Sample` call) with `SD0213` as its
  post-compile reflection backstop, `SD0215` (OpenGL sparse sampler registers), `X0009` (CLI
  flag missing its required value — previously silently ignored, compiling with the default),
  and the `SD0214` warning: DirectX12 DXIL compiled on a non-Windows host is unsigned
  (`dxil.dll` signing is Windows-only) and retail D3D12 rejects it at pipeline-state
  creation — previously shipped silently as a per-host output divergence.
- `BlobKind.Dxil`: DXC's SM6 output blobs were mislabeled `Dxbc`/`Spirv` (harmless today,
  a trap for any future kind-keyed dispatch).

### Changed

- CLI diagnostics print the file path exactly as given instead of stripping to the basename,
  so two same-named includes stay distinguishable and IDE/MSBuild jump-to-file works.
- The CLI's `X0099` internal-error catch-all now prints the full exception (type and stack) in
  release builds — it marks a ShadowDusk bug, and the detail is what a bug report needs.
- `.mgfx`/KNIFX annotation counts are now always written as 0, matching mgfxc (MonoGame
  materializes `count` null `EffectAnnotation` slots, so a real count could NRE consumer code,
  and KNI's writer asserts count == 0). Parsed annotations stay in the IR as metadata.

### Fixed

- OpenGL: the issue-#138/#160 bounded-loop rewrite (Rule 13) is now provable for exactly the
  shapes it accepts. An inclusive (`<=`) inner comparison gets a `provenMax + 1` header cap so
  the loop's else-finalizer stays reachable (the #160 dropped-finalizer failure re-created one
  operator over); descending walks, non-unit steps, bounds below the init, and loops whose
  index is read afterward now decline to the honest `SD0402` warning instead of rewriting
  wrong.
- macOS: the released single-file CLI archives could not load their own bundled DXC/vkd3d
  dylibs outside a repo checkout (the per-arch bundle subdirs were never probed in the
  extraction directory). Both loaders now probe `osx-<arch>/` inside every host native-search
  directory, and the release smoke test runs from outside the checkout so CI can catch this
  class.
- Browser/WASM: the SPIRV-Cross shim now sets `RelaxNanChecks` like the desktop transpiler
  (issue #149) — in-browser output for min/max/clamp shaders re-converges with desktop bytes
  and no longer carries the `isnan()` lowering that strict GL front ends reject.
- Vulkan/DirectX 12: a vertex-attribute reflection failure is now a compile-time `SD0101`
  error instead of a silent empty attribute table that crashed at the consumer's first Draw
  with an unattributed `E_INVALIDARG`.
- DirectX 12: `SV_VertexID`/`SV_InstanceID` are no longer minted as phantom TEXCOORD vertex
  attributes (the SPIR-V path already skipped builtins; the DXIL path now matches).
- Vulkan: two textures sampled through one shared `SamplerState` in the same code path now
  fail loudly with a located `SD0028` (the rewriter tracks `#if` branches, so the legal
  cross-branch re-pairing shape still compiles) plus an `SD0213` reflection backstop —
  instead of silently co-locating onto one descriptor and sampling the wrong texture.
- Test infrastructure: the macOS test gates pick the dylib arch by `ProcessArchitecture`
  (not `OSArchitecture`), fixing silently mis-targeted gating under Rosetta 2.
- OpenGL: sparse explicit sampler registers (`register(s3)` with no `s0`) now fail loudly
  (`SD0215`) instead of silently binding the wrong texture units (the `.mgfx` record and the
  emitted GLSL numbered samplers from different sources).
- OpenGL: HLSL semantics are matched case-insensitively in the GL rewriter (`: Position`,
  `: TexCoord0`), matching HLSL's own rules — mixed-case position semantics no longer render
  garbage and mixed-case varyings link correctly; `POSITIONT`-style non-numeric semantic tails
  are a located unsupported-semantic error instead of an unhandled `FormatException`; any
  stage-interface identifier that survives the rewrite is now a loud error instead of invalid
  GLSL that failed only at Effect-load.
- `#include`: Windows-style backslash paths now resolve on Linux/macOS hosts, and an include
  that exists but cannot be read (locked/ACL-denied) returns a located `SD0004` error instead
  of throwing a raw `IOException` through `CompileAsync`.
- Vertex semantics: `PSIZE` (the real D3D9 point-size semantic) now maps to PointSize instead
  of falling through to the TEXCOORD default and colliding with real texture coordinates;
  absurd numeric semantic suffixes no longer throw `OverflowException`.
- DX11/FNA diagnostics: vkd3d-shader's colon-style messages (`file:line:col: E5005: ...`) are
  now parsed into real file/line/column diagnostics instead of collapsing into a single
  line-less `X0000`.
- CLI: a compile wedged inside a native compiler is now hard-terminated by the watchdog with a
  proper `X0007` (previously the documented timeout could never fire on a hung native call and
  MGCB waited forever). A failed sampler-to-texture parameter join now fails the compile via
  the writer's range guard instead of silently pointing the sampler at parameter 0.
- Native loading: the SPIRV-Cross fallback RID map now distinguishes `win-arm64` and
  `linux-arm64` instead of collapsing them to x64; four macOS test gates now key on
  `ProcessArchitecture` instead of the Rosetta-2 `OSArchitecture` trap the production
  loaders already avoid (they silently skipped or mis-targeted coverage on Apple Silicon).
- Determinism: injected `#line` directives and the platform-macro prepend now use `\n` like
  the body they join, so the flattened compiler input no longer differs by build OS
  (previously CRLF-mixed on Windows, visible in debug-mode artifacts via embedded source).
- ShaderToy front-end: `uint`/`uvecN` now map to real HLSL `uint` types with faithful
  unsigned semantics (`>>` zero-fills, `float(x)` is unsigned) and `u`-suffix literals are
  accepted — hash/PRNG shaders no longer silently produce different noise; vector `==`/`!=`
  scalarizes with `all()`/`any()` in every context (not just `if` conditions);
  `for`-conditions get the same paren/vector handling as other conditions; function-like
  macro calls may span lines; `mainSound`/`mainVR` in comments no longer false-reject;
  non-zero `textureLod` is a located convert-time reject instead of doomed generated HLSL;
  nested-block shadowing no longer poisons type inference; locals shadowing emitter intrinsics
  (`frac`, `lerp`, ...) are renamed like reserved words.
- Docs: the validation matrix, gate-script header, `RELEASING.md` gate list, and contributor
  validation page no longer describe the pre-0.14.0 Apos.Shapes golden-arm setup or the
  deleted 13-element harness; `DirectX_12` is listed in the CLI usage/help and error text;
  Android's status wording matches the validation matrix; the 0.14.0 changelog's #149 fix is
  filed under Fixed.
- Docs: `/Defines` and `CompilerOptions.Defines` are documented on the published site (the CLI
  option table previously listed every flag *except* this one and closed with "unknown flags
  are silently ignored", so a consumer would conclude it was unsupported). The `SD0214`
  DirectX 12 constraint is now stated wherever it matters: the DX12 backend page, the
  per-OS caveats guide, the host x target matrix, and a new validation-matrix gap row. Three
  blanket "output bytes are OS-independent" claims (`the-purpose`, `validation-matrix`,
  contributor validation page) gained the DX12 carve-out they now need, since the byte-identity
  manifest covers `DirectX_Vkd3d`/`FNA`/`OpenGL` only. Also: the README pipeline block and CLI
  README list DirectX 12, the `SD0400`-`SD0403` gap row covers the new code, and package tags
  mention `dx12`/`vulkan`.

## [0.14.2] - 2026-07-25

### Fixed

- OpenGL: `trunc()` (which SPIRV-Cross emits when lowering HLSL's truncating `%`/`fmod`) is now
  lowered to `sign(x)*floor(abs(x))`, a GLSL ES 1.00-safe expression. `trunc()` is a GLSL ES 3.00 /
  GL 1.30+ builtin, absent from the versionless legacy dialect ShadowDusk targets; strict GLSL ES
  1.00 front ends (ANGLE on macOS DesktopGL) rejected it as an undeclared identifier where lenient
  desktop drivers did not (Apos.Shapes issue #34).

## [0.14.1] - 2026-07-25

### Added

- Thin-ellipse slice in the OpenGL Apos.Shapes render gate (`validation/VsDriven -- apos`),
  supplementing the existing circle with a needle-thin ellipse compared same-backend against the
  mgfxc GL golden. Supplementary coverage for the issue #160 shape; the authoritative guard is a
  rewriter unit test.

### Fixed

- **OpenGL regression from 0.14.0: thin/eccentric ellipses in iterative SDF shaders rendered from
  garbage distances (issue #160).** The issue #138 GL loop rewrite (`LowerBoundedHeaderlessForLoop`)
  bounded the hoisted `for` header with `< provenMax` instead of `<= provenMax`, so when the
  runtime trip count equalled the loop's ceiling the rewritten loop exited one iteration early and
  skipped the `else` branch that finalizes the solver's result, leaving it read from an
  uninitialized variable. Affected only OpenGL, only shaders whose SPIRV-Cross output takes this
  header-less-loop shape (e.g. Apos.Shapes' `EllipseSDF`); DirectX/DX12/Vulkan were never affected.

## [0.14.0] - 2026-07-24

### Added

- **New target: DirectX 12 (MonoGame `WindowsDX12`), rung-4 proven (Phase 54).**
  `PlatformTarget.DirectX12` compiles to plain SM6 DXIL via DXC and is auto-selected for
  consumers targeting MonoGame's `WindowsDX12` runtime (3.8.5+) — seamless, no flag to pick.
  Render-proven maxd 0 against a real `mgfxc` `DirectX_12` golden, for both the 10-shader
  PS/SpriteBatch corpus and Apos.Shapes/VS-driven custom-vertex-shader effects
  (`validation/BaselineDx12`/`CandidateDx12`/`compare_dx12.py`, `validation/VsDrivenDx12`).
- **Apos.Shapes (Gum's SDF shape renderer) render-proof — DX and GL (Phase 51 A3), then
  expanded to the full shape gallery (Phase 55).** Vulkan already shipped in 0.13.0
  (`validation/VsDrivenVulkan -- apos`, maxd 0). This release closes the remaining single-shape
  DX/GL slices, then supersedes all of them with a 30-cell gallery driven through the REAL
  `Apos.Shapes` NuGet package's `ShapeBatch(GraphicsDevice, Effect?)` effect-injection
  constructor — every `Draw*`/`Fill*`/`Border*` shape kind, not one hand-built circle:
  - **DirectX 11 and DirectX 12.** Both pixel-diffed against a real, locally-generated `mgfxc`
    golden at **maxd 0** across all 30 cells for DX11 (`d3dcompiler_47` oracle and
    `vkd3d-shader`), and 28/30 at maxd 0 for DX12 (2 cells at 1/255, an open, unexplained
    finding — see `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md`). Along the way,
    found that Apos.Shapes' own embedded DX11 effect is compiled by `vkd3d-shader`, not
    `mgfxc` — comparing the `d3dcompiler_47` oracle against it was comparing two independent
    compilers, not a ShadowDusk fidelity gap; fixed by comparing against the real local
    `mgfxc` golden instead.
  - **Vulkan.** Full 30-cell gallery at **maxd 0** against Apos.Shapes' own (DXC-family)
    embedded golden.
  - **OpenGL.** No trustworthy `mgfxc` oracle exists for this shader revision on GL (a
    confirmed MojoShader codegen bug renders every non-textured shape solid black, unrelated
    to ShadowDusk) — the gallery renders through ShadowDusk's compile only, confirming all 30
    shapes produce visible output. The original single-shape GL proof (against an older,
    MojoShader-safe fixture revision) stays in place: **max Δ 2/255** (documented
    transcendental-math GLSL-dialect drift on the shader's OkLab round-trip).

  Wired into `run-windows-render-gates.ps1`. FNA stays permanently excluded (SM3
  instruction-slot ceiling).

### Fixed

- **GL profile emitted `isnan()` into versionless GLSL; rejected on macOS (issue #149).**
  Found while closing the GL slice above: ShadowDusk's own GL candidate for
  `apos-shapes.fx` contained 28 `isnan(` occurrences and no `#version` directive (the real
  mgfxc golden has zero of either). Desktop NVIDIA/AMD/Intel drivers tolerated it; Apple's
  strict GL compiler did not, breaking any GL shader using `min`/`max`/`clamp` on macOS — real
  downstream breakage (Apos.Shapes 0.7.6). Fixed by defaulting SPIRV-Cross's
  `RELAX_NAN_CHECKS` compiler option on for the whole OpenGL profile: zero `isnan(` now, zero
  byte changes anywhere else in the corpus. See `plan/DONE/ISSUE-149-gl-isnan-versionless-glsl.md`.

- **GL loop shapes outside GLSL ES 1.00 Appendix A: both shapes SD0402 covers are now
  auto-fixed where provably safe, not just warned about (issue #138).** GLSL ES 1.00
  (WebGL1 / KNI Reach) requires a loop's increment to live in the for-header and its bound
  to be a compile-time constant; SPIRV-Cross emits two shapes that violate this, and both
  used to fail to *load* there (desktop GL, WebGL2, and KNI HiDef were unaffected) while
  Phase 53 only added a compile-time warning (`SD0402`) for them.
  - **Constant-bounded, empty increment** (`for (int i = 0; i < N; ) { …; i++; continue;
    }`, the index advanced in the body). `MonoGameGlslRewriter` now hoists the increment
    into the header whenever it can prove the rewrite safe (no other write to the index, no
    other `continue` in the body). Confirmed end-to-end on the real vendored
    `Nez/GaussianBlur.fx`: compiling it through the CLI no longer emits `SD0402` at all.
  - **Header-less, runtime-looking trip count** (`for (;;) { if (i < bound) {…} else
    break; }`). Turns out "runtime" doesn't always mean unprovable: when `bound`'s own
    value is traceable to a compile-time-constant expression (a literal, or a ternary
    between two literals), the shader's real ceiling is still knowable even though
    SPIRV-Cross renamed it into a runtime-looking temporary. `MonoGameGlslRewriter` now
    gives the header that real, exact bound and hoists the increment the same way — not an
    approximation, since the derived bound IS the shader's true maximum. Confirmed on the
    real vendored `Apos.Shapes/apos-shapes.fx` (its Newton-iteration SDF): compiling it
    through the CLI no longer emits `SD0402` either, and the existing GL render-proof
    (`validation/VsDriven -- apos`) still matches the `mgfxc` golden at the same max Δ
    2/255 — pixels unchanged, as expected from an exact rewrite.

  A genuinely unfixable case remains: a loop bounded by a plain runtime uniform with no
  compile-time ceiling anywhere in the shader. There's no safe constant to derive there, so
  it keeps warning via `SD0402` — pinned by a fresh regression fixture,
  `examples/Sd0402UniformBoundedLoop.fx`, compiling clean while still warning through the
  real CLI.

## [0.13.0] - 2026-07-23

### Added

- **One-call shader validation: `Validate()` / `ValidateAsync()`.**
  `Console.WriteLine(await compiler.ValidateAsync(fx))` prints everything wrong with a
  shader: every error and every warning, per target, with source locations and the
  underlying compiler's complete verbatim text. Defaults to OpenGL + DirectX, putting the
  classic "compiles for DirectX, fails for OpenGL" report on one screen; an overload takes
  explicit targets (FNA, Vulkan). It runs the real compile pipeline per target, so what
  validates is exactly what compiles, and works on desktop and in the browser alike.
- **`CompiledShader.Warnings`** — successful compiles now carry their non-fatal diagnostics
  instead of discarding them: the underlying compiler's own warnings (DXC, d3dcompiler, and
  vkd3d's message buffer, previously thrown away on success) plus the new GL portability
  findings. The CLI prints them as MGCB-parseable `warning` lines on stderr with exit 0, and
  the ShaderFiddle sample lists them in its diagnostics panel. Warnings raised by an earlier
  technique also survive a later hard failure in the same effect instead of being dropped.
- **GL portability lint (`SD0400`–`SD0402`)**: compile-time warnings for constructs that
  compile fine but are known to fail or misbehave at *runtime* on narrower GL stacks, where
  the only previous signal was the engine's generic draw-time "Shader Compilation Failed"
  with the real driver log hidden in `Debug.WriteLine`.
  `SD0400` ([#141](https://github.com/kaltinril/ShadowDusk/issues/141)): a gradient op inside
  a divergent loop, silently 0.0 on ANGLE Direct3D11 (WebGL in every Windows browser).
  `SD0401`: a pass with no vertex shader whose pixel shader reads interpolants SpriteBatch's
  built-in vertex shader never writes, a strict-driver link failure at the first draw.
  `SD0402` ([#138](https://github.com/kaltinril/ShadowDusk/issues/138)): loop shapes outside
  GLSL ES 1.00 Appendix A that may fail to load on WebGL1 / KNI Reach.
  Warnings only; the lint never rejects a shader.
- **The published documentation now includes the [Diagnostic Codes](https://kaltinril.github.io/ShadowDusk/diagnostics.html)
  registry**, so any code seen in build output can be looked up.
- **A VS-driven Vulkan render gate with a real reference-compiler oracle**
  (`validation/VsDrivenVulkan`, issue #145): pixel-diffs ShadowDusk against the `mgfxc 3.8.5`
  golden on a real MonoGame DesktopVK device at **max Δ 0**, both for a non-identity
  asymmetric transform and for the upstream `Apos.Shapes` reproducer through its own
  13-element vertex layout. This is the first Vulkan comparison against the reference
  compiler; it works because explicit registers keep mgfxc's slot arithmetic in range. Both
  Vulkan gates are now default-ON in `run-windows-render-gates.ps1`.
- **A corpus-wide, device-free Vulkan structural gate** plus **MonoGame's own 17 test effects
  vendored into the corpus** (Ms-PL, tag `v3.8.5`), which carry the reference compiler's
  acceptance set. Every fixture must now either produce a structurally valid Vulkan container
  or fail with a real diagnostic, never an exception. They found two real defects on the day
  they landed.

### Changed

- **Compile errors are verbatim, everywhere, by default.** Diagnostic text that could not be
  parsed into file:line:col entries (DXC SPIR-V codegen failures, disproportionately the
  OpenGL leg) used to collapse into a fixed `X0000: "Shader compilation failed"`, with the
  real text hidden in a field no surface printed. The compiler's own words are now the
  message; the primary error prefers the first error-severity diagnostic, so a leading
  warning can no longer masquerade as the failure, and always carries the complete raw
  output, which the CLI and the ShaderFiddle sample print by default. There is no verbosity
  flag to find, deliberately.
- **The OpenGL/Vulkan leg no longer forces `-WX` (warnings-as-errors).** mgfxc's fxc front
  end never passed `/WX`, so ShadowDusk's GL leg was *stricter than the reference compiler*:
  warning-grade HLSL such as an implicit truncation compiled for DirectX but hard-failed for
  OpenGL, a confirmed "DX works, GL doesn't" divergence class. Those warnings now surface
  through `CompiledShader.Warnings` instead of failing the compile. Output bytes for
  previously-compiling shaders are unchanged.

### Fixed

- **Vulkan: matrices were packed row-major, so every VS-driven effect rendered nothing**
  (issue [#145](https://github.com/kaltinril/ShadowDusk/issues/145)). `-Zpr` was applied to
  every DXC compile, Vulkan included, but MonoGame uploads a `Matrix` parameter for HLSL's
  column-major default, so a Vulkan vertex shader read `mul(pos, worldViewProj)` transposed
  and threw its geometry out of clip space: loads fine, draws without error, renders nothing.
  mgfxc's Vulkan command line carries no `-Zpr`; now neither does ShadowDusk's. OpenGL keeps
  the flag (its rewriter compensates) and DirectX never used DXC.
- **Vulkan: legacy `tex2D` shaders access-violated inside `GraphicsDevice_DrawIndexed`**
  (issue #145). The `Texture2D` synthesized for a legacy `sampler` was excluded from the
  register-pairing rewrite, so the image auto-numbered to binding 0/1 while its sampler
  shifted to 32/33; MonoGame recovers the texture slot as `binding - 32` and indexed its
  texture array at -32. Pairs are now always co-located, explicit `register` indices are
  reserved so an auto-assigned pair cannot collide with them, and every texture
  dimensionality is covered (`TextureCube`/`Texture3D` had the same crash shape).
- **Vertex shaders now get the stage-agnostic GL body lowerings** (issue
  [#137](https://github.com/kaltinril/ShadowDusk/issues/137)). The rewriter returned early
  for the vertex stage, so a VS using `round()` shipped `roundEven()` (absent from GLSL ES
  1.00) and a VS with an inlined early-return helper shipped the raw `do { … } while(false)`
  Appendix A forbids, both silent Effect-load failures on Mesa/WebGL1 with compile exit 0.
- **Derivative-using fragment shaders ship `#extension GL_OES_standard_derivatives : enable`**
  as the first line of the emitted GL source, where mgfxc puts it (issue
  [#139](https://github.com/kaltinril/ShadowDusk/issues/139)). Strict ESSL 1.00 compilers
  reject derivative builtins without it. The scan covers `fwidth` too.
- **A `round()` nested inside another `round()`'s argument is now fully lowered** (issue
  [#140](https://github.com/kaltinril/ShadowDusk/issues/140)); the inner call previously
  survived as `roundEven()`, the exact load failure the lowering exists to prevent.
- **Vulkan: a texture/sampler pair could still be handed a binding another texture already
  held.** Explicit `register` indices were only honoured by the auto-assign path, so a pair
  that *inherited* its index from an explicitly-registered half could collide with a
  different pair — two textures on one binding, the invalid descriptor layout that
  access-violates in MonoGame's descriptor writer. Pair co-location now outranks a
  disagreeing explicit register (the runtime binds by slot index, not by the source's
  register number), and explicitly-registered textures are assigned first so the guard cannot
  be defeated by declaration order. Two textures still share an index when they name the same
  sampler, which is the same-sampler-in-two-`#if`-branches shape where only one branch
  survives the compile. No shader in the test corpus changes output: every fixture is
  byte-identical through the rewrite on all four targets.
- **Vulkan: `Gather*` calls, `Texture2DArray`/`TextureCubeArray`/`Texture2DMS` declarations,
  and `SamplerComparisonState` are now seen by the pairing pass.** Being invisible to the
  scan meant those pairs were left at separate bindings and their explicit registers were
  never reserved. `Texture2DArray` and `SamplerComparisonState` appear in MonoGame's own
  vendored test effects (both already carry agreeing registers, so no corpus output changes);
  the rest are covered defensively.
- **A `while` loop following any block no longer loses its `SD0402` warning.** The do-while
  tail check accepted *any* `while` preceded by `}`, so `if (…) { … } while (…) { … }` was
  misread as a do-while's trailing clause and the finding silently dropped.
- **The compiler's diagnostic text no longer prints twice.** The "the message already says
  this" check compared the raw blob and the message without normalizing line endings or
  blank lines, so it never matched on multi-line diagnostics — exactly the unparseable-text
  path the verbatim work was built for. Multi-line messages are also indented under their
  parseable first line now, so compiler-controlled text can no longer start a stderr line and
  be misread by a build-log parser as a separate diagnostic.
- **`Validate` reports name the file for line-less findings**, matching the CLI.
- **Warnings without a line number now name their source file.** The GL portability warnings
  are derived from emitted GLSL and carry no line mapping, so the CLI printed a bare
  `warning SD0401: …` with no way to tell which effect produced it in a build compiling many.
- **Anonymous techniques (`technique { pass { … } }`) are accepted** — legal FX that mgfxc
  compiles, and what 8 of MonoGame's 17 own test effects use, previously rejected with
  `FX0001: Expected technique name`. Relatedly, the FNA writer no longer rejects an empty
  technique name, which `d3dcompiler_47` at `fx_2_0` compiles cleanly.
- **A native process crash on the FNA path is now a diagnostic.** `SamplerComparisonState`
  made vkd3d's SM1 lowering hit "Unreachable code reached" and took the whole process down
  with an access violation; it is rejected up front with the new `FX0013` instead.
- **Vulkan container faithfulness:** vertex shaders now carry the attribute table mgfxc emits
  (recovered from the SPIR-V input semantics), a combined image-sampler sets both the texture
  and sampler slot masks, and the shipped SPIR-V no longer carries `-fspv-reflect`'s
  `SPV_GOOGLE_*` extensions.

## [0.12.1] - 2026-07-21

### Added

- Vendored the derivative-based-antialiasing revision of Apos.Shapes' shader
  (`apos-shapes-aa.fx`, upstream `d507a73`) into the third-party corpus (GL + DX compile
  pins), plus a structural pin that fails if any gradient op ever again lands inside a
  loop with a divergent exit in emitted GL GLSL (issue #136).
- `validation/AngleDerivativeProbe`: a self-asserting headless-browser gate that renders
  the emitted fragment control-flow shapes on real ANGLE Direct3D11 (the WebGL backend of
  every Windows browser) and fails if ShadowDusk's shape loses derivatives — the first
  gate that sees the browser backend the issue-#136 bug lives on. Wired into
  `run-windows-render-gates.ps1` as default-ON, alongside the real-KNI OpenGL desktop and
  VS-driven drivers (previously manual-only, now impossible to forget for GL-affecting
  changes).

### Changed

### Fixed

- **`dFdx`/`dFdy` no longer return 0 in Windows browsers (ANGLE D3D11)** — issue #136,
  reported by Jean-David Moisan (Apostolique). ANGLE's D3D11 backend silently zeroes every
  gradient op inside a loop with a divergent exit (a conditional `break` or `discard`), and
  the issue-#107 for-loop lowering of SPIRV-Cross's entry-point `do { … } while(false);`
  wrapper put the whole fragment body inside exactly such a loop, disabling derivative-based
  antialiasing (Apos.Shapes SDF shapes) on KNI BlazorGL with no compile or link error. The
  GLSL rewriter now **unwraps** the wrapper when it can prove it safe: plain brace block,
  each loop-level `break` → duplicated tail + `return;` — straight-line `main` with real
  early exits, valid in every GLSL profile including ESSL 1.00, and the same shape
  mgfxc/MojoShader emits. The unwrap recurses through the plain blocks it creates, so an
  **inlined helper that both early-returns and takes a derivative** (its wrapper nests
  inside the entry wrapper) unwraps too — the shape the fix's adversarial review found
  still poisoned. A tail whose duplication would move a gradient op or implicit-LOD
  sample into divergent flow (undefined per GLSL §8.13.1) is never unwrapped; those and
  all other unprovable shapes keep the WebGL1-safe for-loop fallback. Desktop GL/KNI
  output remains render-equivalent (Windows render gates + KNI desktop GL/VS-driven
  drivers + Linux CI GL gates).

## [0.12.0] - 2026-07-18

The headline: **a Vulkan backend**. MonoGame 3.8.5 (stable 2026-07-15) ships `DesktopVK`, a
native Vulkan desktop platform — and ShadowDusk now compiles for it: the same faithful
pipeline, one DXC compile straight to SPIR-V, wrapped in the real MonoGame Vulkan `.mgfx`
container. Additive and seamless like every backend before it: existing OpenGL / DirectX /
FNA / WASM output is byte-identical, and the MGFX v10 default is unchanged.

### Added

- **Vulkan output target** (`PlatformTarget.Vulkan` / CLI `/Profile:Vulkan`) — Phase 32,
  contributed via PR #126 (Victor Chelaru / vchelaru). HLSL compiles through the pinned DXC
  frontend directly to SPIR-V (`vs_6_0`/`ps_6_0`) and is emitted in MonoGame 3.8.5's own
  Vulkan `.mgfx` container (profile byte 80, v11-shaped shader records, the SPIR-V wrapped in
  the descriptor-layout header MonoGame's native Vulkan pipeline reads), with reflection from
  the SPIR-V itself. Texture+sampler pairs bind as single combined image-sampler descriptors,
  matching MonoGame's runtime. **Render-proven on real hardware:** the shader corpus loads and
  renders correctly (10/10) in real MonoGame 3.8.5 `DesktopVK`
  (`validation/CandidateVulkan`; local gate `./validation/run-windows-render-gates.ps1
  -IncludeVulkan`). A pixel-diff against `mgfxc`'s own Vulkan output is blocked upstream —
  that output currently crashes in real DesktopVK due to a confirmed MonoGame `SlotOffset`
  bug. MonoGame-only: KNI ships no Vulkan platform (a KNI+Vulkan request fails loudly with
  `SD0025`).

### Changed

- Documentation: **DirectX 12 (MonoGame 3.8.5 `WindowsDX12`) is now recorded as an explicit
  not-yet-supported target** across the support matrix and site pages (planned,
  research-first — see `plan/PHASE-52-monogame-3.8.5-support.md`). The validation matrix's
  accumulated update trail moved to a compact bottom-of-page history, consumer-facing pages
  were trimmed of internal status noise, and the `ShadowDusk.Compiler` / `ShadowDusk.Cli`
  package READMEs now list the Vulkan target.

### Fixed

- **OpenGL codegen fidelity: `pow(x, 2.0)` strength-reduced to a multiply** (issue
  [#127](https://github.com/kaltinril/ShadowDusk/issues/127)). GLSL leaves `pow` undefined for a
  negative base (drivers lowering it to `exp2(y*log2(x))` return NaN), while fxc constant-folds
  `pow(x, 2)` into a multiply — so HLSL that squares a possibly-negative value via `pow` (e.g.
  Apos.Shapes' `LinearGradient` squaring normalized-direction components) was well-defined through
  mgfxc but a latent driver-dependent hazard through ShadowDusk's GL output. `MonoGameGlslRewriter`
  Rule 10 now emits the multiply (exact, and the reference compiler's semantics); simple-operand
  bases only, so no expression is ever duplicated unsafely.
- **OpenGL codegen fidelity: `1.0 / (a / b)` folded to `b / a`** (issue
  [#127](https://github.com/kaltinril/ShadowDusk/issues/127)). SPIRV-Cross preserves the HLSL
  reciprocal-of-quotient shape literally (fxc folds it), costing an extra rounding step at every
  such site (all 8 `SmoothDiscontinuity` call sites in apos-shapes.fx). Rule 11 emits the single
  correctly-rounded division; value-equivalent across the zero/infinity edge cases, applied only
  when the division is provably the group's root operator. Both rules are pinned by rewriter unit
  tests and an end-to-end regression test compiling the vendored `apos-shapes.fx` on GL
  (`AposShapes_OpenGl_EmitsNoPowSquare_NoReciprocalOfQuotient_Issue127`); the full suite, the
  Windows DX render gates, and every GL render gate (corpus vs mgfxc, VS-driven at 1/255, KNI
  desktop GL, state/cbuffer/texture/reserved-word) stayed green.

## [0.11.0] - 2026-06-28

Android joins the supported runtime-compile platforms: ShadowDusk now compiles `.fx` -> `.mgfx`
**in memory, at runtime, on an Android device** (the "shader fiddle on a phone" shape), through the
same faithful HLSL -> DXC -> SPIR-V -> SPIRV-Cross -> GLSL -> MGFX pipeline used everywhere else, via
the seamless `new EffectCompiler()`. This is additive and seamless: all existing OpenGL / DirectX /
FNA / WASM output is byte-identical, the MGFX v10 default is unchanged, and desktop/WASM consumers are
unaffected (the Android natives are RID-scoped, so they are never deployed into a non-Android app).

### Added

- **On-device runtime shader compilation on Android (arm64-v8a)** (Phase 50). A .NET-for-Android
  MonoGame app can take a user's shader **text**, compile it to a MonoGame `.mgfx` on the device, and
  load it into a live `Effect`, with no host precompile and no content pipeline. The two native pieces
  the OpenGL pipeline needs (`libdxcompiler.so` and `libspirv-cross.so`, built for `android-arm64`)
  now ship inside `ShadowDusk.HLSL` and `ShadowDusk.GLSL` under `runtimes/android-arm64/native/`, so
  "add the package, call the API" is the entire setup, exactly as on desktop. Proven on-device by
  compiling and rendering a pixel shader on an Android emulator.
- A consumer guide, **"On-Device (Android Runtime Compile)"**, in the published documentation site,
  with the integration recipe (`new EffectCompiler().CompileAsync(fx, new CompilerOptions { Target =
  PlatformTarget.OpenGL })` -> `result.Value.Data` -> `new Effect(GraphicsDevice, mgfx)`) and notes for
  embedding the compiler in an Android shader fiddle.

### Changed

- `EffectCompiler` **auto-selects the pure-managed `SpirvReflector` on Android** (the native
  DXIL-oracle reflection path is unavailable there). The selection is automatic and produces the same
  reflection result, so consumers do nothing and desktop behavior is unchanged.

### Fixed

## [0.10.0] - 2026-06-28

Fidelity fixes driven by real shipping shaders from the Gum / Apos.Shapes ecosystem (requested by
vchelaru, Gum's author): several real-world effects that previously failed now compile and render on
the targets they ship for, across FNA, OpenGL, and KNI WebGL. Every change is additive: the seamless
MGFX v10 default and all existing OpenGL / DirectX / FNA output stay byte-identical (pinned by the
cross-host byte-identity gate), and the new behavior only enables previously-failing shaders.

### Added

- Real, MIT-licensed **Apos.Shapes and Gum** `.fx` shaders vendored under
  `tests/fixtures/shaders/third-party/` as **compile-level** regression inputs (Phase 49), each
  classified by an actual GL / DX / FNA compile probe. These guard the compiler against the exact
  shaders the Gum / Apos.Shapes ecosystem ships; provenance and per-shader target classification are
  in `docs/test-shader-corpus.md`.

### Changed

- **`__KNIFX__` is now defined for a KNIFX-targeted compile** (`--target-runtime kni-knifx` /
  `CapabilityProfile.KniGL_4_02`), matching KNI's own effect compiler, so a shader that branches on
  `#ifdef __KNIFX__` (e.g. Apos.Shapes selecting its SM4 profile) takes the correct branch. The
  seamless universal MGFX default deliberately does **not** define it, so default output is unchanged.

### Fixed

- **FNA: macro-defined techniques are now recovered** (Phase 41 GAP-1). An effect whose techniques
  come only from a `TECHNIQUE(...)` `#define` (the stock-MonoGame / Gum idiom) previously failed
  `SD0010` on FNA because techniques were counted before macro expansion. The zero-technique recovery
  (preprocess then re-parse) now extends to the FNA path, so **7 MonoGame stock effects compile on
  FNA** (SpriteEffect, AlphaTestEffect, DualTextureEffect, and the Penumbra hull/light/shadow/texture
  effects). Effects that still fail now do so for honest shader-model reasons (register pressure /
  sub-SM2 profiles), not technique-blindness. Only effects that returned zero bytes before are
  affected, so existing FNA output is byte-identical.
- **OpenGL: multi-render-target pixel shaders now compile** (Phase 41 GAP-2). A deferred-rendering
  effect whose pixel shader returns a struct with `COLOR0` / `COLOR1` output semantics (e.g. Nez
  `DeferredSprite.fx`) failed on the GL target with `Semantic COLOR is invalid`. A GL-only struct
  output `COLOR` to `SV_Target` rewrite (applied only to the OpenGL compile, so DirectX bytes stay
  identical) fixes it, and true multi-target slot 0 now emits `gl_FragData[0]` instead of
  `gl_FragColor` so writing one target no longer corrupts the others.
- **KNI WebGL: a one-shot `do { ... } while(false)` loop no longer breaks loading**
  ([#107](https://github.com/kaltinril/ShadowDusk/issues/107)). A helper with a nested `if` that early
  returns made SPIRV-Cross emit a `do/while(false)`, which compiles and loads on desktop GL but is not
  guaranteed by GLSL ES 1.00, so the effect **failed to load in KNI WebGL / Reach**. The GL rewriter
  now lowers it to an equivalent WebGL-safe bounded `for` loop (pixels unchanged); render-proven in
  real KNI WebGL on both Reach (WebGL1) and HiDef (WebGL2). DirectX / FNA bytecode is unchanged.
- **KNIFX now loads and renders on KNI WebGL and mobile GLES** (the opt-in KNIFX container). A
  KNIFX-targeted compile previously advertised only the desktop OpenGL backend, so KNI WebGL rejected
  it ("profile is not compatible with the graphics backend 'WebGL'"). The KNIFX writer now emits a
  multi-backend GL-family directory (desktop OpenGL keeps its faithful body byte-for-byte; GLES and
  WebGL share a body KNI's runtime converts to GL ES at load, the same proven path that already loads
  MGFX v10 in KNI WebGL). One `.knifx` now loads on every KNI GL host; desktop KNI render is unchanged.

## [0.9.0] - 2026-06-22

Robustness pass on the **ShaderToy GLSL -> `.fx` converter** (`ShadowDusk.ShaderToy`): several real
ShadowToy shaders that previously failed to convert now compile, and the cases that genuinely cannot be
faithfully translated reject with a clear, located message instead of an opaque downstream parser error.
The converter is a pure-managed front end that emits `.fx`; the core compile pipeline and all existing
`.mgfx` / `.fxb` output are unchanged (zero golden churn).

### Added

- **`ShadowDusk.ShaderToy` is now a published NuGet package** (the seventh `ShadowDusk.*` package).
  It is the standalone, **pure-managed, zero-native** ShaderToy/GLSL → `.fx` converter
  (`ShaderToyConverter.Convert`), so anyone can convert ShaderToy shaders **in-process** (e.g. an
  XNA/KNI web shader fiddle or an in-app importer) without the CLI. It is **optional and separate**:
  `ShadowDusk.Compiler` does not depend on it, so existing consumers are unaffected. (The converter
  also continues to ship embedded in the `ShadowDuskCLI` tool's `.glsl` input.)
- Regression fixtures and unit tests for every case below: authored corpus shaders
  (`matrix_from_vector`, `mirror_happy_accident`, `infinite_cube_starfield`, `abstract_waterfall`,
  `chimera_final_pass`) auto-converted and golden-compared, plus reject fixtures
  (`unsigned_int_literal`, `texture_cubemap_coord`) and targeted unit suites
  (`MatrixConstructorTests`, `ForLoopScopingTests`, `ConstGlobalTests`, `TextureLodTests`).

### Changed

- **Clearer, located rejects for constructs outside the float-based subset.** An unsigned-integer
  literal (`123U`, which drives uint/uvec bit-hash arithmetic) now rejects **at the literal** instead of
  the stray `U` surfacing later as a confusing "expected `)`" parse error, and `texture(sampler, vec3)`
  (a cubemap sample) rejects with a message naming the cubemap rather than truncating silently.

### Fixed

- **`mat2` constructed from a `vec4` now converts** (e.g. `mat2(someVec4)`): the four-component vector is
  flattened into the `float2x2` instead of failing to emit.
- **Reused `for`-loop induction variables now convert** under HLSL's legacy for-scope rule. GLSL scopes a
  `for`-init declaration to its loop; legacy HLSL leaks it to the enclosing scope, so a second
  `for (int i = ...)` in the same function tripped `-Wfor-redefinition` under `-WX`. The converter now
  scope-renames reused induction variables per loop (first occurrence keeps its name), so multi-loop
  raymarchers convert.
- **Multi-declarator `const` globals now parse** (e.g. `const float A = 1., B = 2.;`). The additional
  declarators were previously swallowed by the comma operator, surfacing as a misleading "Undeclared
  identifier" error on later use.
- **A base-level `textureLod(s, uv, 0.)` lowers to a plain `tex2D`.** The legacy `tex2Dlod` intrinsic
  does not rewrite to a modern Texture method on the OpenGL/DirectX targets (`FX0012`); since the
  single-pass harness binds each iChannelN without mipmaps, mip 0 is the only level, so the two are
  equivalent and the shader now compiles on every backend. A non-zero LOD keeps the explicit `tex2Dlod`.

## [0.8.0] - 2026-06-18

### Added
- `SECURITY.md` at the repo root: the project's trust model (compiling a `.fx` runs code; the
  shader author and the compiler-runner are the same developer, so the library is a build-time/in-app
  tool, not a sandbox), the consumer's isolation responsibility for any service that compiles
  third-party `.fx`, the supply-chain integrity model for the natives we ship, and how to report a
  vulnerability.
- KNI **DirectX** render validation (`validation/KniWinFormsDX`): ShadowDusk's DX output renders
  pixel-equivalent to the `mgfxc` DirectX golden in real KNI v4.02 WinForms.DX11.
- `validation/run-windows-render-gates.ps1`: one command that runs the Windows-GPU render proofs CI
  cannot (DirectX corpus, vertex-texture-fetch, KNI-DX, and FNA under `-IncludeFna`), required before a
  release.
- CI render gates for the in-process OpenGL validation drivers
  (`.github/workflows/validation-render.yml`, Mesa llvmpipe under xvfb).
- 15 real, MIT-licensed **Nez** `.fx` shaders vendored under
  `tests/fixtures/shaders/third-party/Nez/` as **compile-level** regression inputs (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) / Phase 45), plus author-original
  regression fixtures for the pre-parser fixes below. These guard the FX9 pre-parser against
  real-world shaders; they are not new render-equivalence proofs (provenance + per-shader target
  classification in `docs/test-shader-corpus.md`).
- `validation/ReservedWordGl`: a GL render driver that render-proves the reserved-word uniform
  binding fix below pixel-identical to `mgfxc`.
- `tests/fixtures/shaders/examples/Issue106Repro.fx`: the verbatim shader from the issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) report (a helper using `==`, `<=`, a
  nested `if`, and an early `return`), pinned as a permanent regression fixture and compile-asserted
  on OpenGL, DirectX, and FNA.

### Changed
- The in-process MGFX/KNIFX/FNA golden-comparison and cross-host byte-identity tests now run on the
  fast PR lane (previously only on the heavier integration lane), so a writer/transpiler/render-state
  regression that still compiles is caught on every pull request, not just at release time.
- The KNI WebGL render proof was refreshed on the current KNI v4.02 runtime, and the browser harness now
  stamps the KNI runtime version into every generated results file.
- Documentation accuracy pass: corrected Vulkan's status (it compiles to a SPIR-V `.mgfx` but has no
  shipping runtime to render-validate against, i.e. experimental/unvalidated, not "future"), the
  fixture-corpus count, and assorted status/cross-reference drift surfaced by a full project review.

### Fixed
- **FX pre-parser: a whole class of valid shaders that previously failed now compiles** (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106) / Phase 45). The global-parameter
  annotation heuristic matched the bare token shape `Identifier Identifier <` anywhere in the stream,
  so a **relational, shift, or ternary expression** in a shader body (e.g.
  `return value <= 0.5f ? 0 : 1;`) was misread as an FX annotation and failed with `FX0001`. The path
  is now gated on the genuine annotation-block shape, and several related pre-parser bugs are fixed in
  the same pass: modern `sampler_state` + `.Sample`, `ColorWriteEnable = Red | Green | Blue` masks,
  legacy `texture < ... >` annotations, a texture variable named `Texture`, a vertex shader returning
  `: COLOR`, array-indexed relational/ternary assignment, and sampler register/annotation variants
  (B1-B9). Purely additive: it only enables previously-failing shaders, so existing output stays
  byte-identical (pinned by the cross-host byte-identity gate).
- **OpenGL: a uniform whose name collides with a GLSL reserved word now binds correctly** (issue
  [#106](https://github.com/kaltinril/ShadowDusk/issues/106), B10). `float noise;` is valid HLSL that
  `mgfxc`/`fxc` accept, but SPIRV-Cross renames the colliding uniform (`noise` to `_noise`) for legal
  GLSL, so the GL cbuffer/parameter join — matching by name — missed it and failed loudly with
  `SD0012`. The join now falls back to an offset bridge that recovers the parameter by byte offset
  (keeping its original name), render-proven pixel-identical to `mgfxc` in real MonoGame GL. The
  primary name match is unchanged and runs first, so every shader that compiles today is byte-for-byte
  identical; this only enables the reserved-word case (re-enabling the real Nez `Noise.fx` on GL).
- Closed soft-skip-as-green holes in the validation drivers/tests: `validation/TextureBreadthValidation`
  now honors `SHADOWDUSK_REQUIRE_GL` (a missing GL device fails loudly instead of reporting success),
  and the `mgfxc` cross-validation test fails (rather than silently passing) when a known-good fixture
  stops compiling.

## [0.7.0] - 2026-06-14

The seamless default is unchanged: MGFX **v10** is still the default container and output is
**byte-identical to 0.6.0** for every existing call (`CrossHostByteIdentity` stays green). This
release adds an opt-in **capability-profile** API for naming a full output target (graphics backend
+ container/version) in one value, a matching CLI flag, and runtime detection. It also validates the
KNIFX optimized-matrix (`columnsActual`) fidelity against KNI's own compiler (KNIFXC).

### Added

- **Capability-profile output-target selector.** New `CapabilityProfile` (a **closed set** of
  render-proven (runtime, format) contracts: `MonoGameGL_3_8_2`, `MonoGameDX_SM5`, `MonoGameGL_3_8_5`
  for MGFX v11, `KniGL_4_02` for KNIFX v11, and `Fna_Fx2`) plus `CompilerOptions.Profile`. A profile
  fully specifies the output target, **including the graphics backend**, so setting `Profile` alone
  picks both format and backend; it overrides `Target` / `Container` / `MgfxVersion`. The default
  (`null`) reproduces today's behavior exactly.
- **CLI `--target-runtime <name>`** (also `/target-runtime:<name>`): selects the output target by
  name (`monogame-gl`, `monogame-dx`, `monogame-gl-v11`, `kni-knifx`, `fna`), mapping to a
  `CapabilityProfile`. Overrides `/Profile` and `--mgfx-version`. Unknown values fail with `X0008`.
- **Runtime detection.** `RuntimeProfileDetector` classifies the loaded XNA framework assembly
  (MonoGame / KNI / FNA) and recommends a proven `CapabilityProfile` to pass to
  `CompilerOptions.Profile`. Conservative: it returns the universally-loadable MGFX v10 (fx_2_0 for
  FNA) and never silently upgrades a consumer to a newer container.
- **Shader-feature capability axis.** `ShaderFeatures` + `ShaderFeatureSupport`, which **rejects
  (`SD0201`)** any GL feature no shipping runtime consumes yet, so an unsupported feature can never
  silently compile into bytes no runtime can load. (No shipping runtime supports these today.)

### Changed

- A `CapabilityProfile` **implies its graphics backend**, so a set `CompilerOptions.Profile`
  determines the backend (overriding `Target`); the runtime-detection advisory composes with this so
  one recommended profile picks both format and backend.
- **KNIFX `columnsActual` validated against a KNIFXC golden.** Full matrices match KNI's own compiler
  exactly; the partially-used-matrix case (ShadowDusk emits `columnsActual = columns`) is a
  render-safe, storage-only divergence, not a correctness difference. KNIFX output is unchanged.

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

[Unreleased]: https://github.com/kaltinril/ShadowDusk/compare/v0.20.0...HEAD
[0.20.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.19.0...v0.20.0
[0.19.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.18.0...v0.19.0
[0.18.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.17.0...v0.18.0
[0.17.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.16.0...v0.17.0
[0.16.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.15.1...v0.16.0
[0.15.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.15.0...v0.15.1
[0.15.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.2...v0.15.0
[0.14.2]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.1...v0.14.2
[0.14.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.14.0...v0.14.1
[0.14.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.13.0...v0.14.0
[0.13.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.12.1...v0.13.0
[0.12.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.12.0...v0.12.1
[0.12.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.10.0...v0.11.0
[0.10.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.8.0...v0.9.0
[0.8.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/kaltinril/ShadowDusk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/kaltinril/ShadowDusk/releases/tag/v0.1.0
