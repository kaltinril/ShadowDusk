# Two-Source HLSL (a Vertex File and a Pixel File)

Many HLSL shaders live in two files, one per stage, with no `technique` block. MonoGame, KNI and FNA load an *effect*, which does need one. The two-source frontend takes the two texts, writes the technique for you, and compiles the pair to the same outputs a single `.fx` produces: MGFX v10/v11, KNIFX, FNA `fx_2_0`, on OpenGL, DirectX 11, DirectX 12 and Vulkan.

## In code

```csharp
using ShadowDusk.Compiler;
using ShadowDusk.Core;

var result = await HlslStageCompiler.CompileAsync(
    new EffectCompiler(),
    new HlslStageSources(
        VertexSource: File.ReadAllText("Sprite.vs.hlsl"),
        PixelSource: File.ReadAllText("Sprite.ps.hlsl"),
        VertexName: "Sprite.vs.hlsl",   // the names errors will carry
        PixelName: "Sprite.ps.hlsl",
        VertexEntry: "VSMain",
        PixelEntry: "PSMain"),
    new CompilerOptions { Target = PlatformTarget.OpenGL, Container = EffectContainer.Knifx });

if (result.IsSuccess)
    File.WriteAllBytes("Sprite.knifx", result.Value.Data);
```

Every <xref:ShadowDusk.Core.CompilerOptions> setting works as it does for a `.fx`. `HlslStageCompiler.Compile` is the synchronous twin, and `HlslStageCompiler.ValidateAsync` reports every error and warning for OpenGL and DirectX at once, like `ValidateAsync` does for a `.fx`. To see the merged effect, call `HlslStageFrontend.ConvertToFx`.

## On the command line

```sh
ShadowDuskCLI --vertex Sprite.vs.hlsl --pixel Sprite.ps.hlsl Sprite.mgfx /Profile:OpenGL --vertex-entry VSMain --pixel-entry PSMain
```

The only positional argument is the output file. See the [CLI reference](../cli/index.md).

## Entry points

HLSL does not mark which function is the entry point, so you name them (`VertexEntry` and `PixelEntry`, `main` by default). If both are the same name, as is common, the frontend renames them to `<name>_VS` and `<name>_PS` for you, at the definition and at calls only, never at a member access. If the name also appears on a preprocessor line (a `#define main ...`), renaming is not safe and the frontend stops with `SD0663` rather than guess.

## What happens when both files declare the same thing

The two files are merged into one effect, so a declaration both make must agree:

| Both files declare... | Result |
|---|---|
| the same text (ignoring whitespace and comments) | written once |
| a struct, typedef, constant, macro or function with different text | `SD0664` |
| a constant buffer, texture, sampler or uniform parameter with different text | `SD0665` (these are effect parameters, so both stages must declare them identically) |
| a function with the same name and a different parameter list | kept, as an overload |
| a parameter only one of them declares | kept: the effect has the union |

Each conflict is reported in the pixel file at its line, and the message names the vertex file's line.

## Errors point at your files

The merged effect carries a `#line` marker before every declaration, so a compiler error is reported in `Sprite.ps.hlsl` at the pixel file's own line and column, not in a generated file. (One exception: the FNA target's effect-build errors, `SD0303`, carry the merged effect's lines.) After a renamed entry point, a column on that same line shifts by the length of the suffix.

## What it does not check

- **Each file must compile on its own.** The merge does not verify that a function in one file uses only declarations that file makes, so a function that works only because the other file declares something compiles.
- **Macros leak across.** A macro one file defines is visible to the other.
- **Vertex outputs and pixel inputs are not compared.** The pipeline links them by semantic.
- **No `technique` blocks.** A source that has one is rejected (`SD0666`); an effect that already has a technique is a single `.fx`: give it to `EffectCompiler`.
- **One pass.** The frontend writes one technique with one pass.

## Proof

The pair's output is byte-identical to the same effect written by hand as one `.fx`, on OpenGL (MGFX v10 and v11), DirectX 11, DirectX 12, Vulkan, FNA, and KNIFX. That is a distinct evidence axis: no `mgfxc` reads two sources, so the claim is never `mgfxc`-equivalence of the input. See [Validation & the Evidence Ladder](../contributing/validation.md) and the validation matrix in the repository (`docs/validation-matrix.md`, section 8.0aa).

## Diagnostics

`SD0660` to `SD0667` and `X0012` are listed in the [diagnostic codes](../diagnostics.md).
