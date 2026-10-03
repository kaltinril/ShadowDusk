# In-process slangc (WebAssembly): restore

This directory holds `shadowdusk-slangc.{js,wasm}`: the pinned slangc **v2026.14.1**
running inside the browser, which is how `ShadowDusk.Slang.Wasm` gives the browser full
Slang input (issue #257, `plan/PHASE-67-slang-in-process-wasm.md`). Both files are
**built, not committed** (gitignored).

| File | What |
|---|---|
| `shadowdusk-slangc.js` (~90 KB) | emscripten 6.0.0 `MODULARIZE` + `EXPORT_ES6` loader; embind `runSlangc(source, args[]) -> {exitCode, stdout, stderr}`. Finds the `.wasm` beside it through `import.meta.url`. |
| `shadowdusk-slangc.wasm` (~23 MB) | upstream's own prebuilt `libslang-compiler.a` and friends (`slang-2026.14.1-wasm-libs.zip`, SHA-256 pinned) linked with `.wasm-build/slang-wasm/slangc-wasm-glue.cpp`. |
| `../shadowdusk-slangc.js` | the committed `[JSImport]` shim (`ensureReady`, `runSlangc`). |

## Build

```powershell
pwsh .wasm-build/slang-wasm/build-slangc-wasm.ps1            # installs emsdk 6.0.0 into a temp work dir
pwsh .wasm-build/slang-wasm/build-slangc-wasm.ps1 -EmsdkRoot <existing emsdk clone>
```

Output lands here by default. Then prove it against native slangc (needs `tools/restore.*`):

```powershell
node .wasm-build/slang-wasm/node-test-slangc-wasm.mjs src/ShadowDusk.Slang.Wasm/wwwroot/slangc
```

The gate runs every corpus `.slang` file, every entry point, and every target's platform
macros through native slangc and through this module with the identical argument list, and
requires the exit code, stdout and stderr to match exactly (measured 235/235 on 2026-10-01). It
does the same for the preprocess-only command line (`slangc -E`, which `SlangCompiler` uses to
find author-written registers; measured 200/200, 25 with a texture/sampler register) and for two register shapes only a
preprocessor can resolve. Issue #292 added a combined `Sampler2D C : register(t2)` shape (slangc splits it into
`C_texture_0`/`C_sampler_0`) and the preprocess-only pass over another file's path
(`SlangcArguments.BuildPreprocessFiles`, alone and beside the entry source), which on a path neither host can open must report `E00001` with exit 0 on
both, the shape `SlangCompiler` turns into `SD0628`.

Why not upstream's own `slang-wasm.js`: its embind API takes a compile target and nothing
else (no `-no-mangle`, no `-no-hlsl-pack-constant-buffer-elements`, no `-D` macros) and its
HLSL session defaults differ from the command line's, so its output matched native slangc's
for 0 of 20 corpus entries. That is the "flags don't forward" defect that ruled slang-wasm out
in Phase 23.
