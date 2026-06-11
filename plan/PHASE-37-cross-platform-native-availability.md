# Phase 37 — Cross-platform integration-test native availability (macOS DXC gap, Linux DXC ICE, vkd3d in CI)

**Status:** 🟠 **Open — created 2026-06-07; Finding C ✅ done 2026-06-10 (PRs #35/#36/#37 — see the as-built section under Finding C).** Remaining: a **consumer-facing product gap** (macOS DXC, Finding A — repo wiring landed 2026-06-10, hosted dylib build pending; see "A — AS-BUILT, part 1") and the dominant **Linux DXC ICE** (Finding B, expands/absorbs Phase 36).
**Track:** Reach (Part 1 of THE PURPOSE) — "compile where `mgfxc` can't (Linux/macOS)." The integration tests are RED because they are faithfully catching real cross-platform gaps; this is not a test-logic problem.

> **Supersedes the scope of [Phase 36](PHASE-36-dxc-linux-spirv-ice.md).** Phase 36 concluded the Linux DXC ICE was confined to the **Debug-CLI / spawned-process** `wasm.yml` path and that "Release in-process works on real Linux." New evidence in this phase **disproves that**: the `ci.yml` **integration-tests** job (Release, **in-process** `EffectCompiler`) ICEs on ~120 tests on `ubuntu-latest`. Phase 36's "likely-quick Release-CLI experiment" is now just one sub-hypothesis under Finding B below.

---

## TL;DR

The `ci.yml` **Integration Tests** job is red on all three OS. Build & Test (the unit gate) is green everywhere; only the integration job (which exercises the real native toolchain) fails — for **three distinct root causes**, one per OS family:

| OS | Failing | Root cause | Category |
|---|---|---|---|
| **Windows** | 2 tests | `vkd3d-shader` native absent (gitignored, built out-of-band) | **C — vkd3d** |
| **Ubuntu** | 132 tests | 12 = vkd3d/DX (no native + no Windows oracle); **120 = DXC "Internal Compiler error" on Linux** | **C (12) + B (120)** |
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
1. Dispatch `dxc-build.yml` (`gh workflow run dxc-build.yml --ref <branch>`); iterate
   until both RID jobs are green (expect iteration — vkd3d-build needed 3 fix commits;
   the main risk is 2022-era LLVM source vs the runners' newer Xcode clang).
2. Download the two artifacts; `gh release create native-dxc-1.7.2212.40 \
   libdxcompiler.osx-x64.dylib libdxcompiler.osx-arm64.dylib LICENSE-DXC.TXT`
   (rename each artifact's `libdxcompiler.dylib` to its per-RID asset name first).
3. Paste the workflow-printed SHA-256s over both `PENDING-FIRST-HOSTED-BUILD`
   placeholders in `tools/restore.sh` + `tools/restore.ps1` (this flips restore to
   enforcing automatically — the placeholder check is the only bypass).
4. Add a `tools/dxc` cache + restore to `ci.yml`'s macOS lanes if not already covered
   by the existing restore step, and re-run CI: the macOS integration job's
   `DllNotFoundException` wall should fall.
5. **Fidelity gate before calling A done:** byte-identity corpus check (macOS SPIR-V
   == win-x64 oracle) + `Compile_Minimal_OpenGL_ReturnsBytes` and the GL corpus green
   on a real Mac (macos CI lane) + the scratch-consumer check from *Test steps* below.

---

## Finding B — Linux: DXC "Internal Compiler error" (expands Phase 36)

### Issue
On `ubuntu-latest`, **120 of 132** integration failures are `error X0000: Shader compilation failed` at position `(0,0-0)` — including on trivial shaders like `Minimal.fx`.

### Cause (signature decoded — but root cause UNRESOLVED)
`DxcDiagnosticReformatter` (`src/ShadowDusk.HLSL/Dxc/DxcDiagnosticReformatter.cs:75-76`) emits `X0000 / "Shader compilation failed" / (0,0-0)` **only when DXC's error text has no parseable `file(line,col)`** — exactly what DXC's `Internal Compiler error:` (an ICE) produces. So every X0000/(0,0-0) failure is a **DXC ICE on Linux**, not a shader syntax error. (Confirmed by the pass/fail pattern: the only DXC tests that *pass* are ones that *expect* failure; every DXC test expecting success fails. SPIRV-Cross itself loads fine — the invalid/empty-SPIR-V tests pass — so **only the DXC compile step is broken.**)

### The paradox (the crux to investigate)
In the **same CI run**:
- `build-and-test` (ubuntu, Release) runs `ShadowDusk.ImageTests` → **27/27 PASS**. Those tests call `new EffectCompiler().CompileAsync(... OpenGL ...)` — DXC→SPIR-V→GLSL **in-process, Release** — and succeed.
- `integration-tests` (ubuntu, Release) ICEs on ~120 in-process DXC compiles, **including `Compile_Minimal_OpenGL_ReturnsBytes`** (a trivial shader).

Same OS image, same commit, same NuGet-restored DXC native, same in-process `EffectCompiler` — **one job compiles, the other ICEs.** This contradicts Phase 36's "in-process works on Linux." The delta between the two `ubuntu-latest` runners is the open question this phase must resolve. Hypotheses to test:
1. **Shader-set difference** — does the ImageTests corpus (curated SM3 PS-only) avoid an intrinsic/construct DXC-on-Linux chokes on, while the broader integration corpus (VS, Vulkan target, multipass, includes) hits it? (But `Minimal.fx` ICEing argues against pure shader-specificity.)
2. **Environment delta** — a system/runtime dependency, working directory, env var, or native-resolution difference between the two jobs.
3. **Spawned-vs-in-process / Debug-vs-Release** — Phase 36's original hypothesis (the CLI corpus path builds Debug + spawns). Largely refuted now (integration is Release in-process), but rule it out.

### How to investigate (reproduce-first)
- **Reproduce locally on real Linux:** Docker `mcr.microsoft.com/dotnet/sdk:8.0` (closest to CI) — `dotnet test ShadowDusk.slnx -c Release --filter "Category=Integration"` and capture the raw DXC error (set `SHADOWDUSK_SAVE_DIAGNOSTICS=1` / log the unreformatted DXC text **before** `DxcDiagnosticReformatter` flattens it to X0000). WSL is a **degraded** repro (missing DXC runtime deps) — use Docker.
- **Bisect the paradox:** run *only* the ImageTests corpus shaders through the integration entry point in Docker, then the integration corpus through the ImageTests entry point, to isolate shader-set vs environment.
- **Capture the real ICE text:** the `(0,0-0)` flattening hides DXC's actual message. Temporarily surface the raw `DxcCompiler` error string to see *what* DXC is choking on (assertion? unsupported intrinsic? missing runtime dep?).

### How to fix (depends on root cause)
- If the Linux Vortice native genuinely ICEs, the fix is likely the **same "build our own `libdxcompiler.so` from `e043f4a1`"** approach as Finding A (one DXC source built per-RID for linux-x64 too), replacing the Vortice-provided `.so`.
- If it's an environment delta, fix the integration job's environment to match the working build job.
- Either way, **keep one DXC version everywhere** (constraint 3 / cross-host byte-identity) and re-validate Windows output is unregressed.

### Definition of done (B)
Linux compiles the PS-only + VS-driven corpus **with no ICE**, the Linux `.mgfx` **renders pixel-equivalent to `mgfxc`** (rung-4, same bar as Phase 17/18), Windows output is **unregressed** (golden + byte-identity green), and `ci.yml`'s integration job runs the Linux compile to completion.

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

**Residue (the honest delta vs the original definition of done):**

- The third vkd3d test (`DxbcReflectionExtractor_ReflectsVkd3dOutput`) **cannot pass
  off-Windows**: `DxbcReflectionExtractor` P/Invokes d3dcompiler_47's `D3DReflect`
  (Windows-only — "Phase 18 Track A"). PR #37 un-gated it everywhere so it *failed* on
  ubuntu/macOS; it now skips truthfully off-Windows (`Vkd3dFact(requiresD3DReflect: true)`).
- Same root cause: the **DX11 `.mgfx` pipeline end-to-end still requires Windows** — the
  vkd3d backend compiles DXBC cross-platform, but reflection (SD0210) blocks the rest of
  the pipeline, so `Compile_Minimal_DirectX_ReturnsBytes` + the `DirectX_11` fixture rows
  stay red off-Windows until cross-platform DXBC reflection (Track A) exists. This is
  **not** a vkd3d-provisioning gap; it predates C and is the remaining DX-reach item
  (natural follow-on work alongside Finding A/B, or its own phase).
- Cross-host DXBC byte-equality remains asserted only at the raw-DXBC level (the vkd3d
  live tests), not `.mgfx`-level, for the same reason.

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
1. **Finding B diagnosis FIRST** (free, no user action) — reproduce the Linux ICE in Docker and capture the raw DXC error. It's the dominant blocker (120 tests) and may share a root (and therefore a fix) with Finding A. Knowing its true cause sizes the whole phase.
2. **Finding A (macOS native)** — the actual product gap; highest user impact. Likely the same from-source DXC build, retargeted.
3. ~~**Finding C (vkd3d)** — smallest, opt-in; needs the user to build the linux/macOS 1.17 binaries.~~ ✅ Done 2026-06-10 (see the as-built section).

## Severity & release note
- **0.1.0 (already on nuget) and 0.1.1 both carry the macOS DXC gap** — the product cannot compile shaders on macOS until Finding A lands. This was previously (incorrectly) described as "works everywhere." 0.1.1 does **not** regress it (it adds branding + the CLI rename + CI fixes), so shipping 0.1.1 for the icons is fine — but the macOS claim must be qualified in docs until Phase 37 closes.

## Provenance / related
Found 2026-06-07 investigating the `ci.yml` Integration Tests red on main (run `27105718108`), at the user's request to "fix the integration test for real." Three parallel investigations (DXC-macOS, vkd3d-CI, ubuntu-categorization). Related: [Phase 36](PHASE-36-dxc-linux-spirv-ice.md) (absorbed — Linux DXC ICE), [Phase 30](PHASE-30-ci-and-nuget-release.md) (CI + the per-RID hosting follow-up), `plan/PHASE-4.1-SPIKE-wasm-directx-dxbc.md` (WASM+DX). Memory: `macos-ci-test-stall`, `dxc-linux-spirv-ice`, `cli-rename-and-brand-0.1.1`, `nuget-selfcontained-fix`.
