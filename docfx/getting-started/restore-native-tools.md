# Restore Native Tools

ShadowDusk's in-memory **OpenGL/WebGL** path is self-contained from NuGet — you do **not** need to run a restore script to compile for OpenGL:

- **DXC** (HLSL → SPIR-V) comes from the `Vortice.Dxc` NuGet package.
- **SPIRV-Cross** (SPIR-V → GLSL) ships transitively via the `Silk.NET.SPIRV.Cross.Native` NuGet package.

Both are pulled in by `dotnet restore` and live in the NuGet package cache.

## When you *do* need the restore script

The restore script provisions native artifacts that are **not committed to the repo** — it is for **building ShadowDusk itself from source** (and CI). Consumers never run it: everything below ships inside the published NuGet packages.

| Artifact | Used by | Notes |
|---|---|---|
| `vkd3d-shader` | the cross-platform DirectX DXBC backend (`DxbcBackend.Vkd3d`) and the FNA fx_2_0 target | Downloaded (SHA-256-pinned) from the hosted `native-vkd3d-1.17` release for **all four desktop RIDs** (win-x64, linux-x64, osx-x64, osx-arm64) and packed under `runtimes/<rid>/native` — self-contained in the NuGet since Phase 37 C. The default DX backend (`d3dcompiler_47`) is Windows-only and doesn't need it. |
| `dxcompiler.wasm` | the in-browser `ShadowDusk.Wasm` frontend (OpenGL/WebGL) | Copied from the committed source-of-truth in `.wasm-build/` into the package's `wwwroot/dxc/`. |
| `vkd3d-shader.{js,wasm}` | the in-browser `ShadowDusk.Wasm` DirectX/FNA export backend | Downloaded (SHA-256-pinned) from the hosted release into the package's `wwwroot/vkd3d/`; a local `.wasm-build/vkd3d-wasm-out/` build takes precedence. See [DirectX & FNA in the Browser](../backends/directx-in-wasm.md). |

## Running it

```sh
./tools/restore.sh        # Linux / macOS
```

```powershell
.\tools\restore.ps1       # Windows
```

The script is **best-effort and non-fatal**: every artifact is downloaded from a fixed, SHA-256-pinned GitHub release, and an offline/failed download prints a warning and continues (the affected backends then fail loudly at compile time — SD0211 for the desktop vkd3d native, SD1902 for the browser module — and the package-level gates stop a release from packing without them). CI runs the same script (see the [Contributing Guide](../contributing/index.md)).

## What lands where

- `tools/spirv-cross/`, `tools/dxc/` — restore targets for the desktop natives (largely obsolete now that SPIRV-Cross and DXC come from NuGet; kept for compatibility).
- `tools/vkd3d/` — the `vkd3d-shader` native for the cross-platform DXBC backend (headers kept for binding reference; only the binary is git-ignored).

See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md) and [WASM In-Browser Frontend](../architecture/wasm-frontend.md) for how each artifact is consumed.
