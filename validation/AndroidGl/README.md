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

## The native dependencies (bundled into the APK `lib/<abi>/`)

The faithful OpenGL pipeline needs DXC (`libdxcompiler.so`) and SPIRV-Cross
(`libspirv-cross.so`) for each ABI. `tools/restore.ps1` / `tools/restore.sh` download all four
(SHA-256 pinned; DXC from release tag `native-dxc-android-1.7.2212.40-16k`, SPIRV-Cross from `native-spirv-cross-android-d8e3e2b1`) into `tools/dxc/<rid>/` and
`tools/spirv-cross/<rid>/`; the csproj bundles them via `<AndroidNativeLibrary>`:

| ABI | Used by | Shipped in a package? |
|---|---|---|
| `arm64-v8a` (`android-arm64`) | real devices | yes, `ShadowDusk.HLSL` / `ShadowDusk.GLSL` |
| `x86_64` (`android-x64`) | the emulator (and its CI lane) | no, restored for this harness only |

All four are reproducible CI builds, 16 KB page aligned: `tools/build-dxc-android.sh`
(`dxc-android-build.yml`, the pinned DXC commit) and `tools/build-spirv-cross-android.sh`
(`spirv-cross-android-build.yml`, the desktop's SPIRV-Cross commit). A missing native is reported as `SD0219`
(DXC) or `SD0103` (SPIRV-Cross), never a raw `DllNotFoundException`.

## The identity checks (CI)

`run-dxc-identity-checks.ps1` builds this app five times against one attached x86_64 device or
emulator and reads each verdict from logcat: the pinned natives compile and load an `Effect`; a
DXC or SPIRV-Cross whose GNU build id differs by one byte, or that is absent, is refused. The
first run also compiles the OpenGL fixture corpus ON the device (`CorpusCheck.cs`, started with
`am start ... --es mode corpus`) and requires its SPIR-V, GLSL and `.mgfx` to be byte-identical to
the committed desktop manifests; a positive control with one manifest hash changed must be
reported. CI runs
it on an API-34 emulator in `.github/workflows/android-emulator.yml` (label `run-android`, weekly,
and on relevant pushes to main). Locally (PowerShell 7, `adb` on PATH):

```powershell
./tools/restore.ps1
<sdk>/emulator/emulator -avd <x86_64 AVD> -no-window -no-audio   # in another shell
./validation/AndroidGl/run-dxc-identity-checks.ps1
```

## Build & run (needs the Android workload + a connected device/emulator)

```powershell
# compile SDK pinned to an installed API level via the net9.0-android35.0 TFM
dotnet build validation/AndroidGl/AndroidGl.csproj -c Debug -t:Run
adb logcat -s SHADOWDUSK   # watch the on-device compile outcome
```
