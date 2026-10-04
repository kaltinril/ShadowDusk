# Runtime Compilation on Android (On-Device)

The **same faithful pipeline** runs on Android through .NET for Android, so a MonoGame or KNI Android game can compile `.fx` → `.mgfx` **at runtime, on the device, in memory** — a shader fiddle on a phone. Add the package, call the API; the Android native compilers ride inside the package, exactly like every other platform.

This is the Android counterpart of the [in-browser path](in-browser-kni-blazor.md): the constrained host runs the *same* DirectXShaderCompiler + SPIRV-Cross pipeline, cross-compiled for Android (`arm64-v8a` for devices, `x86_64` for emulators), **never a substitute compiler** — so the `.mgfx` it produces is identical to the desktop build and loads + renders the same.

## When to use it

On-device runtime compilation is for shaders whose source isn't known until runtime:

- a live shader-editing / shader-fiddle UI on a phone or tablet,
- user-generated or hot-reloaded shaders,
- mod support.

For shaders that are fixed at ship time, prefer **build-time precompilation**: compile `.fx` → `.mgfx` on your dev box or CI and ship the bytes in the app. ShadowDusk's output is OS-independent, so the artifact that renders on desktop renders identically on the device, with no native compiler in the app at all. On-device compilation is the additive capability for the runtime-source cases.

## Integration recipe

The call is identical to every other platform — `new EffectCompiler()` is seamless on Android (no flag, no injection):

```csharp
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Microsoft.Xna.Framework.Graphics;

// HLSL .fx the user typed, downloaded, or hot-reloaded.
string fxSource = /* ... */;

var result = await new EffectCompiler()
    .CompileAsync(fxSource, new CompilerOptions { Target = PlatformTarget.OpenGL });

if (result.IsFailure)
{
    // Each ShaderError carries Code + a file:line:col Message — surface it in your editor UI.
    foreach (var e in result.Error)
        ShowError($"{e.Code}: {e.Message}");
    return;
}

byte[] mgfx = result.Value.Data;
Effect effect = new Effect(GraphicsDevice, mgfx);   // a live Effect, compiled on the device
```

`Target = PlatformTarget.OpenGL` because Android renders through OpenGL ES; the GLSL `.mgfx` loads in MonoGame's and KNI's GL runtime. A synchronous `Compile(...)` overload exists for synchronous call sites (for example a custom `ContentManager.Load<Effect>`), and an `InitializeAsync()` is available for the warm-once pattern shared with the in-browser host.

## What it needs

- A standard `net*-android` MonoGame or KNI project.
- The `ShadowDusk.Compiler` package. Its Android native compilers (DXC + SPIRV-Cross for `arm64-v8a`) ride inside the package as native assets and land in your APK automatically — the same "add the package, call the API" setup as desktop, no separate install. Real devices (`arm64-v8a`) are covered; the package ships no `x86_64` natives, so on an x86\_64 emulator image a DXC-backed compile returns `SD0219` unless the app bundles ShadowDusk's own x86\_64 build.
- ShadowDusk checks that the DXC it loaded is its own pinned build, by the build id of the library as mapped from the APK. If another package in your app ships its own `libdxcompiler.so` for the same ABI and the build keeps that one, compiles return `SD0219` instead of running a different compiler.

## Notes & caveats

- **OpenGL ES only.** Compile with `PlatformTarget.OpenGL`. The DirectX (DXBC) and FNA (`fx_2_0`) targets are desktop concerns and are not part of the Android runtime.
- **Identical output.** The `.mgfx` is byte-identical to the desktop build, so behavior and rendering match `mgfxc` exactly (see [What "faithful" means](../architecture/the-faithful-pipeline.md)).
- **Reflection on Android.** The compiler reflects shader parameters with its pure-managed SPIR-V reflector on Android (the native DXIL-reflection oracle used on desktop isn't available on the .NET-for-Android runtime). This is automatic and produces the same metadata; you don't configure anything.
- **Same code on every host.** The snippet above is unchanged from desktop and from the [in-browser (WASM)](in-browser-kni-blazor.md) host — a shader-fiddle app written against `IShaderCompiler` runs the identical compile path on Windows/Linux/macOS, in the browser, and on Android.

## Consuming an unreleased build

If you are building ShadowDusk from source rather than from a published package, the Android native binaries are produced by the NDK build scripts and staged under `tools/`:

- **SPIRV-Cross** — an NDK CMake cross-compile of the **same SPIRV-Cross commit the desktop runs** (`d8e3e2b1`, what Silk.NET.SPIRV.Cross.Native 2.23.0 is built from), so the GLSL an Android device produces is the desktop's. One script pins the commit, the NDK (r27c), the API level and every flag, and its output is byte-reproducible:

  ```
  tools/build-spirv-cross-android.sh <NDK r27c dir> <out dir>
  ```

  It writes `<out>/android-arm64/libspirv-cross.so` and `<out>/android-x64/libspirv-cross.so`; stage them under `tools/spirv-cross/<rid>/`. (`tools/restore.*` downloads the CI-built, SHA-256-pinned copies instead.)

- **DXC** — the pinned DirectXShaderCompiler cross-compiled for the NDK (a port of the WebAssembly recipe; it reuses the same host tablegen tools and CMake cross-compile patches):

  ```
  ./.wasm-build/build-dxc-android.ps1 -Abi arm64-v8a   # and -Abi x86_64 for emulators
  ```

  This stages a stripped `libdxcompiler.so` as `tools/dxc/<rid>/libdxcompiler.so`.

Bundle the per-ABI `.so` into your APK with `<AndroidNativeLibrary Include="..." Abi="arm64-v8a">` items (the published package does this for you). A worked, end-to-end harness — compile a shader on the device and render with it — lives in `validation/AndroidGl`.

## Slang on Android

The free `.slang` subset built into `ShadowDusk.Compiler` works on-device like any `.fx`. Full Slang (the optional `ShadowDusk.Slang` package, real `slangc`) does **not** run on-device yet: upstream ships no Android `slangc`, and an Android app can only start a packaged executable when native libraries are extracted, which a default Release build does not do (measured on an API-34 emulator, issue #257). Compile full-Slang shaders at build time, or use the subset. The planned on-device route runs `slangc` in-process instead of spawning it (see [Phase 67](https://github.com/kaltinril/ShadowDusk/blob/main/plan/PHASE-67-slang-in-process-wasm.md)).
