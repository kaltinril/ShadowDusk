# vkd3d-shader → WebAssembly (Phase 4.1)

This directory holds the **durable build code** for compiling the pinned
**vkd3d-shader 1.17** to WebAssembly, so the browser host can compile HLSL →
**SM5 DXBC** (MonoGame DX11) and **SM1–3 D3D bytecode** (FNA fx_2_0) with the
**same pinned compiler as desktop** — no substitute compiler, per THE PURPOSE
(CLAUDE.md) and `plan/PHASE-4.1-SPIKE-wasm-directx-dxbc.md` (Option A).

The compiled `vkd3d-shader.{js,wasm}` artifacts are **NOT committed** — they are
built by `.github/workflows/vkd3d-wasm-build.yml` and hosted on the fixed
release tag **`native-vkd3d-wasm-1.17`** (same hosting model as
`native-vkd3d-1.17` / `native-dxc-1.7.2212.40`, Phase 37).

## Contents

| File | Purpose |
|---|---|
| `sdw_vkd3d_wrapper.c` | Thin C wrapper exposing the flat `sdw_vkd3d_*` ABI over `vkd3d_shader_compile()`. **The ABI is a contract** — the C# `[JSImport]` interop side is written against it; do not change signatures without recording the change in the phase doc. |
| `smoke-test.mjs` | Node gate run by the workflow: ps_2_0 → d3dbc (version token `0xFFFF0200`), ps_5_0 → dxbc-tpf (`DXBC` magic), and a fail-loudly diagnostics check. |

## Pins (never drift these silently)

| What | Pin |
|---|---|
| vkd3d source | `vkd3d-1.17.tar.xz` from WineHQ, SHA-256 `bc61cb9e84d5045cbcaffbdd707940d399d8bf62874663dfe5809a0bfb87e9b6` (same tarball as the desktop natives — output byte-stability is a product promise) |
| emscripten | **3.1.34** — the .NET 8 WASM runtime's pin (same as the DXC→WASM build; see `tools/restore.sh` and `.wasm-build/DXC-WASM-BUILD.md`) |
| Vulkan-Headers | v1.3.296 (compile-time only, header-only — same pin as the linux-x64 native build) |

## Module shape (matches the DXC→WASM module)

`emcc … -sMODULARIZE=1 -sEXPORT_ES6=1 -sEXPORT_NAME=createVkd3dModule
-sFILESYSTEM=0 -sALLOW_MEMORY_GROWTH=1` with exported functions
`_sdw_vkd3d_compile, _sdw_vkd3d_free_code, _sdw_vkd3d_free_messages, _malloc,
_free` and runtime methods `cwrap, ccall, getValue, setValue, UTF8ToString,
stringToUTF8, lengthBytesUTF8`. The JS file default-exports the factory;
`vkd3d-shader.wasm` is located relative to the JS via `import.meta.url`, so
co-locating the two files is all the wiring needed (same loading style as
`src/ShadowDusk.Wasm/wwwroot/shadowdusk-dxc.js` ↔ `dxc/dxcompiler.js`).

## License

vkd3d is **LGPL-2.1+**. The workflow ships vkd3d's license text as
`LICENSE-vkd3d.txt` next to the artifact. Note the WASM-specific nuance
recorded in the phase doc: the `.wasm` module statically links the thin wrapper
with `libvkd3d-shader.a`, but the module as a whole remains a separately
served, user-replaceable file (the dynamic-link analog). See
`plan/PHASE-4.1-SPIKE-wasm-directx-dxbc.md` → *License note*.
