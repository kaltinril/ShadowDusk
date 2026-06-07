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
        Target = PlatformTarget.OpenGL,   // or PlatformTarget.DirectX
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

## 3. Load it into MonoGame

The bytes are a standard `.mgfx` blob — feed them straight to MonoGame's `Effect`:

```csharp
var effect = new Effect(graphicsDevice, mgfx);
```

It renders the same image `mgfxc`'s output would.

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
- `DxbcBackend.Vkd3d` — the cross-platform `vkd3d-shader` backend (the shipping reach backend; works on Linux/macOS/Windows). Requires the restored vkd3d native — see [Restore Native Tools](restore-native-tools.md).

```csharp
var options = new CompilerOptions
{
    Target = PlatformTarget.DirectX,
    DxbcBackend = DxbcBackend.Vkd3d,   // cross-platform DXBC
};
```

See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md) for why DXC is **not** used here (it emits SM6 DXIL, which MonoGame's DX11 runtime cannot load).

## Reusing the compiler

`EffectCompiler` is cheap to construct and safe to reuse across many `CompileAsync` calls. Pass a `CancellationToken` to bound long compiles.
