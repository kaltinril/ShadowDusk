# Phase 50 — Android Runtime Target Support (compile `.fx` on-device)

**Status:** 🟢 **On-device runtime compile PROVEN (2026-06-28)** — a real `pixel_7` API-34
emulator compiled an HLSL string to a `.mgfx` and loaded it into a live MonoGame `Effect`
entirely on the device, via seamless plain `new EffectCompiler()` (see §6.2). Productionization
follow-ups (host the natives + flip the restore pins, CI workflow, on-device pixel-vs-`mgfxc`
render diff) are open. (Track: *OS / delivery-shape breadth, post-1.0*.) Today ShadowDusk's
self-contained library runs on **Windows, Linux, and macOS** (and the browser via WASM); this
phase **adds Android to that OS list** so the *same* faithful pipeline compiles `.fx` → `.mgfx`
**at runtime, on an Android device**, inside a MonoGame or KNI game. Like [Phase 31](PHASE-31-metal-msl-backend.md) (Metal), the *easy*
half is the managed code; the *hard* half is producing the native binaries for the new
host and defining how the result is render-validated against the reference compiler.

> **Pre-work done (2026-06-28):** this phase doc is the output of a three-agent feasibility
> evaluation (native-deps-on-Android, .NET/MonoGame-Android runtime, in-repo RID/loader
> audit). The headline finding: **there are no hard architectural blockers** — the product
> library is **100% in-process P/Invoke with zero child-process spawning and zero temp-file
> I/O in the compile path**, which is exactly the property Android requires (it forbids
> `fork`/`exec` of arbitrary executables). What remains is bounded native-build + loader +
> packaging + validation work, captured below.

**Depends on:**
- [Phase 6](DONE/PHASE-6-spirv-cross-glsl-transpilation.md) — the SPIRV-Cross C-API P/Invoke (`SpvcLoader`/`SpvcNative`); Android reuses it with an added RID branch.
- [Phase 4](DONE/PHASE-4-dxc-integration.md) / [Phase 37](DONE/PHASE-37-cross-platform-native-availability.md) — the DXC frontend and the **per-RID native-binary hosting + restore + pack + `ResolveLibrary` loader** machinery (`DxcLoader`); Android is one more RID through that same machinery.
- [Phase 23](DONE/PHASE-23-in-browser-compilation.md) / the `.wasm-build/` DXC cross-compile recipe — **the precedent that the pinned DXC can be cross-compiled for a non-desktop host** (`build-dxc-wasm.ps1`, `dxc-wasm-patches.txt`). Its host-tablegen split + the two CMake cross-compile patches port directly to an Android NDK toolchain.
- [Phase 17](DONE/PHASE-17-monogame-runtime-validation.md) — the OpenGL render-equivalence bar that an Android render proof inherits.

**Blocks:** nothing on the path to 1.0. Android is an **OS-breadth** target, parallel to the existing desktop OSes, not a prerequisite for any current consumer.

> The product is the in-memory `IShaderCompiler` library (see `CLAUDE.md` → THE PURPOSE).
> Android widens *which OS* that library runs on; it does **not** change what the product is,
> the pipeline, the MGFX v10 default, or any output byte. Per THE PURPOSE, **one faithful
> pipeline everywhere — no substitute compilers**: Android must run the *same* DXC +
> SPIRV-Cross legs, just compiled for `android-arm64`. A host that cannot yet run a faithful
> component is *not done* — never a licence to swap in a different compiler.

---

## 0. The two shapes — and why this phase is the *reach* one

There are two ways an Android MonoGame/KNI consumer can use ShadowDusk. Be explicit about
which one this phase is about, because only one of them needs any new work:

1. **Build-time precompile (works today, zero Android work).** The consumer compiles their
   `.fx` → `.mgfx` on a **host** (dev box / CI on Windows/Linux/macOS, all already proven)
   and ships the `.mgfx` in the APK. ShadowDusk's output bytes are **OS-independent**
   (`CrossHostByteIdentityTests`), so the artifact that renders correctly on desktop renders
   correctly on the device. **This is the idiomatic Android path** and the one most shipping
   games should use — running a heavy HLSL→SPIR-V→GLSL toolchain on a phone is *not* the
   norm; offline precompilation is.

