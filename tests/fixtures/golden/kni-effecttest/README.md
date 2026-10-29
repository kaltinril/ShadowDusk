# Reference-compiler builds for the KNI EffectTest gate

Real `mgfxc` builds of KNI's test shaders (`tests/fixtures/shaders/third-party/KNI/*.fx`: KNI's shader
code unmodified, with a provenance comment header prepended) and of the gate's companion shaders
(`validation/SharedKniEffectTest/Companion/*.fx`, ShadowDusk-authored).

**Licence:** the builds of KNI's shaders are compiled form of Ms-PL code and are distributed under
the same Ms-PL; see `tests/fixtures/shaders/third-party/KNI/LICENSE` and that directory's `NOTICE.md`.
The `validation/KniEffectTest*` drivers load them as the **`mgfxc` reference arm**: printed next to
ShadowDusk's rows for comparison, never scored. They are what lets the gate say "mgfxc does the same"
or "mgfxc passes where we fail" with evidence rather than memory.

Built 2026-10-28 on Windows:

| Folder | Compiler | Source |
|---|---|---|
| `mgfxc-3.8.4.1-OpenGL` | `dotnet-mgfxc` 3.8.4.1, `/Profile:OpenGL` | the vendored `.fx` as is |
| `mgfxc-3.8.4.1-DirectX_11` | `dotnet-mgfxc` 3.8.4.1, `/Profile:DirectX_11` | KNI shaders with KNI's own DX11 profile substitution on the `compile` token (`s_2_0` -> `s_4_0_level_9_1`, `s_3_0` -> `s_4_0_level_9_3`, KNI's `ShaderProfileDX11`), since `mgfxc` refuses `ps_2_0` there; companions as is |
| `mgfxc-3.8.5-DirectX_12` | `dotnet-mgfxc` 3.8.5, `/Profile:DirectX_12` | KNI shaders with `s_2_0`/`s_3_0` -> `s_6_0` (`mgfxc` 3.8.5 accepts exactly `vs_6_0`/`ps_6_0` there); companions as is |
| `mgfxc-3.8.5-Vulkan` | `dotnet-mgfxc` 3.8.5, `/Profile:Vulkan` | as for DirectX_12 |

```powershell
mgfxc <in.fx> <out.mgfx> /Profile:OpenGL          # dotnet-mgfxc 3.8.4.1 (global tool)
dotnet ~/.nuget/packages/dotnet-mgfxc/3.8.5/tools/net8.0/any/mgfxc.dll <in.fx> <out.mgfx> /Profile:DirectX_12
```

## What `mgfxc` refused (so there is no file, and the gate prints "no mgfxc build")

| Shader | Refused on | `mgfxc`'s message |
|---|---|---|
| `ComputeShader` | every profile | `Bad token CppNet.Token` (its parser has no compute stage) |
| `TextureArraySlice` | OpenGL | `error X4545` (no texture-array sampler at SM 3) |
| `VertexTextureSampleLevel`, `VertexTextureTex2Dlod` | OpenGL | `invalid DCL register type for this shader model` |
| every KNI shader, `MatrixBrace`, `VertexTextureTex2Dlod` | Vulkan | DXC's SPIR-V path: `unknown type name 'texture2D'` / `sampler2D` (legacy syntax), or `invalid usage of semantic 'COLOR0' in shader profile ps` |

## What the reference arm shows (measured 2026-10-28, NVIDIA RTX 3080)

- `MatrixBrace`: `mgfxc` DirectX_11 renders `(64,0,0)`, the TRANSPOSE of the HLSL initializer
  (`M[0][1] = 1` lands at `M[1][0]`); ShadowDusk renders the HLSL-correct `(0,128,0)` on every
  target. `mgfxc` OpenGL stores zeros (black).
- `OptimizedMatrix`: `mgfxc` DirectX_11's `GetValueMatrix()` default is XNA's (what KNI's test
  asserts), the transpose of the HLSL initializer; `mgfxc` OpenGL throws `InvalidCastException`.
- `VertexTextureSampleLevel`: `mgfxc` 3.8.5's DirectX_12 build throws `SEHException` and its Vulkan
  build `IndexOutOfRangeException` when drawn; ShadowDusk's builds render green on both.
- `TextureAtRegisterT1` on Vulkan: `mgfxc` 3.8.5 honours `register(t1)` (green); ShadowDusk does
  not yet (a listed known-failing row).
- `mgfxc` 3.8.5 DirectX_12 builds of KNI's legacy-syntax shaders load but expose no parameters.
