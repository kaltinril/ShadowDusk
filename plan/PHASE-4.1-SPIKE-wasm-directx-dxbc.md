# Research Spike — WASM + DirectX DXBC

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

### Option C — Server-side DXBC compilation relay

The WASM client sends HLSL source to a lightweight server endpoint; the server runs the CLI version of ShadowDusk (which has full native P/Invoke access) and returns the compiled DXBC bytes.

**Research questions:**
1. Does the XNA Fiddle / KNI web use case already have a server component that could host this relay, or is fully in-browser compilation a hard requirement?
2. What is the latency impact for interactive shader editing?
3. Does this break the "no server roundtrip" design goal stated in the project overview?

**Pros:** Trivially correct — the CLI path already works.  
**Cons:** Requires a server; breaks the offline/serverless model.

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

1. Attempt an emscripten build of `vkd3d-shader` locally (Option A). Record build errors, patches required, and final binary size.
2. Contact the KNI maintainers to clarify Option D viability.
3. Determine whether XNA Fiddle requires fully in-browser compilation or can tolerate a server relay (Option C).
4. Update this document with findings and propose a path forward.

---

## Related

- [plan.md — DXC SM5 constraint](plan.md#-known-constraint-dxc-cannot-produce-sm5-dxbc)
- [PHASE-4-dxc-integration.md — flag table deviation](PHASE-4-dxc-integration.md)
- Memory: `project-dxc-sm5-constraint.md`
