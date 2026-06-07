# In-Browser Compilation (KNI / Blazor WASM)

The **same faithful pipeline** runs inside .NET WebAssembly via the `ShadowDusk.Wasm` package (`WasmShaderCompiler : IShaderCompiler`), so a KNI / Blazor WebAssembly game can compile `.fx` → `.mgfx` **at runtime, in the browser, with no server roundtrip** and no native toolchain on the user's machine.

The in-browser frontend is the **faithful pinned DirectXShaderCompiler compiled to WebAssembly** (matching the desktop `Vortice.Dxc` commit), so its SPIR-V is byte-identical to the desktop pipeline — one faithful compiler everywhere, **no substitute frontend**. (The older Slang-WASM frontend in the sample is *dead, sample-only reference* and never runs.) See [WASM In-Browser Frontend](../architecture/wasm-frontend.md) for the architecture.

The [ShaderFiddle.Web sample](../samples/shaderfiddle-web.md) is a working demonstration of this reach — itself only a **sample**, not the product.

The complete walkthrough (setup, package wiring, KNI specifics, gotchas) is maintained in the repository and reproduced below as the single source of truth:

[!INCLUDE [HOWTO-WASM-KNI](../../docs/HOWTO-WASM-KNI.md)]
