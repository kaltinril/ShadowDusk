# Project Facts

Source of truth for statements about the project. One short fact per line. Update in place; delete facts that become false.

## What ShadowDusk is

- ShadowDusk is a cross-platform HLSL shader compiler for MonoGame, KNI, and FNA, delivered as a drop-in `mgfxc` replacement.
- The product is the in-memory library (`IShaderCompiler.CompileAsync(fx) -> .mgfx bytes`); the CLI and the MGCB plugin are delivery shapes of it.
- The browser/WASM shader-fiddle is only a sample of reach, never the product; sample work must not redefine the goal.
- "Works" means the real-engine render proof (the evidence ladder in CLAUDE.md, rung 4), not a green test suite.
- "Same as `mgfxc`" means behaviorally equivalent and `Effect`-loadable, never byte-identical; byte-identity with `mgfxc` is an explicit non-goal.
- Determinism means ShadowDusk-vs-itself: same version + same source + same target = same bytes, on every host, for the targets the cross-host byte-identity manifest covers (OpenGL, DirectX_Vkd3d, FNA); DirectX 12 is the one carve-out, because its DXIL signing is Windows-only.
- Repository is github.com/kaltinril/ShadowDusk; the owner/operator is Jeremy Swartwood (GitHub `kaltinril`).
- Ships as seven NuGet packages (`ShadowDusk.{Core,HLSL,GLSL,ShaderToy,Compiler,Cli,Wasm}`) at one shared version, plus the `ShadowDuskCLI` dotnet tool.
- `ShadowDusk.Metal` and `ShadowDusk.MgcbPlugin` are stubs, not shipped packages.

## The drop-in contract

- Drop-in means the whole `mgfxc` surface: same CLI flags, same `.mgfx` output format, same exit codes, and stderr diagnostics in a format MGCB can parse.
- A game using the MonoGame Content Pipeline requires zero code changes to switch; ShadowDusk works via MGCB's `ExternalTool` config or a PATH-based override of `mgfxc`.
- The CLI and the WASM library produce byte-identical output; only the invocation differs, and `IShaderCompiler` abstracts both.
- `dotnet publish -r <rid> --self-contained` must produce a working single-file CLI that bundles every native dependency.

## Packaging and WASM interop

- WASM interop uses `[JSImport]`/`[JSExport]` to reach WASM-compiled DXC, SPIRV-Cross, and vkd3d.
- `ShadowDusk.Wasm` is a self-registering Razor SDK package: a consumer needs only a `PackageReference`, no wiring.
- Central Package Management is on; third-party versions live in `Directory.Packages.props`, the ShadowDusk version in `Directory.Build.props`.

## Vocabulary

- Effect pass: a single vertex+pixel shader pair compiled to a `PassBlob`.
- Effect technique: one or more named passes; maps to MonoGame's `Technique`.
- Platform blob: the platform-specific compiled binary (DXBC, SPIR-V, DXIL, or MSL source).
- ShaderIR: ShadowDusk's internal representation between parsed HLSL and platform emission.
- Rung 4 / render-proven: the real-engine render proof. The full four-rung ladder is defined in CLAUDE.md.

## Targets, and what constrains them

> **Proof status is NOT recorded here.** How far each target is proven changes as evidence advances, so it has one home: `docs/validation-matrix.md` (with a cold-start summary table in CLAUDE.md). This section records only what constrains the targets, which does not change when a gate goes green.

- Shader model and output format per target: OpenGL SM3 GLSL, DirectX 11 SM5 DXBC, DirectX 12 SM6 DXIL, Vulkan SPIR-V, FNA SM1-3 fx_2_0.
- The OpenGL feature ceiling is the consumer runtime's, not ours: MonoGame and KNI both still use MojoShader for GL, so an SD0210 rejection (vertex texture fetch, texture arrays) is a runtime constraint to respect, never a compiler gap to fix. The DirectX target has no such cap and emits the SM4/5 features the GL path refuses.
- KNI ships no Vulkan platform and no DirectX 12 platform, which is why those two targets are MonoGame-only.
- DirectX 12 output is host-dependent: DXIL validation and signing run through the Windows-only `dxil.dll`, so a non-Windows DX12 compile emits unsigned DXIL (warned `SD0214`) that retail D3D12 rejects at pipeline-state creation. DX12 is therefore excluded from the cross-host byte-identity manifest.
- KNI loads the same `.mgfx` as MonoGame; the only divergence is its HiDef profile, whose runtime converter rewrites `mgfxc`'s `#define ps_oC0 gl_FragColor` form and nothing else.
- FNA is proven against a different oracle: `fxc.exe /T fx_2_0`, not `mgfxc`.
- Reach and WebGL1 cannot do 3D textures or fragment LOD at all; that is a platform wall, not a gap to close.

## Pins, natives, and supply chain

