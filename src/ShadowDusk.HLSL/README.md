# ShadowDusk.HLSL

A supporting package for **ShadowDusk**. You normally don't install it directly. To compile shaders, install one of [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler) (in-process library), [ShadowDusk.Cli](https://www.nuget.org/packages/ShadowDusk.Cli) (`dotnet tool`), or [ShadowDusk.Wasm](https://www.nuget.org/packages/ShadowDusk.Wasm) (in-browser) instead — each pulls this package in automatically.

It's the HLSL front half of the pipeline: the FX9 technique/pass pre-parser and preprocessor, DXC integration (HLSL to SPIR-V), and the DirectX/FNA bytecode backends — the cross-platform **vkd3d-shader** (the default; its pinned natives for win-x64/linux-x64/osx-x64/osx-arm64 ship inside this package), the opt-in Windows `d3dcompiler_47`, and ShadowDusk's own pinned macOS DXC builds.

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
