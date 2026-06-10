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
│   ├── ShadowDusk.Cli/           # CLI entry-point (dotnet tool `ShadowDuskCLI`)
│   ├── ShadowDusk.MgcbPlugin/    # MGCB content-processor plugin — STUB/scaffold (Tier-1 PATH override is the shipping MGCB path)
│   └── ShadowDusk.Wasm/          # In-browser WASM IShaderCompiler (WasmShaderCompiler); [JSImport] to WASM-compiled DXC + SPIRV-Cross
├── tests/
│   ├── ShadowDusk.Core.Tests/
│   ├── ShadowDusk.HLSL.Tests/
│   ├── ShadowDusk.GLSL.Tests/
│   ├── ShadowDusk.Compiler.Tests/
│   ├── ShadowDusk.Integration.Tests/   # Compile real .fx files end-to-end
│   ├── ShadowDusk.ImageTests/          # Offscreen-render image regression
│   ├── ShadowDusk.BrowserTests/        # Headless KNI WebGL render validation (Playwright)
│   └── fixtures/
│       ├── shaders/                    # Canonical .fx test shaders (52 .fx + 5 .fxh headers)
│       └── golden/                     # Reference outputs: .mgfx (DirectX_11/, OpenGL/) + fxc fx_2_0 .fxb (FNA/)
├── samples/
│   ├── ShaderFiddle.Web/               # KNI Blazor-WASM in-browser fiddle (sample of reach)
│   ├── ShaderViewer/                   # Desktop shader viewer
│   └── mgcb/                           # MGCB content-pipeline sample
├── tools/                         # Vendored / downloaded native binaries (restored, not committed)
│   ├── dxc/                       # unused — desktop DXC comes from Vortice.Dxc NuGet
│   ├── spirv-cross/               # libspirv-cross-c-shared (.dll/.so/.dylib)
│   └── vkd3d/                     # vkd3d-shader native (cross-platform DXBC backend)
├── docs/                          # Architecture docs, research, HOWTO-WASM-KNI
└── CLAUDE.md
```
