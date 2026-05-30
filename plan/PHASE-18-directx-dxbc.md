# Phase 18 — DirectX 11 DXBC Output (Runtime-Loadable WindowsDX Effects)

**Status:** Not started
**Depends on:** Phase 4 (DXC integration), [Phase 4.1 SPIKE](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) (cross-platform DXBC backend survey), Phase 7 + Phase 17 (the `MgfxWriter` header/shader-record rework — a DX `.mgfx` needs the same MonoGame-loadable container as the GL one), Phase 17 harness (reuse the same compare-in-engine instrument for DX).
**Blocks:** A credible "drop-in `mgfxc` replacement" claim for **Windows / `MonoGame.Framework.WindowsDX`** games — the **DirectX half** of the fidelity (Part 2) goal. The OpenGL half is Phase 17.

> This phase is referenced throughout [PHASE-17-monogame-runtime-validation.md](PHASE-17-monogame-runtime-validation.md) (§7, §3 punch-list #8, Definition of Done) as "Fixing DX = Phase 18," but never had its own document. This is that document. The architecture survey for it is [`monogame_runtime_mgfx_compiler_research.md`](../monogame_runtime_mgfx_compiler_research.md) §9.3 (DXC → DXIL) and the [Known Constraint section in plan.md](plan.md#-known-constraint-dxc-cannot-produce-sm5-dxbc).

---

## Overview

ShadowDusk's DirectX path currently compiles HLSL to **SM6 DXIL** — `DxcFlagBuilder` targets `ps_6_0`/`vs_6_0` because **DXC cannot emit SM ≤ 5 DXBC** (it has never supported DXBC output). MonoGame 3.8's DX11 runtime loads **only DXBC (SM ≤ 5)**: `ID3D11Device::CreateVertexShader` / `CreatePixelShader` reject DXIL unconditionally (DXIL is a D3D12-only format). So even after the Phase 17 `.mgfx` container rework, a ShadowDusk DirectX `.mgfx` **will not load** in a real WindowsDX game. This phase closes the format gap so the DirectX target reaches the same in-engine equivalence bar Phase 17 establishes for OpenGL.

The two are deliberately separated because **each backend is a different emitted artifact loaded by a different runtime path** (CLAUDE.md → *Compare same-backend, never cross-backend*). A green OpenGL Phase 17 result says nothing about DirectX; DX must be produced correctly and validated on its own.

---

## Scope and Non-Goals

**In scope:**
- A cross-platform HLSL → **DXBC (SM4/SM5)** backend that does **not** require Wine or the Windows SDK on Linux/macOS.
- DX `.mgfx` that loads in `MonoGame.Framework.WindowsDX`'s `EffectReader` and renders the same image as the `mgfxc` DirectX golden, validated through the Phase 17 compare harness (Windows-gated, see §3).
- The DirectX golden corpus under `tests/fixtures/golden/DirectX_11/`.

**Out of scope:**
- WASM + DirectX DXBC (no native P/Invoke in the browser; no prebuilt WASM artifact of the DXBC backend) — that remains the open problem tracked in [Phase 4.1 SPIKE](PHASE-4.1-SPIKE-wasm-directx-dxbc.md) and [Phase 19 WASM](PHASE-19-wasm-runtime-compilation.md).
- DirectX 12 / KNI DXIL — already works (D3D12 natively accepts SM6 DXIL via the existing `vs_6_0`/`ps_6_0` path).
- Byte-identical output vs `fxc.exe` (never a goal; semantic/render equivalence only).

---

## The backend decision

Per the [plan.md Known Constraint analysis](plan.md#cross-platform-sm5-dxbc-options), the only viable cross-platform HLSL→DXBC compiler with **no Wine runtime** is **`vkd3d-shader`** (`gitlab.winehq.org/wine/vkd3d`):

- Standalone C library, runs independently of Wine; P/Invoke pattern mirrors SPIRV-Cross exactly.
- `vkd3d_shader_compile()` HLSL → SM4/SM5 DXBC; LGPL-2.1+ (safe as a dynamically-linked native binary).
- Prebuilt shared libs for Linux/macOS/Windows; restored by `tools/restore.{sh,ps1}`, bundled into `dotnet publish`.
- Adequate coverage for MonoGame-style effects (cbuffers, `Texture2D` samplers, VS/PS). Known partial areas (SM5 UAV/RWBuffer, tessellation) are irrelevant for the post-process corpus.
- On Windows, `d3dcompiler_47.dll` (ships with Windows) is available as a fidelity fallback / cross-check.

| Platform | Library | Delivery |
|----------|---------|----------|
| Windows | `d3dcompiler_47.dll` (fallback) + `vkd3d-shader` | DLL ships with Windows; vkd3d via restore |
| Linux | `libvkd3d-shader.so` | `tools/restore.sh`, bundled into publish |
| macOS | `libvkd3d-shader.dylib` | same as Linux |

---

## Tasks

- [ ] **Spike vkd3d-shader interop** — confirm `vkd3d_shader_compile()` produces DXBC for one minimal VS+PS; verify `MonoGame.Framework.WindowsDX`'s `Effect` loads it. (May be partly covered by [Phase 4.1 SPIKE](PHASE-4.1-SPIKE-wasm-directx-dxbc.md).)
- [ ] Add `tools/restore` entries + pinned releases for `libvkd3d-shader` (mirror the SPIRV-Cross packaging).
- [ ] Implement a DXBC backend behind the existing shader-compiler abstraction; route `PlatformTarget.DirectX` through it instead of DXC `ps_6_0`/`vs_6_0` for the DX11 profile (keep DXIL for the DX12/KNI profile).
- [ ] Plumb DXBC reflection into the same `CompiledShaderBlob` shape Phase 17 defines (sampler table, cbuffer-index list — DX needs the cbuffer layout, not a GL vertex-attribute table).
- [ ] Emit a MonoGame-loadable **DirectX** `.mgfx` (reuse Phase 17's corrected header/footer/shader-record writer; DX-specific shader-record fields per MonoGame 3.8.2's open-source `Shader`/`EffectReader`).
- [ ] Stand up `tests/ShadowDusk.MonoGameValidation.DirectX/` properly (`net8.0-windows`, `MonoGame.Framework.WindowsDX`, **Windows-gated** so the cross-platform `slnx` build stays green) and run the Phase 17 compare flow against `tests/fixtures/golden/DirectX_11/`.
- [ ] Document any diff-justified tolerance (DXBC-vs-`fxc` driver precision drift) with observed deltas — no silent caps.

---

## Acceptance Criteria

- [ ] ShadowDusk produces **DXBC (SM ≤ 5)** for the DX11 profile on Linux, macOS, and Windows — no Wine, no Windows SDK.
- [ ] A ShadowDusk DirectX `.mgfx` **loads** in `MonoGame.Framework.WindowsDX`'s `EffectReader` (the §3.1-class container fix applied to the DX record).
- [ ] The uniform-free corpus matches the `mgfxc` DirectX golden **in-engine** (exact or diff-justified tolerance).
- [ ] Uniform-driven corpus matches with parameters set by name.
- [ ] DX12/KNI DXIL path is unchanged and still works.
- [ ] DirectX validation project is Windows-gated and does not break the Linux/macOS `slnx` build.

---

## Definition of Done

Swap `mgfxc` → ShadowDusk for a **WindowsDX** game's `.fx` shaders, load the resulting DirectX `.mgfx` into a real `MonoGame.Framework.WindowsDX` `GraphicsDevice`, and the game renders the same image as the `mgfxc` build — produced by a DXBC backend that runs on any OS. With Phase 17 (OpenGL) this completes the fidelity (Part 2) goal for both desktop backends; WASM reach is [Phase 19](PHASE-19-wasm-runtime-compilation.md).
