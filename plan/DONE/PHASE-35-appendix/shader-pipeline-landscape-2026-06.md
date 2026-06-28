# Shader-pipeline landscape: MonoGame / KNI / ShadowDusk (verified June 2026)

**Status:** Research record. **Verified:** 2026-06-14 against live source via the authenticated GitHub
API (KNI `main`, MonoGame `develop`/releases) and the MonoGame/mojoshader fork. Re-verify on each
MonoGame/KNI release, these are live, evolving forks.

**Why this exists:** A KNI-Discord thread (squarebananas) raised whether ShadowDusk "uses the bad library"
(MojoShader), whether KNI/MonoGame have moved to modern shaders, and where modern shader features (vertex
texture fetch, texture arrays, higher shader models) actually work. This doc captures the primary-source
answers so they don't have to be re-derived. Companion to
[knifx-vs-mgfx-v11-research.md](knifx-vs-mgfx-v11-research.md) (version/container formats) and
[knifx-autoselect-design.md](knifx-autoselect-design.md) (the capability auto-detect/override design).

---

## TL;DR

1. **ShadowDusk uses DXC + SPIRV-Cross, NOT MojoShader.** The "MojoShader-dialect rewriter" is named for the
   output *dialect* it matches, not a library it links. ShadowDusk is already the modern toolchain.
2. **Both MonoGame and KNI still use MojoShader for OpenGL** (primary-source verified, June 2026). Neither
   has shipped the DXC/SPIRV-Cross GL pipeline squarebananas described as upcoming.
3. **The MojoShader limitation is OpenGL-only.** DirectX (both engines) supports SM5 + vertex texture fetch
   + texture arrays *today*. MonoGame's new Vulkan and DX12 backends (3.8.5) are modern (DXC -> SPIR-V /
   DXIL).
4. **So the "modern GLSL on OpenGL" capability has no runtime to consume it in either engine.** The modern
   frontier is DirectX (now) and Vulkan/DX12 (MonoGame 3.8.5, still preview), not OpenGL.

---

## 1. ShadowDusk uses SPIRV-Cross, not MojoShader (the common confusion)

- **Dependencies (verified `Directory.Packages.props`):** `Vortice.Dxc` (DXC) + `Silk.NET.SPIRV.Cross.Native`
  (SPIRV-Cross) + vkd3d-shader. **No MojoShader package or binary anywhere.**
- **Pipeline:** HLSL -> DXC -> SPIR-V -> SPIRV-Cross -> modern GLSL (`#version 140`) ->
  `MonoGameGlslRewriter` (down-convert to the legacy MojoShader *dialect*) -> MGFX v10 `.mgfx`.
- The rewriter is called the "MojoShader-dialect rewriter" because its **output matches** what MojoShader
  produces (so the MojoShader-era GL runtime loads it), not because it *uses* MojoShader.
- **Consequence:** ShadowDusk already sidesteps MojoShader's build-time codegen pathologies (aggressive
  dead-code elimination, dropped defaults, the things squarebananas complained about) because it generates
  GLSL with the modern toolchain. It only *caps its output* to what today's MojoShader-era GL runtimes can
  load (the SD0210 rejections for vertex texture fetch / texture arrays).

## 2. The two-layer model (compiler vs runtime) — both are on MojoShader for GL

There are two places MojoShader matters, and for OpenGL **both** MonoGame and KNI are still on it:

