# ShadowDusk.GLSL

A supporting package for **ShadowDusk**. You normally don't install it directly. To compile shaders, install one of [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler) (in-process library), [ShadowDusk.Cli](https://www.nuget.org/packages/ShadowDusk.Cli) (`dotnet tool`), or [ShadowDusk.Wasm](https://www.nuget.org/packages/ShadowDusk.Wasm) (in-browser) instead — each pulls this package in automatically.

It's the GLSL back half of the pipeline: SPIR-V to GLSL transpilation via the SPIRV-Cross C API, plus the rewriter that makes the output load and render in MonoGame's and KNI's OpenGL/WebGL runtimes exactly like mgfxc's (uniform naming, `posFixup`, legacy `attribute`/`varying` I/O, Reach + HiDef in one artifact).

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
