# Legacy LOD intrinsic fixtures and goldens (`tex2Dlod` / `tex2Dbias` / `tex2Dproj`)

The XNA spellings of explicit-LOD, biased and projective sampling, which ShadowDusk refused with
`FX0012` on every non-FNA target before 2026-10-28 while `mgfxc` compiles them. The pre-parser now
rewrites each call to a helper returning `T.SampleLevel(s, t.xy, t.w)` / `T.SampleBias(s, t.xy, t.w)`
/ `T.Sample(s, t.xy / t.w)` (see `docfx/architecture/fx9-preparser-preprocessor.md`).

| File | What |
|---|---|
| `Tex2DLodRender.fx` | The render fixture, legacy sampler syntax: `tex2Dlod` in the VS (vertex texture fetch) and the PS, `tex2Dbias`, `tex2Dproj`. Four columns, each reading a different mip level. Macros `LOD_VS`, `LOD_PS`, `BIAS`, `PROJ_Q` are the positive controls' knobs. |
| `Tex2DLodRender.DirectX_11.mgfx` | `mgfxc` 3.8.4.1 `/Profile:DirectX_11` (the DX11 reference). |
| `Tex2DLodRenderModern.fx` | The same shader in `Texture2D` / `SamplerState` syntax, explicit registers. |
| `Tex2DLodRenderModern.DirectX_11.mgfx` | `mgfxc` 3.8.4.1 `/Profile:DirectX_11`: must render the same as the legacy golden (the measured equivalence the SM6 arms rely on). |
| `Tex2DLodRenderModern.DirectX_12.mgfx` / `.Vulkan.mgfx` | `mgfxc` 3.8.5 `/Profile:DirectX_12` / `/Profile:Vulkan`: the DX12 and Vulkan references. 3.8.5 has none for the legacy file: `/Profile:Vulkan` refuses `sampler_state`, `/Profile:DirectX_12` compiles it to an effect with no texture parameter. |
| `Tex2DLodPixel.fx` + `.OpenGL.mgfx` / `.DirectX_11.mgfx` | Pixel-stage only, SpriteBatch-compatible, `ps_3_0` on OpenGL. `mgfxc` 3.8.4.1 builds. |
| `VertexTextureEffect.DirectX_11.mgfx` / `.DirectX_12.mgfx` / `.Vulkan.mgfx` | The reference builds of MonoGame's own `Tests/Assets/Effects/VertexTextureEffect.fx` (vendored at `tests/fixtures/shaders/third-party/MonoGame/`): `mgfxc` 3.8.4.1 DirectX_11 (its SM4 branch, `tex2Dlod`), `mgfxc` 3.8.5 DirectX_12 / Vulkan (its SM6 branch, `SampleLevel`). ShadowDusk refused the file with `FX0012` on all three before the rewrite (on the SM6 targets because the raw-text pre-parse saw the inactive branch's call). |

`mgfxc` 3.8.4.1 `/Profile:OpenGL` refuses `Tex2DLodRender.fx` (its VS is `vs_4_0`; a `vs_3_0` vertex
fetch is refused too, "invalid DCL register type", which is why ShadowDusk's `SD0210` there is
parity). Its OpenGL build of `Tex2DLodPixel.fx` samples the `tex2Dlod` with a plain `texture2D`:
MojoShader drops the explicit LOD in a pixel shader (measured 2026-10-28; `Texture2D.SampleLevel`
the same). ShadowDusk's OpenGL output honors it; see `project_facts.md`.

Consumers:

- `tests/ShadowDusk.Integration.Tests/Tests/LegacyLodIntrinsicTests.cs` (tables, layouts, GLSL).
- `validation/DxModernFeatures` arm "legacy-lod" (real WindowsDX), `validation/VsDrivenDx12 -- legacy-lod`
  (real WindowsDX12), `validation/VsDrivenVulkan -- legacy-lod` (real DesktopVK), all through
  `validation/SharedLegacyLod/LegacyLodScene.cs`.

The folder sits outside `tests/fixtures/shaders/` so the shapes do not join the corpus sweeps.

## Regenerating

From this folder (relative source names keep the build path out of the MGFX v11 goldens):

```powershell
$m4 = Join-Path $env:USERPROFILE ".nuget\packages\dotnet-mgcb\3.8.4.1\tools\net8.0\any\mgfxc.dll"
$m5 = (Get-ChildItem "$env:USERPROFILE\.nuget\packages\dotnet-mgfxc\3.8.5\tools\*\any\mgfxc.dll").FullName
dotnet $m4 Tex2DLodRender.fx Tex2DLodRender.DirectX_11.mgfx /Profile:DirectX_11
dotnet $m4 Tex2DLodRenderModern.fx Tex2DLodRenderModern.DirectX_11.mgfx /Profile:DirectX_11
dotnet $m5 Tex2DLodRenderModern.fx Tex2DLodRenderModern.DirectX_12.mgfx /Profile:DirectX_12
dotnet $m5 Tex2DLodRenderModern.fx Tex2DLodRenderModern.Vulkan.mgfx /Profile:Vulkan
dotnet $m4 Tex2DLodPixel.fx Tex2DLodPixel.DirectX_11.mgfx /Profile:DirectX_11
dotnet $m4 Tex2DLodPixel.fx Tex2DLodPixel.OpenGL.mgfx /Profile:OpenGL
cd ../../shaders/third-party/MonoGame
$g = "../../../golden/legacy-lod-intrinsics"
dotnet $m4 VertexTextureEffect.fx "$g/VertexTextureEffect.DirectX_11.mgfx" /Profile:DirectX_11
dotnet $m5 VertexTextureEffect.fx "$g/VertexTextureEffect.DirectX_12.mgfx" /Profile:DirectX_12
dotnet $m5 VertexTextureEffect.fx "$g/VertexTextureEffect.Vulkan.mgfx" /Profile:Vulkan
```
