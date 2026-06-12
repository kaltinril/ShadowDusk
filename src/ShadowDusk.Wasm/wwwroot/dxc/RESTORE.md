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

The built module **is committed to the repo** at `.wasm-build/dxc-wasm-out/dxcompiler.{js,wasm}`
(force-added past the `.wasm-build/` ignore). So on a fresh clone the restore is a
plain **copy** — no rebuild — and `tools/restore.*` (`Restore-DxcWasm`) does it for
you before build/pack. Manual copy if needed:

```pwsh
Copy-Item .wasm-build/dxc-wasm-out/dxcompiler.js   src/ShadowDusk.Wasm/wwwroot/dxc/
Copy-Item .wasm-build/dxc-wasm-out/dxcompiler.wasm src/ShadowDusk.Wasm/wwwroot/dxc/
```

Only if you need to **rebuild from source** (e.g. bump the DXC pin) run the M0 build
(`pwsh -File .wasm-build/Invoke-DxcWasmBuild.ps1`) — an out-of-session LLVM-fork
emscripten build; see `.wasm-build/DXC-WASM-BUILD.md`. **Carry-forward:** that build
script is Windows/MSVC-only today; a Linux/macOS rebuild path + CI is owned by Phase 30 §16.

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
  inside the compile path (`WasmModuleRegistration.EnsureDxcChainRegisteredAsync`) — so a
  consumer adds ONLY a `PackageReference` and wires nothing.
- `tools/restore.*` (`Restore-DxcWasm` / `restore_dxc_wasm`) copies the built
  `.wasm-build/dxc-wasm-out/dxcompiler.wasm` into this directory before build/pack;
  the csproj's `VerifyDxcWasmPresent` target fails the build loudly if it is missing.
  This 17.4 MB `.wasm` stays gitignored.

The restore step must run before `pack` so the `.wasm` is present to be packaged.
