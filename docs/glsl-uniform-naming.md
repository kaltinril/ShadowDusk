# GLSL Uniform Naming: MonoGame / MojoShader Convention

## Background

When MonoGame's OpenGL backend loads a compiled `.mgfx` effect, it calls `glGetUniformLocation`
on the GLSL shader source embedded in the file. The name string passed to `glGetUniformLocation`
must exactly match the uniform declarations in the GLSL source.

## MonoGame's Expected Convention

MonoGame's OpenGL path uses the MojoShader uniform naming convention. The runtime expects
vertex-shader float4 uniforms to be declared as elements of an array named `vs_uniforms_vec4`,
and pixel-shader float4 uniforms as `ps_uniforms_vec4`:

```glsl
uniform vec4 vs_uniforms_vec4[N];   // vertex constant buffer
uniform vec4 ps_uniforms_vec4[N];   // pixel constant buffer
```

MonoGame looks up uniforms by these fixed array names, not by the original HLSL variable names
(`WorldViewProj`, `DiffuseColor`, etc.).

## What SPIRV-Cross Emits by Default

SPIRV-Cross preserves the original HLSL variable names in its GLSL output. A shader that
declares `float4x4 WorldViewProj : register(c0)` will appear as:

```glsl
uniform mat4 WorldViewProj;
```

This is **incompatible** with the MonoGame OpenGL runtime, which will not find these uniforms
via its `vs_uniforms_vec4` / `ps_uniforms_vec4` lookups.

## Phase 6 Decision

Phase 6 produces SPIRV-Cross default GLSL output (HLSL variable names). This is correct
GLSL that compiles cleanly, but will not bind uniforms at MonoGame runtime.

This is a **known incompatibility** deferred to Phase 7 (binary writer). The Phase 6
deliverable is a structurally correct SPIRV-Cross transpilation pipeline.

## Strategies for Resolving the Incompatibility (Phase 7)

Three approaches are available:

### Strategy 1 — Post-process GLSL to MojoShader convention (required for drop-in compatibility)

After SPIRV-Cross emits GLSL, a post-processing pass renames individual uniform variables to
the `vs_uniforms_vec4[N]` / `ps_uniforms_vec4[N]` array form. This requires:

1. Querying SPIRV-Cross for each uniform's register index via `spvc_compiler_get_decoration`
   with `SpvDecorationBinding` or `SpvDecorationLocation`.
2. Replacing each uniform declaration in the GLSL text with the corresponding array element.
3. Replacing all use-sites in the shader body.

This is the **required strategy** for maintaining drop-in `mgfxc` compatibility, since it
matches what MonoGame's existing `.mgfx` loader expects.

### Strategy 2 — Patch MonoGame runtime

Modify the MonoGame OpenGL effect loader to use HLSL variable names instead of
`vs_uniforms_vec4` array lookups. This breaks compatibility with existing `.mgfx` files
compiled by the stock `mgfxc` tool and is therefore not viable for a drop-in replacement.

### Strategy 3 — UBO binding points

Use SPIRV-Cross's UBO (Uniform Buffer Object) support to pack uniforms into a single
buffer at a fixed binding point that MonoGame's OpenGL loader can find. This requires
MonoGame runtime changes to interpret the UBO layout and is a larger undertaking.

## Recommendation

Implement Strategy 1 in Phase 7 as part of the binary writer. The post-processing pass
should be integrated into `SpirvCrossGlslTranspiler` or added as a separate
`GlslUniformRemapper` class that runs after transpilation.
