# DirectX 12

DirectX 12 consumes **DXIL** (shader model 6) directly, which is convenient because DXC already emits DXIL as an intermediate on other paths — DX12 just carries it through instead of translating to DXBC:

```text
HLSL → DXC → DXIL (SM6)   (consumed directly by MonoGame WindowsDX12)
```

## Current state

The CLI and `PlatformTarget` accept a **`DirectX12`** profile. Compiling targets MonoGame's `WindowsDX12` platform (new in MonoGame 3.8.5, stable since 2026-07-15): the `.mgfx` uses its own container (profile byte `2`), with each shader's DXIL wrapped via `DirectX12ShaderCodeWrapper`. ShadowDusk's own DX12 output is validated end-to-end against a real `mgfxc /platform:WindowsDX12` golden (MonoGame's own 3.8.5 content pipeline) — **max Δ 0** across the standard PS/SpriteBatch corpus, a VS-driven rig, and the real-world `Apos.Shapes` SDF shape renderer (custom vertex shader).

DirectX 12 requires MonoGame 3.8.5+ on the consumer's side (the `WindowsDX12` platform didn't exist before). ShadowDusk's own MonoGame reference pin stays at 3.8.2.1105 regardless — targeting DX12 is a choice about the *consumer's* runtime, the same way choosing Vulkan is.

KNI does not ship a DirectX 12 platform, so this target is MonoGame-only, like Vulkan.

## Compile DirectX 12 effects on Windows (`SD0214`)

**DXIL signing is Windows-only.** DXC's validation-and-signing step runs through `dxil.dll`, which exists only on Windows (it is a no-op on Linux, and macOS ships no `dxil` at all). A `DirectX12` compile on a non-Windows host therefore produces **unsigned DXIL**, which:

- loads only on a machine with **Windows Developer Mode** enabled, and
- is **rejected by retail D3D12 at pipeline-state creation**.

Same source, same ShadowDusk version, different build host, differently-broken artifact. ShadowDusk does not hide this: a non-Windows DX12 compile emits the **`SD0214`** warning ([`DxcShaderCompiler`](https://github.com/kaltinril/ShadowDusk/blob/main/src/ShadowDusk.HLSL/Dxc/DxcShaderCompiler.cs)) rather than shipping a silently host-dependent output.

Practically: **build your DX12 content on Windows** until cross-platform signing ships. This is the one target where the usual "compile anywhere, get the same bytes" property does not hold; DX11, OpenGL, and FNA are unaffected and remain byte-identical across hosts.

## Additive by policy

Like all backends, targeting DirectX 12 is **opt-in per compile** (`PlatformTarget.DirectX12`) and does not change OpenGL/DX11/v10 output for consumers who don't ask for it.
