# Phase 0 — Fixture Setup & Golden Compilation

Everything done before a single line of ShadowDusk compiler code was written.
The goal of Phase 0 is to have a known-good reference set that ShadowDusk output
can be diffed against once the tool is built, plus a live viewer to visually
confirm the compiled shaders actually render correctly.

---

## Repository layout (as of Phase 0)

```
ShadowDusk/
├── plan/
│   └── phase-0-setup.md          <- this file
├── samples/
│   └── ShaderViewer/              <- interactive viewer (see section 3)
├── tests/
│   └── fixtures/
│       ├── shaders/               <- 43 source .fx/.fxh files
│       └── golden/
│           ├── DirectX_11/        <- 39 .mgfx reference files
│           └── OpenGL/            <- 38 .mgfx reference files
└── tools/
    └── compile-fixtures.ps1       <- re-runnable compilation script
```

---

## 1. Shader Fixture Corpus

**Location:** `tests/fixtures/shaders/` — 43 files (39 .fx + 4 .fxh headers)

### Source shaders (39 .fx files)

| Source repo | URL | Count | Key files |
|---|---|---|---|
| MonoGame/MonoGame `develop` branch | https://github.com/MonoGame/MonoGame/tree/develop/MonoGame.Framework/Platform/Graphics/Effect/Resources | 6 | BasicEffect, AlphaTestEffect, DualTextureEffect, EnvironmentMapEffect, SkinnedEffect, SpriteEffect |
| manbeardgames/monogame-hlsl-examples | https://github.com/manbeardgames/monogame-hlsl-examples | 4 | BasicShader, TintShader, BlendShader, SimpleLightShader |
| damian-666/MGShadersXPlatform | https://github.com/damian-666/MGShadersXPlatform/tree/master/MGCore/Content | 25 | Bloom, BloomCombine, BloomExtract, ClipShader (3 variants), DeferredLighting, DeferredSprite, Dissolve, Dots, Fading, ForwardLighting, GaussianBlur, Grayscale, Invert, MultiTexture, MultiTextureOverlay, Pixelated, PolygonLight, Saturate, Scanlines, Sepia, SpriteAlphaTest, Teleport, VertexAndPixel |
| discosultan/penumbra | https://github.com/discosultan/penumbra/tree/master/Source/Content | 4 | PenumbraHull, PenumbraLight, PenumbraShadow, PenumbraTexture |

### Shared headers (4 .fxh files)

All headers are from **MonoGame/MonoGame `develop` branch** and must live alongside
the .fx files so `mgfxc` can resolve `#include` directives:

| File | Used by |
|---|---|
| `Structures.fxh` | BasicEffect, AlphaTestEffect, DualTextureEffect, EnvironmentMapEffect, SkinnedEffect |
| `Common.fxh` | Included by Structures.fxh |
| `Lighting.fxh` | Included by Structures.fxh |
| `Macros.fxh` | All MonoGame built-ins + Penumbra shaders |

**Important:** The penumbra repo ships its own `Macros.fxh`. It was replaced with
MonoGame's version because MonoGame's `Lighting.fxh` uses the `UNROLL` macro
(`#define UNROLL [unroll]`) which only MonoGame's `Macros.fxh` defines.

### Fixes applied to downloaded shaders

Several downloaded shaders had authoring bugs. They were fixed in place so the golden
corpus represents correct MonoGame shaders. ShadowDusk's compiler test still validates
byte-identical output for whatever source is provided — the fixups just mean the
corpus tests correct patterns rather than broken ones.

**ps_4_0 → ps_4_0_level_9_1 (load failure fix)**

On D3D11, MonoGame requires the `_level_9_1` variant which embeds two DXBC sub-blobs
(SM4 + SM2 downlevel). Plain `ps_4_0` / `vs_4_0` produces a single-blob binary that
`ID3D11Device::CreatePixelShader` rejects at runtime with `E_INVALIDARG`. These shaders
were meant for MonoGame but had the wrong compilation target:

`MultiTexture`, `MultiTextureOverlay`, `Dots`, `SpriteAlphaTest`, `PolygonLight`,
`ClipShaderNew`, `Scanlines` (also affected `vs_4_0_level_9_1`)

**Outdated shader model → ps_4_0_level_9_1 (compile failure fix)**

DX9-era shader models (`ps_2_0`, `ps_3_0`) fail to compile for the `DirectX_11`
profile. These were updated to emit `#if OPENGL`/`#else` guards with
`ps_4_0_level_9_1` for DX and `ps_3_0` for OpenGL:

