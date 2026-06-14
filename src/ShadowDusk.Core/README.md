# ShadowDusk.Core

Core types and contracts for the **ShadowDusk** cross-platform HLSL shader compiler: `IShaderCompiler`, `CompilerOptions` / `PlatformTarget` (plus the opt-in `EffectContainer` selector and `MgfxVersion`), the `Result<T, TError>` / `ShaderError` diagnostics model, the binary writers (MGFX v10/v11, KNIFX v11, and fx_2_0), and pure-managed SPIR-V/DXBC reflection.

This is a building block consumed by the other `ShadowDusk.*` packages. **To compile shaders, install [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler)** (in-process library), [ShadowDusk.Cli](https://www.nuget.org/packages/ShadowDusk.Cli) (`dotnet tool`), or [ShadowDusk.Wasm](https://www.nuget.org/packages/ShadowDusk.Wasm) (in-browser) instead — each pulls this package transitively.

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
