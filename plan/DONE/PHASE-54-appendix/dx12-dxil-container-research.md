# The real DirectX 12 `.mgfx` container — source-grounded spec

**Status:** authoritative, read directly from MonoGame's own source (not reverse-engineered).
Source: `github.com/MonoGame/MonoGame`, tag `v3.8.5`:
- `Tools/MonoGame.Effect.Compiler/Effect/ShaderProfile.DirectX12.cs` (the reference writer)
- `MonoGame.Framework/Graphics/Effect/Effect.cs` (the shared MGFX header reader)
- `MonoGame.Framework/Graphics/Shader/Shader.cs`, `MonoGame.Framework/Platform/Native/{Shader,GraphicsDevice}.Native.cs` (the new native-architecture runtime load path)
- `native/monogame/directx12/MGG_DX12.cpp`, `native/monogame/directx12/CommandContext.cpp` (the C++ native layer that actually consumes the bytecode)

## Headline finding

**This is a much smaller container change than Vulkan's (Phase 32).** No descriptor-layout
wrapper table, no bitmasks, no per-binding VkDescriptorSetLayoutBinding-style list. The
DX12-specific wrapper is three fields: a magic marker + two ints. The real complexity is
elsewhere: (1) the runtime load path has moved to a new native C++ architecture with a
value profile byte MonoGame hasn't used before, and (2) the reference compiler embeds a
fixed root signature via a DXC flag ShadowDusk's `DxcFlagBuilder` has no equivalent for yet,
though empirical testing below suggests the runtime may not actually depend on it.

## Header: profile byte is 2, not a placeholder

`DirectX12ShaderProfile : ShaderProfile` (`ShaderProfile.DirectX12.cs:19-22`):
```csharp
public DirectX12ShaderProfile() : base("DirectX_12", 2) { }
```
**Profile byte `2`.** Confirmed free in ShadowDusk's `MgfxProfile` enum (`OpenGL=0,
DirectX11=1, Vulkan=80` — no existing `2`). `MgfxProfile.DirectX12 = 2` is a clean addition,
no collision.

Version is forced to **11** for every profile as of 3.8.5 (`EffectObject.writer.cs:16`,
identical to what the Phase 32 Vulkan research already found) — DX12 must also force the v11
shader-record shape (`SourceFile`/`Entrypoint` always written) regardless of
`CompilerOptions.MgfxVersion`, matching the Vulkan precedent exactly.

## Shader model + macros: reuses the EXACT same SM6 branch as Vulkan

`ShaderProfile.DirectX12.cs:24-28`:
```csharp
internal override void AddMacros(Dictionary<string, string> macros)
{
    macros.Add("HLSL", "1");
    macros.Add("SM6", "1");
}
```
`ValidateShaderModels` requires exactly `vs_6_0`/`ps_6_0` (throws otherwise, `:30-43`).

**This means DX12 needs NO new fixture variant.** `apos-shapes-sm6.fx`'s existing SM6 branch
(built for the Vulkan proof, `{MGFX, HLSL, SM6}`) is *the same* branch DX12 takes — modern
`Texture2D`/`SamplerState`/`.Sample()`/`SV_Target` syntax, `vs_6_0`/`ps_6_0`. The DX11 SM4
`{MGFX, HLSL, SM4}` branch (legacy `sampler`/`tex2D`) is NOT what DX12 compiles.

**Matrix packing differs from Vulkan: `/Zpc` (column-major), not `-Zpr` (row-major).**
`ShaderProfile.DirectX12.cs:61`: `toolArgs += "/Zpc "; // Pack matrices in column-major order`.
DXC's HLSL default IS column-major, so this is achieved by simply **not** passing `-Zpr` —
same as Vulkan's case, NOT DirectX11's (`DirectX11` compiles through `d3dcompiler_47`/
`ShaderFlags.PackMatrixColumnMajor`, an entirely separate path DXC never touches; the
existing `-Zpr` conditional in ShadowDusk's `DxcFlagBuilder.cs` — `if (platform !=
PlatformTarget.Vulkan) args.Add("-Zpr")` — must be widened to exclude `DirectX12` too, or
DX12 shaders will ship transposed-matrix bytecode and repeat issue #145's failure mode.

DXC invocation for DX12 has **no** `-spirv`, `-auto-binding-space`, or `-fvk-*` flags at all
— it is a plain SM6 DXIL compile, just like ShadowDusk's existing (currently-reflection-only)
`(PlatformTarget.DirectX, ShaderStage.*)` case in `DxcFlagBuilder.cs` (profile `vs_6_0`/
`ps_6_0`, empty `platformFlags`). **That case already exists in the codebase** — it's used
today to feed `DxilReflectionExtractor`/`ReflectionPipeline` for the DirectX11 (DXBC) target's
reflection data, per the comment at `DxcFlagBuilder.cs`: "DXC does not support SM5 DXBC
output; minimum profile is SM6 (DXIL)." This is the concrete basis for the Phase 52 doc's
claim "the DXIL path is already built" — it's accurate for the *compile-to-DXIL* step, not for
a DX12 `PlatformTarget`/container/writer, none of which exist yet (`PlatformTarget` enum has
only `DirectX=0, OpenGL=1, Metal=2, Vulkan=3, Fna=4` — confirmed by reading
`src/ShadowDusk.Core/PlatformTarget.cs` directly).

## Root signature: DXC flags exist in the reference compiler, but the runtime doesn't obviously need them

`ShaderProfile.DirectX12.cs:57-73` adds to every DXC invocation:
```
/force-rootsig-ver rootsig_1_0
/rootsig-define _MG_ROOT_SIGNATURE
/D _MG_ROOT_SIGNATURE="RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT | DENY_DOMAIN_SHADER_ROOT_ACCESS | DENY_GEOMETRY_SHADER_ROOT_ACCESS | DENY_HULL_SHADER_ROOT_ACCESS),
  CBV(b0, visibility = SHADER_VISIBILITY_VERTEX),
  CBV(b0, visibility = SHADER_VISIBILITY_PIXEL),
  DescriptorTable(SRV(t0, numDescriptors = unbounded), visibility = SHADER_VISIBILITY_VERTEX),
  DescriptorTable(SRV(t0, numDescriptors = unbounded), visibility = SHADER_VISIBILITY_PIXEL),
  DescriptorTable(Sampler(s0, numDescriptors = unbounded), visibility = SHADER_VISIBILITY_VERTEX),
  DescriptorTable(Sampler(s0, numDescriptors = unbounded), visibility = SHADER_VISIBILITY_PIXEL)"
