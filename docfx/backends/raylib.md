# raylib (Raylib-cs)

raylib has no effect container: a raylib shader is GLSL text loaded with `LoadShaderFromMemory`. ShadowDusk converts a single-pass, pixel-only `.fx` to a `glsl330` fragment shader that runs behind raylib's built-in vertex shader, through the same front half every target uses:

```text
HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL → [raylib convention mapper] → glsl330
```

There is no reference compiler for raylib, so this route is judged by rendered-image fidelity in real Raylib-cs against the same `.fx` built for OpenGL and drawn in real MonoGame DesktopGL, never by equivalence with `mgfxc`.

## Converting

```csharp
using ShadowDusk.Compiler.Raylib;

var result = RaylibConverter.Convert(fxSource, new RaylibConvertOptions { SourceName = "Outline.fx" });
if (result.IsFailure)
    throw new InvalidOperationException(string.Join("\n", result.Error.Select(e => $"{e.Code}: {e.Message}")));

RaylibShader converted = result.Value;
Shader shader = Raylib.LoadShaderFromMemory(null, converted.FragmentShader);
```

The interface is raylib's own: `TEXCOORD0` is `fragTexCoord`, `COLOR0` is `fragColor` (the draw call's tint), the output is `finalColor`, and the texture on unit 0 is `texture0`, the one the draw call binds. `RaylibShader.Samplers` lists every sampler (bind the others with `SetShaderValueTexture`, and apply any baked `sampler_state`, reported as `SD0637`, with `SetTextureFilter`/`SetTextureWrap`). `RaylibShader.Uniforms` lists every other uniform under the name to pass to `GetShaderLocation`.

What raylib's fixed model cannot hold is refused with `SD0630`–`SD0636`: more than one technique or pass, render states, a vertex shader, an interpolant other than `TEXCOORD0`/`COLOR0`, constructs whose value depends on the render target's Y orientation, and matrix uniforms.

## Declared defaults

The fragment shader declares every uniform **without a value**, so a uniform you do not set reads as zero in raylib, while MonoGame applies the `.fx`'s declared default (`float Thickness = 1.0;`) from the `.mgfx`. `RaylibUniform.DefaultValue` gives you that default so you can apply it yourself:

```csharp
foreach (RaylibUniform u in converted.Uniforms)
{
    if (u.DefaultValue is not { } values)
        continue;                                  // no initializer
    int loc = Raylib.GetShaderLocation(shader, u.Name);
    int count = Math.Max(1, u.ArrayLength);
    switch (u.GlslType)
    {
        case "float": Raylib.SetShaderValueV(shader, loc, values.ToArray(), ShaderUniformDataType.Float, count); break;
        case "vec2":  Raylib.SetShaderValueV(shader, loc, values.ToArray(), ShaderUniformDataType.Vec2, count); break;
        case "vec3":  Raylib.SetShaderValueV(shader, loc, values.ToArray(), ShaderUniformDataType.Vec3, count); break;
        case "vec4":  Raylib.SetShaderValueV(shader, loc, values.ToArray(), ShaderUniformDataType.Vec4, count); break;
        // int/ivecN (and uint/uvecN, which a bool global becomes) take the same values as integers.
        case "int":   Raylib.SetShaderValueV(shader, loc, values.Select(v => (int)v).ToArray(), ShaderUniformDataType.Int, count); break;
        case "ivec2": Raylib.SetShaderValueV(shader, loc, values.Select(v => (int)v).ToArray(), ShaderUniformDataType.IVec2, count); break;
        // ... ivec3, ivec4, and the unsigned types the same way
    }
}
```

`DefaultValue` holds the same values, in the same layout, as `ParameterReflection.DefaultValue` on the `.mgfx` route for the same source: array element after array element, each element's components in order, no padding (exactly what `SetShaderValueV` takes with a count of `max(1, ArrayLength)`); `int` as a float, `uint` by its unsigned value, `bool` as 0 or 1 (a `bool` global is a `uint` uniform, because SPIR-V has no bool in a uniform block). It is `null` when the global has no initializer, or when the initializer could not be evaluated or does not match the uniform (the conversion then carries an `SD0017` warning). The defaults are reported beside the shader, never written into it: the fragment shader text is the same whether or not the `.fx` declares initializers.

## Slang input

`.slang` input converts through `RaylibConverter.ConvertSlang` (built-in subset) or `SlangCompiler.ConvertToRaylib` in `ShadowDusk.Slang` (real slangc). Both feed the same converter.

## Not emitted

raylib's web profile (`glsl100`, GLES 2) is not emitted; only desktop `glsl330`.
