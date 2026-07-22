# In-Memory Quickstart

This is the product in its purest form: add the `ShadowDusk.Compiler` package, call <xref:ShadowDusk.Core.IShaderCompiler.CompileAsync*>, and get `.mgfx` bytes back **in memory** — no temp files, no child process, no `mgfxc`.

## 1. Add the package

```sh
dotnet add package ShadowDusk.Compiler
```

## 2. Compile a shader

```csharp
using ShadowDusk.Compiler;
using ShadowDusk.Core;

string hlsl = File.ReadAllText("MyShader.fx");   // or any HLSL/.fx string

var compiler = new EffectCompiler();

Result<CompiledShader, ShaderError[]> result = await compiler.CompileAsync(
    hlsl,
    new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,   // or PlatformTarget.DirectX / PlatformTarget.Fna
        SourceFileName = "MyShader.fx",   // optional — improves error messages
    });

if (result.IsSuccess)
{
    byte[] mgfx = result.Value.Data;      // the .mgfx binary, ready to load
    File.WriteAllBytes("MyShader.mgfx", mgfx);
}
else
{
    foreach (ShaderError error in result.Error)
        Console.Error.WriteLine(error.FxcFormattedMessage);
}
```

The result is a [`Result<CompiledShader, ShaderError[]>`](xref:ShadowDusk.Core.Result`2) — a discriminated union. On success, <xref:ShadowDusk.Core.CompiledShader.Data> is the `.mgfx` byte array (and <xref:ShadowDusk.Core.CompiledShader.Target> echoes the platform). On failure you get an array of <xref:ShadowDusk.Core.ShaderError> with the file, line, column, code, and message exactly as the underlying compiler emitted them — the first entry is the fatal error; when earlier passes of the same effect had already compiled with warnings, those ride along after it so nothing is lost.

A successful compile can still have things worth knowing: <xref:ShadowDusk.Core.CompiledShader.Warnings> carries the underlying compiler's own warnings verbatim, plus ShadowDusk's GL portability findings (`SD0400`–`SD0402`) — constructs that compile fine but are known to fail or silently misbehave **at runtime** on narrower GL stacks (WebGL1 / KNI Reach, ANGLE Direct3D11 in Windows browsers, strict Mesa), where the engine's only signal is a generic draw-time exception. Warnings never gate output; the bytes are valid regardless.

## Shader not working? Validate it in one call

When a shader compiles for one target but fails for another (the classic report: works on DirectX, fails on OpenGL), <xref:ShadowDusk.Core.ShaderCompilerValidationExtensions.ValidateAsync*> compiles it for **OpenGL and DirectX** and reports every error and every warning, per target, with the underlying compiler's complete verbatim text. Printing the report is the whole story:

```csharp
ShaderValidationReport report = await compiler.ValidateAsync(hlsl);
Console.WriteLine(report);   // per-target status, every error with its source
                             // location, every warning, verbatim compiler text

if (!report.IsValid)
{
    // structured access: report.Targets[i].Target / .Succeeded / .Errors / .Warnings
}
```

`Validate`/`ValidateAsync` run the exact same pipeline as `Compile`/`CompileAsync` per target (never a fork), so what validates is precisely what compiles. Need FNA or Vulkan in the sweep? Pass an explicit target list via the overload — they are not in the default pair because FNA's SM2–3 dialect would false-alarm MonoGame/KNI shaders.

## 3. Load it into your game

The call is the same `new Effect(graphicsDevice, bytes)` for all three runtimes — only **which bytes** you pass differs by target.

For **MonoGame and KNI**, the bytes are a standard `.mgfx` blob (KNI reads the identical MGFX v10 container):

```csharp
var effect = new Effect(graphicsDevice, mgfx);   // MonoGame / KNI — .mgfx
```

It renders the same image `mgfxc`'s output would.

> **Newer runtimes (optional).** The default v10 container loads on MonoGame 3.8.2+ and KNI, so you usually do nothing. To target a newer runtime, set `CompilerOptions.MgfxVersion = 11` (MonoGame 3.8.5+) or `CompilerOptions.Container = EffectContainer.Knifx` (KNI v4.02+) — both render identically to v10. See [Parameters & Caveats](../guides/parameters-and-caveats.md#effect-container-mgfx-v10-default-and-opt-in-mgfx-v11--knifx).

For **FNA**, you pass the `.fxb` produced by `PlatformTarget.Fna` (see [below](#compiling-for-fna)); FNA loads it through MojoShader:

```csharp
var effect = new Effect(graphicsDevice, fxb);    // FNA — .fxb
```

It renders the same image `fxc /T fx_2_0`'s output would.

## Library vs CLI defaults

The **library** and the **CLI** default to different targets:

| Surface | Default target |
|---|---|
| Library — <xref:ShadowDusk.Core.CompilerOptions.Target> | **`OpenGL`** |
| CLI — `mgfxc /Profile` | **`DirectX_11`** |

So the code above (no explicit `Target`) compiles for **OpenGL**, while `mgfxc MyShader.fx out.mgfx` (no `/Profile`) compiles for **DirectX_11**. Always set the target explicitly to avoid surprises. See the [CLI Reference](../cli/index.md).

## Choosing the DirectX backend

When `Target = PlatformTarget.DirectX`, ShadowDusk emits DXBC (SM5) via a backend selected by <xref:ShadowDusk.Core.CompilerOptions.DxbcBackend>:

- `DxbcBackend.Vkd3d` (**default**) — the cross-platform `vkd3d-shader` backend; works on Linux/macOS/Windows and emits the same bytes on every OS. The vkd3d natives for all four desktop RIDs **ship inside the NuGet package** — consumers install nothing (self-contained; the repo's [restore script](restore-native-tools.md) is only for building ShadowDusk itself from source).
- `DxbcBackend.D3DCompiler` — the Windows-only `d3dcompiler_47` correctness oracle (opt-in; hard-fails off Windows).

You only set the property to opt in to the oracle:

```csharp
var options = new CompilerOptions
{
    Target = PlatformTarget.DirectX,
    DxbcBackend = DxbcBackend.D3DCompiler,   // opt in to the Windows-only oracle
};
```

See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md) for why DXC is **not** used here (it emits SM6 DXIL, which MonoGame's DX11 runtime cannot load).

## Compiling for FNA

[FNA](https://fna-xna.github.io/) doesn't read the `.mgfx` container — it loads the legacy **D3D9 fx_2_0** `.fxb` through MojoShader at runtime. Select it with `PlatformTarget.Fna`; everything else is the same call:

```csharp
var result = await compiler.CompileAsync(hlsl, new CompilerOptions
{
    Target = PlatformTarget.Fna,
    SourceFileName = "MyShader.fx",
});

// on success:
byte[] fxb = result.Value.Data;                  // the .fxb bytes (fx_2_0, SM <= 3)
var effect = new Effect(graphicsDevice, fxb);    // FNA's Effect, loaded via MojoShader
```

Notes specific to the FNA target:

- **Same package, no FNA-specific flag.** `ShadowDusk.Compiler` serves every target; only `Target` changes. FNA itself is added to your project as a **project reference**, not a NuGet — see [Installation → Targeting FNA](installation.md#targeting-fna).
- **Shader Model ≤ 3.** fx_2_0 caps at SM3; a shader needing SM4+ features fails loudly with a diagnostic instead of miscompiling.
- **Validated.** The output loads and renders **pixel-equivalent (max Δ ≤ 1/255) to `fxc /T fx_2_0` in real FNA** across the pixel-shader-only and vertex-shader-driven corpora — multi-pass effects and in-pass render states included.

## Reusing the compiler

`EffectCompiler` is cheap to construct and safe to reuse across many `CompileAsync` calls. Pass a `CancellationToken` to bound long compiles.
