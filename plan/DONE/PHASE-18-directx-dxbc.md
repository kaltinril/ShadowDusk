# Phase 18 — DirectX 11 DXBC Output (Runtime-Loadable WindowsDX Effects)

**Status:** ✅ **COMPLETE (2026-05-30)** — DirectX SM5 PS-only corpus reaches in-engine equivalence in the real MonoGame WindowsDX runtime, via the cross-platform vkd3d-shader backend (with `d3dcompiler_47` as a Windows correctness oracle). Carry-forwards: VS-driven effects → backlog 17-VS; Linux/macOS *run* validation → Phase 30 CI; per-RID hosted vkd3d binaries → follow-up.
**Depends on:** Phase 4 (DXC integration), [Phase 4.1 SPIKE](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) (cross-platform DXBC backend survey), Phase 7 + Phase 17 (the `MgfxWriter` header/shader-record rework — a DX `.mgfx` needs the same MonoGame-loadable container as the GL one), Phase 17 harness (reuse the same compare-in-engine instrument for DX).
**Blocks:** A credible "drop-in `mgfxc` replacement" claim for **Windows / `MonoGame.Framework.WindowsDX`** games — the **DirectX half** of the fidelity (Part 2) goal. The OpenGL half is Phase 17.

> This phase is referenced throughout [PHASE-17-monogame-runtime-validation.md](PHASE-17-monogame-runtime-validation.md) (§7, §3 punch-list #8, Definition of Done) as "Fixing DX = Phase 18," but never had its own document. This is that document. The architecture survey for it is [`monogame_runtime_mgfx_compiler_research.md`](../../monogame_runtime_mgfx_compiler_research.md) §9.3 (DXC → DXIL) and the [Known Constraint section in plan.md](../plan.md#-resolved-constraint-phase-18-dxc-cannot-produce-sm5-dxbc).

---

## RESULT (2026-05-30) — both axes demonstrated for DirectX

**Rung-4 proof (CLAUDE.md evidence ladder), same-backend (DX↔DX) in the real `MonoGame.Framework.WindowsDX` DX11 runtime, tolerance 4 (matching Phase 17):**

| Backend | Compiles | Loads via `new Effect(gd, bytes)` | Renders pixel-equivalent to `mgfxc` DX golden |
|---|---|---|---|
| **d3dcompiler_47 oracle** (Windows-only) | 10/10 | 10/10 | **10/10 pixel-identical (maxΔ 0)** |
| **vkd3d-shader** (cross-platform shipping backend) | 10/10 | 10/10 | **10/10** (9 exact; Dots Δ=1, 0 px over tol) — identical to the oracle too |

Corpus: the 10 SM3 PS-only shaders (Grayscale, Invert, TintShader, Sepia, Saturate, Pixelated, Scanlines, Fading, Dots, Dissolve), parameters set by name exactly as Phase 17. WindowsDX boots via the Phase-17-style `Game.Run()` + offscreen `RenderTarget2D` lifecycle (creates a never-shown Win32 swap-chain window — effectively headless). The feared `ps_5_0` vs `ps_4_0_level_9_1` / "not a MonoGame MGFX file" / DXBC-rejection failures **did not occur**.

**Why both backends:** `d3dcompiler_47.dll` is Windows-only, so it can only ever be a *correctness oracle* — a DX backend built on it has zero *reach* (it runs exactly where `mgfxc` already runs). **vkd3d-shader is the shipping backend** because it is the only cross-platform HLSL→DXBC compiler — it is what lets DX `.fx` be compiled where `mgfxc` can't run. The oracle proved the `.mgfx` container + reflection + load-in-MonoGame chain is correct; vkd3d then matched it byte-for-render, so the cross-platform path inherits a proven-correct baseline.

---

## Overview

ShadowDusk's DirectX path previously compiled HLSL to **SM6 DXIL** — `DxcFlagBuilder` targeted `ps_6_0`/`vs_6_0` because **DXC cannot emit SM ≤ 5 DXBC** (it has never supported DXBC output). MonoGame 3.8's DX11 runtime loads **only DXBC (SM ≤ 5)**: `ID3D11Device::CreateVertexShader` / `CreatePixelShader` reject DXIL unconditionally (DXIL is a D3D12-only format). So even after the Phase 17 `.mgfx` container rework, a ShadowDusk DirectX `.mgfx` **would not load** in a real WindowsDX game. This phase closed the format gap so the DirectX target reaches the same in-engine equivalence bar Phase 17 established for OpenGL.

The two are deliberately separated because **each backend is a different emitted artifact loaded by a different runtime path** (CLAUDE.md → *Compare same-backend, never cross-backend*). A green OpenGL Phase 17 result says nothing about DirectX; DX is produced correctly and validated on its own.

### How it was built (as-built architecture)

- **DX `.mgfx` format pinned** — byte-walked 6 `DirectX_11/` goldens against MonoGame 3.8.2's `Shader.cs`/`Effect.cs`. Finding: the DX shader record is **byte-compatible with the existing GL writer** — MonoGame reads the attribute table / sampler loop / cbuffer block unconditionally (no `#if OPENGL/DIRECTX`). DX simply supplies different *values*: header profile byte = 1, DXBC bytecode, **empty** sampler/cbuffer names (DX binds by register, not name), and `Attributes = []` for every shader incl. VS (the count byte stays, = 0). No structural per-profile branch was needed in `MgfxWriter`. Decode tool: `validation/decode_mgfx_dx.py`.
- **`IDxbcShaderCompiler` seam** (`src/ShadowDusk.HLSL/D3DCompiler/`) with two implementations: `D3DCompilerShaderCompiler` (Vortice.D3DCompiler `Compile`, `vs_5_0`/`ps_5_0`, the oracle) and `Vkd3dShaderCompiler` (`src/ShadowDusk.HLSL/Vkd3d/`, P/Invoke to `libvkd3d-shader` via `Vkd3dNative`/`Vkd3dLoader`, mirroring `SpvcNative`/`SpvcLoader`). Backend chosen via `DxbcBackend` (`src/ShadowDusk.Core/DxbcBackend.cs`), **default `D3DCompiler`**, `Vkd3d` opt-in.
- **DXBC reflection** — `DxbcReflectionExtractor` (`ID3D11ShaderReflection` via `Compiler.Reflect<>`) is the D3D11 analogue of `DxilReflectionExtractor`. It reflects **both** d3dcompiler and vkd3d DXBC cleanly (standard DXBC_TPF), feeding cbuffer layout + sampler registers into `CompiledShaderBlob`. The DX path folds the standalone sampler into its texture parameter (`ParameterListBuilder.includeSamplerParameters = false`) to match the golden; the GL path is unchanged.
- **vkd3d binary** — built from WineHQ source (vkd3d-shader 1.17, portable MSYS2/autotools, `SONAME_LIBVULKAN=vulkan-1.dll`, static libgcc+winpthread → zero non-system runtime deps). Restored, **not committed** (`tools/vkd3d/*.dll` git-ignored); `tools/restore.{ps1,sh}` carry the build recipe.

---

## Scope and Non-Goals

**In scope:**
- A cross-platform HLSL → **DXBC (SM4/SM5)** backend that does **not** require Wine or the Windows SDK on Linux/macOS. ✅ (vkd3d-shader)
- DX `.mgfx` that loads in `MonoGame.Framework.WindowsDX`'s `EffectReader` and renders the same image as the `mgfxc` DirectX golden, validated through the Phase 17 compare harness (Windows-gated). ✅
- The DirectX golden corpus under `tests/fixtures/golden/DirectX_11/`. ✅

**Out of scope:**
- WASM + DirectX DXBC (no native P/Invoke in the browser; no prebuilt WASM artifact of the DXBC backend) — remains the open problem in [Phase 4.1 SPIKE](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) and [Phase 19 WASM](PHASE-19-wasm-runtime-compilation.md).
- DirectX 12 / KNI DXIL — already works (D3D12 natively accepts SM6 DXIL via the existing `vs_6_0`/`ps_6_0` path, retained).
- Byte-identical output vs `fxc.exe` (never a goal; semantic/render equivalence only).

---

## The backend decision (as chosen)

The only viable cross-platform HLSL→DXBC compiler with **no Wine runtime** is **`vkd3d-shader`** (`gitlab.winehq.org/wine/vkd3d`):

- Standalone C library, runs independently of Wine; P/Invoke pattern mirrors SPIRV-Cross exactly.
- `vkd3d_shader_compile()` HLSL → SM4/SM5 DXBC (`VKD3D_SHADER_SOURCE_HLSL` → `VKD3D_SHADER_TARGET_DXBC_TPF`, since vkd3d 1.3, matured through 1.17); LGPL-2.1+ (safe as a dynamically-linked native binary).
- Adequate coverage for MonoGame-style effects (cbuffers, `Texture2D` samplers, VS/PS) — confirmed in production by FNA and now by this phase's 10/10 result.
- **Binary-acquisition finding:** there is **no official prebuilt Windows DLL**; we build `libvkd3d-shader` from source (MSYS2/autotools, the *tarball* not a git clone — the tarball ships pre-generated IDL/SPIR-V grammar headers, avoiding `widl`). Linux/macOS have distro/source builds. Hosting per-RID pinned artifacts for `restore` is the remaining follow-up.
- On Windows, `d3dcompiler_47.dll` (ships with Windows) serves as the correctness oracle / cross-check.

| Platform | Library | Delivery |
|----------|---------|----------|
| Windows | `libvkd3d-shader-1.dll` (shipping) + `d3dcompiler_47.dll` (oracle) | vkd3d built/hosted per-RID; d3dcompiler ships with Windows |
| Linux | `libvkd3d-shader.so` | `tools/restore.sh`, bundled into publish |
| macOS | `libvkd3d-shader.dylib` | same as Linux |

---

## Tasks

- [x] **Spike vkd3d-shader interop** — confirmed `vkd3d_shader_compile()` produces DXBC for a minimal PS (smoke test: `ps_5_0` → `DXBC` fourcc blob) and that `MonoGame.Framework.WindowsDX`'s `Effect` loads it (10/10).
- [x] Add `tools/restore` entries + the build recipe for `libvkd3d-shader` (mirror the SPIRV-Cross packaging; per-RID hosted download is a noted follow-up).
- [x] Implement a DXBC backend behind a shader-compiler seam (`IDxbcShaderCompiler`); route `PlatformTarget.DirectX` (DX11 profile) through it instead of DXC `ps_6_0`/`vs_6_0` (DXIL kept for DX12/KNI).
- [x] Plumb DXBC reflection (`DxbcReflectionExtractor`, `ID3D11ShaderReflection`) into the same `CompiledShaderBlob` shape Phase 17 defines (sampler table + cbuffer layout — DX needs the cbuffer layout, not a GL vertex-attribute table).
- [x] **Verified the per-shader record's attribute-table field for the DX profile** — byte-decoded `DirectX_11/` goldens against MonoGame's reader: DX keeps the attribute-count byte (= 0), so the writer needs **no** profile branch; the DX backend simply supplies `Attributes = []`.
- [x] Emit a MonoGame-loadable **DirectX** `.mgfx` (reused Phase 17's corrected header/footer/shader-record writer; DX supplies empty sampler/cbuffer names + DXBC bytes + profile byte 1).
- [x] Stood up the DX validation harness (`validation/BaselineDx`, `validation/CandidateDx`, `validation/CandidateVkd3d`, `validation/SharedDx`, `validation/compare_dx.py`; `net8.0-windows`, `MonoGame.Framework.WindowsDX 3.8.2.1105`, **Windows-gated**, excluded from the cross-platform `slnx`) and ran the Phase 17 compare flow against `tests/fixtures/golden/DirectX_11/`.
- [x] Documented diff-justified tolerance: only Dots shows Δ=1 (single-channel sub-rounding noise), 0 pixels over tolerance 4; everything else maxΔ 0.

---

## Acceptance Criteria

- [x] ShadowDusk produces **DXBC (SM ≤ 5)** for the DX11 profile via vkd3d-shader — no Wine, no Windows SDK (cross-platform binding, no Windows-only TFM on `ShadowDusk.HLSL`). Windows proven; Linux/macOS *run* validation → Phase 30 CI.
- [x] A ShadowDusk DirectX `.mgfx` **loads** in `MonoGame.Framework.WindowsDX`'s `EffectReader` (10/10, both backends).
- [x] The uniform-free corpus matches the `mgfxc` DirectX golden **in-engine** (Grayscale/Invert/Pixelated/Fading — exact).
- [x] Uniform-driven corpus matches with parameters set by name (TintShader/Sepia/Saturate/Scanlines/Dots/Dissolve — exact or within tolerance).
- [x] DX12/KNI DXIL path is unchanged and still works (DXC `ps_6_0`/`vs_6_0` retained).
- [x] DirectX validation project is Windows-gated and does not break the Linux/macOS `slnx` build (verified: `dotnet build ShadowDusk.slnx` green, 0 warnings).

---

## Definition of Done

Swap `mgfxc` → ShadowDusk for a **WindowsDX** game's `.fx` shaders, load the resulting DirectX `.mgfx` into a real `MonoGame.Framework.WindowsDX` `GraphicsDevice`, and the game renders the same image as the `mgfxc` build — produced by a DXBC backend that runs on any OS. ✅ **Met** for the SM5 PS-only corpus (10/10 pixel-equivalent, both backends; vkd3d is the any-OS one). With Phase 17 (OpenGL) this completes the fidelity (Part 2) goal for both desktop backends; WASM reach is [Phase 19](PHASE-19-wasm-runtime-compilation.md).

---

## Track A landed (2026-06-10) — cross-platform DXBC reflection

The "Phase 18 Track A" carry-forward (DXBC reflection P/Invoked Windows-only
`D3DReflect`, keeping the DX11 `.mgfx` pipeline Windows-bound even with the vkd3d
backend) is **done**: `DxbcReflectionExtractor` now delegates to the pure-managed
**`RdefReader`** (`src/ShadowDusk.Core/Reflection/RdefReader.cs` — RDEF + ISGN/OSGN
chunk parsing, the CtabReader sibling) on every OS. `D3DReflect` is demoted to a test
oracle; `DxbcReflectionParityTests` proves the managed output deeply equal to it for
both backends' DXBC, and a full-corpus A/B proved zero `.mgfx` byte change. As-built
detail, evidence, and the uncovered corner cases:
[PHASE-37 § Phase 18 Track A](PHASE-37-cross-platform-native-availability.md#phase-18-track-a--cross-platform-dxbc-reflection--done-2026-06-10).

## Carry-forwards (tracked, not swept)

- **VS-driven effects** — the validated corpus is PS-only SM3 (same scope as Phase 17). A VS+PS effect (BasicEffect) was decoded structurally (VS attr count = 0, clean) but not run through the render harness → backlog **17-VS**.
- **Linux/macOS *run* validation of the vkd3d backend** — the backend is cross-platform-capable (no platform assumptions, builds clean, lib available for all 3 OSes) and proven correct on Windows; rendering DXBC requires a Windows MonoGame game, so the off-Windows proof is "compiles byte-identically there" → **Phase 30 CI** (cross-host byte-equality).
- **Per-RID hosted vkd3d binaries for `restore`** — currently a local Windows build + documented recipe; hosting pinned per-RID artifacts (like SPIRV-Cross) is a follow-up.
- **GL extra `SpriteTextureSampler` parameter** — the GL path emits a standalone sampler param that diverges from its golden yet renders pixel-equivalent (Phase 17). Optional cleanup to fold it like the DX path now does.
