# ShadowDusk.GLSL

The GLSL back half of the **ShadowDusk** cross-platform shader compiler: SPIR-V → GLSL transpilation via the SPIRV-Cross C API, plus the MojoShader-dialect rewriter that makes the output load and render in MonoGame's and KNI's OpenGL/WebGL runtimes exactly like `mgfxc`'s (uniform naming, `posFixup`, legacy `attribute`/`varying` I/O, Reach + HiDef in one artifact).

This is a building block consumed by the other `ShadowDusk.*` packages. **To compile shaders, install [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler)** (in-process library), [ShadowDusk.Cli](https://www.nuget.org/packages/ShadowDusk.Cli) (`dotnet tool`), or [ShadowDusk.Wasm](https://www.nuget.org/packages/ShadowDusk.Wasm) (in-browser) instead — each pulls this package transitively.

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
