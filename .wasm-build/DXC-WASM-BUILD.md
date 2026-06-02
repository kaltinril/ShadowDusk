# DXC -> WASM Build Report (Phase 23, Milestone M0)

**Goal:** produce `dxcompiler.{js,wasm}` — an emscripten module exporting
`compileToSpirv(hlsl: string, args: string[]) -> Uint8Array`, built from the SAME
DXC version the desktop pipeline uses, so its SPIR-V is **byte-identical to desktop
DXC** on the corpus. Byte-identity is M0's DoD and is what licenses the free
transitive render-proof (per `plan/PHASE-23-in-browser-compilation.md`).

This is the **faithful** frontend (Option A). No substitute compiler. Slang is
sample-only and is NOT used here.

---

## 1. Pinned DXC version — and how it was mapped from Vortice.Dxc

| What | Value |
|---|---|
| Desktop NuGet | `Vortice.Dxc` **3.3.4** (`Directory.Packages.props`) |
| Bundled native | `Vortice.Dxc/3.3.4/runtimes/{win-x64,win-arm64,linux-x64}/native/dxcompiler.{dll,so}` — Vortice ships its OWN DXC build directly in the package (no separate native dep). The desktop pipeline loads THIS binary. |
| DLL FileVersion | **1.7.2212.40** |
| DLL ProductVersion | **1.7.2212.40 (e043f4a12)** |
| Upstream release | DXC **v1.7.2212** ("DX Compiler release for December 2022") |
| **Pinned commit** | **`e043f4a1286f4e1026222ab1bc94e25de8d0e959`** — "Merge pull request #5067 from pow2clk/cp-release-1.7.2212", dated 2023-03-01. This is the `release-1.7.2212` branch HEAD-ish, i.e. the exact commit Vortice's build reports — NOT the `v1.7.2212` tag commit (`8c9d92be…`), which is earlier. The FileVersion build commit is authoritative for byte-identity. |

**Mapping method:** read `dxcompiler.dll`'s `VersionInfo` (`ProductVersion = "1.7.2212.40 (e043f4a12)"`) → the 8-hex suffix is the build commit → confirmed against the GitHub API that `e043f4a1286f4e1026222ab1bc94e25de8d0e959` exists on the `release-1.7.2212` line.

