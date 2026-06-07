# Restore Native Tools

ShadowDusk's in-memory **OpenGL/WebGL** path is self-contained from NuGet — you do **not** need to run a restore script to compile for OpenGL:

- **DXC** (HLSL → SPIR-V) comes from the `Vortice.Dxc` NuGet package.
- **SPIRV-Cross** (SPIR-V → GLSL) ships transitively via the `Silk.NET.SPIRV.Cross.Native` NuGet package.

Both are pulled in by `dotnet restore` and live in the NuGet package cache.

## When you *do* need the restore script

The restore script handles native artifacts that are **not** redistributed through NuGet:

| Artifact | Used by | Notes |
|---|---|---|
| `vkd3d-shader` | the cross-platform DirectX DXBC backend (`DxbcBackend.Vkd3d`) | Restored, not committed. The script no-ops with a build-recipe note when no prebuilt binary is available, so the build stays green. The default DX backend (`d3dcompiler_47`) is Windows-only and doesn't need it. |
| `dxcompiler.wasm` | the in-browser `ShadowDusk.Wasm` frontend | Copied from the committed source-of-truth in `.wasm-build/` into the package's `wwwroot/dxc/`. |

## Running it

```sh
./tools/restore.sh        # Linux / macOS
```

```powershell
.\tools\restore.ps1       # Windows
```

The script is **best-effort and non-fatal** for the cross-platform native pieces: if a prebuilt `vkd3d-shader` binary isn't available for your platform it prints a build recipe and continues, because vkd3d is opt-in (`DxbcBackend.Vkd3d`) while the default DX backend is the `d3dcompiler_47` oracle. CI runs the same script and tolerates its absence (see the [Contributing Guide](../contributing/index.md)).

## What lands where

- `tools/spirv-cross/`, `tools/dxc/` — restore targets for the desktop natives (largely obsolete now that SPIRV-Cross and DXC come from NuGet; kept for compatibility).
- `tools/vkd3d/` — the `vkd3d-shader` native for the cross-platform DXBC backend (headers kept for binding reference; only the binary is git-ignored).

See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md) and [WASM In-Browser Frontend](../architecture/wasm-frontend.md) for how each artifact is consumed.
