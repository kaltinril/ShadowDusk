# ShadowDusk.SkSL

A pure-managed, **zero-native** converter that turns a Skia **SkSL** runtime-effect shader (`half4 main(float2 coord)`) into a self-contained HLSL **`.fx`** source you can compile for MonoGame, KNI, or FNA. `uniform shader` children become textures, `uniform int`/`bool` become float parameters, and unsupported SkSL fails with a coded diagnostic (`SD0660` to `SD0673`) that says what to change.

Standalone and optional: it is not part of ShadowDusk's faithful `mgfxc`-replacement pipeline, and `ShadowDusk.Compiler` does not depend on it. It depends on [ShadowDusk.ShaderToy](https://www.nuget.org/packages/ShadowDusk.ShaderToy) (installed with it) and `ShadowDusk.Core`.

## Usage

With [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler) installed beside it, `ShaderConverter` reads SkSL with no setup. SkSL text has no marker, so name the source with a `.sksl` hint:

```csharp
var result = ShaderConverter.Compile(new EffectCompiler(), skslSource,
    new CompilerOptions { Target = PlatformTarget.OpenGL }, "tint.sksl");
```

Or convert on its own and compile the `.fx` yourself:

```csharp
using ShadowDusk.ShaderToy;
using ShadowDusk.SkSL;

ConvertResult result = SkSLToFxConverter.Convert(skslSource);
if (!result.Success)
    foreach (ConvertDiagnostic d in result.Diagnostics)
        Console.Error.WriteLine($"{d.Code} ({d.Line},{d.Column}): {d.Message}");
```

A trimmed application that cannot have the assembly loaded by name calls `SkSLFrontend.Register()` itself. `SkSLToFxConverter` documents the host contract (`SpriteBatch`, `iResolution`, child texture and texel-size parameters, the premultiplied and straight-alpha techniques).