**Pinned submodules** (DXC's gitlinked SHAs at that commit — byte-identity needs the exact SPIR-V backend, part of which lives in SPIRV-Tools):

| Submodule | Pinned SHA |
|---|---|
| `external/SPIRV-Headers` | `1d31a100405cf8783ca7a31e31cdd727c9fc54c3` |
| `external/SPIRV-Tools` | `40f5bf59c6acb4754a0bffd3c53a715732883a12` |
| `external/DirectX-Headers` | `980971e835876dc0cde415e8f9bc646e64667bf7` |

`external/{googletest,re2,effcee}` are test-only and intentionally NOT fetched (tests are disabled in the WASM build).

### ⚠ Byte-identity caveat to validate (the one real risk)
Vortice publishes its **own** DXC builds. The DLL's commit `e043f4a12` is what we
pinned, so source-wise we build the same compiler. BUT byte-identical SPIR-V also
requires the build to be configured the same way Vortice configured theirs (same
SPIRV-Tools, same codegen options, no incidental `LLVM_APPEND_VC_REV`-style data
leaking into the SPIR-V module). DXC's SPIR-V output does **not** embed a compiler
version string in the module by default, so this is *expected* to hold — but it is
**only proven** when `node-test-dxc-wasm.mjs` passes on the corpus. Do not claim M0
done on the source pin alone.

---

## 2. Toolchain (pinned)

| Tool | Version | Notes |
|---|---|---|
| emscripten | **3.1.34** | HARD constraint — the .NET 8 WASM runtime's pin (`Microsoft.NET.Runtime.Emscripten.3.1.34.Sdk.*`). Installed/activated via `.wasm-build/emsdk` (emsdk SDK commit `2fdd6b9e5b67d5b62f84d0501a876513ff118ef1`). A mismatch fails at link/load, not cleanly. (Note: the SPIRV-Cross module was built with whatever emsdk was previously active — 5.0.7; DXC is pinned to 3.1.34 as required.) |
| Host C++ | MSVC 14.44 (VS 2022 Community) | builds the native tablegen tools (Stage 0). |
| CMake | VS-bundled (`…/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe`) | |
| Ninja | VS-bundled (`…/CMake/Ninja/ninja.exe`) | |
| node | v22 | runs the byte-identity gate. |

---

## 3. WASM patch / configure strategy

| Obstacle (Hexops devlog / DXC #4766) | Resolution in this build |
|---|---|
| **COM** (`IDxcCompiler3`/`IDxcUtils` need the Windows COM runtime) | DXC's bundled **WinAdapter** (`include/dxc/WinAdapter.h`, `lib/DxcSupport/WinAdapter.cpp`) provides the non-Windows COM shim; it compiles into `libdxcompiler` automatically for non-WIN32 targets (emscripten is non-WIN32). `DxcCreateInstance` resolves without OLE. |
| **C++ exceptions** (DXC throws internally; default no-exceptions WASM traps) | `-fwasm-exceptions` on compile AND link (Stage 1 `CMAKE_*_FLAGS` + Stage 2 em++). RTTI/EH already ON via `PredefinedParams.cmake` (`LLVM_ENABLE_EH/RTTI`). |
| **Filesystem / `#include`** | `-sFILESYSTEM=0`. ShadowDusk pre-flattens all `#include`s in its managed preprocessor *before* DXC, and passes a null `IDxcIncludeHandler` on desktop too — so DXC never needs a real FS for the corpus. |
| **threading / pthread** | `-DLLVM_ENABLE_THREADS=OFF`. |
| **DXIL validator/signer (`dxil.dll`)** | NOT built and NOT needed — signing is a DXIL-only step; the `-spirv` target never touches it. (`SPIRV_BUILD_TESTS=OFF`, no `dxil.dll` dependency in the `-spirv` path.) |
| **LLVM tablegen is host-only** | THE classic LLVM cross-compile gate. `llvm-tblgen`/`clang-tblgen` are build-time tools — an emscripten build would compile them to WASM (unrunnable). Stage 0 builds them with the HOST (MSVC) toolchain; Stage 1 points at them via `-DLLVM_TABLEGEN` / `-DCLANG_TABLEGEN`. |
| **Module shape / export contract** | `-sMODULARIZE=1 -sEXPORT_ES6=1 -sEXPORT_NAME=createDxcModule`, embind (`--bind`). Glue (`dxc-wasm-glue.cpp`) exports `compileToSpirv(hlsl, args[]) -> Uint8Array` matching the existing `shadowdusk-dxc` JS contract, so `JsDxcShaderCompiler` + `DxcFlagBuilder` are reused unchanged (the desktop arg list forwards verbatim — the property Slang lacked). |

### The build script (3 stages, re-runnable)
`.wasm-build/build-dxc-wasm.ps1` (launched via `.wasm-build/Invoke-DxcWasmBuild.ps1`,
which loads the MSVC host env from `msvc-env.txt`):

- **Stage 0** — native `llvm-tblgen` + `clang-tblgen` (`-SkipHostTblgen` to reuse).
- **Stage 1** — `emcmake cmake -GNinja -C cmake/caches/PredefinedParams.cmake -DENABLE_SPIRV_CODEGEN=ON` + tests OFF + `-fwasm-exceptions` + `-DLLVM_ENABLE_THREADS=OFF` + the host tablegen paths; then `ninja libdxcompiler` (`-SkipLib` to reuse). This is the long LLVM-fork build.
- **Stage 2** — `em++ --bind dxc-wasm-glue.cpp -ldxcompiler …` → `dxc-wasm-out/dxcompiler.{js,wasm}`.

---

## 4. Byte-identity gate (M0 DoD)

**Oracle:** `.wasm-build/dxc-corpus-probe` drives the REAL desktop pipeline
(`EffectCompiler` + `FxPreParser` + `Preprocessor` + `DxcShaderCompiler` @ Vortice.Dxc
3.3.4) over the 10-shader Phase-17 PS-only corpus, capturing for each shader the EXACT
triple `(preprocessed HLSL, DXC arg list, desktop SPIR-V bytes)` — written to
`.wasm-build/corpus-spirv/{name}.{hlsl,args.json,spv}`. A capturing `IDxcShaderCompiler`
decorator records precisely what the desktop pipeline feeds DXC; the arg list is read
from the internal `DxcFlagBuilder` via reflection (no reimplementation drift).

Corpus (== `SpirvReflectionByteIdentityTests.s_corpus`): Grayscale, Invert, TintShader,
Sepia, Saturate, Pixelated, Scanlines, Fading, Dots, Dissolve. Captured args are uniformly
`-E <entry> -T ps_5_0 -spirv -fvk-use-dx-layout -auto-binding-space 1 -Zpr -WX`.

**Gate:** `node .wasm-build/node-test-dxc-wasm.mjs` loads the WASM module and asserts,
for each corpus shader, that `compileToSpirv(hlsl, args)` equals the desktop `.spv`
byte-for-byte. The WASM module receives byte-for-byte the same `hlsl`+`args`, so
byte-equal SPIR-V proves it is the same compiler → the transitive render-proof.

---

## 5. Build status

(Updated as the build progresses — see the STATUS line at the bottom.)

### Stage 0 — host tablegen: ✅ DONE
`llvm-tblgen.exe` + `clang-tblgen.exe` built with the host MSVC 14.44 toolchain
(`.wasm-build/dxc-src/build-host-tblgen/bin/`). Pointed at by Stage 1 via
`-DLLVM_TABLEGEN` / `-DCLANG_TABLEGEN`.

### Stage 1 — WASM configure: ✅ DONE (after 3 patches)
`emcmake cmake` for DXC's LLVM fork under emscripten required three fixes beyond the
documented COM/exceptions/FS/threading set — found by iterating the configure:

1. **`config.guess` is a bash script and fails on Windows** (`GetHostTriple.cmake:24:
   Failed to execute autoconf/config.guess`). Fix: `-DLLVM_INFERRED_HOST_TRIPLE=wasm32-unknown-emscripten`
   (skips `get_host_triple()` entirely).
2. **`PredefinedParams.cmake` only honors `-D` params specified BEFORE `-C`** (its own
   header says so). With test-disabling `-D`s placed *after* `-C`, the cache force-set
   `HLSL_INCLUDE_TESTS=ON`, leaving a dangling `check-clang-taef -> ClangHLSLTests`
   lit target that failed CMake **generate**. Fix: move all `-D` overrides (tests OFF,
   SPIRV ON, host triple, etc.) **before** `-C $predef`.
3. **emscripten sets `CMAKE_CROSSCOMPILING`, which forces `LLVM_USE_HOST_TOOLS=ON` ->
   `include(CrossCompile)` -> a NATIVE sub-build that wrongly uses `emcc.bat` as the
   *host* C compiler** (`CMakeTestCCompiler.cmake:67: emcc.bat ... is not able to
   compile a simple test program`, in `build-wasm/NATIVE/`). Because we already supply
   prebuilt host tablegen, the NATIVE build is redundant. Fix: `-DLLVM_USE_HOST_TOOLS=OFF`.

With (1)+(2)+(3) the top-level `build.ninja` (~3.3 MB) generates and the configure
exits clean. (All three fixes are baked into `build-dxc-wasm.ps1`.)

### Stage 1 — WASM configure: ✅ CONFIRMED CLEAN (with Patches 1 + 2)
After Patch 1 (no NATIVE) the generate step hit a *second* dangling-target error:
`tools/llvm-config/CMakeLists.txt:50: dependency target "CONFIGURE_LLVM_NATIVE" of
"NativeLLVMConfig" does not exist`. `llvm-config` keys its own cross-compile NATIVE
wiring on `CMAKE_CROSSCOMPILING` (not `LLVM_USE_HOST_TOOLS`), so disabling NATIVE left
it dangling. **Patch 2** guards that block on `TARGET CONFIGURE_LLVM_NATIVE`. With both
patches the configure is clean:
- `Configuring done (156.3s)` → `Generating done` → `Build files have been written to:
  .../build-wasm`
- `build.ninja` = ~3.34 MB
- **0 CMake errors**, **no NATIVE/ sub-build**
Both patches are applied idempotently by `build-dxc-wasm.ps1` (and documented in
`dxc-wasm-patches.txt`), so the configure is fully reproducible.

The diagnosed NATIVE root cause (for the record): `CrossCompile.cmake` calls
`llvm_create_cross_target_internal(NATIVE "" Release)` with an **empty toolchain name**,
so no `CMAKE_TOOLCHAIN_FILE` is set for the NATIVE sub-configure and it inherits the
**emscripten** compiler (emcc) as the *host* compiler — which then fails its own
compiler check. Disabling the redundant NATIVE build (we supply prebuilt host tablegen)
is cleaner than trying to point it at MSVC.

### Stage 1 — `ninja dxcompiler`: ✅ DONE
`ninja -n dxcompiler` reported **~803 compile actions** (full LLVM core + clang frontend
+ SPIR-V codegen → `lib/libdxcompiler.so`). Under emscripten this is the multi-hour
LLVM-fork build the phase doc anticipated. Completed via
`Invoke-DxcWasmBuild.ps1 -SkipHostTblgen`.

### Stage 2 — link the embind module: ✅ DONE
`em++ --bind dxc-wasm-glue.cpp -ldxcompiler …` → `dxc-wasm-out/dxcompiler.{js,wasm}`
(`dxcompiler.wasm` ≈ 17.4 MB; `dxcompiler.js` ≈ 54 KB).

### Byte-identity gate (M0 DoD): ✅ PASSED
`node .wasm-build/node-test-dxc-wasm.mjs` — **ALL 10/10 corpus shaders byte-identical
to desktop DXC**:
Dissolve 2060, Dots 1912, Fading 1020, Grayscale 1104, Invert 1080, Pixelated 1024,
Saturate 1616, Scanlines 1364, Sepia 1240, TintShader 1172 (bytes) — each exact.

**STATUS: M0 COMPLETE.** The faithful pinned DXC→WASM (Option A) emits SPIR-V byte-for-byte
identical to the desktop CLI on the full corpus — no substitute compiler. This licenses the
transitive render-proof per `plan/PHASE-23-in-browser-compilation.md`.

> Provenance note: this report's final three subsections (Stage 2, gate) were recorded
> after-the-fact — the build completed and `node-test-dxc-wasm.mjs` passed, but the agent
> driving it was stopped before it wrote them. Re-verified independently on 2026-06-02 by
> re-running `node node-test-dxc-wasm.mjs` against the on-disk artifacts: 10/10 byte-identical.
