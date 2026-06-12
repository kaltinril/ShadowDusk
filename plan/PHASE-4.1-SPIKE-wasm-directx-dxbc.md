# Phase 4.1 — Research Spike: WASM + DirectX DXBC (now: vkd3d → WASM)

**Status:** 🟢 **Compile path DONE — byte-identity gate PASSED 98/98 (2026-06-11).** Both
halves landed the same day: the emscripten build is green + hosted on
`native-vkd3d-wasm-1.17` (*Build pipeline* section), the managed interop is wired
end-to-end (*Managed-interop half* section), restore pins are real, and
`node-test-vkd3d-wasm.mjs` proves WASM vkd3d == desktop vkd3d byte-for-byte over the full
DX (SM5) + FNA (SM1–3) corpus. **Remaining rung:** a real-browser render proof (G2
analogue) in the KNI/Blazor consumers, + removing the temporary CI `push:` trigger after
merge. Un-parked 2026-06-09; elevated from
far-future spike to the **keystone of the browser-export-station vision** (owner decision;
the question this doc left pending is answered: yes, browser-DirectX *and browser-FNA* are
goals — as **export targets**, not render backends). The what and why:

- **Compile-target ≠ render-backend.** A `ShadowDusk.Wasm` website is intended to let
  users upload `.fx` and download compiled artifacts for *any* supported consumer —
  MonoGame GL (works today, byte-identical to desktop), MonoGame **DX11 DXBC**, and **FNA
  fx_2_0 `.fxb`** (which did not exist when this spike was written — Phase 39 added it).
  Host-appropriate default, explicit override.
- **One artifact closes two cells.** Both missing browser targets ride vkd3d-shader; the
  fx_2_0 writer, `D3d9BytecodePatcher`, and CTAB reflection are managed C# that already
  run in WASM. Compiling vkd3d (same pinned 1.17 source) to WASM therefore unlocks DX
  **and** FNA export at once, with no substitute compiler and desktop byte-identity as
  the bar (the G1-gate pattern). `ShadowDusk.Wasm`'s `SD0304` "Fna unavailable on WASM"
  guard then flips to supported.
- **Sequencing:** after Phase 37 (artifact hosting + macOS natives — the desktop matrix
  and release pipeline come first); reuse Phase 23's emscripten toolchain/recipes (the
  DXC→WASM build was the harder precedent). Size expectation: single-digit MB compressed
  (cf. dxcompiler.wasm 16.6 MB raw → the whole `ShadowDusk.Wasm` nupkg is 6.3 MB).
- Strategy context: `docs/the-purpose.md` → *Compiler-leverage strategy* and the
  host×target matrix; `plan/plan.md` → Key Decisions.

Original spike framing follows (the candidate analysis below remains the work plan;
Option A is the chosen direction).

---

## Managed-interop half — DONE (2026-06-11, branch `feature/phase4.1-wasm-vkd3d-interop`)

The Phase 4.1 work was split in two: this half is **everything managed** (interop,
composition, restore plumbing, gates); the parallel build-pipeline half owns the
emscripten build of `vkd3d-shader.{js,wasm}` (`tools/vkd3d-wasm/`, workflows) and
hosting it on the fixed release tag **`native-vkd3d-wasm-1.17`**.

### The wrapper C ABI (the contract between the two halves)

```c
// Returns 0 (VKD3D_OK) on success, negative vkd3d error code on failure.
// target_type: 4 = VKD3D_SHADER_TARGET_D3D_BYTECODE (SM1–3, FNA),
//              5 = VKD3D_SHADER_TARGET_DXBC_TPF (SM4/5, DX11).
int sdw_vkd3d_compile(const unsigned char* source, int source_len,
                      const char* entry_point, const char* profile,
                      const char* source_name, int target_type,
                      unsigned char** out_code, int* out_size, char** out_messages);
void sdw_vkd3d_free_code(unsigned char* p);
void sdw_vkd3d_free_messages(char* p);
```

Source bytes are UTF-8, NOT null-terminated (pointer + length); entry/profile/
source-name are C strings (`source_name` may be NULL). Module shape: emscripten
MODULARIZE=1 + EXPORT_ES6 (`export default createVkd3dModule`), locating its `.wasm`
via `import.meta.url`.

**Glue expectations beyond the C ABI** (deviation-class notes for the build half —
the shim was written to need the MINIMUM runtime surface):