- MonoGame is pinned at 3.8.2.1105 and the default output format is MGFX v10; MGFX v11 and KNIFX exist as additive opt-ins.
- vkd3d-shader is pinned at 1.17 for all four desktop RIDs plus the WASM build.
- `tools/restore.{ps1,sh}` downloads the natives from fixed GitHub Release tags (`native-vkd3d-1.17`, `native-dxc-1.7.2212.40`, `native-vkd3d-wasm-1.17`) and verifies SHA-256 against pins embedded in the scripts.
- CI caches restored natives by hash, so a clean runner is only pack-ready after the restore step.
- Packing natives into the NuGet is `Exists(...)`-conditioned, and `release.yml` fails red if a packed nupkg is missing any native or the third-party notices.
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
- CI's browser smoke renders on SwiftShader, which is structurally blind to ANGLE-D3D11 behavior such as the issue-#136 gradient poisoning. (This is *why* the ANGLE probe in the local gate cannot move to CI.)
- The integration lane runs automatically on pushes to `main` but on PRs only when the `run-integration` label is applied.
- Slow `ShadowDusk.Integration.Tests` runs are environmental (antivirus scanning cold native binaries), not algorithmic; `--settings ShadowDusk.runsettings` gives a 5-minute session backstop.
- Publishing is driven by the `NUGET_API_KEY` repository secret; no credentials live in the repo.
- The Phase-41 structural-divergence appendix stays outside `plan/DONE/` because `dotnet test` regenerates it on every run; diffing it across a change is the cheapest oracle for an unintended `.mgfx` structural change.
- `SamplerReflection.TextureName` is never assigned by any reflection backend; it is always null, so any texture/sampler pairing must key on bind slot or fall back to the sole shared sampler.
- SPIRV-Cross's `build_combined_image_samplers` declares one GLSL sampler per (texture, sampler) PAIR, and `MonoGameGlslRewriter` numbers those `ps_s{k}` in declaration order; neither the reflected texture list nor the reflected sampler list is that pair list, and no layer above the transpiler currently exposes it.
- `docfx/images/pipeline-overview.svg` is regenerated by `tools/render-diagrams.{ps1,sh}`, which downloads and SHA-256-verifies a pinned PlantUML jar into `tools/plantuml/`; the site embeds the SVG, never the `.puml`.

## Consumers and stakeholders

- External users drive a large share of the backlog: vchelaru (Gum, FlatRedBall, XnaFiddle) and Apostolique (Apos.Shapes) file issues that become phases.
- Gum's and Apos.Shapes' real shaders are vendored verbatim (both MIT) as compile-level regression fixtures.
- Apos.Shapes 0.7.7 serves as both harness and golden for the shape-gallery render proof, via its effect-injection `ShapeBatch` constructor.
- The security trust model is stated in `SECURITY.md`: the shader author and the compiler-runner are the same trust domain.

## Known upstream bugs we live with (not ours)

- MonoGame's Vulkan runtime has a `SlotOffset` byte-wrap bug that crashes on `mgfxc`'s own auto-numbered output, blocking the Vulkan pixel diff for auto-numbered resources (effects using explicit registers already diff pixel-for-pixel).
- `mgfxc` + MonoGame GL mis-handles statically-partially-read uniform arrays (MojoShader register compaction); we deliberately emit the correct full layout instead.
- `mgfxc`'s own GL compile of the current Apos.Shapes revision renders solid black, so GL has no trustworthy golden for that shader.

## Known gaps in our own reach

- The Linux and macOS Vortice DXC builds reject `tex3D`, `tex2Dlod`, and `tex2Dgrad` family intrinsics that the Windows DXC accepts; `Phase34TextureBreadthTests` is Windows-only for that reason.
- DirectX 12 renders 2 of the 30 Apos.Shapes gallery cells (`DrawCircle`, `FillArc`) at maxd 1 against the real `mgfxc` `DirectX_12` golden; not root-caused (independent-DXC-build drift on the shader's transcendental math is the unconfirmed leading hypothesis).
- GL macro-defined techniques (Phase 41 GAP-1, GL half) remain blocked on DXC legacy-SM2 codegen.
- OpenGL cannot yet emit a sampler record per (texture, sampler) pair, so several textures read through one shared `SamplerState` are rejected with `SD0216` instead of compiled; `mgfxc` compiles that shape correctly, so this is our fidelity gap, not a reference-compiler bug. DirectX and DirectX 12 already handle it. Tracked as Phase 51 A7.
- `spirv-cross.wasm` exports exactly 11 `spvc_*` functions and is an out-of-band emscripten build, so any new SPIRV-Cross C API used on the desktop path (e.g. `spvc_compiler_get_combined_image_samplers`) would break the CLI-vs-WASM byte-identity promise until that module is rebuilt.