2. **On-device runtime compile (this phase).** The game calls
   `IShaderCompiler.CompileAsync(fx)` **on the device** to produce `.mgfx` at runtime — for
   user-generated shaders, hot-reloaded/live-edited shaders, a shader-fiddle-style app, or
   mod support. This is a genuine but **niche / reach** capability, the direct analogue of
   the WASM in-browser compile (which `CLAUDE.md` frames as "a sample of reach, never the
   product"). It is the only shape that needs the native + loader + packaging work below.

**Decision to record:** Android **build-time precompile is already supported and should be
the documented default for shipping games.** This phase scopes the **on-device runtime
compile** capability as additive reach. Neither shape changes existing output (purely
additive, the seamless rule holds).

---

## 1. Why there are no hard blockers (the in-repo audit result)

Android's hard constraints for "run a compiler at runtime" are: (a) you cannot `fork`/`exec`
arbitrary executables (SELinux + W^X), and (b) native code must load from the **read-only
APK** `lib/<abi>/`, never be extracted to and executed from writable storage (Android 10+
"safer dynamic code loading"). The audit confirmed ShadowDusk already satisfies (a) and is a
small change away from (b):

- **Zero child-process spawning in the product.** A grep of `src/` for `Process.Start` /
  `ProcessStartInfo` / `new Process(` returns **nothing**. Every native leg is in-process
  P/Invoke:
  - **DXC** via `Vortice.Dxc` → `DxcNativeInterop` raw vtable calls (`DxcShaderCompiler.cs`, wrapped in `Task.Run`) — no `dxc` exe.
  - **SPIRV-Cross** via `NativeLibrary.SetDllImportResolver` + `DllImport "spirv-cross"` (`SpvcLoader`/`SpvcNative`).
  - **vkd3d** via the same resolver pattern (not needed for Android, see §3).
  The only `Process.Start` calls live in `tests/`, `tools/`, and `validation/`, which **do
  not ship** in the consumer NuGet.
- **No temp-file I/O in the compile path.** No `Path.GetTempPath`/`GetTempFileName` anywhere
  in `src/`; includes are resolved in-memory (`DxcIncludeHandler` marshals bytes via
  `Marshal.AllocHGlobal`, not disk). Android's sandboxed filesystem is therefore a non-issue
  for the compile itself.
- **The managed side is ordinary .NET 8.** `async`/`Task`, `Result<T,E>`, no dynamic codegen
  in the hot path — all supported on **.NET for Android (Mono)**, which is what MonoGame
  3.8.2 and KNI Android games already run on (`net8.0-android`, rendering via OpenGL ES).

So the question reduces from the *intractable* "can we exec a toolchain on Android?" to the
*tractable* "can we build SPIRV-Cross + DXC as Android `.so` per ABI and load them in-process
from the APK under W^X?".

---

## 2. The pipeline shape on Android (OpenGL only)

Android MonoGame/KNI games render via **OpenGL ES 2/3 (GLSL ES)**, loaded from the **OpenGL
`.mgfx` backend** — so the relevant target is `PlatformTarget.OpenGL` (the default library
target, `src/ShadowDusk.Core/PlatformTarget.cs:22`). DirectX (DXBC) and FNA (D3D9 fx_2_0)
are **irrelevant on Android**. The Android leg is therefore the existing OpenGL branch, host-
compiled for `android-arm64`:

```
HLSL → DXC(android-arm64) → SPIR-V → SPIRV-Cross(android-arm64) → [managed: rewrite + MGFX writer] → .mgfx (OpenGL v10)
```

Only **two** native libraries are on this path — **DXC** and **SPIRV-Cross**. The managed
`MonoGameGlslRewriter` + `MgfxWriter` are pure C# and already run anywhere .NET runs. **vkd3d
and `d3dcompiler_47` are NOT built for Android** (DirectX/FNA are desktop-only).

---

## 3. What exists today (verified)

- **No `android-*` RID anywhere.** `SpvcLoader.GetCurrentRid()`
  (`src/ShadowDusk.GLSL/Interop/SpvcLoader.cs:58-67`) is a 4-way switch over
  win-x64 / osx-arm64 / osx-x64 with a **`linux-x64` default** — on Android it would fall
  through to "linux-x64" and probe for a `.so` that does not exist for the ABI. The
  release/restore RID matrix is likewise win-x64 / linux-x64 / osx-x64 / osx-arm64 only.
- **The loaders assume the desktop self-contained-publish model.** `SpvcLoader` probes
  `AppContext.BaseDirectory/runtimes/<rid>/native/<file>` and then falls back to a bare
  `TryLoad` of the file name on the assumption the host "extracted natives to a temp
  directory on the native search path" (`SpvcLoader.cs:48-52`). **That extract-then-load
  assumption is exactly what Android W^X forbids** — on Android the `.so` must load from the
  read-only APK `lib/<abi>/` by bare name (the Android linker resolves it), never from a
  writable temp dir.
- **DXC is loaded via Vortice's `Dxc.ResolveLibrary` event**, not `SetDllImportResolver`
  (`src/ShadowDusk.HLSL/Dxc/DxcLoader.cs`); Phase 37 A already uses this hook to load our
  own pinned `libdxcompiler.dylib` on macOS where Vortice ships no native. **The same hook
  extends to Android** — Vortice has no Android RID, so we ship and resolve our own
  `libdxcompiler.so`, exactly as the macOS path does.
- **The cross-compile precedent exists.** `.wasm-build/build-dxc-wasm.ps1` +
  `dxc-wasm-patches.txt` already cross-compile the **pinned** DXC commit (`e043f4a1`) for a
  non-desktop host (WASM), solving the structural LLVM blockers: the host/target tablegen
  split and two CMake `CMAKE_CROSSCOMPILING` patches. `build-spirv-cross-wasm.ps1` does the
  same for SPIRV-Cross.
- **The DI seam exists.** `EffectCompiler` takes its DXC / GLSL-transpiler / reflector
  backends via injected factories; `WasmShaderCompiler` already swaps in WASM-backed
  implementations. An Android host swaps in native-`.so`-backed implementations the same way
  (or, as a fallback, reuses the WASM modules — see §6 option C).

---

## 4. Scope & Non-Goals

**In scope:**
- Add `android-arm64` (primary; `arm64-v8a`) as a recognized RID/host across the loaders, the
  restore scripts, and the pack/runtime-asset graph. (`android-arm` / `android-x64` for older
  devices / emulators are stretch.)
- Produce **Android NDK builds** of **SPIRV-Cross** and **DXC** (`libspirv-cross.so`,
  `libdxcompiler.so`) for `arm64-v8a`, hosted + SHA-256-pinned + restored exactly like the
  existing per-RID natives (Phase 37 model), and packed into the package's
  `net8.0-android` / per-ABI native assets.
- Teach `SpvcLoader` and `DxcLoader` an **Android branch** that loads the `.so` by bare name
  from the APK `lib/<abi>/` (W^X-safe), bypassing the desktop extract-to-temp assumption.
- Prove the Android-host compile is **byte-identical to the proven desktop output** (extend
  `CrossHostByteIdentityTests`-style coverage to an `android-arm64` host) so render-
  equivalence transfers transitively (the WASM/Phase-23 evidence pattern).
- Define and (where a device/emulator is reachable) execute the **rung-4 Android render
  proof**: the ShadowDusk-on-Android `.mgfx` loads in a real MonoGame/KNI Android app and
  renders equivalently to the same shader compiled by `mgfxc`.
- Document Android usage: build-time precompile as the default (§0 shape 1), on-device
  runtime compile as additive reach (§0 shape 2).

**Out of scope / Non-Goals:**
- **Changing the default for shipping games.** Build-time precompile-to-`.mgfx` stays the
  recommended Android path; on-device runtime compile is opt-in reach.
- **vkd3d / DirectX / FNA on Android.** Android renders via GL ES; those backends are
  desktop-only and not built for the NDK.
- **A substitute compiler.** No swapping DXC/SPIRV-Cross for some "Android-friendly" frontend
  — that would diverge from `mgfxc` and break THE PURPOSE. Android must run the same pinned
  components.
- **APK-size optimization beyond what's needed to ship.** A full LLVM-based `libdxcompiler.so`
  is large and ships per-ABI; documenting the cost and offering the build-time-precompile
  alternative is in scope, but exotic size reduction (custom DXC slimming) is not.
- **iOS.** A separate future host (and Apple toolchain) — not this phase.
- Claiming Android "done" on an unvalidated `.so` build — until a rung-4 device/emulator
  render proof exists, on-device Android compile ships **experimental / unvalidated** and is
  labelled so (matching the Metal treatment).

---

## 5. Architecture & key decisions

- **Android is a new HOST, not a new TARGET.** The emitted artifact is the same OpenGL
  `.mgfx` v10 every desktop host already produces. Nothing in `PlatformTarget`, the writers,
  the MGFX format, or the MonoGame 3.8.2 pin changes. This is the cleanest possible additive
  change: a fourth desktop-class OS plus the browser.
- **Reuse the per-RID native machinery, don't invent one.** Phase 37 established the pattern:
  host the binary on a pinned GitHub release tag, restore + SHA-256-verify in `tools/restore.*`,
  pack into `runtimes/<rid>/native`, load via the loader's resolver. Android is one more RID
  through that exact pipeline — plus the Android-specific packaging detail that native `.so`
  ride in the APK `lib/<abi>/` (via `net8.0-android` runtime assets / `<AndroidNativeLibrary>`
  with the correct ABI), not a desktop `runtimes/` folder the device would read from disk.
- **The loader's Android branch loads from the read-only APK, by bare name.** Add an
  `IsAndroid` arm to `SpvcLoader`/`DxcLoader` that skips the `AppContext.BaseDirectory` +
  extract-to-temp probes (which violate W^X) and does a bare `NativeLibrary.TryLoad("spirv-cross")`
  / Vortice `ResolveLibrary("dxcompiler")` so the **Android linker** resolves it out of
  `lib/<abi>/`. This is *less* code than the desktop path, not more.
- **DXC build = port the WASM recipe to the NDK, not a from-scratch effort.** The known LLVM
  cross-compile blockers (host tablegen, `CMAKE_CROSSCOMPILING` forcing a native sub-build,
  EH/RTTI) are *already solved* in `.wasm-build/`. The Android delta is: NDK toolchain file
  instead of emscripten, threads can stay **on** (simpler than the WASM `LLVM_ENABLE_THREADS=OFF`
  case), and the output is `arm64-v8a` ELF. Build it on CI (a new `dxc-android-build.yml`,
  sibling of `dxc-build.yml` / `dxc-wasm-build.yml`).
- **SPIRV-Cross build = a stock NDK CMake cross-compile.** SPIRV-Cross officially lists
  Android as a tested platform and has no heavy deps; `-DCMAKE_SYSTEM_NAME=Android
  -DCMAKE_ANDROID_ARCH_ABI=arm64-v8a -DSPIRV_CROSS_SHARED=ON` produces `libspirv-cross.so`
  exporting the C API `SpvcNative` already uses. This is the easy native piece.
- **Validation strategy mirrors WASM/Phase-23: byte-identity ⇒ transitive render proof.**
  We cannot run `mgfxc` on Android (no `mgfxc` Android build) — but we don't need to.
  ShadowDusk's compiler output is OS-independent; if the **android-arm64 host's** DXC +
  SPIRV-Cross produce bytes **identical to the desktop bytes** that are already rung-4
  render-proven vs `mgfxc` (Phase 17), render-equivalence transfers. The *Android-specific*
  proof that remains is then only **"does that `.mgfx` load + render in a real MonoGame/KNI
  Android runtime"** — a load+render rung on a device or emulator, not a fresh oracle
  comparison. **The harness for that rung is concrete, not hypothetical** — see §6.1 (the
  MyFiddle template + its `pixel_7` API-34 emulator).

---

## 6. Native delivery options (DXC is the cost driver)

| Option | DXC source on device | Pros | Cons |
|---|---|---|---|
| **A — native NDK `.so` (recommended)** | `libdxcompiler.so` (arm64-v8a) built from the pinned commit via NDK | Same in-process P/Invoke as desktop; full-speed; the faithful component, no substitute | Large `.so` per ABI (LLVM-class binary); the build is the hard engineering job (de-risked by the WASM recipe) |
| **B — host-only (no on-device compile)** | none | Zero Android native work; idiomatic; ships today | No runtime compile on device — only the §0 build-time-precompile shape |
| **C — reuse the DXC/SPIRV-Cross WASM modules** | the existing `.wasm` modules run on a WASM runtime embedded in the app | Reuses already-built artifacts; no new native build | Needs a WASM runtime on Android + a `[JSImport]`-equivalent shim; ~tens of MB; slower; an odd architecture for a native app — a fallback, not the goal |

**Recommendation:** ship **B as the documented default for all Android consumers** (it needs
nothing and is the right shape for shipping games), and pursue **A** to deliver the additive
on-device runtime-compile reach capability. **C** is a documented fallback only if the NDK DXC
build proves intractable (the WASM precedent suggests it will not).

---

## 6.1 The Android test project & integration shape (the MyFiddle template)

A reference MonoGame Android project (`MyFiddle`, an XnaFiddle Android export) is the concrete
template for both the **render-validation harness** and the **consumer integration shape**.
Verified facts from it (these supersede the earlier net8.0 / MonoGame-3.8.2 *assumptions* in
this doc — both load our v10 output fine):

- **TFM / packages:** `net9.0-android`, `MonoGame.Framework.Android` **3.8.4.1**,
  `MonoGame.Content.Builder.Task` 3.8.4.1, `<MonoGamePlatform>Android</MonoGamePlatform>`,
  `<SupportedOSPlatformVersion>21</SupportedOSPlatformVersion>`. ShadowDusk's product library
  targets `net8.0` and is consumable from a `net9.0-android` app; our `.mgfx` **v10** loads on
  MonoGame 3.8.4.1 (forward-compat proven in [Phase 35 A](DONE/PHASE-35-forward-version-support.md)).
- **Runtime:** `Activity1 : AndroidGameActivity` → `new Game1()` → `game.Run()`; manifest
  declares `glEsVersion 0x00020000` (**GL ES 2.0**), `minSdk 21`, `targetSdk 36`. Confirms the
  **OpenGL/GLSL path** is the only relevant target (matches §2).
- **Content as raw assets:** `<AndroidAsset Include="Content\**\*.*" />` ships the `.fx`
  (`Content/Pixelated.fx`, a ShadowDusk corpus shader) **as a raw file inside the APK**, read
  via `TitleContainer.OpenStream` — no `.xnb`, no MGCB build of it.
- **The integration gap ShadowDusk fills:** `RawContentManager : ContentManager` overrides
  `Load<T>` for `Texture2D`/`SoundEffect` (raw decode) but **falls through to the base
  (`.xnb`) loader for `Effect`** — so `Content.Load<Effect>("Pixelated")` has nothing to load
  on-device today. **The Phase-50 on-device path slots in exactly here:** an `Effect` arm in a
  content-manager shim that reads the raw `.fx`, calls
  `IShaderCompiler.Compile(fxSource, OpenGL)` → `.mgfx` bytes → `new Effect(gd, bytes)`. This
  is the canonical §0-shape-2 consumer pattern and is what the `validation/AndroidGl` driver
  must exercise.
- **Emulator is wired:** the project's `.csproj.user` targets `pixel_7_-_api_34` (Android 14 /
  API 34) — i.e. a concrete, runnable emulator for the rung-4 render proof, and the same shape
  GitHub Actions' Android emulator runner provides.

