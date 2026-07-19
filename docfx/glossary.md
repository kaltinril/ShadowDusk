# Glossary

Plain-language definitions of the shader and compiler terms used across these docs. You don't need most of these to use ShadowDusk; they're here so the deeper pages can stay precise.

## File types

- **`.fx`** — an HLSL effect source file: the shader code you write (techniques, passes, vertex/pixel shaders).
- **`.mgfx`** — the compiled effect MonoGame and KNI load via `new Effect(graphicsDevice, bytes)`. This is ShadowDusk's main output (the same thing mgfxc produces).
- **`.fxb`** — the compiled effect **FNA** loads: a legacy Direct3D 9 "fx_2_0" effect binary. ShadowDusk produces this for the FNA target instead of `.mgfx`.
- **`.xnb`** — the Content Pipeline container that *wraps* a `.mgfx` (or any other content), loaded via `Content.Load<Effect>`. ShadowDusk emits the raw `.mgfx`, not the `.xnb` wrapper.

## Formats and containers

- **MGFX** — MonoGame's compiled-effect format. **v10** is the default ShadowDusk emits and loads on MonoGame 3.8.2+ and KNI; **v11** is a newer optional container for MonoGame 3.8.5+.
- **KNIFX** — KNI's own newer effect container, an optional target for KNI v4.02+.
- **fx_2_0** — the legacy Direct3D 9 effect format FNA uses (Shader Model 3 and below).
- **Vulkan / DesktopVK** — MonoGame 3.8.5's Vulkan platform (`DesktopVK`). ShadowDusk's Vulkan target emits a `.mgfx` with **profile byte 80** (matching MonoGame's own `VulkanShaderProfile`) carrying SPIR-V directly; validated end-to-end in a real DesktopVK `Effect`. MonoGame-only — KNI has no Vulkan platform.
- **Shader Model (SM)** — a versioned GPU feature level. SM3 and below are the old Direct3D 9 era (FNA); SM5 is Direct3D 11. Higher numbers mean more features.

## Intermediate representations

- **SPIR-V** — a portable, binary shader intermediate language. ShadowDusk compiles HLSL to SPIR-V, then converts that to GLSL for the OpenGL path. For the **Vulkan** target the SPIR-V *is* the shipped shader payload — the `.mgfx` carries it directly, no GLSL conversion.
- **GLSL** — the OpenGL Shading Language (source text). What a `.mgfx` carries for the OpenGL, WebGL, and Android targets.
- **DXBC** — Direct3D **bytecode**, the compiled GPU program the DirectX 11 runtime loads. What a DirectX `.mgfx` carries.
- **DXIL** — a newer DirectX intermediate (Direct3D 12 / Shader Model 6). The DX11 runtime can't load it, which is why ShadowDusk doesn't use DXC for the DX11 path.

## Tools (the compilers)

- **mgfxc** — MonoGame's stock, Windows-only effect compiler. ShadowDusk is a drop-in replacement for it.
- **fxc** — Microsoft's classic HLSL compiler (`fxc.exe`, part of the Windows DirectX SDK), the original reference for DirectX and FNA output.
- **DXC** — Microsoft's modern HLSL compiler (the DirectX Shader Compiler). ShadowDusk uses it to turn HLSL into SPIR-V.
- **SPIRV-Cross** — Khronos's tool that converts SPIR-V into GLSL. ShadowDusk uses it for the OpenGL path.
- **vkd3d-shader** — a cross-platform HLSL-to-DXBC compiler from the Wine project. ShadowDusk's default DirectX backend, so DirectX compiles work off Windows too.
- **d3dcompiler_47** — a Microsoft HLSL compiler shipped as a system DLL on Windows. An optional, most-`fxc`-faithful DirectX backend.
- **MojoShader** — the runtime that loads legacy GLSL / D3D9 effects in MonoGame's OpenGL path and in FNA. ShadowDusk's output matches the dialect it expects.

## Concepts

- **oracle** — a trusted reference output you compare against to check correctness. ShadowDusk treats `fxc` / `d3dcompiler_47` (and mgfxc) as oracles: the "right answer" its output should match.
- **faithful** — producing output that behaves and renders the same as the reference compiler's, rather than a re-implementation that's merely close. ShadowDusk's pipeline drives the same core compilers (DXC, vkd3d) the references use.
- **RID** — Runtime Identifier, .NET's name for a platform (for example `win-x64`, `linux-x64`, `osx-arm64`, `android-arm64`). Native binaries ship per RID.
- **Reach / HiDef** — MonoGame `GraphicsProfile` settings (a runtime feature level, not a compile target). One `.mgfx` serves both.
