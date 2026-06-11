# Phase 37 — Cross-platform integration-test native availability (macOS DXC gap, Linux DXC ICE, vkd3d in CI)

**Status:** 🟠 **Open — created 2026-06-07; Finding C ✅ done 2026-06-10 (PRs #35/#36/#37); Finding B ✅ root cause found + fixed 2026-06-10 (Vortice UTF-16 args vs Linux 4-byte `wchar_t` — see Finding B); C-residue DX11 reflection ✅ done 2026-06-10 (Phase 18 Track A — managed `RdefReader`, see the Track A section below).** Remaining: **Finding A** (macOS DXC — repo wiring + green dylib builds landed 2026-06-10/11, artifacts hosted on `native-dxc-1.7.2212.40`, pins flipped to enforcing; awaiting the macOS CI fidelity gate before A is declared done — see "A — AS-BUILT, part 1"). A now gates only the GL/Vulkan targets on macOS — the DX11 path no longer constructs DXC (Track A's lazy-DXC change).
**Track:** Reach (Part 1 of THE PURPOSE) — "compile where `mgfxc` can't (Linux/macOS)." The integration tests are RED because they are faithfully catching real cross-platform gaps; this is not a test-logic problem.

> **Supersedes the scope of [Phase 36](PHASE-36-dxc-linux-spirv-ice.md).** Phase 36 concluded the Linux DXC ICE was confined to the **Debug-CLI / spawned-process** `wasm.yml` path and that "Release in-process works on real Linux." New evidence in this phase **disproves that**: the `ci.yml` **integration-tests** job (Release, **in-process** `EffectCompiler`) ICEs on ~120 tests on `ubuntu-latest`. Phase 36's "likely-quick Release-CLI experiment" is now just one sub-hypothesis under Finding B below.

---

## TL;DR

The `ci.yml` **Integration Tests** job is red on all three OS. Build & Test (the unit gate) is green everywhere; only the integration job (which exercises the real native toolchain) fails — for **three distinct root causes**, one per OS family:

| OS | Failing | Root cause | Category |
|---|---|---|---|
| **Windows** | 2 tests | `vkd3d-shader` native absent (gitignored, built out-of-band) | **C — vkd3d** ✅ fixed |
| **Ubuntu** | 132 tests | 12 = vkd3d/DX (no native + no Windows oracle); **120 = DXC "Internal Compiler error" on Linux** | **C (12) + B (120)** ✅ both fixed (the DX11-reflection residue ✅ resolved 2026-06-10 — Phase 18 Track A section below) |
| **macOS** | ~124 tests | **Vortice.Dxc ships NO macOS native** → `DllNotFoundException` loading `dxcompiler.dll` | **A — product gap** |

**The most important finding (A) is not a test problem — it is a broken product on macOS.** Any developer who runs `dotnet add package ShadowDusk.Compiler` on a Mac and calls `CompileAsync` gets `DllNotFoundException`. The integration test is the canary.

---

## Background — why the integration tests need native libraries

The faithful pipeline is `HLSL →[DXC]→ SPIR-V →[SPIRV-Cross]→ GLSL →[managed]→ .mgfx` for OpenGL, and `HLSL →[vkd3d-shader | d3dcompiler_47]→ DXBC` for DirectX. Three native libraries are involved:

| Native | Comes from | Used by | On default consumer path? |
|---|---|---|---|
| **DXC** (`dxcompiler`) | `Vortice.Dxc` 3.3.4 NuGet (transitive) | the **front of the OpenGL pipeline on every OS** | **YES** — every GL compile, every OS |
| **SPIRV-Cross** (`spirv-cross-c-shared`) | `Silk.NET.SPIRV.Cross.Native` NuGet (transitive) | SPIR-V → GLSL | YES |
| **vkd3d-shader** | gitignored, restored out-of-band | cross-platform DXBC, **only when `DxbcBackend.Vkd3d`** | **NO** — opt-in (default DX backend is the Windows `d3dcompiler_47` oracle) |

Unit tests (`Category!=Integration`) are pure (or, for ImageTests, render-proxy) and largely avoid the heavyweight DX/vkd3d paths; the **integration** tests are where the real native toolchain is exercised end-to-end — which is exactly why they expose these gaps.

---

## Finding A — macOS: Vortice.Dxc ships no macOS DXC native (PRODUCT GAP)

### Issue
On `macos-15-arm64`, nearly every integration test fails with:
```
System.DllNotFoundException : Unable to load shared library 'dxcompiler.dll' or one of its dependencies.
dlopen(.../runtimes/osx-arm64/native/dxcompiler.dll, ...) (no such file)
dlopen(.../runtimes/osx-arm64/native/libdxcompiler.dll.dylib, ...) (no such file)   ... (all probes fail)
```

### Cause (root cause, confirmed two ways)
`Vortice.Dxc` **3.3.4 ships native binaries for `linux-x64`, `win-x64`, `win-arm64` only — there is no `osx-arm64` or `osx-x64` native at all.** Verified by inspecting the package in the NuGet cache:
```
~/.nuget/packages/vortice.dxc/3.3.4/runtimes/
  linux-x64/native/  libdxcompiler.so, libdxil.so
  win-arm64/native/  dxcompiler.dll, dxil.dll
  win-x64/native/    dxcompiler.dll, dxil.dll
  (no osx-* folder)
```
The `dxcompiler.dll` P/Invoke lives in the **`Vortice.Dxc` assembly** (not a ShadowDusk assembly). With no macOS binary present, .NET's loader probes `dxcompiler.dll`, `dxcompiler.dll.dylib`, `libdxcompiler.dll.dylib`, … — none can match because nothing macOS is in the package. So this is root cause **(a) "no native present,"** not (b) a name-mapping bug.

### Why it matters / the "why"
- **DXC is the front of the OpenGL pipeline on every OS.** `DxcShaderCompiler` constructs the compiler in its constructor (`CreateDxcCompiler<IDxcCompiler3>()` + `LoadDxil()`), so `new EffectCompiler().CompileAsync(... OpenGL ...)` throws on macOS before any compile happens. **The entire GL path is dead on Mac.**
- **This is consumer-facing, not CI-only.** The `Vortice.Dxc` dependency flows transitively to consumers. A Mac dev (Apple Silicon *or* Intel) installing `ShadowDusk.Compiler` hits the same `DllNotFoundException`. This directly violates THE PURPOSE ("self-contained, works on Linux/macOS/Windows from a plain package add") and the seamless-for-end-user rule. Apple Silicon has been the default Mac since 2020, so this is the primary Mac platform, not an edge case.
- The SPIRV-Cross half is already Apple-Silicon-ready (`tools/restore.sh` restores `osx-arm64/libspirv-cross-c-shared.dylib`, and `Silk.NET.SPIRV.Cross.Native` carries macOS natives). **DXC is the sole blocker.**

### How to fix (the "how" + "what")
Ship our **own** `libdxcompiler.dylib` for macOS, built from the **exact pinned DXC source** the rest of the pipeline uses, and load it via a resolver:

1. **Build `libdxcompiler.dylib` (osx-arm64, and ideally osx-x64) from DXC commit `e043f4a1286f4e1026222ab1bc94e25de8d0e959`** — the exact commit `Vortice.Dxc` 3.3.4's `dxcompiler.dll` reports (FileVersion `1.7.2212.40`), with the same gitlinked SPIR-V submodules (`SPIRV-Headers @ 1d31a10…`, `SPIRV-Tools @ 40f5bf59…`). This is the **same source already pinned for the WASM build** in `tools/restore.sh` — reuse it, native macOS target instead of emscripten. Building from the identical commit is what keeps the emitted SPIR-V byte-identical to win-x64 / linux-x64 / WASM.
2. **Add a `DxcLoader` with `NativeLibrary.SetDllImportResolver`** (modeled on `src/ShadowDusk.GLSL/Interop/SpvcLoader.cs` and `src/ShadowDusk.HLSL/Vkd3d/Vkd3dLoader.cs`) mapping `dxcompiler.dll` → `libdxcompiler.dylib`. **CRITICAL difference from the existing loaders:** the DXC P/Invoke is in the *Vortice.Dxc* assembly, so register the resolver on **`typeof(Vortice.Dxc.Dxc).Assembly`**, not a ShadowDusk assembly. Call `DxcLoader.Register()` from `DxcShaderCompiler`'s constructor before `CreateDxcCompiler<>`.
3. **Ship the dylib as a restored native asset** — extend `tools/restore.sh`/`.ps1` and the `ShadowDusk.HLSL.csproj` `<None>`-copy block (mirror the vkd3d pattern) so it rides inside the package per-RID. "Add the package, it just works" must hold — **zero consumer action, no flag.**

### What NOT to do (rejected, with reasons)
- **Do NOT bump `Vortice.Dxc`** — *no* Vortice version ships a macOS native (checked through 3.8.3, Mar 2026), and a bump would change the DXC version → break deterministic/byte-identical output.
- **Do NOT adopt `DirectXShaderCompiler.NET`** (a zig-built **fork** with osx natives) or `MethanePowered/...Binary` (only at 1.9.2602) — both are *different compiler builds* → violate the "NO substitute compilers" rule **and** break byte-identity.

### Risk / fidelity note
Changing/adding the DXC native is fidelity-sensitive: it must be gated by the existing **byte-identity corpus test** (the same gate used for the WASM module) so the macOS dylib's SPIR-V matches the win-x64/linux-x64 oracle, and the rung-4 render-vs-`mgfxc` bar must stay green.

### A — AS-BUILT, part 1 (2026-06-10): implementation landed, build/hosting PENDING — **A is NOT done**

All repo-side wiring landed on `feature/phase37a-macos-dxc` (mirrors the Phase 37 C
vkd3d patterns one-for-one); **no macOS dylib exists yet**, so the gap is still open
until the hosted artifacts land and a real macOS run compiles. What landed:

- **`.github/workflows/dxc-build.yml`** — dispatchable build of `libdxcompiler.dylib`
  from the pinned commit `e043f4a1…` (verified again from the Vortice 3.3.4 native:
  ProductVersion `1.7.2212.40 (e043f4a12)`) + the three pinned submodules (gitlinks
  asserted against the documented SHAs). osx-arm64 on macos-14
  (`MACOSX_DEPLOYMENT_TARGET=11.0`), osx-x64 on macos-15-intel (`10.15`; macos-13 is
  retired — both floors are far below .NET 8's own 12+ requirement). CMake+Ninja
  Release with DXC's own `PredefinedParams.cmake` cache + `ENABLE_SPIRV_CODEGEN=ON`;
  smoke gate = `dxc -T ps_6_0 -spirv` + SPIR-V magic; `otool -L` system-only-linkage
  gate (skipping the dylib's own install name — the 694190e lesson); ships the
  upstream `LICENSE.TXT` as `LICENSE-DXC.TXT` in the artifact. `dxil.dll` is NOT
  built: `LoadDxil()` is a kernel32 no-op off Windows and `-spirv` never needs the
  signer.
- **`DxcLoader`** (`src/ShadowDusk.HLSL/Dxc/DxcLoader.cs`) — **CORRECTION to the
  "how" above:** step 2's "register `SetDllImportResolver` on
  `typeof(Vortice.Dxc.Dxc).Assembly`" would **throw `InvalidOperationException`** —
  Vortice.Dxc 3.3.4's `Dxc` static ctor already registers its own resolver on that
  assembly (verified by decompilation), and .NET allows one resolver per assembly.
  Vortice instead exposes the public **`Dxc.ResolveLibrary` event**, consulted by its
  resolver *before* the default-load fallback, and its built-in handler returns Zero
  on macOS — so `DxcLoader.Register()` (macOS-only, idempotent) appends a handler
  there. Probe order mirrors `Vkd3dLoader`: per-arch `osx-{x64,arm64}/` then flat
  next to the binaries, the publish `runtimes/<rid>/native` layout, `tools/dxc/`
  walking up to the root, `NATIVE_DLL_SEARCH_DIRECTORIES`, bare name. Registered
  from `DxcShaderCompiler`'s ctor and `DxilReflectionExtractor` before any DXC
  P/Invoke. Zero behavior change on Windows/Linux. Pure candidate generation is
  unit-tested (`tests/ShadowDusk.HLSL.Tests/Dxc/DxcLoaderTests.cs`).
- **`tools/restore.{sh,ps1}`** — `restore_dxc_macos`/`Restore-DxcMacos` mirroring the
  vkd3d pinned-download pattern (fixed tag **`native-dxc-1.7.2212.40`**, per-arch
  dest `tools/dxc/osx-{x64,arm64}/libdxcompiler.dylib`). Pins are
  **`PENDING-FIRST-HOSTED-BUILD`** placeholders: the section skips with a notice
  (non-fatal) until the hosted build exists, so win/linux restores are unaffected.
- **`ShadowDusk.HLSL.csproj`** — `Exists()`-gated per-arch copy-to-output links +
  `runtimes/osx-{x64,arm64}/native` pack entries (inert until restore places files).
  `.gitignore` already covered `tools/dxc/`.
- **`THIRD-PARTY-NOTICES.txt`** — DXC entry added. NOTE: the license is the **LLVM
  Release License (University of Illinois/NCSA)** — DXC's LLVM-3.7 fork predates
  LLVM's Apache-2.0 relicense; the notice is marked prospective until the dylibs
  actually ship (the Vortice-shipped win/linux natives are NOT ours to notice).

**Exact remaining steps to close A** (in order):
1. ~~Dispatch `dxc-build.yml`; iterate until both RID jobs are green.~~ ✅ **GREEN
   2026-06-11**, run https://github.com/kaltinril/ShadowDusk/actions/runs/27327330108
   (attempt 2 — one fix iteration: the runners' CMake 4 rejects the 2022-era source
   (`CMP0051 OLD` + SPIRV-Headers' pre-3.5 `cmake_minimum_required`), fixed by
   pinning the official Kitware CMake 3.31.8 universal binary, SHA-256-verified.
   Both dylibs passed the SPIR-V smoke + otool system-only-linkage gates; artifacts
   downloaded and hash-verified locally. Two notes for posterity: GitHub had never
   registered the dispatch-only workflow off the default branch, so a branch+path-
   scoped push bootstrap trigger was added — remove on merge; and the Actions REST
   API flapped wildly during the run (phantom job failures/successes) — trust the
   artifacts endpoint over status fields. arm64 built in 9.5 min, x64 in ~25 min.)
2. ~~Download + host the artifacts.~~ ✅ **Done 2026-06-11**: both dylibs downloaded
   from the green run, hash-verified against the build-reported SHA-256s, and hosted
   on https://github.com/kaltinril/ShadowDusk/releases/tag/native-dxc-1.7.2212.40
   (per-RID asset names + `LICENSE-DXC.TXT`; prerelease-flagged artifact-hosting tag).
3. ~~Paste the verified SHA-256s over the placeholders.~~ ✅ **Done 2026-06-11** in
   both `tools/restore.sh` + `tools/restore.ps1` — restore is now enforcing:
   - osx-arm64: `4f29ef90af61426a39037a2e9d7215a48c7c746328a38a20028e456c1ee3d811`
   - osx-x64:   `9e61d5c1993d2cd5a5ea6701011d0a86e8c8dd89c995ef0c4d03ff3b83dbbc17`
4. Add a `tools/dxc` cache + restore to `ci.yml`'s macOS lanes if not already covered
   by the existing restore step, and re-run CI: the macOS integration job's
   `DllNotFoundException` wall should fall.
5. **Fidelity gate before calling A done:** byte-identity corpus check (macOS SPIR-V
   == win-x64 oracle) + `Compile_Minimal_OpenGL_ReturnsBytes` and the GL corpus green
   on a real Mac (macos CI lane) + the scratch-consumer check from *Test steps* below.

---

## Finding B — Linux: DXC "Internal Compiler error" ✅ ROOT CAUSE FOUND + FIXED (2026-06-10, expands Phase 36)

### Issue
On `ubuntu-latest`, **120 of 132** integration failures are `error X0000: Shader compilation failed` at position `(0,0-0)` — including on trivial shaders like `Minimal.fx`.

### Root cause (proven by single-factor toggle)
**A `wchar_t`-width marshalling bug in Vortice.Dxc's managed wrapper — the pinned native DXC is fine.**

DXC's C API declares the compiler arguments as `LPCWSTR*`. On Windows that is UTF-16 (`wchar_t` == 2 bytes), but DXC's non-Windows builds compile `WinAdapter.h` against the platform's native `wchar_t`, which is **4 bytes (UTF-32) on Linux and macOS**. Vortice.Dxc 3.3.4's generated wrapper (`Interop.AllocToPointers`, confirmed by decompiling the NuGet assembly) marshals every argument with `Marshal.StringToHGlobalUni` — **UTF-16 on every OS**. So on Linux `libdxcompiler.so` reads garbage arguments (`"-E"` as UTF-16 becomes the invalid UTF-32 unit `0x0045002D`), throws during argument conversion, and DXC's top-level catch reports `HRESULT 0x80AA000C` with the error text `Internal Compiler error: ` — **empty message** — which `DxcDiagnosticReformatter` (`src/ShadowDusk.HLSL/Dxc/DxcDiagnosticReformatter.cs:75-76`) flattens to `X0000 / (0,0-0)`.

**Toggle proof** (the bar set by this phase: make the failure appear AND disappear by one factor). In a clean `mcr.microsoft.com/dotnet/sdk:8.0` container, same shader, same flags, same pinned `libdxcompiler.so` from Vortice.Dxc 3.3.4, raw COM-vtable call (bypassing Vortice's marshalling), the **only** variable being the argument-string encoding:

| Argument encoding | Result |
|---|---|
| UTF-16 (what Vortice does) | `GetStatus = 0x80AA000C`, errors = `Internal Compiler error:` — the exact CI signature |
| UTF-32 (Linux `wchar_t`) | `GetStatus = S_OK`, compiles to SPIR-V successfully |

This also retro-explains **Phase 36's Debug-CLI/spawned ICE and the WSL ICE** — every Linux invocation through Vortice was broken identically; Debug-vs-Release and spawned-vs-in-process were never factors.

### The paradox — RESOLVED: it was a false positive, not a real delta
The "ImageTests 27/27 pass on ubuntu and compile via DXC in-process" evidence (the basis for Phase 36's "Release in-process works on real Linux") was wrong. Every ImageTests class guards on `GlContextFixture.IsSkipped` and **returns early as PASS** when no OpenGL context exists — and `ubuntu-latest` is headless, so GLFW init fails and all 27 tests "pass" **without ever invoking DXC** (visible in the run-27326162402 log: 27 tests in **73 ms**, impossible for 27 real DXC compiles). There is **no** environment in which Vortice-marshalled DXC worked on Linux. Lesson recorded: a *soft-skip-as-pass* guard can fabricate cross-platform "proof" — when a green job is load-bearing evidence, verify the tests actually exercised the machinery (duration is a cheap tell).

### The fix (as-built — same pinned DXC native, marshalling corrected)
`src/ShadowDusk.HLSL/Dxc/DxcNativeInterop.cs` performs the **same vtable call** Vortice's generated code makes (`IDxcCompiler3::Compile`, slot 3), with two deliberate differences:

1. **Arguments are encoded in the platform's `wchar_t` width** — `Marshal.StringToHGlobalUni` (UTF-16) on Windows (bit-identical to Vortice's previous behavior there), manual UTF-32 + NUL elsewhere.
2. **The source buffer is explicit UTF-8 / `DXC_CP_UTF8`** instead of Vortice's `StringToHGlobalAnsi` + `DXC_CP_ACP` (ANSI is system-codepage-dependent on Windows ⇒ non-deterministic for non-ASCII sources; byte-identical for the ASCII corpus).

`DxcShaderCompiler.CompileCore` routes through it; everything downstream (`GetStatus`/`GetErrors`/`GetObjectBytecodeMemory`) is unchanged Vortice. The native binary is untouched — **same pinned Vortice.Dxc 3.3.4 `dxcompiler` everywhere** (no substitute compiler, no version bump). Scope note: the *only* live wide-string surface was the argument array — includes are flattened by ShadowDusk's own preprocessor before DXC (the `IDxcIncludeHandler` reverse callback, which would have the same `wchar_t` hazard inbound, is never exercised by the product pipeline), and the reflection APIs use `LPCSTR`.

**Forward synergy with Finding A:** macOS `wchar_t` is also 4 bytes — when the macOS `libdxcompiler.dylib` lands, the marshalling is already correct; without this fix, Finding A's dylib would have ICEd identically on arrival.

### Evidence (fix verified)
- **Windows unregressed:** full suite (unit + integration + ImageTests with a real GL context, golden corpus + byte-identity + render proxies) green locally — 851/851.
- **Linux fixed:** in the `dotnet/sdk:8.0` container, `grep -c X0000` over the full integration run = **0** (was ~120). `ShadowDusk.Compiler.Tests` integration rows 11/12 (was 1/12), `ShadowDusk.Integration.Tests` 163/174 (was 53/174). Every remaining failure is an `SD0210` DX11-reflection row — the documented Finding-C residue ("DX11 `.mgfx` end-to-end requires Windows until Phase 18 Track A"), which predates and is unrelated to B.
- **Repro tooling note:** the CI runner builds `.slnx` with the runner image's newer pre-installed SDK; the `dotnet/sdk:8.0` container (8.0.422) can't parse `.slnx` — test per-project (`dotnet test tests/<project>`) in the container instead.

### Definition of done (B)
Linux compiles the PS-only + VS-driven corpus **with no ICE** ✅, the Linux `.mgfx` **renders pixel-equivalent to `mgfxc`** (rung-4, same bar as Phase 17/18 — still proxy-pending: the render bar needs a GL-capable Linux host, tracked as follow-up), Windows output is **unregressed** (golden + byte-identity green) ✅, and `ci.yml`'s integration job runs the Linux compile to completion ✅ (remaining red rows are Finding-C-residue SD0210, not B).

---

## Finding C — vkd3d-shader native absent in CI

### Issue
- **Windows:** `Vkd3dShaderCompilerTests.Compile_ProducesDxbcContainer` + `DxbcReflectionExtractor_ReflectsVkd3dOutput` fail with `Unable to load DLL 'vkd3d-shader-1'`.
- **Ubuntu/macOS:** the unguarded `EffectCompilerTests.Compile_Minimal_DirectX_ReturnsBytes` and the `DirectX_11` fixture tests fail with `SD0210: DXBC oracle backend requires Windows` — the default oracle is Windows-only and there is no vkd3d native to fall back to (12 ubuntu tests).

### Cause
The `vkd3d-shader` native is **gitignored and built out-of-band**; only a locally-built **win-x64** binary exists. `tools/restore.*` only *verify presence* and print a build recipe — they don't obtain it. So CI runners have no vkd3d native. **This is opt-in** (default `DxbcBackend = D3DCompiler`), so it is **not** on the default consumer path — it's about test/CI coverage and the cross-platform DX **reach**.

### How to fix (the "how" + "what")
**Host pinned vkd3d 1.17 per-RID binaries as GitHub Release assets; have `tools/restore.*` download + SHA-256-verify them.** (Package managers are disqualified: Ubuntu/Debian ship vkd3d **1.2**, which predates HLSL→DXBC entirely — the `dxbc-tpf` target arrived in 1.3; Homebrew has **no vkd3d formula**. Only Fedora ships 1.17, and it isn't a runner OS.)

1. **User, once:** build vkd3d **1.17** `libvkd3d-shader.so.1` (linux-x64) and `libvkd3d-shader.dylib` (osx-arm64, +osx-x64) from `https://dl.winehq.org/vkd3d/source/vkd3d-1.17.tar.xz` with the native autotools toolchain (the tarball ships pre-generated IDL/SPIR-V headers, so `widl` is not needed; needs Vulkan + SPIR-V headers). win-x64 already exists. Compute SHA-256 for each.
2. **Host them** on a **fixed** tag (e.g. `native-vkd3d-1.17`) so the URL never moves — `gh release create native-vkd3d-1.17 libvkd3d-shader-1.dll libvkd3d-shader.so.1 libvkd3d-shader.dylib`. (Reuse the `softprops/action-gh-release@v2` pattern already in `release.yml`.)
3. **`tools/restore.*`:** replace the "verify + print recipe" body of `restore_vkd3d_shader`/`Restore-Vkd3dShader` with: if present → OK; else download from the pinned asset URL, verify SHA-256 against an embedded pinned hash, place at `tools/vkd3d/<file>`. Keep non-fatal on failure.
4. **`ci.yml`:** add a `tools/vkd3d` cache keyed `vkd3d-1.17-${{ runner.os }}` before the existing restore step (restore already runs in both jobs).
5. **Test changes (load-bearing):** point `Compile_Minimal_DirectX_ReturnsBytes` at `CompilerOptions { DxbcBackend = DxbcBackend.Vkd3d }` on non-Windows (or OS-gate it to Windows and add a vkd3d-backed equivalent), and relax `Vkd3dFactAttribute` (currently Windows-only) to run when the native is present — so all three vkd3d tests run cross-platform (the Phase-18 carry-forward DXBC reach proof).

### Definition of done (C)
The 2 Windows + 12 non-Windows vkd3d/DX tests pass; the three live vkd3d tests execute (not skip) on all 3 OS; cross-host DXBC bytes match (Phase 30 byte-equality).

> **Pin discipline:** the tests pin vkd3d **1.17** behavior (DXBC_TPF resource tables the reflection asserts on). Never use the distro version — it produces different DXBC bytes per OS, breaking constraint 3.

### C — addendum (2026-06-09, post-Phase-39/40): C is now RELEASE-CRITICAL, and bigger than test coverage

What changed since this finding was written, and why C moved to the front of the queue:

- **C now gates releases, not just CI coverage.** Phase 39 made vkd3d the engine of the FNA
  target (`PlatformTarget.Fna` — on the *default* consumer path, unlike the opt-in DX
  backend), and Phase 40 added a `release.yml` pack gate that **fails any release** whose
  `ShadowDusk.HLSL` nupkg is missing the vkd3d natives — deliberately, because the
  published 0.2.0 package shipped without them (60 KB — FNA/vkd3d dead from nuget.org
  today). Until C lands, no release can ship. C is the single blocker between "FNA works"
  and "FNA ships" (see `plan/DONE/PHASE-40-fna-fidelity-hardening.md`).
- **C also turns on the FNA CI net**: `FnaFactAttribute` is availability-probed by design —
  the moment restore provisions vkd3d in CI, the FNA integration suite stops silently
  skipping on all three OSes.
- **macOS dylibs must land WITH the four pre-wiring fixes** recorded in Phase 40's
  evaluation (otherwise arrival is a debugging session, not turnkey): the loader and
  `FnaTestGate` probe only `tools/vkd3d/` flat (can't see the per-arch `osx-{x64,arm64}/`
  subdirs the csproj expects); `restore.sh`'s Darwin check looks for the wrong filename
  (`libvkd3d-shader.dylib`, flat, missing the `.1`); the csproj's osx-arm64 entry lacks
  the copy-to-output link osx-x64 has; `.gitignore` doesn't cover the per-arch subdirs.
- **macOS build cautions** (the analogs of lessons already paid for elsewhere): set
  `MACOSX_DEPLOYMENT_TARGET` old enough (the macOS glibc-baseline analog — the linux
  artifact itself should be REBUILT on the oldest supported distro while hosting it, it
  was built on Ubuntu 24.04 against a 20.04+ support claim); verify with `otool -L` that
  the dylibs link only system libraries (no Homebrew deps); GitHub Actions macos-14
  (arm64) / macos-13 or `-arch x86_64` (x64) are the natural builders.
- **Size is a non-issue** (measured 2026-06-09, recorded so it isn't re-litigated): the
  win+linux vkd3d natives pack to a 3.3 MB nupkg; the two macOS dylibs add ~1.5–2.5 MB
  compressed; the all-RID `ShadowDusk.HLSL` lands ~5–6 MB — smaller than the existing
  `ShadowDusk.Wasm` package alone (6.3 MB) and a fifth of the `Vortice.Dxc` transitive
  (~29 MB). Multi-RID cost is package-cache-only; deployed apps carry one RID's binary.
- **Licensing housekeeping:** vkd3d-shader is LGPL-2.1+; same posture as the win/linux
  binaries already shipped, but the package should carry the license notice properly when
  the artifact hosting is formalized.
- Strategy context (why we build-and-bundle rather than make users fetch anything):
  `docs/the-purpose.md` → *Compiler-leverage strategy* — self-containment is a hard
  requirement; there are no upstream prebuilt macOS binaries to point at anyway (Wine
  ships source tarballs only).

### C — AS-BUILT ✅ (done 2026-06-10, PRs #35 / #36 / #37)

What landed, in merge order:

- **PR #35 (pre-wiring):** the four macOS-arrival fixes recorded above (loader +
  `FnaTestGate`/`Vkd3dTestGate` per-arch `osx-{x64,arm64}/` probing, `restore.sh` Darwin
  filename, csproj osx-arm64 copy-link, `.gitignore` subdirs).
- **PR #36 (builds):** a dispatchable `vkd3d-build.yml` workflow builds vkd3d **1.17**
  from the WineHQ tarball on ubuntu **20.04** (glibc 2.31 baseline — the linux .so was
  REBUILT there per the caution above) and macos arm64/x64, with `otool -L` /
  `readelf -d` link-purity gates. win-x64 stays the byte-exact MSYS2 binary the
  Phase 18/39/40 goldens were proven against.
- **PR #37 (hosting + restore + tests + licensing):** all four per-RID binaries hosted on
  the fixed release tag **`native-vkd3d-1.17`**; `tools/restore.{sh,ps1}` download +
  SHA-256-verify against embedded pins (present+match → no-op; mismatch → re-download;
  offline → non-fatal); `ci.yml` caches `tools/vkd3d` keyed `vkd3d-1.17-<os>`;
  `Vkd3dFactAttribute` became an availability probe; DirectX integration rows select
  `DxbcBackend.Vkd3d` off-Windows; `THIRD-PARTY-NOTICES.txt` (LGPL-2.1) packs into the
  nupkg root; `release.yml`'s pack gate now requires **all four** natives **and** the
  notice file.

**Evidence (main CI run 27276635277, 2026-06-10):** restore reports hash-OK for all four
binaries on all 3 OS; the FNA integration suite (rungs 1–2) runs **green on ubuntu and
macOS** (0 skips — it was silently skipping before); the two vkd3d live-compile tests
pass on all 3 OS; the release pack gate is now satisfiable from any clean machine.

**Residue (the honest delta vs the original definition of done) — ✅ RESOLVED 2026-06-10 by Phase 18 Track A (next section):**

- ~~The third vkd3d test (`DxbcReflectionExtractor_ReflectsVkd3dOutput`) **cannot pass
  off-Windows**: `DxbcReflectionExtractor` P/Invokes d3dcompiler_47's `D3DReflect`
  (Windows-only — "Phase 18 Track A"). PR #37 un-gated it everywhere so it *failed* on
  ubuntu/macOS; it now skips truthfully off-Windows (`Vkd3dFact(requiresD3DReflect: true)`).~~
  → Reflection is now the pure-managed `RdefReader`; the test is un-gated (plain
  `[Vkd3dFact]`) and runs wherever the vkd3d native exists. The `requiresD3DReflect`
  parameter remains, repurposed for tests that need the **D3DReflect test oracle**
  (`DxbcReflectionParityTests`).
- ~~Same root cause: the **DX11 `.mgfx` pipeline end-to-end still requires Windows** …
  `Compile_Minimal_DirectX_ReturnsBytes` + the `DirectX_11` fixture rows stay red
  off-Windows until cross-platform DXBC reflection (Track A) exists.~~ → The SD0210
  "reflection requires Windows" error is gone; those rows compile end-to-end via the
  vkd3d backend on any OS (they already selected `DxbcBackend.Vkd3d` off-Windows —
  Phase 37 C step 5 — reflection was the last blocker).
- Cross-host `.mgfx`-level byte-equality for DX11 is now *possible*; asserting it in CI
  (the Phase 30 cross-host pattern) is a follow-up, not done in Track A.

---

## Phase 18 Track A — cross-platform DXBC reflection ✅ (done 2026-06-10)

The Finding-C residue above, closed. `D3DReflect` (d3dcompiler_47, Windows-only) was the
last native in the DX11 `.mgfx` pipeline; it is replaced by **`RdefReader`**
(`src/ShadowDusk.Core/Reflection/RdefReader.cs`) — a pure-managed parser of the DXBC
container's `RDEF` + `ISGN`/`OSGN` chunks, the SM4/SM5 sibling of `CtabReader` (and like
it, exactly the "we own container readers, never compilers" leverage posture). Placed in
`ShadowDusk.Core` (dependency-free) so the future WASM DX path (Phase 4.1) gets it for free.

**Decision: full replacement, one code path.** `DxbcReflectionExtractor.Extract` delegates
to `RdefReader` on EVERY OS — no managed-off-Windows/D3DReflect-on-Windows split (two code
paths would violate the determinism spirit). `D3DReflect` survives only as a **test
oracle** (`tests/ShadowDusk.HLSL.Tests/Reflection/D3DReflectOracle.cs` — the pre-Track-A
extractor code verbatim); the `Vortice.Direct3D11` package reference moved from
`ShadowDusk.HLSL` (product) to `ShadowDusk.HLSL.Tests` accordingly.

**Evidence (the bar was oracle parity, not "looks right"):**

- **Oracle parity** — `DxbcReflectionParityTests` (Integration, Windows): for a 9-shader
  corpus (minimal PS; textured PS; texture-only PS = the empty-`$Globals` drop; cbuffer-heavy
  with float/bool/int/uint scalars, vectors, `float4x4`/`float3x3`, scalar/vector arrays and a
  `float4x3[2]` matrix array; struct cbuffer with nested struct + struct array; VS+PS-style VS;
  `SV_VertexID`; `SV_Depth` output; cube+volume textures with explicit registers), the managed
  `ReflectedEffect` is **deeply equal (strict ordering) to D3DReflect's** for the DXBC of
  **both backends** — d3dcompiler_47 AND vkd3d 1.17. Both tests green.
- **Pure unit tests** — `RdefReaderTests` (+ `DxbcSyntheticBlobs`, the `Fx2SyntheticShaders`
  pattern): 18 disk-free tests over synthetic containers covering field-by-field parsing,
  the array size-rounding quirk, nested struct members, the empty-cbuffer drop, the
  `SV_Target`/`SV_Depth` system-value fix-up (D3DReflect fixes up the 0 fxc stores, by
  semantic name — verified against d3dcompiler_47 byte-level), SRV-dimension folding, the
  SM4 (no-RD11, 24-byte variable record) layout, and loud structured failures
  (truncation, bad fourcc, missing RDEF, unmapped class/type).
- **No `.mgfx` byte change** — direct A/B: the full `tests/fixtures/shaders/*.fx` corpus
  compiled with the pre-Track-A CLI (e3b21d7) and the post-Track-A CLI for **both**
  `DirectX_11` and `OpenGL`; all 34 compiling fixtures **SHA-256-identical**, 15
  non-DX/GL fixtures fail identically on both sides. Full suite 871/871 green
  (golden corpus + byte-identity + render proxies included), **0 skips** on Windows.
- **Bonus unblock:** `CompilationPipeline` now constructs DXC **lazily** — the DX11 path
  never uses DXC, so DX11 compiles no longer die in `DxcShaderCompiler`'s constructor on
  hosts without the DXC native (i.e. **macOS DX11 compiles work before Finding A lands**;
  Finding A still gates GL/Vulkan on macOS). Byte-transparent (A/B above covers it).

**Corner cases deliberately NOT covered (honest list):**

- **Signature variants `ISG1`/`OSG1`/`OSG5`** are parsed (stride-adjusted) but have no
  parity coverage — d3dcompiler/vkd3d emit classic `ISGN`/`OSGN` for the vs_5_0/ps_5_0
  profiles ShadowDusk uses (SG1 needs min-precision; OSG5 needs GS streams).
- **tbuffers, UAVs, structured/byte-address buffers, interface classes, `double`/min-precision
  types**: not modeled — exactly as the previous extractor (it threw "Unmapped …"; the reader
  fails loudly with SD0101). A tbuffer cbuffer record still lands in `ConstantBuffers`
  with BindSlot 0, matching the old extractor's behavior verbatim.
- **Input-signature system-value fix-up** is applied by semantic name whenever the stored
  value is 0 (matching Wine's d3dcompiler and the observed MS behavior for outputs); a PS
  *input* `SV_Coverage` (SM5.1 niche) is untested against the oracle.
- **`D3D_NAME` values 17–22** (domain/hull tessellator values absent from Vortice's enum)
  render numerically; HS/DS/GS/CS stages are out of ShadowDusk's scope entirely.
- Cross-host DX11 `.mgfx` byte-equality is enabled but not yet asserted in CI (follow-up).

---

## Investigation technique (how these were found — reproducible)

1. **Read the failing jobs, per OS, not the aggregate.** `gh run view <run> --json jobs --jq '.jobs[]|select(.name|test("Integration"))|...'` to get job IDs, then `gh run view --job <id> --log-failed | grep -iE "unable to load|dlopen|X0000|SD0210|Failed!|Expected.*because"`. The three OS failed for **different** reasons — the aggregate "Integration red" hides that.
2. **Decode the error signature.** `X0000 / (0,0-0)` is not "syntax error" — trace it to `DxcDiagnosticReformatter` to learn it means "DXC emitted unparseable text," i.e. an ICE. Signatures matter more than counts.
3. **Inspect the NuGet package directly.** `ls -R ~/.nuget/packages/vortice.dxc/3.3.4/runtimes/` proved the macOS native is simply absent — faster and more certain than guessing from the load error.
4. **Cross-check same-run jobs.** Comparing `build-and-test` (ImageTests 27/27) vs `integration-tests` (DXC ICE) **in the same run** surfaced the paradox that reframes Phase 36.
5. **Map each failing test to its compile target** by reading the test source — distinguishes vkd3d/DX (Finding C) from DXC-GL (Finding B) failures that share the `X0000` text.
6. **Research upstream availability** (NuGet runtimes, WineHQ tarballs, distro versions, Homebrew formulae) before assuming a package-manager fix exists — it usually doesn't at the pinned version.

---

## Test steps (how to verify each fix)

**Per finding, locally + CI:**
- **A (macOS):** on an Apple-Silicon Mac (or `macos-latest` CI), `dotnet test --filter "Category=Integration"` → no `DllNotFoundException`; `Compile_Minimal_OpenGL_ReturnsBytes` passes; the byte-identity corpus test confirms macOS SPIR-V == win-x64 oracle; a scratch consumer (`dotnet add package ShadowDusk.Compiler`; `CompileAsync(fx, OpenGL)`) returns `.mgfx` bytes on macOS.
- **B (Linux):** in Docker `dotnet/sdk:8.0`, `dotnet test -c Release --filter "Category=Integration"` → no `X0000`/ICE; rung-4 render-vs-`mgfxc` on the Linux-compiled `.mgfx` matches; Windows golden + byte-identity unregressed.
- **C (vkd3d):** after restore downloads the native, the 3 vkd3d tests run (not skip) and pass on all 3 OS; `Compile_Minimal_DirectX_ReturnsBytes` passes on Linux/macOS via the vkd3d backend; DXBC bytes identical across hosts.
- **Overall:** `ci.yml` Integration Tests job **green on all 3 OS**; then it can be promoted back to a hard gate and `release.yml` can re-include Integration if desired.

---

## Cross-cutting constraints (do not violate)
- **Byte-identical / deterministic output (constraint 3):** one DXC version + one vkd3d version everywhere; gate every native change with the byte-identity corpus test + rung-4 renders.
- **No substitute compilers (THE PURPOSE):** the fix is the *same* DXC/vkd3d built for the missing RID — never a different HLSL→SPIR-V/DXBC tool.
- **Seamless for the end user:** consumers add the package and it works on their OS — natives ride inside per-RID; no manual install, no required flag.

---

## Sequencing / priority
1. ~~**Finding B diagnosis FIRST** (free, no user action) — reproduce the Linux ICE in Docker and capture the raw DXC error.~~ ✅ Done 2026-06-10 — root cause was Vortice's UTF-16 argument marshalling vs the 4-byte Linux `wchar_t` (see Finding B); fixed in-repo with `DxcNativeInterop`, no native change. The diagnosis also pre-cleared the same hazard for Finding A's macOS dylib.
2. **Finding A (macOS native)** — the actual product gap; highest user impact. Likely the same from-source DXC build, retargeted.
3. ~~**Finding C (vkd3d)** — smallest, opt-in; needs the user to build the linux/macOS 1.17 binaries.~~ ✅ Done 2026-06-10 (see the as-built section).

## Severity & release note
- **0.1.0 (already on nuget) and 0.1.1 both carry the macOS DXC gap** — the product cannot compile shaders on macOS until Finding A lands. This was previously (incorrectly) described as "works everywhere." 0.1.1 does **not** regress it (it adds branding + the CLI rename + CI fixes), so shipping 0.1.1 for the icons is fine — but the macOS claim must be qualified in docs until Phase 37 closes.

## Provenance / related
Found 2026-06-07 investigating the `ci.yml` Integration Tests red on main (run `27105718108`), at the user's request to "fix the integration test for real." Three parallel investigations (DXC-macOS, vkd3d-CI, ubuntu-categorization). Related: [Phase 36](PHASE-36-dxc-linux-spirv-ice.md) (absorbed — Linux DXC ICE), [Phase 30](PHASE-30-ci-and-nuget-release.md) (CI + the per-RID hosting follow-up), `plan/PHASE-4.1-SPIKE-wasm-directx-dxbc.md` (WASM+DX). Memory: `macos-ci-test-stall`, `dxc-linux-spirv-ice`, `cli-rename-and-brand-0.1.1`, `nuget-selfcontained-fix`.
