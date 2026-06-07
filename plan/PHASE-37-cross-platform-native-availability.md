# Phase 37 — Cross-platform integration-test native availability (macOS DXC gap, Linux DXC ICE, vkd3d in CI)

**Status:** 🔴 **Open — created 2026-06-07.** Contains a **consumer-facing product gap** (macOS), a dominant **Linux DXC ICE** (expands/absorbs Phase 36), and a smaller **vkd3d CI-provisioning** task.
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
3. **Finding C (vkd3d)** — smallest, opt-in; needs the user to build the linux/macOS 1.17 binaries.

## Severity & release note
- **0.1.0 (already on nuget) and 0.1.1 both carry the macOS DXC gap** — the product cannot compile shaders on macOS until Finding A lands. This was previously (incorrectly) described as "works everywhere." 0.1.1 does **not** regress it (it adds branding + the CLI rename + CI fixes), so shipping 0.1.1 for the icons is fine — but the macOS claim must be qualified in docs until Phase 37 closes.

## Provenance / related
Found 2026-06-07 investigating the `ci.yml` Integration Tests red on main (run `27105718108`), at the user's request to "fix the integration test for real." Three parallel investigations (DXC-macOS, vkd3d-CI, ubuntu-categorization). Related: [Phase 36](PHASE-36-dxc-linux-spirv-ice.md) (absorbed — Linux DXC ICE), [Phase 30](PHASE-30-ci-and-nuget-release.md) (CI + the per-RID hosting follow-up), `plan/PHASE-4.1-SPIKE-wasm-directx-dxbc.md` (WASM+DX). Memory: `macos-ci-test-stall`, `dxc-linux-spirv-ice`, `cli-rename-and-brand-0.1.1`, `nuget-selfcontained-fix`.
