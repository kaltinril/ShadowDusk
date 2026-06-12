# How to use ShadowDusk in a KNI WebAssembly (Blazor) app

Compile `.fx` shaders **to `.mgfx` in the browser at runtime** and load them with
`new Effect(graphicsDevice, bytes)` — no server, no `mgfxc`, no native toolchain on
the user's machine. The faithful pinned-DXC→WASM pipeline is packaged so a consumer
**adds one package reference and wires nothing**.

---

## 1. What works (and what doesn't, yet)

| | Status |
|---|---|
| **KNI** (nkast's MonoGame fork) **Blazor WebAssembly + WebGL**, target **OpenGL** | ✅ supported — this is the path below |
| Output loads in real KNI WebGL `Effect` and renders like `mgfxc` | ✅ proven (10/10 corpus, headless) |
| MonoGame *proper* in the browser | ❌ MonoGame has no mature browser-WASM runtime; **use KNI** for WASM |
| **DirectX/DXBC** compiled *in the browser* (`PlatformTarget.DirectX`) | ✅ supported — as an **export** target: the `.mgfx` is byte-identical to the desktop compile, but a browser cannot *render* DXBC; it renders in your MonoGame WindowsDX game |
| **FNA fx_2_0 `.fxb`** compiled *in the browser* (`PlatformTarget.Fna`) | ✅ supported — likewise export-only (no D3D9 in a browser); renders in your FNA game |
| Vertex-shader-driven effects (the corpus is pixel-shader-only) | ⚠️ untested in WASM — tracked (backlog 17-VS) |

So: **KNI + Blazor WASM + OpenGL/WebGL**, pixel-shader effects — solid; that's the
*render-in-the-browser* path this guide targets. The DirectX/FNA rows compile through
the same pinned `vkd3d-shader` compiled to WebAssembly (never a substitute compiler);
use them to *export* artifacts from a browser tool — see
`samples/ShaderFiddle.Web` for a working export station.

---

## 2. Prerequisites

- **.NET 8 SDK** (the browser runtime is pinned to .NET 8 / emscripten 3.1.34).
- The **WASM tools workload**: `dotnet workload install wasm-tools`
- **KNI Blazor templates / packages.** This guide uses `nkast.Kni.Platform.Blazor.GL`
  version `4.2.9001.*` (the version this repo's working sample uses). Install KNI's
  templates per nkast's instructions (see https://github.com/nkast/MonoGame), or just
  **crib from the working sample in this repo**: `samples/ShaderFiddle.Web/` is a
  complete KNI Blazor-WASM app that already uses the package exactly as below — copy its
  `index.html`, `Program.cs`, and `ShaderFiddleGame.cs` for the KNI host plumbing
  (canvas + JS shims + the tick loop), which is the same for any KNI Blazor app.

---

## 3. Get the ShadowDusk packages

Add the `ShadowDusk.Wasm` package to your app — that single reference is all you need
(§4 shows the `.csproj`). It brings the compiler and the native DXC + SPIRV-Cross +
vkd3d-shader WASM modules transitively; there's nothing else to install and nothing to
copy into `wwwroot`.

### Consuming an unreleased / source build (local feed)

If you're building against a source checkout ahead of the published version, pack a local
NuGet feed from this repo. The faithful
DXC→WASM module (`dxcompiler.wasm`, ~17 MB) is gitignored in the package `wwwroot`, but
its source-of-truth is committed at `.wasm-build/dxc-wasm-out/`, so `restore` just
**copies** it (no rebuild); the same script also downloads the pinned
`vkd3d-shader.{js,wasm}` module (SHA-256-verified) into the package `wwwroot`:

```pwsh
# from the repo root
pwsh -File tools/restore.ps1          # or: ./tools/restore.sh   (copies dxcompiler.wasm into the package wwwroot)

# pack the 5 packages in the dependency chain (Wasm -> Compiler -> Core/HLSL/GLSL),
# all at one shared version — the repo's Directory.Build.props <Version>
$feed = "$PWD/local-feed"
dotnet pack src/ShadowDusk.Core/ShadowDusk.Core.csproj         -c Release -o $feed
dotnet pack src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj         -c Release -o $feed
dotnet pack src/ShadowDusk.GLSL/ShadowDusk.GLSL.csproj         -c Release -o $feed
dotnet pack src/ShadowDusk.Compiler/ShadowDusk.Compiler.csproj -c Release -o $feed
dotnet pack src/ShadowDusk.Wasm/ShadowDusk.Wasm.csproj         -c Release -o $feed
```

In your **consumer app**, add a `nuget.config` next to the `.csproj` pointing at that
feed (use the absolute path to the `local-feed` folder you packed into above):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="shadowdusk-local" value="/path/to/ShadowDusk/local-feed" />
  </packageSources>
</configuration>
```

---

## 4. Create the KNI Blazor WASM app

Create a KNI Blazor-WASM project from the KNI template (or copy `samples/ShaderFiddle.Web/`).
The project must target **`net8.0-browser`** (required for `[JSImport]`):

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net8.0-browser</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>  <!-- the [JSImport] generator needs /unsafe -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="nkast.Kni.Platform.Blazor.GL" Version="4.2.9001.*" />
    <PackageReference Include="ShadowDusk.Wasm" Version="*" />  <!-- the ONLY ShadowDusk line you add -->
  </ItemGroup>
</Project>
```

That single `ShadowDusk.Wasm` reference brings `ShadowDusk.Compiler`/`Core`/`HLSL`/`GLSL`
transitively **and** the native DXC + SPIRV-Cross + vkd3d-shader WASM modules (as Blazor
static web assets). You do **not** add anything to `wwwroot`, and you do **not** call
`JSHost.ImportAsync` — the library self-registers its modules on the first compile.

---

## 5. Compile a shader and render it

The API is `IShaderCompiler.CompileAsync(hlsl, options) → Result<CompiledShader, ShaderError[]>`.
In your KNI `Game` (or a Blazor component that holds one):

```csharp
using ShadowDusk.Core;     // CompilerOptions, PlatformTarget, Result, CompiledShader, ShaderError
using ShadowDusk.Wasm;     // WasmShaderCompiler
using Microsoft.Xna.Framework.Graphics;

private readonly IShaderCompiler _compiler = new WasmShaderCompiler();

// Call this when the user wants to (re)compile. First call lazily downloads the
// ~17 MB dxcompiler.wasm, so show a "compiling…" state.
public async Task<Effect?> CompileEffectAsync(string fxSource)
{
    var options = new CompilerOptions
    {
        Target         = PlatformTarget.OpenGL,   // WebGL path
        SourceFileName = "myshader.fx",            // shows up in error messages
        // MgfxVersion = 10 (default; what KNI's MGFXReader10 loads)
    };

    Result<CompiledShader, ShaderError[]> result = await _compiler.CompileAsync(fxSource, options);

    if (result.IsFailure)
    {
        foreach (ShaderError e in result.Error)
            Console.Error.WriteLine(e.FxcFormattedMessage);   // file(line,col): error CODE: message
        return null;
    }

    byte[] mgfx = result.Value.Data;                 // the .mgfx bytes
    return new Effect(GraphicsDevice, mgfx);         // load into the real KNI WebGL runtime
}
```

Then draw with it, exactly like a `mgfxc`-compiled effect — e.g. via `SpriteBatch`:

```csharp
// set parameters by name (null-safe), then draw
effect.Parameters["SpriteTexture"]?.SetValue(myTexture);
_spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp,
                   null, null, effect);
_spriteBatch.Draw(myTexture, destRect, Color.White);
_spriteBatch.End();
```

> **Multi-texture pixel shaders:** if your effect samples a *second* texture (a
> separate sampler slot), pin that slot's state before the draw, e.g.
> `GraphicsDevice.SamplerStates[1] = SamplerState.LinearClamp;` — `SpriteBatch` only
> sets slot 0, and WebGL vs desktop GL resolve an unset slot differently for
> non-power-of-two textures. (This is what the corpus "Dissolve" shader needs.)

That's the whole integration. The full working version is
`samples/ShaderFiddle.Web/{Pages/Index.razor.cs, ShaderFiddleGame.cs}`.

---

## 6. How it works (so you can debug it)

- `WasmShaderCompiler` runs the **same faithful pipeline as the desktop CLI**: HLSL → **DXC
  (compiled to WASM)** → SPIR-V → **SPIRV-Cross (WASM)** → managed reflect + MojoShader-dialect
  rewrite + MGFX writer → `.mgfx`. The in-browser SPIR-V is **byte-identical to desktop DXC**.
- The native modules ride inside the package and are served at
  `_content/ShadowDusk.Wasm/` (Blazor static web assets). `ShadowDusk.Wasm` calls
  `JSHost.ImportAsync` itself (in `WasmModuleRegistration`) against
  `../_content/ShadowDusk.Wasm/<file>` — so it works whether your app is hosted at the
  site root or a sub-path, with no consumer wiring.
- Each WASM module is **lazy-loaded by the first `CompileAsync` that needs it**, not at
  page boot — so app startup stays fast: the ~17 MB `dxcompiler.wasm` on the first
  OpenGL compile, the ~1.3 MB `vkd3d-shader.wasm` on the first DirectX/FNA compile.
  Serve your site with **HTTP compression** (brotli/gzip): on the wire they compress to
  ~6 MB and ~0.4 MB respectively.

---

## 7. Troubleshooting

| Symptom | Fix |
|---|---|
| Build error: `dxcompiler.wasm is missing` or `vkd3d-shader.{js,wasm} is missing` | Run `tools/restore.ps1`/`.sh` before pack (copies/downloads the modules into the package wwwroot). |
| `SD1902` on a DirectX/FNA compile | The `vkd3d-shader.{js,wasm}` module couldn't be loaded (e.g. not restored in a source build, or not served). Run `tools/restore.ps1`/`.sh` and check the browser fetched `_content/ShadowDusk.Wasm/vkd3d/vkd3d-shader.wasm`. |
| Restore can't find `ShadowDusk.Compiler` etc. | You only packed `Wasm` — pack **all five** projects (§3) into the local feed. |
| `new Effect(...)` throws on load | You're not on KNI (MonoGame proper has no v10 WebGL reader), or the `.mgfx` is for the wrong target. Use KNI + `PlatformTarget.OpenGL`. |
| First compile hangs/slow | That's the one-time ~17 MB `dxcompiler.wasm` download. Show a loading state; enable server compression. |
| Effect renders wrong only in the browser | Check sampler-slot state for multi-texture shaders (§5 note); compare against the desktop render of the *same* bytes. |
| `[JSImport]`/unsafe build error | Add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` and target `net8.0-browser`. |

---

## 8. Rebuilding the DXC→WASM module (only if you bump the DXC pin)

You don't need to — the built module is committed. But the full reproducible recipe is
`.wasm-build/DXC-WASM-BUILD.md` + `Invoke-DxcWasmBuild.ps1` (pinned DXC `e043f4a1` ==
Vortice.Dxc 3.3.4, emscripten 3.1.34). It's a multi-hour LLVM-fork build and is
**Windows/MSVC-only today**; a Linux/macOS rebuild + CI is planned future work (Phase 30 §16).
