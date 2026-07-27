# Project Facts

Source of truth for statements about the project. One short fact per line. Update in place; delete facts that become false.

## What ShadowDusk is

- ShadowDusk is a cross-platform HLSL shader compiler for MonoGame, KNI, and FNA, delivered as a drop-in `mgfxc` replacement.
- The product is the in-memory library (`IShaderCompiler.CompileAsync(fx) -> .mgfx bytes`); the CLI and the MGCB plugin are delivery shapes of it.
- The browser/WASM shader-fiddle is only a sample of reach, never the product; sample work must not redefine the goal.
- "Works" means rung 4 of the evidence ladder: our `.mgfx` loads in a real MonoGame/KNI `Effect` and renders like `mgfxc`'s. Our own tests and our own renderer are proxies, not the bar.
- The four evidence rungs are: compiles -> structurally well-formed -> matches the reference compiler's output in our own renderer -> renders correctly in the real runtime.
- "Same as `mgfxc`" means behaviorally equivalent and `Effect`-loadable, never byte-identical; byte-identity with `mgfxc` is an explicit non-goal.
- Determinism means ShadowDusk-vs-itself: same version + same source + same target = same bytes, on every host.
- Repository is github.com/kaltinril/ShadowDusk; the owner/operator is Jeremy Swartwood (GitHub `kaltinril`).
- Ships as seven NuGet packages (`ShadowDusk.{Core,HLSL,GLSL,ShaderToy,Compiler,Cli,Wasm}`) at one shared version, plus the `ShadowDuskCLI` dotnet tool.
- Version at last update of this file: 0.14.2; the project is pre-1.0.
- `ShadowDusk.Metal` and `ShadowDusk.MgcbPlugin` are stubs, not shipped packages.

## The drop-in contract

- Drop-in means the whole `mgfxc` surface: same CLI flags, same `.mgfx` output format, same exit codes, and stderr diagnostics in a format MGCB can parse.
- A game using the MonoGame Content Pipeline requires zero code changes to switch; ShadowDusk works via MGCB's `ExternalTool` config or a PATH-based override of `mgfxc`.
- Loading in `Effect` is necessary but not sufficient; it must also render identically.
- Two delivery shapes cover the same output: the CLI for build-time use and the WASM library for in-browser runtime compilation. Output bytes are identical; only the invocation differs, and `IShaderCompiler` abstracts both.
- `dotnet publish -r <rid> --self-contained` must produce a working single-file CLI that bundles every native dependency.

## Stack and conventions the code assumes

- C# 12 / .NET 8 LTS; xUnit + FluentAssertions; `TreatWarningsAsErrors` is on.
- Native interop: `Vortice.Dxc` for DXC, `Silk.NET` P/Invoke for the SPIRV-Cross C API, vkd3d-shader plus `d3dcompiler_47` for DXBC.
- WASM interop uses `[JSImport]`/`[JSExport]` to reach WASM-compiled DXC, SPIRV-Cross, and vkd3d.
- `ShadowDusk.Wasm` is a self-registering Razor SDK package: a consumer needs only a `PackageReference`, no wiring.
- Central Package Management is on; third-party versions live in `Directory.Packages.props`, the ShadowDusk version in `Directory.Build.props`.

## Vocabulary

- Effect pass: a single vertex+pixel shader pair compiled to a `PassBlob`.
- Effect technique: one or more named passes; maps to MonoGame's `Technique`.
- Platform blob: the platform-specific compiled binary (DXBC, SPIR-V, DXIL, or MSL source).
- ShaderIR: ShadowDusk's internal representation between parsed HLSL and platform emission.
- Rung 4: the real-runtime render proof; the only rung that proves the promise.

## Targets and how far each is proven

- Rung-4 render-proven: OpenGL (SM3), DirectX 11 (SM5 DXBC), Vulkan (SPIR-V), DirectX 12 (DXIL), FNA (fx_2_0 `.fxb`).
- Metal is unimplemented and parked; `PlatformTarget.Metal` is hard-rejected with SD0200.
- Android on-device compile is proven on a real API-34 emulator and shipped in 0.11.0, with a productionization tail open (CI rebuild, x86_64 natives, on-device pixel diff).
- Vulkan uses MGFX profile byte 80 and DX12 profile byte 2; both are MonoGame-only because KNI ships neither platform (`SD0025` guards it).
- FNA's reference compiler is `fxc.exe /T fx_2_0`, not `mgfxc`; its evidence ladder is separate but mirrors the MonoGame one.
- FNA output is a raw `.fxb`, the one non-`.mgfx` output; both are unwrapped, never `.xnb`.
- The OpenGL feature ceiling is the consumer runtime's, not ours: MonoGame and KNI both still use MojoShader for GL, so SD0210 rejections (vertex texture fetch, texture arrays) are a runtime constraint, not a compiler limitation.
- The DirectX target has no such cap and emits the SM4/5 features the GL path refuses.
- KNI uses the same `.mgfx` format as MonoGame; the only divergence is HiDef, which needs `mgfxc`'s `#define ps_oC0 gl_FragColor` form for KNI's ES 3.00 converter to rewrite.
- The ShaderToy frontend (`ShadowDusk.ShaderToy`) is pure-managed with zero native dependencies and is not in the `ShadowDusk.Compiler` dependency graph.

