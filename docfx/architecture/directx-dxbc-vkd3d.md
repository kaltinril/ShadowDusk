# DirectX DXBC (vkd3d) Path

The DirectX backend is the part of ShadowDusk that most often gets misremembered, so the key fact first:

> **DirectX uses `vkd3d-shader` → DXBC, *not* DXC → DXBC.**

## Why DXC is wrong for DX11

DXC (the DirectX Shader Compiler, used on the OpenGL path to emit SPIR-V) only emits **DXIL** — shader model **6**. MonoGame 3.8's DX11 runtime loads **DXBC** — shader model **≤ 5**. So DXC's output simply will not load as a DX11 effect. DXC's `ps_6_0`/`vs_6_0` (DXIL) output is retained **only** for the Vulkan / DX12-KNI SM6 profile, never for DX11.

## The DXBC backends

DirectX compilation routes HLSL → DXBC through a backend behind the `IDxbcShaderCompiler` seam, selected by <xref:ShadowDusk.Core.CompilerOptions.DxbcBackend>:

| `DxbcBackend` | Backend | Platform | Role |
|---|---|---|---|
| `Vkd3d` (**default**) | `vkd3d-shader` (the Wine project) | **Linux / macOS / Windows** | The **shipping cross-platform** backend — what makes the DX path compilable where `mgfxc` can't run; host-independent, so default output is byte-identical on every OS. |
| `D3DCompiler` | `d3dcompiler_47` | **Windows-only** | Correctness **oracle** — the reference output (opt-in). |

```csharp
var options = new CompilerOptions
{
    Target = PlatformTarget.DirectX,
    DxbcBackend = DxbcBackend.D3DCompiler,   // opt in to the Windows-only oracle
};
```

The `vkd3d-shader` natives for all four desktop RIDs **ship inside the NuGet package** (`runtimes/<rid>/native`, SHA-256-pinned builds hosted on the `native-vkd3d-1.17` release) — consumers install nothing; the repo's [restore script](../getting-started/restore-native-tools.md) is only for building ShadowDusk from source. The opt-in `d3dcompiler_47` oracle needs no restore but runs only on Windows.

## Validation

Both backends are validated end-to-end: all 10/10 of the SM5 PS-only corpus shaders' DX `.mgfx` load in **real MonoGame.Framework.WindowsDX** and render pixel-equivalent to `mgfxc` — via **both** the `d3dcompiler_47` oracle and the cross-platform `vkd3d-shader` backend. See [Validation & Evidence Ladder](../contributing/validation.md).

## Reflection from the same bytes

The emitted DXBC is also the [reflection](reflection.md) source, parsed by the pure-managed `RdefReader` (proven deeply equal to the Windows `D3DReflect` oracle for both backends' output), and a `DxbcReflectionExtractor` reflects both backends identically — so the [MGFX writer](mgfx-format.md) gets the same parameter/cbuffer/sampler metadata regardless of which backend produced the bytes, on every OS.

## In the browser

**DirectX DXBC also compiles in the browser**: the same pinned `vkd3d-shader` is compiled to WebAssembly and ships inside the `ShadowDusk.Wasm` package, producing `.mgfx` byte-identical to the desktop output. It is an *export* target — a browser cannot render DXBC — see [DirectX & FNA in the Browser](../backends/directx-in-wasm.md).