- exports `_sdw_vkd3d_compile`, `_sdw_vkd3d_free_code`, `_sdw_vkd3d_free_messages`,
  `_malloc`, `_free`, and the `HEAPU8` view on the module instance. The shim
  deliberately uses **no** `cwrap`/`getValue`/`UTF8ToString` runtime exports
  (TextEncoder/TextDecoder + a DataView over `HEAPU8.buffer` instead).
- `HEAPU8` must be the *current* view after memory growth (standard emscripten
  behavior; the shim re-reads it per access and never caches across the call).
- No other ABI deviations: the [JSImport]/shim surface matches the contract above
  exactly (one `compile()` call per stage compile; messages always freed via
  `sdw_vkd3d_free_messages`, code via `sdw_vkd3d_free_code`).

### What is wired (all committed, builds + tests green)

| Piece | Where | Notes |
|---|---|---|
| Shared request→ABI + error mapping | `src/ShadowDusk.HLSL/Vkd3d/Vkd3dCompileContract.cs` (internal; IVT to Wasm/Tests/probe) | profile defaults vs_5_0/ps_5_0, SM ≤ 3 → target_type 4, else 5; verbatim-diagnostic mapping with the SD0212 fallback. Desktop `Vkd3dShaderCompiler` now delegates to it — semantics unchanged (179 integration tests incl. `CrossHostByteIdentityTests` still green). |
| Pipeline seam | `EffectCompiler` / `CompilationPipeline` `dxbcCompilerFactory` (4th optional ctor factory) | null = pre-existing desktop behavior byte-for-byte (DirectX `DxbcBackend` switch; FNA always vkd3d). Non-null routes BOTH DirectX and FNA through the injected backend. |
| WASM backend | `src/ShadowDusk.Wasm/WasmVkd3dShaderCompiler.cs` + `Vkd3dInterop` ([JSImport]s in `JsShaderBackends.cs`) | sealed `IDxbcShaderCompiler`; mirrors the desktop backend one-for-one via the shared contract; lazy `EnsureReadyAsync` (the DXC pattern — no page-init download). |
| JS shim | `src/ShadowDusk.Wasm/wwwroot/shadowdusk-vkd3d.js` (committed) | lazy `ensureReady()` + synchronous `compile()`; heap marshalling per the ABI; throws vkd3d's verbatim messages (→ `JSException` → shared reformatter). |
| Guard flip | `WasmShaderCompiler` | **SD0304 (FNA-unavailable-on-WASM) guard REMOVED**; DirectX + Fna now compose through the injected WASM vkd3d backend (there was no separate DX-on-WASM guard — DX previously failed incidentally inside the desktop backends). Module-genuinely-not-loadable now fails loudly with **SD1902** (the WASM sibling of SD0211, joining SD1900 DXC / SD1901 SPIRV-Cross). No consumer flag anywhere — host-appropriate backend is injected automatically. |
| Module registration | `WasmModuleRegistration` | third module `shadowdusk-vkd3d` registered alongside dxc/spirv-cross (registration evaluates only the tiny committed shim — safe while the wasm artifact is absent). |
| Restore | `tools/restore.{ps1,sh}` (`Restore-Vkd3dWasm` / `restore_vkd3d_wasm`) | fixed tag `native-vkd3d-wasm-1.17`, SHA-256 pins = `PENDING-FIRST-HOSTED-BUILD` → **skip-with-notice** (the Phase 37 A pattern); local `.wasm-build/vkd3d-wasm-out/` copy takes precedence (the Restore-DxcWasm pattern); dest `src/ShadowDusk.Wasm/wwwroot/vkd3d/vkd3d-shader.{js,wasm}` (gitignored; `RESTORE.md` committed). |
| Packaging | `ShadowDusk.Wasm.csproj` | Razor SDK packs `wwwroot/vkd3d/` automatically as static web assets once restored; until then a high-importance **Message** target notices the absence (upgrade it to an Error like `VerifyDxcWasmPresent` once the hosted build lands). |
| Byte-identity gate (the bar) | `tests/ShadowDusk.BrowserTests/node-test-vkd3d-wasm.mjs` + `Vkd3dCorpusProbe/` (npm `vkd3d-gate`) | the Phase 23 G1 mechanism for vkd3d: the dotnet probe records every vkd3d compile the REAL pipeline issues over the byte-identity corpus (37 DX fixtures + 28 FNA fixtures = **98 stage compiles**, desktop ground truth verified working), the node gate replays each through the PRODUCT shim and asserts byte-identity. Skips loudly (exit 0, "NOT RUN, NOT A PASS") when `vkd3d-shader.wasm` (or the desktop native, probe exit 3) is missing — never fabricates a pass. |
| Unit tests | `Vkd3dCompileContractTests` (20), `DxbcCompilerInjectionTests` (2) | pure per the conventions; pin the ABI constants 4/5 against `Vkd3dTargetType`, the profile/routing rules, the error mapping, and that the seam delivers the DX SM5 + FNA SM ≤ 3 requests and propagates errors unswallowed. |

