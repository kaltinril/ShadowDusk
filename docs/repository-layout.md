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
│   ├── ShadowDusk.MgcbPlugin/    # MGCB content-processor plugin — STUB/scaffold (Phase 29). Since the Tier-1
│   │                             #   PATH override was measured not to fire (MGCB compiles in-process; Phase 52
│   │                             #   Area E), this is the only route to native MGCB integration.
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
│       ├── shaders/                    # Canonical .fx test shaders (144 .fx total + 7 .fxh headers):
│       │                               #   62 in the root + examples/ (43) + shadertoy/ (1, the pinned
│       │                               #   ShaderToyRouteGl golden) + third-party/ (38): Nez (15, MIT),
│       │                               #   MonoGame (17, Ms-PL — the reference compiler's own acceptance set),
│       │                               #   Gum (3), Apos.Shapes (3)
│       └── golden/                     # Reference outputs: mgfxc .mgfx (DirectX_11/, DirectX_12/, OpenGL/, Vulkan/) + fxc fx_2_0 .fxb (FNA/) + byte-identity/
├── samples/
│   ├── ShaderFiddle.Web/               # KNI Blazor-WASM in-browser fiddle (sample of reach)
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
│                                  #   what remains is the standalone PoC CLI, the MonoGame Runtime helper,
│                                  #   the interactive sample, and the fidelity/gallery render-proof driver.
├── validation/                    # Rung-4 render-proof console drivers (NOT in ShadowDusk.slnx, not run by `dotnet test`):
│                                  #   GL (VsDriven, StateFidelity, CbufferModel, ReservedWordGl, SamplerPairsGl,
│                                  #     DeferredSpriteMrtGl (the only driver that binds 2 render targets),
│                                  #     ShaderToyRouteGl (the `.glsl` frontend route), …), DX (VsDrivenDx,
│                                  #   DxModernFeatures, …), DX12 (BaselineDx12, CandidateDx12, VsDrivenDx12
│                                  #     + compare_dx12.py), FNA (FnaValidation), KNI (KniDesktopGL, KniWinFormsDX, KniVsDriven),
│                                  #   Vulkan (BaselineVulkan, CandidateVulkan, VsDrivenVulkan
│                                  #     + compare_vulkan.py/decode_mgfx_vulkan.py),
│                                  #   Android (AndroidGl), v11 (MonoGameV11), browser-ANGLE (AngleDerivativeProbe)
│                                  #   + the compare_*.py oracles. See docs/validation-matrix.md §6.
├── docs/                          # Architecture / reference docs (the-purpose, validation-matrix, references/, HOWTO-WASM-KNI, …)
└── CLAUDE.md
```
