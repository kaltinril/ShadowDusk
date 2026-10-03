# Faithful vkd3d-shader→WASM module — restore

This directory holds the **faithful pinned vkd3d-shader 2.1 compiled to
WebAssembly** — the PRODUCT in-browser HLSL→DXBC (DirectX) and HLSL→D3D9-bytecode
(FNA fx_2_0) backend (Phase 4.1, Option A; see
`plan/DONE/PHASE-4.1-SPIKE-wasm-directx-dxbc.md`). It is the SAME library + version the
desktop pipeline P/Invokes (`tools/vkd3d/`, tag `native-vkd3d-2.1`), so its output
is asserted **byte-identical to the desktop backend** on the corpus — never a
substitute compiler.

| File | Committed? | What |
|---|---|---|
| `vkd3d-shader.js` | **no — restored** | emscripten MODULARIZE + EXPORT_ES6 loader; `export default createVkd3dModule`; exports the `sdw_vkd3d_compile_options` / `sdw_vkd3d_free_code` / `sdw_vkd3d_free_messages` C ABI plus `_malloc`/`_free`/`HEAPU8`. Locates `vkd3d-shader.wasm` via `new URL("vkd3d-shader.wasm", import.meta.url)`. |
| `vkd3d-shader.wasm` | **no — restored** | the pinned vkd3d 2.1 emscripten build. |
| `../shadowdusk-vkd3d.js` | yes | the FAITHFUL `[JSImport]` shim — `ensureReady()` (lazy-load + instantiate) + `compile()` (heap marshalling, verbatim diagnostics). |

## The wrapper C ABI (the Phase 4.1 contract)

```c
// Returns 0 (VKD3D_OK) on success, negative vkd3d error code on failure.
// target_type: 4 = VKD3D_SHADER_TARGET_D3D_BYTECODE (SM1–3, FNA),
//              5 = VKD3D_SHADER_TARGET_DXBC_TPF (SM4/5, DX11).
// options: option_count (name, value) pairs of 32-bit words, handed to vkd3d untouched.
int sdw_vkd3d_compile_options(const unsigned char* source, int source_len,
                      const char* entry_point, const char* profile,
                      const char* source_name, int target_type,
                      const unsigned int* options, int option_count,
                      unsigned char** out_code, int* out_size, char** out_messages);
void sdw_vkd3d_free_code(unsigned char* p);
void sdw_vkd3d_free_messages(char* p);
```

**The vkd3d compile options are the caller's (issue #295).** The list is chosen in one
place, the managed `Vkd3dCompileContract.ResolveCompileOptions`, which the desktop
backend marshals into `vkd3d_shader_compile_info` and the browser backend sends through
the shim; the wrapper forwards it and decides nothing (today: `BACKWARD_COMPATIBILITY` =
`MAP_SEMANTIC_NAMES` for target type 5, none for 4). A module built before issue #295
exports `sdw_vkd3d_compile` instead, the same call without the two option parameters,
always compiling with none.

Source bytes are UTF-8 and **not** null-terminated (pointer + length);
entry/profile/source-name are C strings (`source_name` may be NULL). The shim
additionally needs `_malloc`, `_free`, and the `HEAPU8` view on the module instance
(it deliberately avoids `cwrap`/`getValue`/`UTF8ToString` runtime exports).

## Restore

Both files are restored by `tools/restore.ps1` / `tools/restore.sh`
(`Restore-Vkd3dWasm` / `restore_vkd3d_wasm`) from the **fixed GitHub Release tag
`native-vkd3d-wasm-2.1`**, SHA-256-verified against the pins in those scripts.

> **Status (issues #271 and #295):** the hosted `native-vkd3d-wasm-2.1` module has two
> known defects. It was linked with emscripten's 64 KB default stack and traps on nested
> shaders the desktop compiles (75 `else if`s, 200 nested `if`s, a 1600-deep call chain;
> #271). And its wrapper passes vkd3d no compile options, so a DirectX shader with SM1-3
> semantics on struct fields compiles differently from the desktop or is refused with
> `E5013` (#295); with that module the shim falls back to the old `sdw_vkd3d_compile`
> entry point and warns once on the console. `vkd3d-wasm-build.yml` now links an 8 MB
> stack placed first and builds the option-forwarding wrapper; that rebuild is verified
> (91/91 corpus compiles, 78/78 artifacts in Chromium, 6/6 depth cases byte-identical)
> but must still be uploaded to a NEW tag and re-pinned in both restore scripts
> (`docs/validation-matrix.md` §7 has the run, the hashes and every place to touch). A
> locally built module can be used meanwhile: outside CI the restore copies
> `.wasm-build/vkd3d-wasm-out/vkd3d-shader.{js,wasm}` when present.

## Gates

- **Byte-identity** (`cd tests/ShadowDusk.BrowserTests && node node-test-vkd3d-wasm.mjs`)
  — drives the product shim (`../shadowdusk-vkd3d.js`) through its real contract
  surface under node over the DX (SM5) + FNA (SM1–3) corpus and asserts every output
  byte-identical to the desktop vkd3d backend (captured by `Vkd3dCorpusProbe`, together
  with the compile options the desktop really passed, which the gate replays). Skips
  with a loud notice while the module is not restored — it never fabricates a pass.
  `Sm3SemanticStructs.fx` is the corpus case that depends on the options; while the
  restored module is the hosted pre-#295 build it is reported as an expected difference.
- A real-browser render proof (the Phase 23 G2 analogue) follows once the artifact
  is hosted.