### What flipped to green when the hosted artifact landed (reconciled 2026-06-11, same day)

1. ✅ `tools/restore.*`: the two `PENDING-FIRST-HOSTED-BUILD` pins replaced with the real
   SHA-256s (table in the build-pipeline section below) → restore downloads + verifies
   `wwwroot/vkd3d/vkd3d-shader.{js,wasm}` ("downloaded, hash OK" confirmed).
2. ✅ **`node-test-vkd3d-wasm.mjs` PASSED 98/98 byte-identical** (win-x64, 2026-06-11):
   every vkd3d compile the real pipeline issues over the byte-identity corpus — 37 DX
   fixtures (SM5 DXBC_TPF) + 28 FNA fixtures (SM1–3 D3D_BYTECODE), 98 stage compiles —
   produces the same bytes through the product shim as through the desktop P/Invoke.
   This also empirically confirms the glue expectations above (`HEAPU8` present on the
   module instance, `_malloc`/`_free` exported). The remaining rung is a real-browser
   render proof (G2 analogue) in the KNI/Blazor consumers.
3. ✅ Browser `CompileAsync` with `PlatformTarget.DirectX` / `Fna` now has its module
   restored; SD1902 remains only for a genuinely-absent module (the intended loud path).
4. ✅ The csproj `NoticeVkd3dWasmMissing` Message upgraded to an **Error on pack** (the
   `VerifyDxcWasmPresent` pattern) so a packed nupkg can never ship without the module.

Nothing regressed while gated: OpenGL-on-WASM, desktop DirectX/FNA, and all 921
desktop tests unchanged and green (the seam defaults to off).

---

## Build pipeline (Option A) — as built (2026-06-11)

