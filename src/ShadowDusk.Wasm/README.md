# ShadowDusk.Wasm

**In-browser HLSL shader compiler for MonoGame / KNI / FNA**, for **Blazor WebAssembly** (`net8.0-browser`) apps. Compile `.fx` shaders to MonoGame/KNI `.mgfx` bytes — or DirectX `.mgfx` / FNA `.fxb` for download — **at runtime, in the browser**: no server round-trip, no `fxc.exe`, no `mgfxc`, no native install.

It runs the same faithful pipeline as the desktop compiler (pinned **DXC → SPIR-V → SPIRV-Cross → GLSL** for OpenGL/WebGL; pinned **vkd3d-shader → DXBC / D3D9 bytecode** for DirectX/FNA), so the in-browser output is **byte-identical** to the desktop/CLI output. The DXC + SPIRV-Cross + vkd3d-shader WebAssembly modules ride **inside this package** as Blazor static web assets and self-register — **you wire nothing.**

## Install

```
dotnet add package ShadowDusk.Wasm
```

(or search **ShadowDusk** in Visual Studio's NuGet UI and install **ShadowDusk.Wasm**.)

The `dxcompiler.wasm` / `spirv-cross.wasm` / `vkd3d-shader.wasm` modules ship in the package and are served automatically at `_content/ShadowDusk.Wasm/…` — there is nothing to copy or configure.

## Use (KNI / MonoGame Blazor WASM)

```csharp
using ShadowDusk.Core;
using ShadowDusk.Wasm;

// 1. Compile .fx -> .mgfx bytes, in the browser, at runtime.
IShaderCompiler compiler = new WasmShaderCompiler();
var result = await compiler.CompileAsync(fxSource, new CompilerOptions
{
    Target = PlatformTarget.OpenGL,   // WebGL / KNI
});

if (result.IsFailure)
{
    foreach (var e in result.Error)
        Console.Error.WriteLine($"{e.Code}: {e.Message}");
    return;
}

byte[] mgfx = result.Value.Data;

// 2. Load the bytes into a real KNI/MonoGame Effect and render.
var effect = new Effect(graphicsDevice, mgfx);
```

That's it: search → install → call `WasmShaderCompiler.CompileAsync` → feed the bytes to `new Effect(gd, bytes)`.

## DirectX & FNA — export targets

The same `CompileAsync` also accepts `PlatformTarget.DirectX` (DX11 SM5 DXBC `.mgfx`) and `PlatformTarget.Fna` (D3D9 fx_2_0 `.fxb`) in the browser, via the pinned `vkd3d-shader` compiled to WebAssembly — the bytes are identical to a desktop compile. These are **export** targets: a browser cannot *render* DXBC or D3D9 bytecode, so use them to offer downloads (e.g. a shader-fiddle "export" button) that render in the user's MonoGame WindowsDX / FNA game. If the vkd3d module is genuinely absent the compile fails loudly with diagnostic `SD1902`.

## Notes

- **Target framework:** `net8.0-browser` (Blazor WebAssembly / KNI web).
- **Self-contained:** the WASM native modules are inside the package; first use downloads them with your app's `_framework`/`_content` assets.
- **Lazy, per-target download:** each module is fetched by the first compile that needs it — `dxcompiler.wasm` (~17 MB raw, ~6 MB compressed) for OpenGL/WebGL, `vkd3d-shader.wasm` (~1.3 MB raw, ~0.4 MB compressed) for DirectX/FNA. Serve with HTTP compression.
- **KNI HiDef / WebGL2:** a single `.mgfx` loads in both KNI Reach (WebGL1) and HiDef (WebGL2 / GLSL ES 3.00) — no flag, no separate build.
- **Output container:** the default **MGFX v10** loads on every MonoGame/KNI runtime; the same opt-in newer containers as the desktop library are available via `CompilerOptions` (`MgfxVersion = 11` for MonoGame 3.8.5+, `Container = EffectContainer.Knifx` for KNI v4.02+).
- **Output parity:** byte-identical to the desktop/CLI compiler for the same source + target (machine-verified in a real browser over the full byte-identity corpus).

See the [ShadowDusk repository](https://github.com/kaltinril/ShadowDusk) for the full pipeline, samples (`samples/ShaderFiddle.Web`), and the desktop library (`ShadowDusk.Compiler`) / `mgfxc` CLI tool.
