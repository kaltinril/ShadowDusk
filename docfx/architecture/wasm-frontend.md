# WASM In-Browser Frontend

The `ShadowDusk.Wasm` package (`WasmShaderCompiler : IShaderCompiler`) runs the **same faithful pipeline** inside .NET WebAssembly so a KNI / Blazor game can compile `.fx` → `.mgfx` at runtime in the browser, returning bytes in-memory with no server roundtrip.

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

The managed reflection + rewrite + writer stages are identical to the desktop ones; only the two native calls (DXC, SPIRV-Cross) cross the `[JSImport]` boundary instead of P/Invoke.

## Self-contained packaging

`ShadowDusk.Wasm` ships the WASM modules as **self-registered Blazor static web assets** (`_content/ShadowDusk.Wasm/`). A consumer adds a `ProjectReference`/package reference and wires **nothing** — no `JSHost.ImportAsync`. The ~17 MB `dxcompiler.wasm` is lazily downloaded on first compile. See the [In-Browser guide](../guides/in-browser-kni-blazor.md) and the [ShaderFiddle.Web sample](../samples/shaderfiddle-web.md).

## Why it's excluded from the API reference

`ShadowDusk.Wasm` targets `net8.0-browser` and uses the Razor/Blazor SDK plus a `dxcompiler.wasm` build gate, which make Roslyn metadata extraction fragile in CI. It is therefore documented **conceptually** (this page + the In-Browser guide) rather than via DocFX-generated API metadata.

## Open problem

The OpenGL/WebGL path is fully in-browser. **DirectX DXBC in the browser** is still unsolved — see [DirectX-in-WASM (spike)](../backends/directx-in-wasm.md).
