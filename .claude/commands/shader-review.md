# /shader-review — Deep review of shader or transpilation code

Reviews either a `.fx` shader source file or C# transpilation/compilation code for correctness, cross-API compatibility, and MonoGame compatibility.

## Usage
`/shader-review <path>`

- If path is a `.fx` / `.hlsl` file: review the shader source
- If path is a `.cs` file: review the C# shader pipeline code
- If no path: review all modified files in the current git diff

## Shader Source Review Checklist

**Syntax & Structure**
- [ ] All techniques have at least one pass
- [ ] VS and PS entry points declared in each pass match defined functions
- [ ] Shader model version (`vs_3_0`, `ps_3_0`, etc.) is appropriate for MonoGame target
- [ ] No `discard` in vertex shader

**Semantics**
- [ ] Vertex input semantics use correct MonoGame names (`POSITION0`, `TEXCOORD0`, `COLOR0`, etc.)
- [ ] Pixel shader output uses `SV_TARGET` (not `COLOR` for SM4+)
- [ ] `SV_POSITION` used for VS output clip position
- [ ] `TEXCOORD` slots don't exceed SM 3.0 limit (8 for SM3, 16 for SM4+)

**Cross-API Concerns**
- [ ] No assumptions about UV coordinate origin (document if flipping is needed)
- [ ] cbuffer / uniform layout is explicit (no implicit padding surprises)
- [ ] Sampler state declared with `SamplerState` not legacy `sampler2D`
- [ ] No DX-only intrinsics without fallback (`ddx_fine`, `ddy_fine` → use `ddx`/`ddy`)
- [ ] Texture arrays, geometry shaders, compute shaders: flag as DX-only if present

**MonoGame Compatibility**
- [ ] Parameter names match what the game code will set via `Effect.Parameters["name"]`
- [ ] Matrix parameters use `float4x4` not `matrix` alias (both work but be consistent)
- [ ] Texture parameters paired with a named `SamplerState` for MonoGame's sampler binding

## C# Transpilation Code Review Checklist
- [ ] SPIR-V intermediate is generated correctly from HLSL input
- [ ] SPIRV-Cross options set correctly for target (combined samplers for GL, etc.)
- [ ] Y-flip applied (or documented as not needed) for OpenGL path
- [ ] cbuffer → uniform block mapping is correct for std140 layout
- [ ] Error from native tool is parsed and mapped to `ShaderError` with correct line/col
- [ ] Temp files cleaned up in all code paths

## Report Format
List each finding as:
`[SEVERITY] file.fx:line — description — suggested fix`

Severities: `ERROR` (will fail to compile or render incorrectly), `WARN` (may work but fragile), `INFO` (style/convention)
