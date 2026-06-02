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

## M1 — packaging (DONE)

M1 wired this into `ShadowDusk.Wasm`'s packaged static web assets:

- `ShadowDusk.Wasm.csproj` uses the **Razor SDK** (`Microsoft.NET.Sdk.Razor`) so this
  `wwwroot/` ships as Blazor static web assets, auto-served by a consumer at
  `_content/ShadowDusk.Wasm/dxc/…`.
- `ShadowDusk.Wasm` **self-registers** `shadowdusk-dxc` (and `shadowdusk-spirv-cross`)
  via `JSHost.ImportAsync` against the relative URL
  `../_content/ShadowDusk.Wasm/<file>` (resolved from `_framework/` to the app base),
  inside the compile path (`WasmModuleRegistration.EnsureRegisteredAsync`) — so a
  consumer adds ONLY a `PackageReference` and wires nothing.
- `tools/restore.*` (`Restore-DxcWasm` / `restore_dxc_wasm`) copies the built
  `.wasm-build/dxc-wasm-out/dxcompiler.wasm` into this directory before build/pack;
  the csproj's `VerifyDxcWasmPresent` target fails the build loudly if it is missing.
  This 17.4 MB `.wasm` stays gitignored.

The restore step must run before `pack` so the `.wasm` is present to be packaged.
