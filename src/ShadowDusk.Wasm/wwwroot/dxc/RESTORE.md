# Faithful DXC→WASM module — restore

This directory holds the **faithful pinned DirectXShaderCompiler compiled to
WebAssembly** — the PRODUCT in-browser HLSL→SPIR-V frontend (Phase 23, Option A;
see `plan/PHASE-23-in-browser-compilation.md`). It is the SAME compiler + version
the desktop pipeline uses (Vortice.Dxc 3.3.4 == DXC 1.7.2212.40, commit
`e043f4a1`), so its SPIR-V is byte-identical to the desktop CLI on the corpus.

| File | Committed? | What |
|---|---|---|
| `dxcompiler.js` (~54 KB) | yes | emscripten MODULARIZE + EXPORT_ES6 loader; `export default createDxcModule`; embind `compileToSpirv(hlsl, args[]) → Uint8Array`. Locates `dxcompiler.wasm` via `new URL("dxcompiler.wasm", import.meta.url)`. |
| `dxcompiler.wasm` (~17.4 MB) | **no — gitignored** | the LLVM-fork DXC build. Too large to commit; restored. |
| `../shadowdusk-dxc.js` (~7 KB) | yes | the FAITHFUL `[JSImport]` shim — `ensureReady()` (lazy-load + instantiate) + `compileToSpirv()` (verbatim args). |

## Restore

The `.wasm` is produced by the M0 build and lives under `.wasm-build/` (itself
gitignored). To repopulate this directory after a fresh clone:

```pwsh
# Build it (out-of-session, multi-day LLVM-fork build — see DXC-WASM-BUILD.md):
pwsh -File .wasm-build/Invoke-DxcWasmBuild.ps1
# Or, if .wasm-build/dxc-wasm-out/ already holds a built module, just copy:
Copy-Item .wasm-build/dxc-wasm-out/dxcompiler.js   src/ShadowDusk.Wasm/wwwroot/dxc/
Copy-Item .wasm-build/dxc-wasm-out/dxcompiler.wasm src/ShadowDusk.Wasm/wwwroot/dxc/
```

## Gates

- **G0** (`node .wasm-build/node-test-dxc-wasm.mjs`) — the raw module is 10/10
  byte-identical to desktop DXC on the corpus.
- **G1** (`node .wasm-build/node-test-dxc-shim.mjs`) — the faithful **shim**
  (`../shadowdusk-dxc.js`) wrapping this module is 10/10 byte-identical.
- **G2** (`tests/ShadowDusk.BrowserTests`, `--corpus=faithful`) — the module
  LOADS + RUNS in a real headless browser and the end-to-end faithful pipeline
  renders 10/10 in real KNI WebGL.

## M1 (next agent — packaging)

M1 wires this into `ShadowDusk.Wasm`'s packaged static web assets so a consumer
gets it transitively (auto-served at `_content/ShadowDusk.Wasm/dxc/…`) and
`ShadowDusk.Wasm` self-registers `shadowdusk-dxc` against its own base path — no
consumer `wwwroot`/`JSHost` wiring. The restore step above must run before pack so
the `.wasm` is present to be packaged.