| Layer | What it does | MonoGame | KNI | ShadowDusk |
|---|---|---|---|---|
| **Compiler** (build-time, `.fx` -> shader code) | `mgfxc` / `KNIFXC` | MojoShader (GL) | MojoShader (GL) | **DXC + SPIRV-Cross** |
| **Runtime** (loads + runs the shader) | the GL backend in the engine | MojoShader-era (e.g. `SupportsVertexTextures=false`) | MojoShader-era | n/a (consumes the engine's runtime) |

Both layers must agree on the dialect/limits. That is *why* ShadowDusk (a modern compiler) must still emit
MojoShader-dialect GLSL and respect the runtime's feature ceiling, the runtime is the gate, not ShadowDusk.

## 3. Primary-source verification (June 2026)

### KNI
- **Latest release:** `v4.2.9001` (2025-11-02), hotfixes `.1` (Nov 8) and `.2` (Nov 17). `main` has commits
  through **2026-06-13** (latest: "WebXR templates #2653"), but the 2026 activity is WebXR / SDL2 input /
  templates, **none touches the shader pipeline.**
- **GL pipeline still MojoShader** (current `main`):
  - `src/Xna.Framework.Content.Pipeline.Graphics.MojoProcessor/EffectCompiler/ShaderProfileGL.cs`:
    `// Use MojoShader to convert the HLSL bytecode to GLSL.`
  - The whole GL effect compiler is the `...Graphics.MojoProcessor` project (loads `libmojoshader_64.dll`);
    the processor is `[ContentProcessor(DisplayName = "Effect (MojoShader) - KNI")]`.
  - A current test is skipped: `[Ignore("Comparison samplers are ps_4_0 and up, cannot use them on
    DesktopGL due to MojoShader")]`, the limitation is live.
  - `EffectParameter.cs`: `// TODO: Compile SM 3.0 and above with MojoShader.NativeConstants.PROFILE_GLSL120`,
    so the changelog's "allow SM4.0 on GLSL #2428" is feature-level gating pushed *through* MojoShader, not a
    replacement.
- **KNIFX** is the renamed `2MGFX` tool (`KNIFXC`); it is a new *container* (v11) over a **still-MojoShader
  body** (see [knifx-vs-mgfx-v11-research.md](knifx-vs-mgfx-v11-research.md)).

### MonoGame
- **Latest release:** `v3.8.5-preview.6` (2026-05-22). The 3.8.5 line is **still preview** (no stable 3.8.5).
- **GL pipeline still MojoShader** (current source):
  - `Tools/MonoGame.Effect.Compiler/Effect/ShaderProfile.OpenGL.cs`: `// using MojoShader which works from
    HLSL bytecode.`
  - `MonoGame.Effect.Compiler.csproj`: `<PackageReference Include="MonoGame.Library.MojoShader"
    Version="1.0.0.5" />`; the GL path parses through `ShaderData.mojo.cs` / `ConstantBufferData.mojo.cs`.
- **3.8.5 ADDS modern Vulkan + DX12 backends** (`ShaderProfile.Vulkan.cs` references the DirectXShaderCompiler
  SPIR-V docs; `ShaderProfile.DirectX12.cs` for DXIL/SM6). These are **new targets**, the OpenGL profile
  itself is untouched and still MojoShader.

## 4. The MojoShader limitation is OpenGL-only

| Backend | MojoShader-bound? | Modern features (SM4/5, VTF, texture arrays)? | Notes |
|---|---|---|---|
| **OpenGL / GLES** | **Yes** (both engines, still) | **No** (SM2-3) | the one limited backend |
| **DirectX 11** | No | **Yes (SM5, VTF, arrays) today** | MonoGame changelog: "vertex texture fetch on Windows", "texture arrays on DX platforms" |
| **Vulkan** | No (DXC -> SPIR-V) | Yes | MonoGame 3.8.5 **preview**; KNI has a `GraphicsBackend.Vulkan` enum value but no confirmed shipping Vulkan shader pipeline |
| **DX12** | No (DXIL / SM6) | Yes | MonoGame 3.8.5 **preview** |

So: the features squarebananas wants (vertex texture fetch, texture arrays) **already work on DirectX** on
both engines, they are missing only on GL/GLES because of MojoShader. "Compared to DX, using MojoShader for
GL is fairly broken" (squarebananas) is literally true.

## 5. Implications for ShadowDusk

- **The "modern-GLSL / suppress-MojoShader-limits" capability has no OpenGL runtime in either engine today**
  -> emitting modern GLSL would be silently broken everywhere -> it must stay an inert, future-gated
  capability (see [knifx-autoselect-design.md](knifx-autoselect-design.md)).
- **ShadowDusk's DirectX target already GENERATES modern features (empirically verified 2026-06-14).** The
  SD0210 rejections (VTF / texture arrays) live only in the GL dialect rewriter (`MonoGameGlslRewriter`); the
  DXBC path (vkd3d) does not go through it. Compiling the same `.fx` to both targets:
  - A shader doing **vertex texture fetch** (`HeightMap.SampleLevel(...)` in the VS): `DirectX_11` -> exit 0
    (compiles); `OpenGL` -> SD0210 (rejected).
  - A shader using a **`Texture2DArray`**: `DirectX_11` -> exit 0 (compiles); `OpenGL` -> SD0210 (rejected).

  So ShadowDusk is NOT artificially withholding modern features on DirectX, it emits SM4/5 DXBC for them
  today. A consumer who needs VTF / texture-arrays now targets DirectX. (Caveat: compilation to valid DXBC
  is proven; an in-engine MonoGame-DX *render* of these specific features is not yet rung-4'd, the DX proven
  corpus is PS-only + the Phase 18/issue-70 VS-matrix work. vkd3d emits standard SM5 DXBC, which DX11
  supports, so this is a coverage gap, not a suspected defect.)
- **The real modern-shader frontier is Vulkan + DX12** (MonoGame 3.8.5), which is Phase 35 Areas C/D, gated
  on 3.8.5 going **stable** (currently preview.6, 2026-05-22). ShadowDusk already has DXC -> SPIR-V (Vulkan)
  and DXC -> DXIL (DX12) plumbing built (Phase 4/32), unvalidated for lack of a stable runtime.
- **KNIFX** is a container KNI supports *now*; building a KNIFX writer is parity-polish, gated on a
  reproduce-first finding that our v10 renders *worse* on KNI v4.02 (it loads fine via KNI's MGFX-v10
  migration path). Not the modern-shader path.

## 6. Sources (verified 2026-06-14)

- KNI `main` (via gh): `ShaderProfileGL.cs` ("Use MojoShader to convert the HLSL bytecode to GLSL"),
  `...Graphics.MojoProcessor` project, `MojoEffectProcessor.cs`, `EffectParameter.cs` (PROFILE_GLSL120 TODO),
  `GraphicsBackend.cs` (Vulkan enum), releases (latest v4.2.9001, Nov 2025), commits (through 2026-06-13).
  https://github.com/kniEngine/kni
- MonoGame (via gh): `ShaderProfile.OpenGL.cs` ("using MojoShader"), `ShaderProfile.Vulkan.cs` (DXC SPIR-V),
  `ShaderProfile.DirectX12.cs`, `MonoGame.Library.MojoShader` package ref, releases (latest
  v3.8.5-preview.6, 2026-05-22). https://github.com/MonoGame/MonoGame
- MonoGame/mojoshader fork (the GL translator both engines depend on): https://github.com/MonoGame/mojoshader
- ShadowDusk deps: `Directory.Packages.props` (`Vortice.Dxc`, `Silk.NET.SPIRV.Cross.Native`; no MojoShader).
- squarebananas (KNI community) corroboration of the MojoShader-GL limitations and KNI's upcoming
  DX->SPIRV-Cross direction (issue [#70](https://github.com/kaltinril/ShadowDusk/issues/70) discussion).