**What this resolves:** the render-validation rung is **no longer hypothetical**. The harness is
"take this project, add the ShadowDusk package + the `Effect` content-manager arm, point it at a
corpus `.fx`, render on the `pixel_7` emulator, and compare to the `mgfxc`-built `.mgfx` of the
same shader on the same GL-ES backend." It still needs the `android-arm64` natives (§6 A) and an
emulator runner to execute — which is the only reason this stays *planned* rather than *proven* —
but there is a real, known path, not a research question. `validation/AndroidGl` should be a
trimmed copy of this template (out-of-band, **not** in `ShadowDusk.slnx`, like every other
`validation/*` driver — it needs the Android workload).

---

## 6.2 On-device live compile — PROVEN WORKING (2026-06-28)

The real product ask on Android is **shape 2 done live**: a user types HLSL into the app,
ShadowDusk compiles it **in memory, on the device**, and the result loads as a live `Effect` (an
on-device shader fiddle). **This was achieved and proven end-to-end on a real `pixel_7` API-34
emulator on 2026-06-28.** The `validation/AndroidGl` MonoGame app, calling plain
`new EffectCompiler().CompileAsync(hlslText, OpenGL)`, logged on the device:

```
SHADOWDUSK: On-device compile: ShadowDusk EffectCompiler.CompileAsync(OpenGL) ...
SHADOWDUSK: ON-DEVICE COMPILE OK: 410 byte .mgfx -> Effect technique 'SpriteDrawing'
```

