# Faithful vkd3d-shader→WASM module — restore

This directory holds the **faithful pinned vkd3d-shader 1.17 compiled to
WebAssembly** — the PRODUCT in-browser HLSL→DXBC (DirectX) and HLSL→D3D9-bytecode
(FNA fx_2_0) backend (Phase 4.1, Option A; see
`plan/DONE/PHASE-4.1-SPIKE-wasm-directx-dxbc.md`). It is the SAME library + version the
desktop pipeline P/Invokes (`tools/vkd3d/`, tag `native-vkd3d-1.17`), so its output
is asserted **byte-identical to the desktop backend** on the corpus — never a
substitute compiler.

| File | Committed? | What |
|---|---|---|
| `vkd3d-shader.js` | **no — restored** | emscripten MODULARIZE + EXPORT_ES6 loader; `export default createVkd3dModule`; exports the `sdw_vkd3d_compile` / `sdw_vkd3d_free_code` / `sdw_vkd3d_free_messages` C ABI plus `_malloc`/`_free`/`HEAPU8`. Locates `vkd3d-shader.wasm` via `new URL("vkd3d-shader.wasm", import.meta.url)`. |
| `vkd3d-shader.wasm` | **no — restored** | the pinned vkd3d 1.17 emscripten build. |
| `../shadowdusk-vkd3d.js` | yes | the FAITHFUL `[JSImport]` shim — `ensureReady()` (lazy-load + instantiate) + `compile()` (heap marshalling, verbatim diagnostics). |

## The wrapper C ABI (the Phase 4.1 contract)

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

Source bytes are UTF-8 and **not** null-terminated (pointer + length);
entry/profile/source-name are C strings (`source_name` may be NULL). The shim
additionally needs `_malloc`, `_free`, and the `HEAPU8` view on the module instance
(it deliberately avoids `cwrap`/`getValue`/`UTF8ToString` runtime exports).

## Restore

Both files are restored by `tools/restore.ps1` / `tools/restore.sh`
(`Restore-Vkd3dWasm` / `restore_vkd3d_wasm`) from the **fixed GitHub Release tag
`native-vkd3d-wasm-1.17`**, SHA-256-verified against the pins in those scripts.

> **Status:** the hosted build does not exist yet — the pins are
> `PENDING-FIRST-HOSTED-BUILD` placeholders and the restore **skips with a notice**
> (the Phase 37 A pattern). Until the artifacts land, browser DirectX/FNA compiles
> fail loudly with `SD1902`; everything else (the shim, the C# backend, the gate,
> the restore plumbing) is wired and flips to working the moment the files exist.
> A locally built module can be used meanwhile: the restore also copies from
> `.wasm-build/vkd3d-wasm-out/vkd3d-shader.{js,wasm}` when present.

## Gates

- **Byte-identity** (`cd tests/ShadowDusk.BrowserTests && node node-test-vkd3d-wasm.mjs`)
  — drives the product shim (`../shadowdusk-vkd3d.js`) through its real contract
  surface under node over the DX (SM5) + FNA (SM1–3) corpus and asserts every output
  byte-identical to the desktop vkd3d backend (captured by `Vkd3dCorpusProbe`). Skips
  with a loud notice while the module is not restored — it never fabricates a pass.
- A real-browser render proof (the Phase 23 G2 analogue) follows once the artifact
  is hosted.
