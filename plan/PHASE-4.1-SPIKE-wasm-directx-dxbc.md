# Phase 4.1 — Research Spike: WASM + DirectX DXBC (now: vkd3d → WASM)

**Status:** 🟡 **Un-parked (2026-06-09)** — elevated from far-future spike to the **keystone
of the browser-export-station vision** (owner decision; the question this doc left pending
is answered: yes, browser-DirectX *and browser-FNA* are goals — as **export targets**, not
render backends). The what and why:

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

## Build pipeline (Option A) — as built (2026-06-11)

**Status: 🟡 IN PROGRESS** — wrapper + workflow committed on
`feature/phase4.1-vkd3d-wasm-build`; iterating the CI build to green. This section
is updated as the build progresses (hosted tag + SHA-256s land here when green).

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
- (Further build errors/patches recorded here as the CI iteration finds them.)

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

### Hosted artifact (filled in when the build is green)

- Tag: `native-vkd3d-wasm-1.17` — **PENDING** (never modify existing `native-*`
  releases; this is a NEW tag).
- `vkd3d-shader.js` SHA-256: **PENDING-FIRST-HOSTED-BUILD**
- `vkd3d-shader.wasm` SHA-256: **PENDING-FIRST-HOSTED-BUILD**
- Sizes (raw / gzip -9): **PENDING**

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