i.e. an HLSL **string** → DXC → SPIR-V → SPIRV-Cross → GLSL → `.mgfx` (410 bytes), **entirely on
the phone**, loaded into a live MonoGame `Effect` (green-screen success). Seamless: **no flag, no
injection** — plain `new EffectCompiler()`.

**How the wall (DXC for Android) was cleared.** DXC has no prebuilt for Android (verified: MS
releases / NuGet / vcpkg / NDK / LunarG / wrappers are all desktop-only), so it was
cross-compiled from the pinned source. The **existing `.wasm-build` DXC recipe ported almost
directly to the NDK**: the host tablegen tools (`llvm-tblgen`/`clang-tblgen`) and the two
`CMAKE_CROSSCOMPILING` patches it already built are reused verbatim; only Stage 1 swaps
emscripten for the NDK `android.toolchain.cmake`. The one new fix is passing
`LLVM_INFERRED_HOST_TRIPLE` to bypass DXC's `config.guess` (a shell script that can't run on
Windows). Recipe: **`.wasm-build/build-dxc-android.ps1 -Abi arm64-v8a|x86_64`**. Output: a real
`ELF aarch64`/`x86-64` Android `libdxcompiler.so` (~33 MB stripped, `DxcCreateInstance`
exported). **SPIRV-Cross** is a stock NDK CMake build (~38 MB). Both ABIs were built (arm64-v8a
for real devices; x86_64 for the emulator).

