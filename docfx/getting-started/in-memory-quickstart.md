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

The result is a [`Result<CompiledShader, ShaderError[]>`](xref:ShadowDusk.Core.Result`2) — a discriminated union. On success, <xref:ShadowDusk.Core.CompiledShader.Data> is the `.mgfx` byte array (and <xref:ShadowDusk.Core.CompiledShader.Target> echoes the platform). On failure you get an array of <xref:ShadowDusk.Core.ShaderError> with the file, line, column, code, and message exactly as the underlying compiler emitted them.

## 3. Load it into your game

The call is the same `new Effect(graphicsDevice, bytes)` for all three runtimes — only **which bytes** you pass differs by target.

For **MonoGame and KNI**, the bytes are a standard `.mgfx` blob (KNI reads the identical MGFX v10 container):

```csharp
var effect = new Effect(graphicsDevice, mgfx);   // MonoGame / KNI — .mgfx
```

It renders the same image `mgfxc`'s output would.

For **FNA**, you pass the `.fxb` produced by `PlatformTarget.Fna` (see [below](#compiling-for-fna)); FNA loads it through MojoShader:

```csharp
var effect = new Effect(graphicsDevice, fxb);    // FNA — .fxb
```

It renders the same image `fxc /T fx_2_0`'s output would.

## The default-target caveat (read this)

The **library** default and the **CLI** default differ:

| Surface | Default target |
|---|---|
| Library — <xref:ShadowDusk.Core.CompilerOptions.Target> | **`OpenGL`** |
| CLI — `mgfxc /Profile` | **`DirectX_11`** |

So the code above (no explicit `Target`) compiles for **OpenGL**, while `mgfxc MyShader.fx out.mgfx` (no `/Profile`) compiles for **DirectX_11**. Always set the target explicitly to avoid surprises. See the [CLI Reference](../cli/index.md).

## Choosing the DirectX backend

When `Target = PlatformTarget.DirectX`, ShadowDusk emits DXBC (SM5) via a backend selected by <xref:ShadowDusk.Core.CompilerOptions.DxbcBackend>:

- `DxbcBackend.D3DCompiler` (**default**) — the Windows-only `d3dcompiler_47` correctness oracle.
- `DxbcBackend.Vkd3d` — the cross-platform `vkd3d-shader` backend (the shipping reach backend; works on Linux/macOS/Windows). The vkd3d natives for all four desktop RIDs **ship inside the NuGet package** — consumers install nothing (self-contained since Phase 37 C; the repo's [restore script](restore-native-tools.md) is only for building ShadowDusk itself from source).

```csharp
var options = new CompilerOptions
{
    Target = PlatformTarget.DirectX,
    DxbcBackend = DxbcBackend.Vkd3d,   // cross-platform DXBC
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