**Status: ✅ BUILD PIPELINE GREEN + ARTIFACT HOSTED (2026-06-11)** — the emscripten
build of vkd3d-shader 1.17 **works, first CI run green** (run
[27387535685](https://github.com/kaltinril/ShadowDusk/actions/runs/27387535685),
3m19s end-to-end — vkd3d is a plain C library, not the multi-hour LLVM fork DXC
was). All 9 smoke assertions passed (ps_2_0→d3dbc 112 bytes with version token
`0xFFFF0200`; ps_5_0→dxbc-tpf 424 bytes with `DXBC` magic; broken shader fails
rc=-4 with verbatim `smoke.hlsl:1:57: E5000: syntax error…` diagnostics). The
spike's Option-A research question is answered: **vkd3d-shader builds cleanly
under emscripten 3.1.34 with zero source patches** — configure flags alone
sufficed (see findings below). Remaining for Phase 4.1: the C# `[JSImport]`
interop + restore-script pins (parallel agent), then the browser-side G1 gate.

### What exists

- **`tools/vkd3d-wasm/sdw_vkd3d_wrapper.c`** — the thin C wrapper (durable,
  committed). Internally builds `vkd3d_shader_compile_info` +
  `vkd3d_shader_hlsl_source_info` exactly like the desktop P/Invoke
  (`src/ShadowDusk.HLSL/Vkd3d/Vkd3dShaderCompiler.cs` / `Vkd3dNative.cs`): raw
  UTF-8 source bytes (not null-terminated), C-string entry/profile/source-name,
  `VKD3D_SHADER_LOG_WARNING`, no compile options, messages surfaced verbatim.
- **`tools/vkd3d-wasm/smoke-test.mjs`** — the node gate (same two smoke shaders as
  `build-vkd3d-natives.yml`: ps_2_0→d3dbc asserting version token `0xFFFF0200`,
  ps_5_0→dxbc-tpf asserting the `DXBC` magic, plus a fail-loudly diagnostics case).
- **`.github/workflows/vkd3d-wasm-build.yml`** — ubuntu builder: emsdk pinned
  **3.1.34** (the .NET 8 WASM pin; emsdk repo at its `3.1.34` tag commit
  `f747b2c4c5da`), the SAME WineHQ `vkd3d-1.17.tar.xz` + SHA-256 pin as the desktop
  natives, header-only Vulkan-Headers `v1.3.296` + SPIRV-Headers
  `vulkan-sdk-1.3.296.0`, `emconfigure`/`emmake` static `libvkd3d-shader.a`, `emcc`
  link of the wrapper, node smoke gate, raw+gzip size report, `LICENSE-vkd3d.txt`
  staged with the artifact. A temporary `push:` trigger on
  `feature/phase4.1-vkd3d-wasm-build*` exists for pre-merge iteration (remove after
  merge).

### Wrapper ABI — CONTRACT (the C# `[JSImport]` side is written against this)

```c
// Returns 0 (VKD3D_OK) on success, negative vkd3d error code on failure.
// target_type uses vkd3d's own enum values: 4 = VKD3D_SHADER_TARGET_D3D_BYTECODE
// (SM1–3, FNA), 5 = VKD3D_SHADER_TARGET_DXBC_TPF (SM4/5, DX11).
int sdw_vkd3d_compile(const unsigned char* source, int source_len,
                      const char* entry_point, const char* profile,
                      const char* source_name, int target_type,
                      unsigned char** out_code, int* out_size,
                      char** out_messages);
void sdw_vkd3d_free_code(unsigned char* p);     // vkd3d_shader_free_shader_code
void sdw_vkd3d_free_messages(char* p);          // vkd3d_shader_free_messages
```

No deviations from the agreed contract. Module shape: emscripten
`MODULARIZE=1, EXPORT_ES6=1, EXPORT_NAME=createVkd3dModule, FILESYSTEM=0,
ALLOW_MEMORY_GROWTH=1`; exported functions = the three `sdw_*` + `_malloc`/`_free`;
exported runtime methods `cwrap, ccall, getValue, setValue, UTF8ToString,
stringToUTF8, lengthBytesUTF8`; the JS default-exports the factory and locates
`vkd3d-shader.wasm` via `import.meta.url` (same loading style as
`shadowdusk-dxc.js` ↔ `dxc/dxcompiler.js`).

### Cross-compile findings (recorded as hit)

- vkd3d 1.17's bundled `config.sub` (2024-05-27) **accepts
  `wasm32-unknown-emscripten`** — no config.sub patch needed (verified locally
  against the pinned tarball before CI).
- `PTHREAD_LIBS='-lpthread'` is passed to configure to preempt
  `VKD3D_CHECK_PTHREAD`'s `-pthread` fallback: under emcc, `-pthread` flips the
  whole build to the SharedArrayBuffer/Web-Worker threaded ABI, which the browser
  module must not require. libvkd3d-shader only uses pthread mutex/cond
  (`include/private/vkd3d_common.h`), which emscripten's single-threaded libc
  stubs provide.
- `SONAME_LIBVULKAN` / `SONAME_LIBDXCOMPILER` bypass configure's dlopen-able
  library probes (same trick as the macOS/MSYS2 native recipes); libvkd3d-shader
  links neither at runtime.
- apt's `spirv-headers` cannot be used: `/usr/include` on emcc's include path
  would shadow the emscripten sysroot with glibc headers — SPIRV-Headers is
  fetched from the pinned Khronos tag instead (header-only, compile-time only).
- **Zero source patches were needed.** The autotools configure + build + emcc
  link + node smoke gate went green on the FIRST CI run with the flags above —
  dramatically easier than the DXC→WASM build (no tablegen split, no
  `-fwasm-exceptions`, no CMake cross-compile patches). vkd3d-shader is plain
  C99 with no filesystem/threading/dlopen needs on the compile path.
- Build time: ~3 minutes total on `ubuntu-latest` (vs. multi-hour for DXC).

### License note (LGPL-2.1+ and WASM static linking — flagged, not silently decided)

vkd3d is LGPL-2.1+. Desktop ships it as a genuinely dynamically-linked shared
library (notice in `THIRD-PARTY-NOTICES.txt` — the clean §6 case). The WASM module
**statically links** the thin `sdw_*` wrapper with `libvkd3d-shader.a` into one
`vkd3d-shader.wasm`. Position taken (recorded for review, not a legal opinion):
the `.wasm` module is a separately served, user-replaceable file loaded at runtime
by the application — the dynamic-link analog — and the wrapper is a de-minimis
shim whose source is published in this repo, which satisfies §6's relink/replace
intent. `LICENSE-vkd3d.txt` ships beside the artifact. If counsel ever disagrees,
the fallback is building the wrapper INTO vkd3d as a patched export (same
artifact shape, no proprietary code involved either way).

### Hosted artifact — LIVE (2026-06-11)

Hosted on the NEW fixed prerelease tag
[`native-vkd3d-wasm-1.17`](https://github.com/kaltinril/ShadowDusk/releases/tag/native-vkd3d-wasm-1.17)
(same hosting model as `native-vkd3d-1.17`; existing `native-*` tags untouched).
Assets: `vkd3d-shader.js`, `vkd3d-shader.wasm`, `LICENSE-vkd3d.txt`, `SHA256SUMS`.
Provenance: workflow run 27387535685, built unmodified from the pinned WineHQ
tarball. **These are the pins the restore scripts wait on:**

| Asset | SHA-256 | Size (raw / gzip -9) |
|---|---|---|
| `vkd3d-shader.js` | `aff3ae6dece4d9aea38d32e3e7ed4c2d809dc0e0bf1c12bbaa4ad97e3b5dd7aa` | 19 834 / 6 119 bytes |
| `vkd3d-shader.wasm` | `c80b8bb8a887a629aeb00951e5273a64598e6153b8580db428ee824f70f161e0` | 1 266 170 / 431 947 bytes |
| `LICENSE-vkd3d.txt` | `dc626520dcd53a22f727af3ee42c770e56c97a64fe3adb063799d8ab032fe551` | 26 530 bytes |

The size expectation is beaten by an order of magnitude: **1.27 MB raw / 0.43 MB
compressed** vs. the phase-doc guess of "single-digit MB compressed" (cf.
dxcompiler.wasm 17.4 MB raw). Browser-DX/FNA adds less than half a megabyte of
compressed download.

### What remains (reconciled 2026-06-11)

- ✅ C# `[JSImport]` interop + `shadowdusk-vkd3d.js` shim + SD0304 guard flip —
  landed (the *Managed-interop half* section above).
- ✅ restore-script download + real SHA-256 pins — landed.
- ✅ Desktop byte-identity gate over the DXBC/FNA corpora (G1 pattern) — **PASSED
  98/98** (see *What flipped to green* above). Still open: the `Effect`-load/render
  proof in the real browser consumers (G2 analogue).
- Remove the temporary `push:` trigger from `vkd3d-wasm-build.yml` after merge.

**Relationship to Phase 23:** Phase 23 builds the faithful **DXC→WASM** frontend for the **OpenGL** (SPIR-V) path. This spike asks the parallel question for **DirectX** — getting a faithful **DXBC** producer (vkd3d-shader) into WASM. Same emscripten-to-`[JSImport]` mechanism, different native library. Neither uses a substitute compiler.

## Problem Statement

ShadowDusk's WASM delivery target (`ShadowDusk.Wasm`) must compile HLSL shaders to all supported output formats, including SM5 DXBC for the DirectX 11 backend. The CLI target solves SM5 DXBC via native P/Invoke (`d3dcompiler_47.dll` on Windows, `libvkd3d-shader` on Linux/macOS). Neither of these approaches is available inside a .NET WASM runtime — there is no native P/Invoke to OS libraries in the browser sandbox.

This spike documents the problem, catalogues candidate solutions, and defines acceptance criteria for whichever path is chosen.

---

## What We Know

### Why DXC alone is insufficient
DXC (via `Vortice.Dxc`) only emits SM6 DXIL. It rejects `vs_5_0`/`ps_5_0` profiles for non-SPIRV targets (`error: invalid profile`). MonoGame's DirectX 11 backend (`ID3D11Device::CreateVertexShader`) rejects DXIL unconditionally — DXIL is a D3D12-only format. See [plan.md](plan.md#-known-constraint-dxc-cannot-produce-sm5-dxbc).

### The WASM constraint
.NET WASM runs inside the browser sandbox. Native shared libraries (`.dll`, `.so`, `.dylib`) cannot be loaded or P/Invoked. The only native interop available is via `[JSImport]` / `[JSExport]` to JavaScript, which can call into WASM-compiled C/C++ modules loaded by the browser.

The WASM target already uses this pattern for DXC and SPIRV-Cross: both are compiled to WASM via emscripten and called through `[JSImport]`.

---

## Candidate Solutions

### Option A — Compile `vkd3d-shader` to WASM via emscripten

`vkd3d-shader` is a C library (`gitlab.winehq.org/wine/vkd3d`) that compiles HLSL to SM4/SM5 DXBC cross-platform. Because it is written in C, it is theoretically compilable to WASM via emscripten — the same path taken by DXC and SPIRV-Cross.

**Research questions:**
1. Does `vkd3d-shader` build cleanly with emscripten today? (Check for POSIX-specific syscalls, threading, or filesystem dependencies that would break in the browser sandbox.)
2. Is there a pre-existing emscripten build of `vkd3d-shader` or any CI that produces one? (Check the vkd3d GitLab CI, Wine project forums, SDL3's `SDL_shadercross` CI.)
3. What is the resulting WASM binary size? DXBC compilation is potentially heavy — measure against the existing DXC WASM module for comparison.
4. Does the `vkd3d_shader_compile()` C API surface map cleanly to a `[JSImport]` boundary, or does it need a thin C wrapper?

**Pros:** Same library used by the CLI target on Linux/macOS — single implementation, consistent output.  
**Cons:** Emscripten build may require significant patching; no known existing WASM artifact.

---

### Option B — Use `d3dcompiler_47` compiled to WASM via emscripten

`d3dcompiler_47.dll` is Microsoft's HLSL compiler and is the reference implementation (byte-identical to `fxc.exe`). Some projects have explored compiling it via Wine + emscripten, but this introduces the Wine dependency we explicitly want to avoid.

**Research questions:**
1. Does a clean emscripten build of `d3dcompiler_47` exist that does not involve Wine?
2. Is there any open-source re-implementation of the `D3DCompile` API surface that targets WASM cleanly?

**Assessment:** Very unlikely to be viable without Wine. Document as a dead end if confirmed.

---

### Option C — Server-side DXBC compilation relay — ❌ OUT OF BOUNDS

The WASM client sends HLSL source to a lightweight server endpoint; the server runs the CLI version of ShadowDusk and returns the compiled DXBC bytes.

**Rejected — violates THE PURPOSE.** The product's differentiator is *self-contained, in-memory, no server roundtrip*. A server relay is "nothing but the library" turned into "the library plus a server you must run," and CLAUDE.md's success bar (Part 1: reach **at runtime, in-browser**, no server) explicitly excludes it. It is listed here only to be marked out of bounds — **do not pursue it**, even as a stopgap. The faithful in-browser answer is Option A (vkd3d-shader→WASM); if that is infeasible, browser-DirectX simply stays unsupported rather than becoming a client-server product.

---

### Option D — Emit SM6 DXIL for the WASM DirectX target

DXC already produces SM6 DXIL (`vs_6_0`/`ps_6_0`) and DXC is already WASM-compiled. If the KNI web runtime ever supports D3D12 or a DXIL-capable D3D11 path, DXIL would be sufficient.

**Research questions:**
1. Does KNI's WebAssembly DirectX backend accept SM6 DXIL, or does it strictly require SM5 DXBC?
2. Is there a roadmap item in KNI to upgrade its DirectX shader model support to SM6?

**Pros:** Zero additional tooling — DXC WASM is already in the pipeline.  
**Cons:** Depends on KNI runtime support that may not exist; breaks compatibility with MonoGame's D3D11 backend.

---

## Acceptance Criteria

A solution is acceptable if it meets all of the following:

- [ ] Compiles HLSL VS/PS shaders to SM5 DXBC inside the browser WASM sandbox with no native P/Invoke
- [ ] Requires no user installation beyond loading the ShadowDusk WASM module
- [ ] Produces output that MonoGame's `ID3D11Device::CreateVertexShader` / `CreatePixelShader` accepts
- [ ] Does not introduce a Wine runtime dependency at any layer
- [ ] WASM binary size increase is acceptable (< 5 MB compressed — same order of magnitude as DXC WASM)
- [ ] Build-time: can be produced by the existing `tools/restore.sh` + emscripten CI pattern

---

## Next Steps

1. Attempt an emscripten build of `vkd3d-shader` locally (Option A) — reuse Phase 23's emscripten 3.1.34 pin and `tools/restore.*` recipe pattern. Record build errors, patches required, and final binary size.
2. Contact the KNI maintainers to clarify Option D viability.
3. ~~Determine whether a server relay is tolerable (Option C)~~ — **settled: it is out of bounds** (THE PURPOSE forbids a server roundtrip). No action.
4. Update this document with findings and propose a path forward.

---

## Related

- [plan.md — DXC SM5 constraint](plan.md#-known-constraint-dxc-cannot-produce-sm5-dxbc)
- [PHASE-4-dxc-integration.md — flag table deviation](DONE/PHASE-4-dxc-integration.md)
- Memory: `project-dxc-sm5-constraint.md`