**The three fixes from "native built" to GREEN on-device:**
1. **Loaders** — `DxcLoader`/`SpvcLoader` resolve the natives by **bare SONAME from the APK
   `lib/<abi>/`** (the Android branches added earlier this phase).
2. **Packaging** — the desktop NuGets ship `runtimes/linux-x64/native/*.so` that .NET-Android
   wrongly maps into `lib/x86_64/` (Linux glibc binaries → `DllNotFoundException`). Dropped via
   **`ExcludeAssets="native"`** on direct `Vortice.Dxc` / `Silk.NET.SPIRV.Cross.Native` refs,
   replaced by our own Android `.so` (`<AndroidNativeLibrary Abi="...">`).
3. **Reflection (the last bug)** — the default OpenGL reflector is the **native DXIL oracle**
   (DXC `CreateReflection` → `ID3D12ShaderReflection`), which throws on .NET-for-Android
   (`SD0102`, a wrapped `TargetInvocationException`). Fix: **`EffectCompiler` now auto-selects
   the pure-managed `SpirvReflector` on Android** (`OperatingSystem.IsAndroid()`) — the same
   reflector the WASM host uses, proven **byte-identical** to the DXIL path
   (`SpirvReflectionByteIdentityTests`). Desktop is unchanged (the default stays the DXIL
   oracle), so `new EffectCompiler()` is seamless on Android with no consumer flag.

