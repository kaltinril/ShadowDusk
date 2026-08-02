# Repository Layout

> Full source-tree map. Linked from [`CLAUDE.md`](../CLAUDE.md). Read this when you need to
> know where a project/file lives; the actual directories under `src/`, `tests/`, `samples/`,
> and `tools/` are the source of truth if this drifts.

```
ShadowDusk/
├── src/
│   ├── ShadowDusk.Core/          # Core types & contracts: IShaderCompiler, Result<T,E>, ShaderError,
│   │                             #   CompilerOptions, CompiledShader, ShaderIR, MGFX writer, reflection
│   │                             #   (SpirvReflector, CtabReader), FNA fx_2_0 writer (Fx2EffectWriter)
│   ├── ShadowDusk.HLSL/          # FX9 pre-parser, preprocessor, DXC integration, reflection,
│   │                             #   vkd3d-shader + d3dcompiler_47 DXBC backends
│   ├── ShadowDusk.GLSL/          # SPIR-V → GLSL via SPIRV-Cross + MonoGameGlslRewriter (MojoShader dialect)
│   ├── ShadowDusk.Metal/         # SPIR-V → MSL via SPIRV-Cross — STUB, not yet implemented
│   ├── ShadowDusk.Compiler/      # EffectCompiler : IShaderCompiler + pipeline orchestration —
│   │                             #   the consumer-facing product NuGet (the in-memory library)
│   ├── ShadowDusk.Cli/           # CLI entry-point (dotnet tool `ShadowDuskCLI`); also accepts ShaderToy/GLSL input
│   ├── ShadowDusk.ShaderToy/     # Pure-managed ShaderToy/GLSL → .fx front-end (ShaderToyConverter.Convert); ZERO
│   │                             #   native + ZERO MonoGame dep; additive, upstream of the pipeline. PUBLISHED standalone NuGet (0.9.0).
│   ├── ShadowDusk.MgcbPlugin/    # MGCB content-processor plugin (Phase 29): ShadowDuskEffectImporter +
│   │                             #   ShadowDuskEffectProcessor, discovered by MGCB's /reference:. The native MGCB
│   │                             #   route, since the PATH override was measured not to fire (MGCB compiles
│   │                             #   in-process; Phase 52 Area E). PUBLISHED as a TOOLS-ONLY NuGet (everything
│   │                             #   under tools/net8.0/any/, no lib/ — MGCB resolves a plugin's deps from its own
│   │                             #   directory). The ONLY src/ project allowed a MonoGame reference, and only a
│   │                             #   compile-only, PrivateAssets=all one.
│   └── ShadowDusk.Wasm/          # In-browser WASM IShaderCompiler (WasmShaderCompiler); [JSImport] to WASM-compiled DXC + SPIRV-Cross
├── tests/
│   ├── ShadowDusk.Core.Tests/
│   ├── ShadowDusk.HLSL.Tests/
│   ├── ShadowDusk.GLSL.Tests/
│   ├── ShadowDusk.Compiler.Tests/
│   ├── ShadowDusk.ShaderToy.Tests/     # ShaderToy→.fx converter unit/trap/golden/reject suite (pure managed)
│   ├── ShadowDusk.Integration.Tests/   # Compile real .fx files end-to-end (+ CLI .glsl-input integration)
│   ├── ShadowDusk.ImageTests/          # Offscreen-render image regression
│   ├── ShadowDusk.BrowserTests/        # Headless KNI WebGL render validation (Playwright)
│   └── fixtures/
│       ├── shaders/                    # Canonical .fx test shaders (151 .fx total + 7 .fxh headers):
│       │                               #   62 in the root + examples/ (50) + shadertoy/ (1, the pinned
│       │                               #   ShaderToyRoute{Gl,Dx} fixture) + third-party/ (38): Nez (15, MIT),
│       │                               #   MonoGame (17, Ms-PL — the reference compiler's own acceptance set),
│       │                               #   Gum (3), Apos.Shapes (3)
│       └── golden/                     # Reference outputs: mgfxc .mgfx (DirectX_11/, DirectX_12/, OpenGL/, Vulkan/) + fxc fx_2_0 .fxb (FNA/) + byte-identity/
├── samples/
│   ├── ShaderFiddle.Web/               # KNI Blazor-WASM in-browser fiddle (sample of reach)
│   ├── ShaderToyViewer/                # Interactive ShaderToy viewer: runtime convert -> in-memory
│   │                                   #   compile -> new Effect -> render, + hot-reload and a
│   │                                   #   headless `--smoke` self-test. Runtime/ShaderToyEffect.cs
│   │                                   #   is the MonoGame helper's ONLY home (never src/).
│   ├── ShaderViewer/                   # Desktop shader viewer
│   └── mgcb/                           # MGCB content-pipeline sample
├── tools/                         # Vendored / downloaded native binaries (restored, not committed)
│   ├── dxc/                       # unused — desktop DXC comes from Vortice.Dxc NuGet
│   ├── spirv-cross/               # libspirv-cross-c-shared (.dll/.so/.dylib)
│   ├── vkd3d/                     # vkd3d-shader native (cross-platform DXBC backend)
│   ├── vkd3d-wasm/                # vkd3d-shader compiled to WASM (browser DXBC + FNA export)
│   ├── plantuml/                  # PlantUML jar for regenerating docs/*.puml diagrams
│   └── shadertoy2fx/             # ShaderToy experiment SHELLS (out-of-band, NOT in ShadowDusk.slnx):
│                                  #   the converter LIBRARY + tests were promoted to src/+tests/ (Phase 47);
│                                  #   the runtime helper + interactive sample moved to
│                                  #   samples/ShaderToyViewer/ (Phase 51 A4). What remains is the
│                                  #   standalone PoC CLI (the only `--multipass` batch entry point)
│                                  #   and the fidelity/gallery render-proof driver.
├── validation/                    # Rung-4 render-proof console drivers (NOT in ShadowDusk.slnx, not run by `dotnet test`):
│                                  #   GL (VsDriven, StateFidelity, CbufferModel, ReservedWordGl, SamplerPairsGl,
│                                  #     SamplerRegisterOrderGl (issue #189: the only GL driver that leaves unit 0
│                                  #       to SpriteBatch instead of binding via effect.Parameters, which is what
│                                  #       makes sampler SLOT allocation observable),
│                                  #     DeferredSpriteMrtGl (the only driver that binds 2 render targets),
│                                  #     ShaderToyRouteGl (the `.glsl` frontend route), …), DX (VsDrivenDx,
│                                  #   DxModernFeatures, ShaderToyRouteDx (that route's DirectX arm), …),
│                                  #   DX12 (BaselineDx12, CandidateDx12, VsDrivenDx12
│                                  #     + compare_dx12.py), FNA (FnaValidation), KNI (KniDesktopGL, KniWinFormsDX, KniVsDriven),
│                                  #   Vulkan (BaselineVulkan, CandidateVulkan, VsDrivenVulkan
│                                  #     + compare_vulkan.py/decode_mgfx_vulkan.py),
│                                  #   Android (AndroidGl), v11 (MonoGameV11), browser-ANGLE (AngleDerivativeProbe)
│                                  #   + the compare_*.py oracles. See docs/validation-matrix.md §6.
│                                  #   Two entries here are NOT render proofs:
│                                  #     MgcbPlugin runs a real `dotnet mgcb` content build through the MGCB
│                                  #       content-processor plugin and diffs the .xnb payload against the
│                                  #       CLI's bytes (Phase 29).
│                                  #     DumpPreprocessedHlsl is a no-GPU DIAGNOSTIC: it dumps the exact HLSL
│                                  #       the pipeline hands DXC so a divergence can be replayed through a
│                                  #       different DXC build and attributed.
├── docs/                          # Architecture / reference docs (the-purpose, validation-matrix, references/, HOWTO-WASM-KNI, …)
└── CLAUDE.md
```
