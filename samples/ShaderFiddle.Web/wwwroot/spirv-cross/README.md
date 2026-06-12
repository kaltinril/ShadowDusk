# `wwwroot/spirv-cross/` — WASM SPIRV-Cross for the in-browser SPIR-V → GLSL backend

These two files are the WebAssembly build of [KhronosGroup/SPIRV-Cross]'s C API
(`spirv_cross_c`), used by the **real** `shadowdusk-spirv-cross` `[JSImport]`
module (`../shadowdusk-spirv-cross.js`) to transpile SPIR-V → GLSL entirely in the
browser — the second half of ShaderFiddle's Mode 2 (HLSL → SPIR-V → GLSL) pipeline.

| File | Size | Role |
|---|---|---|
| `spirv-cross.wasm` | ~2.1 MB | SPIRV-Cross core + C API + all backends (GLSL/HLSL/MSL/CPP/REFLECT), `-O3`, `-fexceptions`. |
| `spirv-cross.js`   | ~25 KB | emscripten ES6 loader (`MODULARIZE`, `EXPORT_ES6`, `EXPORT_NAME=createSpirvCrossModule`). |

These artifacts **are committed** (the build needs emscripten, which is not in CI
yet). They are produced by `../../../../.wasm-build/build-spirv-cross-wasm.ps1`
(scratch dir is git-ignored).

## Why this is byte-for-byte faithful to the desktop

The desktop transpiler (`src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs`) uses
`Silk.NET.SPIRV.Cross.Native` **2.23.0**. That package's `.nuspec` pins SPIRV-Cross
commit `94605142f7b7bd6e69c9201e8e721d245c69eb7e`, which is **not present in the
public KhronosGroup/SPIRV-Cross repo** (GitHub returns 422). The closest public tag
to the Silk.NET 2.23.0 era (Vulkan 1.4.336) is **`vulkan-sdk-1.4.335.0`** — that is
what we build.

Rather than trust the version match, parity is **verified empirically**: the
`.wasm-build` probe runs the real desktop pipeline (DXC → SPIR-V, then the desktop
SPIRV-Cross) to produce reference GLSL, and `node-test-spirv-cross.mjs` feeds the
same SPIR-V to this WASM module and asserts the GLSL is **byte-identical**. Both a
texture-free and a textured shader (the latter exercising
`spvc_compiler_build_combined_image_samplers`) pass. See the test for the exact
option set used there (`flipVertexY=true, …` — parity on both sides). The PRODUCT
option set (`JsShaderBackends.cs`, matching the desktop `SpirvCrossGlslTranspiler`)
is `flipVertexY=false, fixupDepthConvention=true, glslVersion=140, glslEs=false,
vulkanSemantics=false` — `flipVertexY` is false since Phase 43 F3: the Y-flip is
the runtime `posFixup` uniform's job (injected by `MonoGameGlslRewriter`), not a
baked negation.

## Rebuilding

```powershell
# 1. (once) install emscripten — any recent version; this is the [JSImport] JS-module
#    path, so the emscripten version need NOT match the .NET WASM runtime.
git clone https://github.com/emscripten-core/emsdk .wasm-build/emsdk
.wasm-build/emsdk/emsdk install latest
.wasm-build/emsdk/emsdk activate latest

# 2. clone SPIRV-Cross at the matching tag
git clone https://github.com/KhronosGroup/SPIRV-Cross .wasm-build/spirv-cross-src
git -C .wasm-build/spirv-cross-src checkout vulkan-sdk-1.4.335.0

# 3. build the wasm (writes spirv-cross.{js,wasm} here)
pwsh .wasm-build/build-spirv-cross-wasm.ps1

# 4. regenerate fixtures + verify byte-for-byte against desktop
dotnet run --project .wasm-build/spirv-probe -c Release -- .wasm-build/fixtures
node .wasm-build/node-test-spirv-cross.mjs
```

No CMake/Ninja required — `emcc` compiles the fixed source-file list directly
(emsdk bundles its own LLVM/clang and Node).

## Browser-verification steps (not yet done — no browser in this environment)

`node-test-spirv-cross.mjs` proves the module under Node. To confirm it in a real
browser through the full Blazor app:

1. `cd samples/ShaderFiddle.Web && dotnet run` and open the printed `https://localhost:…` URL.
2. The page boots into Mode 1 (precompiled `.mgfx`); the cat renders via KNI WebGL.
3. Open DevTools → Network and confirm `spirv-cross/spirv-cross.wasm` loads. It
   should be served as **`application/wasm`**. If your host serves it as
   `application/octet-stream`, emscripten still works (it falls back from
   `WebAssembly.instantiateStreaming` to `arrayBuffer()`), but `application/wasm`
   avoids a console warning. The Blazor WASM dev server (`Components.WebAssembly.DevServer`)
   serves `.wasm` correctly.
4. Paste/keep an HLSL pixel shader and click **Compile & Apply** (Mode 2). The
   SPIR-V → GLSL step now runs through *this* module instead of the old throwing
   stub. A SPIRV-Cross failure surfaces as `ShaderError SD1901` in the error panel
   (not a page crash).
5. End-to-end Mode 2 also depends on the HLSL → SPIR-V step (`shadowdusk-dxc.js`,
   owned separately). If that step is unwired/diverges, Mode 2 fails *before*
   reaching SPIRV-Cross — that does not implicate this module (proven independently
   by the Node test).

[KhronosGroup/SPIRV-Cross]: https://github.com/KhronosGroup/SPIRV-Cross