**Status & remaining rungs (none are blockers to the proof):**
- **arm64-v8a** (real-device deliverable) and **x86_64** (emulator) natives both built; on-device
  compile + `Effect` load proven (green-screen). A finer **pixel-vs-`mgfxc`** on-device render
  diff is the next rung (the harness today proves compile + `Effect` load, not a pixel compare).
- **Productionization:** host the per-ABI natives on a pinned release tag and flip the
  `tools/restore.*` `PENDING` SHAs (so other devs/CI restore them instead of building locally);
  author the CI form `dxc-android-build.yml`; size-optimize; decide the minimum API level. The
  local build recipe `build-dxc-android.ps1` is the durable artifact.
- **Emulator gotchas hit & solved** (recorded so they don't bite again): the emulator's primary
  ABI is **x86_64** (so x86_64 natives are needed to demo there); Debug **FastDeployment** keeps
  assemblies outside the APK (use `-p:EmbedAssembliesIntoApk=true` for a self-contained
  `adb install`); and the AVD's default ~5 GB data partition fills up (cold-boot `-wipe-data`).

---

## 7. Tasks

- [ ] **Cross-build SPIRV-Cross for `android-arm64`** (NDK CMake, shared lib exporting the C
      API). Host on a pinned release tag; add restore + SHA-256 verification to
      `tools/restore.ps1`/`tools/restore.sh`.
- [ ] **Cross-build DXC for `android-arm64`** (the long pole; see §6.2). Two routes: (a) add an
      `.android`/aarch64-NDK target to **`hexops/mach-dxcompiler`** (a cleaned-up DXC build that
      already does desktop aarch64 — the verified most-tractable lead), or (b) port
      `.wasm-build/build-dxc-wasm.ps1` + `dxc-wasm-patches.txt` to an NDK toolchain. Either way:
      same pinned DXC commit as desktop/WASM (no substitute compiler), `dxc-android-build.yml`,
      host + restore + SHA-256-pin. **This native is the sole blocker for on-device live compile.**
- [ ] **Add `android-arm64` to `SpvcLoader.GetCurrentRid()`** and an **Android load branch**
      (bare-name `TryLoad` from the APK `lib/<abi>/`, skipping the extract-to-temp probe).
- [ ] **Add an Android branch to `DxcLoader`'s Vortice `ResolveLibrary` hook** that resolves
      `libdxcompiler.so` by bare name on Android (mirroring the macOS own-native path).
- [ ] **Packaging:** add the `net8.0-android` runtime-asset graph / per-ABI
      `<AndroidNativeLibrary>` items so the two `.so` land in the APK `lib/arm64-v8a/`; extend
      the release pack + the release-gate "natives present" check to cover the Android RID.
- [ ] **Byte-identity gate:** extend the cross-host byte-identity proof to an `android-arm64`
      host (emulator or device CI lane) over the OpenGL corpus — assert the Android bytes
      equal the committed Windows/Linux manifest bytes (transfers the Phase-17 render proof).
- [ ] **Consumer integration shim:** an `Effect` arm for a `RawContentManager`-style
      `ContentManager` (per §6.1) that reads a raw `.fx` asset, calls
      `IShaderCompiler.Compile(fx, OpenGL)`, and returns `new Effect(gd, bytes)` — the canonical
      §0-shape-2 on-device pattern, shipped as sample/doc code.
- [ ] **Rung-4 Android render driver:** `validation/AndroidGl`, a trimmed copy of the **MyFiddle
      template** (§6.1; `net9.0-android`, `AndroidGameActivity`, `<AndroidAsset>` content,
      out-of-band — **not** in `ShadowDusk.slnx`) that compiles a corpus `.fx` on-device via
      ShadowDusk, renders it, and compares to the `mgfxc`-compiled `.mgfx` of the same shader
      (same GL-ES backend, same scene) on the `pixel_7` API-34 emulator.
- [ ] **Trim-safety pass** for .NET-for-Android Release trimming (annotate any reflection the
      pipeline relies on; the Mono JIT covers the dynamic cases, but trimming is aggressive).
- [ ] **Docs:** a docs-site "Android" page stating (1) build-time precompile is the default
      and works today, (2) on-device runtime compile is additive/experimental until the render
      proof lands, (3) the APK-size cost of bundling `libdxcompiler.so`. Update `CLAUDE.md`'s
      OS list and the backend/host table.
- [ ] **Matrix + diagram:** add the Android host row/cells to
      [`docs/validation-matrix.md`](../docs/validation-matrix.md) and the Android host path to
      [`docs/pipeline-overview.puml`](../docs/pipeline-overview.puml) (done as part of opening
      this phase — see those files).

---

## 8. Acceptance Criteria

- [ ] `libspirv-cross.so` and `libdxcompiler.so` for `arm64-v8a` are built from the **pinned**
      sources (no substitute compiler), hosted, restored, SHA-256-verified, and packed into the
      Android package assets — self-contained, "add the package, call the API", no manual native
      install.
- [ ] `SpvcLoader` and `DxcLoader` resolve their `.so` **in-process from the read-only APK**
      `lib/<abi>/` on Android (no temp-dir extraction, no child process), and the
      `IShaderCompiler.CompileAsync` OpenGL path runs end-to-end on an `android-arm64` host.
- [ ] The Android-host OpenGL output is **byte-identical to the committed desktop manifest**
      for the corpus (transferring the rung-4 render proof), pinned by an automated test.
- [ ] A **real-runtime render story is recorded**: either a passing rung-4 device/emulator
      render proof (full done), or a clearly-documented gap with on-device Android compile
      shipped **experimental** (partial done) — never silently presented as validated.
- [ ] Existing Windows/Linux/macOS/WASM behavior and **all output bytes are unchanged** (the
      Android additions are host-gated; no shared loader/RID/enum regression). Full
      `dotnet test ShadowDusk.slnx` green; the Windows render gates unaffected.
- [ ] Docs and `CLAUDE.md` state the true status: build-time precompile (default, works today)
      vs on-device runtime compile (additive reach, its real validation level).

## 9. Definition of Done

The **same faithful pipeline** runs on a fourth OS: a MonoGame/KNI consumer can add the
ShadowDusk package to their **Android** project and compile `.fx` → `.mgfx` **on the device**
through the one pipeline (DXC → SPIR-V → SPIRV-Cross → MGFX, `android-arm64` natives), with
the two `.so` riding self-contained inside the APK and loading in-process under W^X — **and**
the phase has an explicit, honest answer to "does this render like `mgfxc` in a real Android
runtime": either a passing rung-4 device/emulator proof (output byte-identical to the desktop-
render-proven bytes, plus a real on-device load+render), or a clearly-documented validation
gap with on-device Android compile shipped **experimental**. Throughout, **build-time
precompile-to-`.mgfx` remains the documented default** for shipping games (it needs nothing
new), and no existing output byte, the MGFX v10 default, or the MonoGame 3.8.2 pin changes.

