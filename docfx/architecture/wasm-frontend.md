# WASM In-Browser Frontend

The `ShadowDusk.Wasm` package (`WasmShaderCompiler : IShaderCompiler`) runs the **same faithful pipeline** inside .NET WebAssembly so a KNI / Blazor game can compile `.fx` → `.mgfx` (or FNA `.fxb`) at runtime in the browser, returning bytes in-memory with no server roundtrip.

## One faithful compiler — no substitute

The in-browser HLSL → SPIR-V frontend is the **faithful pinned DirectXShaderCompiler compiled to WebAssembly**, matching the same DXC commit `Vortice.Dxc` uses on the desktop. Its SPIR-V is **byte-identical** to the desktop pipeline's. This is the load-bearing rule from THE PURPOSE: a host must never swap in a *different* HLSL→SPIR-V tool to "make the browser work," because a different compiler produces different output and breaks the "identical to `mgfxc`" promise.

> **Slang is dead, sample-only reference.** An earlier spike used Slang as an in-browser frontend; it is a *substitute* compiler and is **not** part of the product. The faithful DXC→WASM module replaced it. Any Slang artifacts left in the `ShaderFiddle.Web` sample are unregistered and never run.

## How the browser pipeline runs

| Stage | In the browser |
|---|---|
| HLSL → SPIR-V | the pinned **DXC compiled to WASM**, called via `[JSImport]` |
| SPIR-V → reflection | the **pure-managed `SpirvReflector`** — no native dependency, runs in .NET WASM directly (see [Reflection](reflection.md)) |
| SPIR-V → GLSL | **SPIRV-Cross compiled to WASM**, called via `[JSImport]` |
| GLSL → `.mgfx` | the managed [GLSL dialect rewrite](glsl-dialect-rewrite.md) + [MGFX writer](mgfx-format.md), pure managed |
| HLSL → DXBC (DirectX) / D3D9 bytecode (FNA) | the pinned **`vkd3d-shader` compiled to WASM**, called via `[JSImport]` — see [DirectX & FNA in the Browser](../backends/directx-in-wasm.md) |

The managed reflection + rewrite + writer stages are identical to the desktop ones; only the native calls (DXC, SPIRV-Cross, vkd3d-shader) cross the `[JSImport]` boundary instead of P/Invoke.

## Self-contained packaging

`ShadowDusk.Wasm` ships the WASM modules as **self-registered Blazor static web assets** (`_content/ShadowDusk.Wasm/`). A consumer adds a `ProjectReference`/package reference and wires **nothing** — no `JSHost.ImportAsync`. Each module is lazily downloaded on the first compile that needs it: the ~17 MB `dxcompiler.wasm` (~6 MB compressed) on the first OpenGL compile, the ~1.3 MB `vkd3d-shader.wasm` (~0.4 MB compressed) on the first DirectX/FNA compile. See the [In-Browser guide](../guides/in-browser-kni-blazor.md) and the [ShaderFiddle.Web sample](../samples/shaderfiddle-web.md).

## Why it's excluded from the API reference

`ShadowDusk.Wasm` targets `net8.0-browser` and uses the Razor/Blazor SDK plus a `dxcompiler.wasm` build gate, which make Roslyn metadata extraction fragile in CI. It is therefore documented **conceptually** (this page + the In-Browser guide) rather than via DocFX-generated API metadata.

## DirectX & FNA: export targets

The OpenGL/WebGL path is fully in-browser, render included. **DirectX DXBC and FNA fx_2_0 also compile in-browser** — through the same pinned `vkd3d-shader` compiled to WASM, byte-identical to the desktop output — but they are *export* targets: a browser cannot render DXBC or D3D9 bytecode, so the downloaded artifact renders in the consumer's MonoGame WindowsDX / FNA game. See [DirectX & FNA in the Browser](../backends/directx-in-wasm.md).
