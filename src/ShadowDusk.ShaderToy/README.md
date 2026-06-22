# ShadowDusk.ShaderToy

A pure-managed, **zero-native** converter that turns a **ShaderToy** (or plain-GLSL) fragment shader into a self-contained HLSL **`.fx`** source you can compile for MonoGame, KNI, or FNA.

It accepts both entry conventions and auto-detects them (no flag): a ShaderToy `void mainImage(out vec4 fragColor, in vec2 fragCoord)` shader, and a plain-GLSL `void main()` fragment shader that writes `gl_FragColor` / a user-declared `out vec4` and reads `gl_FragCoord`. Unsupported constructs **fail loudly** with a located diagnostic (line/column) rather than producing a silently-wrong `.fx`.

This package is **standalone and optional** — it is *not* part of ShadowDusk's faithful `mgfxc`-replacement pipeline, and `ShadowDusk.Compiler` does not depend on it. Install it when you want to convert ShaderToy/GLSL shaders **in-process** (e.g. a web shader fiddle or an in-app importer). To then compile the resulting `.fx`, use [ShadowDusk.Compiler](https://www.nuget.org/packages/ShadowDusk.Compiler) (in-process library) or [ShadowDusk.Cli](https://www.nuget.org/packages/ShadowDusk.Cli) (the `ShadowDuskCLI` tool, which also accepts `.glsl` input directly).

## Usage

```csharp
using ShadowDusk.ShaderToy;

ConvertResult result = ShaderToyConverter.Convert(glslSource);

if (result.Success)
{
    string fx = result.Fx!;          // self-contained HLSL .fx source
    // result.UsedUniforms lists the ShaderToy built-ins (iTime, iChannel0, …)
    // plus any custom uniforms the shader drives each frame.
}
else
{
    foreach (ConvertDiagnostic d in result.Diagnostics)
        Console.Error.WriteLine($"{d.Severity} ({d.Line},{d.Column}): {d.Message}");
}
```

A batch **multipass** entry point (`MultipassConverter.Convert`) converts a ShaderToy multi-tab export (Buffer A-D + Image) into one `.fx` per pass plus a machine-readable wiring manifest; you drive the render graph yourself with MonoGame render targets (the converter accepts the syntax, it is not a ShaderToy runtime).

- Documentation: <https://kaltinril.github.io/ShadowDusk/>
- Source / issues: <https://github.com/kaltinril/ShadowDusk>
