# SkiaSharp (SkSL)

Skia runtime effects are **SkSL source**, compiled by Skia itself at runtime (`SKRuntimeEffect.CreateShader`). They have no vertex stage and no varyings: a runtime effect is one fragment function that gets a pixel coordinate and its uniforms. ShadowDusk converts a pixel-only `.fx` to that shape through the same front half every target uses:

```text
HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL → [SkSL convention mapper] → SkSL
```

There is no reference compiler for SkSL, so this route is judged by rendered-image fidelity in real SkiaSharp (including against real KNI drawing the same `.fx`), never by equivalence with `mgfxc`.

## Converting

```csharp
using ShadowDusk.Compiler.Sksl;

var result = SkslConverter.Convert(fxSource, new SkslConvertOptions { SourceName = "Outline.fx" });
if (result.IsFailure)
    throw new InvalidOperationException(string.Join("\n", result.Error.Select(e => $"{e.Code}: {e.Message}")));

SkslConversion conversion = result.Value;
using SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
```

The result carries everything the draw code binds:

| Member | What it is |
|---|---|
| `SkslText` | The runtime-effect source. |
| `ChildShaders` | One `uniform shader` child per sampled texture, named after the HLSL texture, in declaration order. |
| `SynthesizedUniforms` | Uniforms the converter invented: `ShadowDusk_Color` (the sprite tint, for a shader that reads `COLOR0`) and `ShadowDusk_Resolution` (the element's pixel size, for a shader that uses its UV arithmetically or samples at a computed coordinate). Set them every draw. |
| `Uniforms` | Every non-child uniform the SkSL declares, in order (`SkslUniform`: `Name`, `SkslType`, `ArrayLength`, `DefaultValue`). |
| `Warnings` | Non-fatal findings, including `SD0614` for each synthesized uniform and `SD0017` for an initializer that could not be evaluated. |

Shapes SkSL cannot hold (a vertex shader, a custom interpolant, derivatives, LOD sampling, more than one pass) are refused with `SD0610`–`SD0615`, never approximated.

## Declared defaults

The SkSL declares every uniform **without a value**, so a uniform you do not set reads as zero in Skia. An `.fx` written for MonoGame often relies on its declared defaults instead (`float Thickness = 1.0;`): MonoGame and KNI read those from the `.mgfx`. `SkslUniform.DefaultValue` gives you the same value, so you can apply it yourself:

```csharp
var uniforms = new SKRuntimeEffectUniforms(effect);
foreach (SkslUniform u in conversion.Uniforms)
{
    if (u.DefaultValue is not { } values)
        continue;                                  // no initializer, or a synthesized uniform
    bool isInt = u.SkslType is "int" || u.SkslType.StartsWith("ivec", StringComparison.Ordinal);
    if (isInt)
        uniforms[u.Name] = values.Count == 1 ? (int)values[0] : values.Select(v => (int)v).ToArray();
    else
        uniforms[u.Name] = values.Count == 1 ? values[0] : values.ToArray();
}
// ...then set the uniforms your game actually controls, and the synthesized ones, over the defaults.
```

`DefaultValue` holds the same values, in the same layout, as `ParameterReflection.DefaultValue` on the `.mgfx` route (`CompiledShader.Parameters`) for the same source, so one lookup by name serves both:

- **Layout:** array element after array element, each element's components in logical row-major order (the HLSL cell at row `r`, column `c` of a `floatRxC` is at `r * C + c`), no padding. That is exactly the float list Skia wants: SkSL packs uniforms tightly, and the converted `matN` holds the HLSL rows as its columns, so even a matrix goes in as is, `row_major` or not.
- **Values:** `int` converted to float, `uint` by its unsigned value, `bool` as 0 or 1.
- **`null`:** the global has no initializer; the initializer could not be evaluated or does not match the uniform's shape (the conversion then carries an `SD0017` warning); or the uniform is synthesized. Textures are child shaders, not uniforms, and are not listed.

The defaults are reported beside the shader, never written into it: the SkSL text is the same whether or not the `.fx` declares initializers.

A global named after a GLSL reserved word comes out of SPIRV-Cross with a leading underscore (`input` becomes `_input`); `Uniforms` lists it under that name, with the default `input` declares. SkSL has no `uint` or `bool` uniforms and no non-square matrices, so Skia's own compiler rejects a converted shader that declares one.

## Slang input

`.slang` input converts through `SkslConverter.ConvertSlang` (the built-in HLSL-compatible subset, no extra package) or `SlangCompiler.ConvertToSksl` in `ShadowDusk.Slang` (real slangc). Both feed the same converter, so every rule above applies unchanged.