`DeferredSprite`, `Sepia` (were `ps_2_0`); `Dissolve` (had hardcoded `ps_3_0` in
technique body, bypassing its own macro); `DeferredLighting` (internal TECHNIQUE
macro used `vs_2_0`/`ps_2_0`)

**PS_SHADERMODEL → vs_5_0/ps_5_0 → vs_4_0_level_9_1/ps_4_0_level_9_1**

`VertexAndPixel` used SM5 which produces a single-blob binary and caused
`E_INVALIDARG` at load time.

**Bare PS input → full VertexShaderOutput struct (flip bug fix)**

D3D11 links interpolants by register position, not semantic name. SpriteBatch's
inherited VS outputs `{SV_POSITION, COLOR0, TEXCOORD0}` in registers 0/1/2. A PS
that only declares `float2 texCoord : TEXCOORD0` gets register 0 = position data,
not UV, causing the image to render upside-down. Fix: replace bare params with the
full `VertexShaderOutput { SV_POSITION, COLOR0, TEXCOORD0 }` struct.

Affected: `BloomExtract`, `BloomCombine`, `GaussianBlur`, `Bloom` (also used
`TEXCOORD` with no index instead of `TEXCOORD0`), `SimpleLightShader`, `Saturate`,
`Sepia`, `DeferredSprite`, `Dissolve`, `MultiTexture`

---

## 2. Golden Reference Compilation

### Tool

**Binary:** `mgfxc.exe` v3.8.3
**Location:** `%USERPROFILE%\.nuget\packages\dotnet-mgcb-editor-windows\3.8.3\tools\net8.0\any\mgcb-editor-windows-data\mgfxc.exe`

The script auto-discovers the highest installed version. If you update MonoGame
tools (`dotnet tool update -g dotnet-mgcb-editor-windows`) re-run the script to
regenerate the golden files with the new compiler.

### Running the compile script

```powershell
# From repo root — use explicit absolute paths to avoid PSScriptRoot issues
.\tools\compile-fixtures.ps1 `
    -ShaderDir "c:\git\ShadowDusk\tests\fixtures\shaders" `
    -GoldenDir "c:\git\ShadowDusk\tests\fixtures\golden"
```

