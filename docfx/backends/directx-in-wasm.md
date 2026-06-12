# DirectX & FNA in the Browser

> **Status: supported — as *export* targets.** In-browser compilation produces DirectX `.mgfx` and FNA `.fxb` **byte-identical to the desktop compiler's** output. A browser cannot *render* DXBC or D3D9 bytecode (there is no Direct3D in a browser, by construction) — the exported artifacts render in your MonoGame WindowsDX / FNA game.

## What works

`ShadowDusk.Wasm`'s `WasmShaderCompiler` accepts `PlatformTarget.DirectX` and `PlatformTarget.Fna` in the browser. Both routes run the **same pinned `vkd3d-shader` 1.17 the desktop uses, compiled to WebAssembly** — never a substitute compiler:

| Target | Output |
|---|---|
| `PlatformTarget.DirectX` | DX11 SM5 **DXBC** `.mgfx` |
| `PlatformTarget.Fna` | D3D9 **fx_2_0** `.fxb` (SM ≤ 3) |

The rest of both pipelines — DXBC reflection, the fx_2_0 writer, the bytecode patcher, the MGFX writer — is pure managed C# that runs in .NET WASM directly, so one WASM-compiled native library unlocks both targets.

## How it ships

The `vkd3d-shader.{js,wasm}` module rides inside the `ShadowDusk.Wasm` package as self-registered Blazor static web assets, alongside `dxcompiler.wasm` and the SPIRV-Cross module — the consumer wires nothing. It is **lazily fetched on the first DirectX/FNA compile** (not at page boot): ~1.3 MB raw, ~0.4 MB compressed on the wire (serve your site with HTTP compression). If the module is genuinely absent, the compile fails loudly with **SD1902** (the WASM sibling of the desktop's native-not-found diagnostic) — never a silent fallback or a substitute compiler.

## The evidence behind "byte-identical"

- A real headless browser running the real `WasmShaderCompiler` over the **full** DirectX + FNA byte-identity corpus produces artifacts SHA-256-identical to the committed cross-host manifest — **65/65** (37 DX `.mgfx` + 28 FNA `.fxb`).
- A node-level gate replays **every** vkd3d stage compile the real pipeline issues over that corpus through the product shim — **98/98** byte-identical to the desktop native.
- The manifest itself is CI-asserted identical across Windows/Linux/macOS, and the desktop bytes are render-proven in real MonoGame WindowsDX and real FNA (see [Validation & Evidence Ladder](../contributing/validation.md)).

So the bytes a browser user exports for DirectX/FNA *are* the render-proven desktop bytes — render-equivalence transfers by transitivity. **Explicitly not claimed:** rendering these targets inside the browser — impossible by construction. The browser's live-render path remains OpenGL/WebGL.

## Try it

The [ShaderFiddle.Web sample](../samples/shaderfiddle-web.md) is the export station: paste or upload a `.fx`, pick OpenGL / DirectX / FNA, and download the compiled artifact. Architecture details: [WASM In-Browser Frontend](../architecture/wasm-frontend.md) and [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md).
