# ShadowDusk.Core

A supporting package for **ShadowDusk**. You normally don't install it directly. To compile shaders, install one of [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler) (in-process library), [ShadowDusk.Cli](https://www.nuget.org/packages/ShadowDusk.Cli) (`dotnet tool`), or [ShadowDusk.Wasm](https://www.nuget.org/packages/ShadowDusk.Wasm) (in-browser) instead — each pulls this package in automatically.

It holds the core types and contracts shared across the compiler: `IShaderCompiler`, `CompilerOptions` / `PlatformTarget` (plus the `EffectContainer` selector and `MgfxVersion`), the `CapabilityProfile` output-target selector and the `RuntimeProfileDetector` helper, the `Result<T, TError>` / `ShaderError` diagnostics model, the binary writers (MGFX v10/v11, KNIFX v11, and fx_2_0), and pure-managed SPIR-V/DXBC reflection.

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