Profiles compiled: `DirectX_11` and `OpenGL` (mgfxc's only two options).

### Compile results

| Profile | Output dir | Compiled OK |
|---|---|---|
| DirectX_11 | `tests/fixtures/golden/DirectX_11/` | 39 .mgfx |
| OpenGL | `tests/fixtures/golden/OpenGL/` | 38 .mgfx |

### Known compile failure

| Profile | Shader | Reason |
|---|---|---|
| OpenGL | DeferredLighting | Multi-render-target outputs (`COLOR0`+`COLOR1`) are not valid in `ps_4_0_level_9_1` for OpenGL. This is a fundamental architecture limitation — DeferredLighting is a deferred pipeline shader requiring MRT. ShadowDusk must reproduce this compile failure for the OpenGL profile. |

---

## 3. Shader Viewer

**Location:** `samples/ShaderViewer/`
**Template:** `mgwindowsdx` (MonoGame Windows Desktop, DirectX 11)
**.NET:** 8.0 / MonoGame 3.8.3

An interactive split-screen viewer that lets you cycle through every compiled
`.mgfx` and see its effect applied to a recognisable reference image (a cat photo)
side-by-side with the unmodified original.

### How it works

```
┌─────────────────────┬─────────────────────┐
│                     │                     │
│   Original (cat)    │  Shader applied     │
│   — no effect —     │  (right half only)  │
│                     │                     │
└─────────────────────┴─────────────────────┘
  [1/40] GaussianBlur                   (header)
  Left/Right to cycle   ESC to quit     (footer)
```

**Render strategy:** Left half uses `SpriteSortMode.Deferred` (no effect).
Right half uses `SpriteSortMode.Immediate` with the custom Effect. For
pixel-only shaders (no VS declared), SpriteBatch's own sprite VS stays bound from
the prior draw call — this is intentional and required for `TEXCOORD0` to resolve
correctly.

### Running

```powershell
# Use explicit paths — PSScriptRoot is unreliable in some shells
Set-Location samples\ShaderViewer; dotnet run
```

Or use the run.ps1 launch script from within the ShaderViewer directory:
```powershell
.\samples\ShaderViewer\run.ps1
```

### Controls

| Key | Action |
|---|---|
| Left / Right (or A / D) | Cycle through shaders |
| ESC | Quit |

### Compatibility tiers at runtime (DirectX 11)

**Tier 1 — Loads and renders correctly**

Simple 2D effects and corrected post-process shaders: Grayscale, Invert, Sepia,
Saturate, BasicShader, TintShader, Fading, Pixelated, Teleport, Scanlines,
BloomExtract, Bloom, Dissolve, Dots, SpriteAlphaTest, MultiTexture,
MultiTextureOverlay, ClipShader variants, ForwardLighting, PolygonLight, SpriteEffect,
BlendShader, PenumbraTexture, and most others.

**Tier 2 — Loads but limited rendering**

Effect loads but rendering is partial or wrong due to viewer limitations:

| Shader | Reason |
|---|---|
| BloomCombine | `BaseSampler` at register s1 is unbound by SpriteBatch (only s0 is set); blend formula produces dim output |
| GaussianBlur | `SampleOffsets`/`SampleWeights` arrays are not set → all-zero weights → black output |
| DeferredSprite | Outputs to MRT (`COLOR0`+`COLOR1`); single-RT SpriteBatch causes draw error |
| BasicEffect, EnvironmentMapEffect, SkinnedEffect | 3D built-in effects; SpriteBatch's 2D vertex layout doesn't match the expected 3D vertex format |

**Tier 3 — Fails to load**

None currently — all DX11 golden files load without `E_INVALIDARG`. Prior to the
shader model fixes, 8 shaders in this tier (see git history).

### Common parameters wired in TrySetCommonParameters

`Game1.cs` probes for well-known parameter names and sets them if present. The full
list covers: standard textures (`Texture`, `DiffuseMap`, `Lightmap`, `SpriteTexture`,
`Character01/02`, `_secondTexture`, `Mask`, `ClipTexture`, `DrawTexture`,
`RenderTargetTexture`, `MaskTexture`, `_normalMap`, `_dissolveTex`, `_colorMap`);
timing (`Time`, `ElapsedTime`); bloom parameters (`BloomThreshold`, `BloomIntensity`,
`BaseIntensity`, `BloomSaturation`, `BaseSaturation`); glow (`GlowIntensity`,
`GlowSize`); light (`lightSource`, `lightColor`, `lightRadius`, `LightPosition`,
`LightColor`, `Radius`); matrix (`_matrixTransform`, `MatrixTransform`,
`viewProjectionMatrix`, `WorldViewProj`, `World`, `View`, `Projection`); and misc
(`ScreenSize`, `TextureSize`, `Progress`, `amount`, `_progress`,
`_dissolveThreshold`, `_dissolveThresholdColor`, `_sepiaTone`, `_alphaTest`,
`Color`, `DiffuseColor`, `Alpha`, directional light params).

Parameters not found are silently skipped via `?.SetValue()`.

### Project structure

```
samples/ShaderViewer/
├── Content/
│   ├── cat.jpg                    <- reference image (copied to output at build)
│   ├── Font.spritefont            <- Arial 16pt, ASCII 32-126 only
│   └── Content.mgcb               <- MGCB content pipeline (builds Font only)
├── Game1.cs                       <- all game logic
├── Program.cs                     <- one-liner: new Game1().Run()
├── ShaderViewer.csproj            <- links golden .mgfx files into Shaders/ at output
└── run.ps1                        <- launch script (must be run from ShaderViewer dir)
```

### Important implementation notes for future agents

**Font character range:** The SpriteFont is compiled for ASCII 32–126 only (no
Unicode). Any string drawn with `DrawShadowed()` must pass through `Sanitize()`
first, which replaces out-of-range characters with `?`. Em dashes, arrows, and
other non-ASCII chars will crash with `ArgumentException` if not sanitised.

**Shader `.mgfx` delivery:** The compiled golden files are linked into the project
via `<Content>` items in the .csproj with `<Link>Shaders\DirectX_11\...</Link>`.
They are copied `PreserveNewest` to the output directory. At runtime they are
loaded from `Path.Combine(AppContext.BaseDirectory, "Shaders", "DirectX_11")`.

**SpriteBatch inherited VS trick:** For pixel-only shaders, SpriteBatch's sprite
vertex shader from the previous `Deferred` draw call stays bound. The left-half
draw (always `Deferred`, no effect) primes this. For this to work correctly, the
PS input must declare the full `VertexShaderOutput { SV_POSITION, COLOR0, TEXCOORD0 }`
struct — otherwise D3D11 links interpolants by register position and TEXCOORD0
receives position data rather than UVs.

**Platform switching at runtime is not possible.** OpenGL `.mgfx` files contain
GLSL bytecode and cannot be loaded by the DirectX 11 device. A separate
`mgdesktopgl` project would be needed to test OpenGL golden output.

**Error resilience:** All draw calls are wrapped in try/catch. A shader that
crashes during draw shows an error in the HUD and falls back to displaying the
original cat image. The viewer never crashes due to a bad shader.

---

## Task Checklist (all complete)

- [x] 1. Acquire 39 `.fx` shader files from MonoGame, manbeardgames, damian-666/MGShadersXPlatform, and Penumbra source repos.
- [x] 2. Acquire the 4 shared `.fxh` headers (`Structures.fxh`, `Common.fxh`, `Lighting.fxh`, `Macros.fxh`) from MonoGame/MonoGame `develop` branch.
- [x] 3. Replace Penumbra's `Macros.fxh` with MonoGame's version (required for `UNROLL` macro used by `Lighting.fxh`).
- [x] 4. Apply shader-model fixes: upgrade `ps_4_0` → `ps_4_0_level_9_1` on DX11-targeting shaders and fix DX9-era shader models that fail `DirectX_11` profile compilation.
- [x] 5. Fix bare PS-input bug (register-position mismatch) in `BloomExtract`, `BloomCombine`, `GaussianBlur`, `Bloom`, and related shaders.
- [x] 6. Fix `VertexAndPixel` SM5 single-blob issue (`vs_5_0`/`ps_5_0` → `vs_4_0_level_9_1`/`ps_4_0_level_9_1`).
- [x] 7. Run `tools/compile-fixtures.ps1` to generate 39 golden `DirectX_11` `.mgfx` reference files.
- [x] 8. Run `tools/compile-fixtures.ps1` to generate 38 golden `OpenGL` `.mgfx` reference files (1 known failure: `DeferredLighting` MRT incompatibility — expected and documented).
- [x] 9. Scaffold `samples/ShaderViewer` using the `mgwindowsdx` MonoGame template.
- [x] 10. Implement split-screen viewer with shader cycling (Left/Right), `TrySetCommonParameters` auto-wiring, and HUD.
- [x] 11. Load and validate all 39 DirectX_11 golden `.mgfx` files in the ShaderViewer — confirm Tier 1 (renders correctly) vs Tier 2 (loads but limited) vs Tier 3 (fails) breakdown.
- [x] 12. Document Tier 1/2 limitations for `BloomCombine`, `GaussianBlur`, `DeferredSprite`, and 3D built-in effects.
- [x] 13. Confirm zero Tier 3 (load failure) shaders after applying all fixes.

---

## 4. What Comes Next (Phase 1)

The golden files establish the correctness bar. Phase 1:

1. Scaffold the solution — `src/ShadowDusk.Core`, `ShadowDusk.HLSL`, `ShadowDusk.Cli`
   per the layout in `CLAUDE.md`.
2. Build the HLSL parser — parse `.fx` Effect files into a `ShaderIR`.
3. Integrate DXC via `Vortice.Dxc` NuGet — compile HLSL to DXBC (DirectX_11 path).
4. Emit `.mgfx` binary format — must match MonoGame's expected layout exactly.
5. Integration test: compile every fixture with ShadowDusk CLI, diff output against
   `tests/fixtures/golden/DirectX_11/*.mgfx` byte-for-byte.
6. Build SPIRV-Cross path for OpenGL — `ShadowDusk.GLSL`, test on Linux/macOS.

**Error-path testing:** ShadowDusk must also reproduce `mgfxc` compile *errors*
correctly (same exit code, same stderr format). Dedicated error-case fixtures
(e.g., a shader with an invalid sampler, unsupported intrinsic, or syntax error)
should be added to `tests/fixtures/shaders/error-cases/` with `.expected-error`
companion files describing the expected failure. Do not rely on the primary fixture
corpus for error-path coverage — those shaders should all compile successfully.

**Expanding the ShaderViewer (if needed before Phase 1 is complete):**
- Add more test images: put additional `.jpg`/`.png` files in `Content/` and add
  them to the .csproj `<Content>` items. Load them in `LoadContent()` and add a
  key to cycle between them.
- Add shader parameter sliders: extend `TrySetCommonParameters()` and add keyboard
  controls (e.g. `[`/`]` to adjust `Intensity`).
- Add an OpenGL viewer: scaffold a second project with `mgdesktopgl` template,
  point `shaderDir` at `Shaders/OpenGL/`, run on Linux/macOS or Windows DesktopGL.
- Add a screenshot key: use `GraphicsDevice.GetBackBufferData()` + a PNG encoder
  to save the current frame.