## Pins, natives, and supply chain

- MonoGame is pinned at 3.8.2.1105 and the default output format is MGFX v10; MGFX v11 and KNIFX exist as additive opt-ins.
- vkd3d-shader is pinned at 1.17 for all four desktop RIDs plus the WASM build.
- Native binaries are never committed; `tools/restore.{ps1,sh}` downloads them from fixed GitHub Release tags (`native-vkd3d-1.17`, `native-dxc-1.7.2212.40`, `native-vkd3d-wasm-1.17`) and verifies SHA-256 against pins embedded in the scripts.
- CI caches restored natives by hash; a clean CI runner is only pack-ready after the restore step; NuGet packing of natives is `Exists(...)`-conditioned, and `release.yml` fails red if a packed nupkg is missing any native or the third-party notices.
- Desktop DXC comes from the `Vortice.Dxc` NuGet, except macOS, which uses our own pinned `libdxcompiler.dylib` (DXC `e043f4a1`, matching Vortice 3.3.4).
- `d3dcompiler_47.dll` and `fxc.exe` are Windows-only test oracles and never ship to consumers.
- DXC cannot produce DXBC at all: it only emits SM6 DXIL, and `ID3D11Device::CreateVertexShader` rejects DXIL unconditionally.
- glslang is not used anywhere in the pipeline; MojoShader is a consumer-runtime component, not one of ours.
- vkd3d-shader is LGPL-2.1+ and ships as a dynamically-linked native binary with a third-party notices file packed into the nupkg root.
- The SPIRV-Cross binding is raw P/Invoke against the C API rather than `Veldrid.SPIRV`; the rationale was never recorded.
- `Vortice.Dxc` was chosen as the DXC wrapper because it bundles prebuilt natives for all platforms; whether other wrappers were evaluated was never recorded.

## Where things run and get proven

- `validation/*` drivers are deliberately outside `ShadowDusk.slnx`, so `dotnet test` never runs them.
- The in-process OpenGL render gates run in CI on Linux via Mesa llvmpipe (`validation-render.yml`); the KNI WebGL smoke runs in `wasm.yml`.
- DX11, DX12, FNA, KNI-DirectX, real-KNI-desktop-GL, Vulkan, and the ANGLE-D3D11 probe have no headless CI driver; the developer's Windows + GPU machine is the only gate.
- CI's browser smoke renders on SwiftShader, which is structurally blind to ANGLE-D3D11 behavior such as the issue-#136 gradient poisoning.
- The integration lane runs automatically on pushes to `main` but on PRs only when the `run-integration` label is applied.
- Slow `ShadowDusk.Integration.Tests` runs are environmental (antivirus scanning cold native binaries), not algorithmic; `--settings ShadowDusk.runsettings` gives a 5-minute session backstop.
- Publishing is driven by the `NUGET_API_KEY` repository secret; no credentials live in the repo.
- The Phase-41 structural-divergence appendix stays outside `plan/DONE/` because `dotnet test` regenerates it on every run.

## Consumers and stakeholders

- External users drive a large share of the backlog: vchelaru (Gum, FlatRedBall, XnaFiddle) and Apostolique (Apos.Shapes) file issues that become phases.
- Gum's and Apos.Shapes' real shaders are vendored verbatim (both MIT) as compile-level regression fixtures.
- Apos.Shapes 0.7.7 serves as both harness and golden for the shape-gallery render proof, via its effect-injection `ShapeBatch` constructor.
- The security trust model is stated in `SECURITY.md`: the shader author and the compiler-runner are the same trust domain.

## Known upstream bugs we live with (not ours)

- MonoGame's Vulkan runtime has a `SlotOffset` byte-wrap bug that crashes on `mgfxc`'s own auto-numbered output, blocking the Vulkan pixel diff for auto-numbered resources (explicit-register effects already diff at maxd 0).
- `mgfxc` + MonoGame GL mis-handles statically-partially-read uniform arrays (MojoShader register compaction); we deliberately emit the correct full layout instead.
- `mgfxc`'s own GL compile of the current Apos.Shapes revision renders solid black, so GL has no trustworthy golden for that shader.

## Known gaps in our own reach

- The Linux and macOS Vortice DXC builds reject `tex3D`, `tex2Dlod`, and `tex2Dgrad` family intrinsics that the Windows DXC accepts; `Phase34TextureBreadthTests` is Windows-only for that reason.
- The DX11 `d3dcompiler_47` oracle arm lacks `mgfxc`'s `ShaderFlags.OptimizationLevel3`, giving maxd 1 on some gallery cells.
- GL macro-defined techniques (Phase 41 GAP-1, GL half) remain blocked on DXC legacy-SM2 codegen.
- Reach/WebGL1 cannot do 3D textures or fragment LOD; that is a platform wall, documented, not a gap to close.