```
This embeds an `RTS0` root-signature blob directly into the compiled DXIL container.

**But the native runtime does NOT read this embedded root signature.**
`CommandContext::CreateDefaultRootSignature` (`CommandContext.cpp:333-368`) independently
constructs, serializes (`D3D12SerializeVersionedRootSignature`), and creates
(`ID3D12Device::CreateRootSignature`) its own fixed root signature at device-init time — with
**exactly the same layout** as the string above (6 root parameters: CBV b0 VS, CBV b0 PS, SRV
descriptor table t0-unbounded VS, SRV descriptor table t0-unbounded PS, Sampler descriptor
table s0-unbounded VS, Sampler descriptor table s0-unbounded PS; same deny flags). This fixed
signature is what's bound at PSO creation (`psoDesc.pRootSignature = m_rootSig.Get()`,
`CommandContext.cpp:310`) — never extracted from either shader blob.

**Open question, empirical only (Step 8-equivalent, not resolvable by source reading):**
whether `ID3D12Device::CreatePipelineState` validates an embedded RTS0 against the explicitly-
bound root signature and rejects a mismatch, or simply ignores it when `pRootSignature` is set
explicitly (the common case for hand-written D3D12 engines that never embed one at all). If
the latter, ShadowDusk's DX12 shaders may not need `-rootsig-define`/`-force-rootsig-ver` at
all — plain DXC SM6 output with resources bound at `b0`/`t0..`/`s0..` (space 0, matching the
fixed layout above) should link into the same PSO. **This is untested and must be resolved by
an actual `CreatePipelineState` call**, not assumed — get the simplest possible shader loading
before spending effort replicating the rootsig-define flags.

## The shader bytecode wrapper: `0xB00B00` + 2 ints — DX12-only, not shared with GL/Vulkan

`ShaderProfile.DirectX12.cs:409-424`:
```csharp
writer.Write((uint)0xB00B00);
writer.Write(samplerMaxSlot);   // int32
writer.Write(textureMaxSlot);   // int32
writer.Write(shaderData.Bytecode);  // the raw DXC-compiled bytes, verbatim
```
This exact `0xB00B00` marker appears **nowhere else** in the reference compiler (`grep` across
all four `ShaderProfile.*.cs` files) — it's DX12-specific, read back on the native side in
`MGG_Shader_Create` (`MGG_DX12.cpp:2235-2247`): if the first `int32` is `0xB00B00`, the next
two `int32`s are consumed as `maxSamplerSlot`/`maxTextureSlot` and skipped; the remainder is
copied verbatim into `shader->bytecode`, later assigned directly as
`D3D12_SHADER_BYTECODE{data(), size()}` for `VS`/`PS` in PSO creation (`MGG_DX12.cpp:981,986`)
— **no other transformation, no translation layer.** Whatever bytes ShadowDusk puts after the
marker+2-ints must be a complete, valid D3D12 shader bytecode blob DXC itself produced (SM6
DXIL container) — same requirement as any hand-written D3D12 app.

This wrapper is the ONE new piece `MgfxWriter`/`CompiledShaderBlob` construction needs for the
DX12 profile — analogous in shape (a small prefix before the raw compiled bytes) to Vulkan's
wrapper but far simpler: no bitmasks, no per-binding table, no descriptor-set math. No new
sibling writer class needed — same conclusion Phase 32 reached for Vulkan.

## Reflection: same two-pass-compile shape as Vulkan

`ShaderProfile.DirectX12.cs:82-107` compiles the shader **twice**: once with no `/Fo` to
capture DXC's stdout reflection-comment dump (parsed via regex for cbuffers/samplers/textures/
input attributes — the same textual `; cbuffer NAME` / `; Name Type ...` comment format
`DxilReflectionExtractor` already parses for the existing DirectX11 reflection step), then a
second real compile with `/Fo <file>` for the shipped bytecode. ShadowDusk already has
`DxilReflectionExtractor`/`ReflectionPipeline`/`ParameterListBuilder` built and working for
DirectX11's reflection-only DXC compile — **the DX12 profile can reuse this exact reflection
pipeline unchanged**, since it's the identical DXIL reflection format at the identical SM6
profile. No new reflection code needed, only wiring the existing extractor's output into the
DX12 wrapper's `samplerMaxSlot`/`textureMaxSlot` fields.

## What's genuinely new vs. reused

| Piece | Status |
|---|---|
| `MgfxProfile.DirectX12 = 2` | New (one enum value) |
| `PlatformTarget.DirectX12` | New (one enum value, ordinal 5) |
| DXC compile to SM6 DXIL, `vs_6_0`/`ps_6_0`, no `-spirv` | **Already exists** (`DxcFlagBuilder`'s `(PlatformTarget.DirectX, *)` case) — needs a new `(PlatformTarget.DirectX12, *)` case that's nearly identical, minus the caveat that DirectX's existing case is used for reflection-only today and DX12's is the actual shipped bytecode |
| Matrix packing (no `-Zpr`) | Needs the `-Zpr` exclusion condition widened to include `DirectX12` |
| DXIL reflection (cbuffers/samplers/textures/attributes) | **Already exists and reusable as-is** (`DxilReflectionExtractor`/`ReflectionPipeline`) |
| `0xB00B00` + maxSamplerSlot + maxTextureSlot wrapper | New (small helper, ~10 lines) |
| Root signature embedding (`-rootsig-define`) | **Unresolved — empirical.** May not be needed at all; verify with the simplest possible shader before replicating |
| MGFX body (ConstantBuffers/Shaders/Parameters/Techniques order, render-state blocks) | **Reused unchanged** — confirmed structurally identical across every profile in Phase 32's research and again here (DX12's writer overrides only `AddMacros`/`ValidateShaderModels`/`CreateShader`, same as Vulkan's profile subclass shape) |

## Net assessment

Smaller than Phase 32's Vulkan container work (no descriptor wrapper table, reflection code
reused wholesale, same fixture branch as Vulkan already provides) but still a genuine new
`PlatformTarget` + compile routing + writer wrapper + validation-driver pair — real
new-backend work, not "just a render-validation rung" as Phase 52 Area D's framing suggested.
Consistent with Area D's own decision gate ("split into its own scoped phase" when research
reveals more than a render-proof), this is filed as **Phase 54**.