---

## 10. Open questions / risks

- **The DXC NDK build is the single biggest risk** — a from-source LLVM/Clang-fork build,
  per-ABI, producing a large `.so`. **De-risked** (not eliminated) by the existing WASM
  cross-compile, whose host-tablegen split + CMake patches port to the NDK; residual risk is a
  bounded engineering job, not an unknown.
- **APK size.** A release/stripped `libdxcompiler.so` is LLVM-class (tens of MB) and ships per
  ABI. This is acceptable for the reach use case but is a real reason build-time precompile
  (Option B) stays the default for shipping games — document the trade-off honestly.
- **CI device/emulator for the render rung.** Like the DX/FNA/KNI-DX gates, an Android render
  proof has no trivial headless lane; it needs an Android emulator (or device) runner. The
  byte-identity gate (which *can* run on an emulator host or even be transferred) carries most
  of the proof; the on-device load+render is the part that needs the runner. Until that exists,
  Android on-device compile is experimental (the Metal pattern).
- **.NET-for-Android trimming / AOT.** Release Android builds trim aggressively and use
  profiled AOT (with a JIT fallback). The managed pipeline is ordinary C#, but a trim-safety
  pass is needed; flag any reflection so trimming doesn't strip it.
- **Which engine for the render driver — MonoGame vs KNI?** Both run on Android via .NET-for-
  Android and GL ES. Pick whichever has the simpler Android sample to stand up; the `.mgfx` is
  identical either way (the same byte-identity argument transfers across both, as the desktop
  KNI/MonoGame proofs already show).
- **Minimum Android API level.** W^X "safer dynamic code loading" tightened at API 29; confirm
  the loader branch and packaging satisfy the project's minimum `targetSdk`/`minSdk` and that
  loading from `lib/<abi>/` (always allowed) is the only path used.
