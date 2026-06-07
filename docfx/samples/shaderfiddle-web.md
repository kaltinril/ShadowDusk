# ShaderFiddle.Web

An XNA-Fiddle-style **browser sample**: paste HLSL `.fx`, compile it **entirely in the browser** via the faithful [WASM frontend](../architecture/wasm-frontend.md), and see a cat re-rendered with the shader applied — no server, no `mgfxc`, no native toolchain on the user's machine. The runtime is **KNI** (nkast's MonoGame fork) on **Blazor WebAssembly + WebGL**.

> This is a **sample of reach**, not the product. It demonstrates that `ShadowDusk.Wasm` runs the same faithful pipeline in the browser. The product is the in-memory `ShadowDusk.Compiler` library.

The sample's own README is maintained in the repository and reproduced here as the single source of truth:

[!INCLUDE [ShaderFiddle.Web README](../../samples/ShaderFiddle.Web/README.md)]
