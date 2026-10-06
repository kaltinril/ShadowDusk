# In-Browser Compilation (KNI / Blazor WASM)

The **same faithful pipeline** runs inside .NET WebAssembly via the `ShadowDusk.Wasm` package (`WasmShaderCompiler : IShaderCompiler`), so a KNI / Blazor WebAssembly game can compile `.fx` → `.mgfx` **at runtime, in the browser, with no server roundtrip** and no native toolchain on the user's machine.

The in-browser frontend is the **faithful pinned DirectXShaderCompiler compiled to WebAssembly** (matching the desktop `Vortice.Dxc` commit), so its SPIR-V is byte-identical to the desktop pipeline — one faithful compiler everywhere, **no substitute frontend**. (The older Slang-WASM frontend in the sample — Slang *in place of DXC* — is dead, sample-only reference and never runs; note this is a different thing from ShadowDusk's support for HLSL-compatible `.slang` as an *input language*, a pure text transform upstream of the pipeline that works wherever the pipeline does, this host included.) See [WASM In-Browser Frontend](../architecture/wasm-frontend.md) for the architecture.

Beyond the OpenGL/WebGL path this guide covers, the same package also compiles **DirectX `.mgfx` and FNA `.fxb` in the browser** (the pinned `vkd3d-shader` compiled to WASM) — as *export* targets, byte-identical to the desktop output; a browser cannot render DXBC/D3D9 bytecode. See [DirectX & FNA in the Browser](../backends/directx-in-wasm.md).

**Full Slang input in the browser** (genuine Slang: `import`, generics, `interface`s) works too, through `ShadowDusk.Slang.Wasm`'s `WasmSlangCompiler` (issue #257). A browser cannot spawn `slangc`, so the same pinned `slangc` v2026.14.1 runs inside the page as WebAssembly, receives the identical command line the desktop route passes, and hands its HLSL to the faithful DXC/vkd3d modules above. Slang is only the *input* here: it never compiles HLSL and never stands in for DXC. Measured: the module's output is byte-identical to native `slangc`'s for every corpus shader, entry point and target, and a real headless browser compiles the Slang corpus to the same OpenGL and DirectX bytes as the desktop. It is not published as a NuGet package yet ([Phase 68](https://github.com/kaltinril/ShadowDusk/blob/main/plan/PHASE-68-slang-in-process-everywhere.md)); the sample uses it from source.

The [ShaderFiddle.Web sample](../samples/shaderfiddle-web.md) is a working demonstration of this reach — itself only a **sample**, not the product.

The complete walkthrough (setup, package wiring, KNI specifics, gotchas) is maintained in the repository and reproduced below as the single source of truth:

[!INCLUDE [HOWTO-WASM-KNI](../../docs/HOWTO-WASM-KNI.md)]
