# Restore Native Tools

ShadowDusk's in-memory **OpenGL/WebGL** path is self-contained from NuGet — you do **not** need to run a restore script to compile for OpenGL:

- **DXC** (HLSL → SPIR-V) rides inside the `ShadowDusk.HLSL` NuGet package for every supported RID.
- **SPIRV-Cross** (SPIR-V → GLSL) ships transitively via the `Silk.NET.SPIRV.Cross.Native` NuGet package.

Both are pulled in by `dotnet restore` and live in the NuGet package cache. Building ShadowDusk **from this repository** on Windows additionally needs the restore script below for its DXC.

## When you *do* need the restore script

The restore script provisions native artifacts that are **not committed to the repo** — it is for **building ShadowDusk itself from source** (and CI). Consumers never run it: everything below ships inside the published NuGet packages.

| Artifact | Used by | Notes |
|---|---|---|
| `vkd3d-shader` | the cross-platform DirectX DXBC backend (`DxbcBackend.Vkd3d`) and the FNA fx_2_0 target | Downloaded (SHA-256-pinned) from the hosted `native-vkd3d-2.1` release for **all four desktop RIDs** (win-x64, linux-x64, osx-x64, osx-arm64) and packed under `runtimes/<rid>/native` — self-contained in the NuGet. `vkd3d-shader` is the default, cross-platform DX backend; the opt-in `d3dcompiler_47` oracle is Windows-only and doesn't need it. |
| `dxcompiler.dll`, `dxil.dll` (Windows DXC) | the DirectX 12, OpenGL and Vulkan frontend **on Windows** | ShadowDusk's own build of the *exact* pinned DXC tag (v1.9.2607). Downloaded (SHA-256-pinned) from the hosted `native-dxc-1.9.2607` release for win-x64 and win-arm64 into `tools/dxc/win-*/` and packed under `runtimes/win-*/native`. |
| `dxcompiler.wasm` | the in-browser `ShadowDusk.Wasm` frontend (OpenGL/WebGL) | Copied from the committed source-of-truth in `.wasm-build/` into the package's `wwwroot/dxc/`. |
| `vkd3d-shader.{js,wasm}` | the in-browser `ShadowDusk.Wasm` DirectX/FNA export backend | Downloaded (SHA-256-pinned) from the hosted release into the package's `wwwroot/vkd3d/`; a local `.wasm-build/vkd3d-wasm-out/` build takes precedence. See [DirectX & FNA in the Browser](../backends/directx-in-wasm.md). |

## Running it

```sh
./tools/restore.sh        # Linux / macOS
```

```powershell
.\tools\restore.ps1       # Windows
```

The script is **download-tolerant, not failure-silent**: every artifact is downloaded from a fixed, SHA-256-pinned GitHub release, and an offline/failed download prints a warning and continues (the affected backends then fail loudly at compile time — SD0211 for the desktop vkd3d native, SD1902 for the browser module). A genuine script error exits non-zero. CI runs the same script and then **hard-gates** on the result: post-restore existence checks in the CI/release workflows fail red when a pinned asset is missing, and the release pack gates verify the vkd3d natives (all four RIDs), **both macOS DXC dylibs**, and the browser vkd3d module are inside the packages before anything publishes. (See the [Contributing Guide](../contributing/index.md).)

## What lands where

- `tools/dxc/win-{x64,arm64}/` and `tools/dxc/android-{arm64,x64}/` — ShadowDusk's own builds of the pinned DXC v1.9.2607, packed into the NuGet (Linux and macOS DXC comes from the `MonoGame.Tool.Dxc` package at build time).
- `tools/spirv-cross/` — optional; the SPIRV-Cross native normally ships transitively via the `Silk.NET.SPIRV.Cross.Native` NuGet, so this stays empty unless you deliberately provide a local copy.
- `tools/vkd3d/` — the `vkd3d-shader` native for the cross-platform DXBC backend (headers kept for binding reference; only the binary is git-ignored).

See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md) and [WASM In-Browser Frontend](../architecture/wasm-frontend.md) for how each artifact is consumed.
