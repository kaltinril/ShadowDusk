# ShadowDusk.HLSL

The HLSL front half of the **ShadowDusk** cross-platform shader compiler: the FX9 technique/pass pre-parser and preprocessor, DXC integration (HLSL → SPIR-V), and the DXBC/D3D9 backends — cross-platform **vkd3d-shader** (the default; its pinned natives for win-x64/linux-x64/osx-x64/osx-arm64 ship inside this package) plus the opt-in Windows `d3dcompiler_47` oracle, and ShadowDusk's own pinned macOS DXC dylibs.

This is a building block consumed by the other `ShadowDusk.*` packages. **To compile shaders, install [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler)** (in-process library), [ShadowDusk.Cli](https://www.nuget.org/packages/ShadowDusk.Cli) (`dotnet tool`), or [ShadowDusk.Wasm](https://www.nuget.org/packages/ShadowDusk.Wasm) (in-browser) instead — each pulls this package transitively.

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
