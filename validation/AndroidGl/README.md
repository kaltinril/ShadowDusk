# validation/AndroidGl - on-device runtime-compile harness (Phase 50)

A real **.NET-for-Android MonoGame app** that proves the Phase 50 product capability: a user's
shader **text** is compiled to a MonoGame `.mgfx` **in memory, at runtime, on the device** via
ShadowDusk's `EffectCompiler`, and loaded into a live `Effect`. No host precompile, no `.xnb`,
no content pipeline - text to renderable Effect, live, on Android (the "shader fiddle on a
phone" shape).

It is the Android analogue of `validation/Candidate`, and like every `validation/*` harness it
is **out-of-band** (not in `ShadowDusk.slnx`) because it needs the Android workload.

## What it does

`FiddleGame.LoadContent` calls
`new EffectCompiler().CompileAsync(hlslString, new CompilerOptions { Target = PlatformTarget.OpenGL })`
and loads the resulting bytes into `new Effect(GraphicsDevice, mgfx)`. The outcome is reported:

- **logcat** tag `SHADOWDUSK` (`adb logcat -s SHADOWDUSK`),
- **clear colour**: GREEN = compiled + Effect loaded on device, ORANGE = the compiler ran but
  rejected the shader, RED = a native (DXC / SPIRV-Cross) is missing.

## The two native dependencies (bundled into the APK `lib/arm64-v8a/`)

The faithful OpenGL pipeline needs two `android-arm64` native `.so` files. The csproj bundles
them via `<AndroidNativeLibrary>` (Exists()-gated, so the app builds before they land):

| Native | How to produce it | Status |
|---|---|---|
| `libspirv-cross.so` | `spirv-cross-android-build.yml` (NDK CMake), or locally: `cmake -S SPIRV-Cross -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<NDK>/build/cmake/android.toolchain.cmake -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-21 -DSPIRV_CROSS_SHARED=ON`, then copy `libspirv-cross-c-shared.so` -> `tools/spirv-cross/android-arm64/libspirv-cross.so`. | **Easy. Built.** |
| `libdxcompiler.so` | `dxc-android-build.yml` (the LLVM-fork NDK cross-compile; lead = `hexops/mach-dxcompiler` aarch64 + an `.android` target). Copy to `tools/dxc/android-arm64/libdxcompiler.so`. | **The wall (long pole).** No prebuilt exists anywhere. |

Until `libdxcompiler.so` exists, the on-device compile fails at the **first** native call
(HLSL -> SPIR-V is DXC) with `DllNotFoundException` -> the harness shows RED and names the
missing library in logcat. That is the honest, demonstrated state: everything is wired and
SPIRV-Cross is present; **DXC for Android is the sole remaining blocker.**

## Build & run (needs the Android workload + a connected device/emulator)

```powershell
# compile SDK pinned to an installed API level via the net9.0-android35.0 TFM
dotnet build validation/AndroidGl/AndroidGl.csproj -c Debug -t:Run
adb logcat -s SHADOWDUSK   # watch the on-device compile outcome
```
