# Choosing a Target

When you compile a `.fx`, the choice that changes the output bytes is mostly **which graphics backend** the result must run on — with one framework-level exception: **FNA**, which loads a different effect format entirely. `GraphicsProfile` (Reach / HiDef) feels like it should matter too, but it doesn't. This page untangles the three axes so you can pick the right target with confidence (e.g. when offering shader downloads from a tool like XnaFiddle).

## The three axes (which ones change the file)

| Axis | Examples | Changes the compiled output? |
|---|---|---|
| **1. Framework** | MonoGame, KNI, FNA | **Mostly no** — MonoGame and KNI read the same MGFX container. **FNA is the exception**: it loads the legacy D3D9 fx_2_0 `.fxb`, so it gets its own output (see below). |
| **2. Graphics backend / API** | DirectX 11, OpenGL/DesktopGL, WebGL, Metal, Vulkan | **YES — for MonoGame/KNI this is the axis that picks the bytes** |
| **3. `GraphicsProfile`** | Reach, HiDef | **No** — a runtime setting, not a compile target |

For MonoGame/KNI the bytes are dictated by **axis 2**. A DirectX `.mgfx` contains **DXBC GPU bytecode**; an OpenGL `.mgfx` contains **GLSL source text**. They are not interchangeable — MonoGame's `Effect` loader reads a profile byte in the header and routes to a completely different code path. So for MonoGame/KNI the one thing you must know to pick a target is **which graphics backend the consuming game runs**. For **FNA**, axis 1 decides it: pick `PlatformTarget.Fna` and you get the `.fxb` FNA loads — one file serves every backend FNA itself runs on (FNA translates at load time).

## Axis 1 — Framework compatibility

| Framework | Status |
|---|---|
| **MonoGame** | ✅ Supported and validated (DesktopGL + WindowsDX, 10/10 each in the real runtime). |
| **KNI** | ✅ Supported — KNI reads the **same MGFX format** as MonoGame (its loader accepts MGFX v10). Validated end-to-end in real headless KNI WebGL. No KNI-specific output. |
| **FNA** | ✅ **Supported — but a different output format.** FNA's effect path differs from MonoGame's: it loads **legacy D3D9 fx_2_0 shader bytecode via MojoShader at runtime**, not the MGFX container. So ShadowDusk does **not** hand FNA a `.mgfx` — it emits the `.fxb` FNA actually loads (`new Effect(gd, bytes)`). Select it with `PlatformTarget.Fna` / CLI `/Profile:FNA` (D3D9-style `.fx`, SM ≤ 3). Validated end-to-end — renders pixel-equivalent to `fxc /T fx_2_0` in real FNA (PS-only and custom-vertex-shader effects, incl. multi-pass + in-pass render states). Shaders needing SM4+ features fail loudly with a clear diagnostic. |
| **Classic Microsoft XNA 4.0** | ❌ **Out of scope.** XNA's effect `.xnb` wraps legacy D3D9 bytecode produced by `fxc` — a different instruction set our pipeline does not emit, on a Windows-only framework abandoned in ~2013. There is no "reach" win (nothing to compile *where `mgfxc` can't*) and no fidelity oracle. (Note: "XnaFiddle" is named for the lineage — the runtime it ships is KNI/MonoGame, which **is** supported.) |

## Axis 2 — Graphics backend (the real matrix)

Select the backend with <xref:ShadowDusk.Core.CompilerOptions.Target> (library) or `/Profile:` (CLI). The defaults differ — see [Parameters & Caveats](parameters-and-caveats.md#the-library-vs-cli-default-target).

| Target | `CompilerOptions.Target` | CLI `/Profile:` | Output | Status |
|---|---|---|---|---|
| **OpenGL / DesktopGL / WebGL** | `PlatformTarget.OpenGL` | `OpenGL` | `.mgfx`, **GLSL text** (MojoShader dialect) | ✅ Validated |
| **DirectX 11 (WindowsDX)** | `PlatformTarget.DirectX` | `DirectX_11` | `.mgfx`, **DXBC binary** (SM5) | ✅ Validated |
| **FNA** | `PlatformTarget.Fna` | `FNA` | `.fxb`, **D3D9 fx_2_0** (SM ≤ 3) | ✅ Validated |
| **Metal** | `PlatformTarget.Metal` | — | MSL | ❌ Not implemented ([future](../backends/metal.md)) |
| **Vulkan** | `PlatformTarget.Vulkan` | `Vulkan` | SPIR-V | ❌ Parked ([future](../backends/vulkan.md)) |

For the MonoGame/KNI targets, the on-disk **profile byte** in the MGFX header encodes the backend choice (`OpenGL = 0`, `DirectX11 = 1`, `Vulkan = 3`). The runtime reads it to pick the shader path, so the target must be chosen **at compile time** — there is no universal `.mgfx` that serves both DirectX and OpenGL. A DirectX `.mgfx` is useless to a DesktopGL game and vice versa. FNA is selected differently (it's a whole separate format, `.fxb` — see Axis 1) and is **not** byte-compatible with the `.mgfx` targets.

> **Practical rule for shader downloads:** first ask *which framework* — if it's **FNA**, emit the `.fxb` (`PlatformTarget.Fna`) and you're done (one file, all FNA backends). For **MonoGame/KNI**, the only remaining question is *"DirectX (WindowsDX) or OpenGL/DesktopGL/Web?"* — offer one `.mgfx` per backend. `GraphicsProfile` never matters.

## Axis 3 — Reach vs HiDef (a runtime concept, not a target)

`GraphicsProfile.Reach` and `GraphicsProfile.HiDef` are **runtime** settings, not compile-time targets. `mgfxc` has no "Reach profile" vs "HiDef profile" — only `OpenGL` and `DirectX_11`. So you do **not** compile twice for Reach vs HiDef.

For the OpenGL/WebGL path specifically:

- `GraphicsProfile.Reach` → WebGL1 / GLSL ES 1.00
- `GraphicsProfile.HiDef` → WebGL2 / GLSL ES 3.00

ShadowDusk emits **one** OpenGL `.mgfx` (legacy GLSL dialect), and **KNI's runtime converts it to ES 3.00 at load time** for HiDef/WebGL2. One file serves both. See [KNI HiDef / WebGL2](parameters-and-caveats.md#kni-hidef--webgl2).

## `.mgfx` vs `.xnb`

For the MonoGame/KNI targets, ShadowDusk emits a raw **`.mgfx`** blob, **not** an `.xnb` (FNA's equivalent is the raw `.fxb`, likewise unwrapped). These are different layers:

| | `.mgfx` | `.xnb` |
|---|---|---|
| What it is | the compiled effect itself | the Content Pipeline container that *wraps* a `.mgfx` (and every other content type) |
| Loaded via | `new Effect(graphicsDevice, mgfxBytes)` | `Content.Load<Effect>("name")` |
| Produced by | **ShadowDusk** (and `mgfxc`) | the Content Pipeline / MGCB |

If a consuming project loads effects with `new Effect(gd, bytes)`, hand it the raw `.mgfx` — done. If it uses `Content.Load<Effect>`, it needs an `.xnb`, which means either building the `.fx` through MGCB (see the [MGCB Content Pipeline](mgcb-content-pipeline.md) guide) or wrapping the bytes yourself. ShadowDusk does not produce `.xnb` directly — that's the content-pipeline layer's job. See also [Parameters & Caveats → `.mgfx` vs `.xnb`](parameters-and-caveats.md#mgfx-vs-xnb).
