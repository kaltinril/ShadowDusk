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

Practically: **build your DX12 content on Windows** until cross-platform signing ships. This is the one target where the usual "compile anywhere, get the same bytes" property does not hold; DX11, OpenGL, and FNA are unaffected and remain byte-identical across hosts. Windows on Arm is a Windows host: `dxil.dll` ships for win-arm64 too, and a native arm64 DX12 compile is measured byte-identical (signed) to win-x64's ([issue #286](https://github.com/kaltinril/ShadowDusk/issues/286)).

## Texture arrays reflect as one parameter (`SD0222`)

An **array of textures** (`Texture2D Tex[N]`) compiles, and the parameter table is exactly what `mgfxc` 3.8.5's `/Profile:DirectX_12` writes (measured for 1, 2 and 4 elements, with and without an explicit register): **one `Tex` parameter bound to the first slot**, with the shader header sizing the descriptor range for one texture. In real MonoGame 3.8.5 `WindowsDX12` that means `effect.Parameters["Tex"]` sets element `[0]` only, and the other elements **read as zero** even when `GraphicsDevice.Textures[i]` is set; `mgfxc`'s own build behaves the same (`validation/VsDrivenDx12 -- texarr` renders both). ShadowDusk keeps that table, so the output stays `mgfxc`'s, and emits the **`SD0222`** warning at the declaration to say so (issue #324). Declare each element as its own texture if every element must be read. A 1-element array is one texture and gets no warning.

(DirectX 11 reflects a texture array the same way since issue #339, one `Tex` parameter and one record at the array's base slot, measured against `mgfxc` 3.8.4.1 and 3.8.5; there the other elements ARE read through `GraphicsDevice.Textures[i]` in real MonoGame WindowsDX, so no warning is raised.)

## Sampler arrays are refused (`SD0224`)

An **array of samplers** (`SamplerState S[N]`) is refused by `mgfxc` on every profile in its own effect parser (`Unexpected token '[' found. Expected Semicolon, Comma, or CloseParenthesis.`), so no reference output exists for it. ShadowDusk used to compile it on DirectX 11 and 12; it now refuses it with the error **`SD0224`** at the declaration on both (issue #340). Declare each sampler separately and sample through each by name. Vulkan refuses the same shape as `SD0221`.

## Additive by policy

Like all backends, targeting DirectX 12 is **opt-in per compile** (`PlatformTarget.DirectX12`) and does not change OpenGL/DX11/v10 output for consumers who don't ask for it.
