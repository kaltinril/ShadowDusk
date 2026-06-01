# ShaderFiddle.Web — ShadowDusk in-browser shader fiddle (Phase 22)

An XNA-Fiddle-style browser sample: paste HLSL `.fx`, and see the standard
ShadowDusk **cat** re-rendered with that shader applied — entirely client-side,
no server. Runtime is **KNI** (nkast's MonoGame fork) on **Blazor WebAssembly +
WebGL**.

```
paste .fx ─▶ WasmShaderCompiler.CompileAsync(src, OpenGL)   ── in-browser (Phase 19 mode 2)
          ─▶ byte[] .mgfx (MonoGame MGFX v10 / MojoShader)  ── Phase 17 format
          ─▶ new Effect(graphicsDevice, mgfxBytes)          ── KNI WebGL runtime
          ─▶ SpriteBatch.Begin(effect:); Draw(cat); End();  ── applied over the cat
          ─▶ WebGL canvas
```

## Status (2026-05-31) — read this first

This sample is built on the Phase 22 **Fallback (mode 1)** path, because two
dependencies are not yet in place:

| Path | What it is | State here |
|---|---|---|
| **Mode 1** — load precompiled `.mgfx` and render | the *Load a precompiled sample* dropdown | **Wired & shipped.** Renders one of the 10 Phase-17 corpus shaders through a real KNI `Effect`. |
| **Mode 2** — compile `.fx` in-browser | the *Compile & Apply* button | **Wired to the real `WasmShaderCompiler`, but it surfaces an honest stub error.** The emscripten DXC + SPIRV-Cross WASM modules are deferred to **Phase 100**; `ShadowDusk.Wasm/Phase19.js` throws by design until they land. We do **not** fake a result. |

When Phase 100 delivers the WASM modules, mode 2 starts working with **no C#
change** — only `Phase19.js` gets its two functions implemented.

### What has and has not been verified

- ✅ **Builds and publishes** clean (`dotnet build`, `dotnet publish -c Release`),
  `net8.0-browser` + BlazorWebAssembly SDK, referencing both KNI (net8.0) and
  `ShadowDusk.Wasm` (net8.0-browser). All `index.html` JS/asset paths resolve in
  the publish output.
- ⚠️ **Not yet verified in a browser.** The dev environment that produced this
  has no browser, so the actual cat render is a **manual run** step (below) and
  a Phase 30 headless-browser CI item — not an automated check here.
- ⚠️ **KNI load-side risk (the big unknown).** KNI v4.x uses its own **KNIFX
  v11** format (`KNIF` signature, `dotnet-knifxc`). KNI's `new Effect(gd, byte[])`
  *also* accepts legacy MonoGame **MGFX v10** (`MGFX` signature, version byte 10,
  profile `OpenGL_Mojo`) — which is exactly what ShadowDusk emits — via a
  backward-compat `MGFXReader10`. **But that reader is a fork:** passing the
  signature/version gate is necessary, not sufficient. Whether ShadowDusk's
  MGFX v10 GLSL dialect actually *renders* in KNI WebGL is unverified. If it
  diverges, ShadowDusk needs a KNIFX-v11 writer (see the phase doc); the UI
  reports the `new Effect` failure verbatim if it happens.

## Run it (manual)

```bash
cd samples/ShaderFiddle.Web
dotnet run                 # serves on https://localhost:5xxx
# then open the printed URL in a browser
```

or `./run.ps1` on Windows. First load is slow — the whole .NET WASM runtime + KNI
must download (no size optimization here; that's deferred).

On the page:
1. It boots showing the cat through the **Grayscale** sample (mode 1).
2. Pick other samples from the dropdown to load their precompiled `.mgfx`
   (Invert, TintShader, Sepia, Saturate, Pixelated, Scanlines, Fading, Dots,
   Dissolve) — each with the same by-name parameter values the desktop Phase-17
   validation uses (`WebShaderInputs.SetParams`).
3. Click **Compile & Apply** to exercise the in-browser compile path; today it
   reports the Phase-100 stub error and keeps the last good render.

## How it mirrors the desktop validation

The render path is a direct port of `validation/Shared/EffectImageRenderer.cs`
(KNI keeps the `Microsoft.Xna.Framework` namespace, so the code is nearly
verbatim): `BlendState.Opaque` + `SamplerState.LinearClamp`, the SpriteBatch VS
**prime** so the PS-only effect inherits SpriteBatch's vertex shader, then an
`Immediate` batch with the effect. Parameters are set **by name**
(`WebShaderInputs`, ported from `validation/Shared/ShaderInputs.cs`). The cat is
`samples/ShaderViewer/Content/cat.jpg`; the mode-1 `.mgfx` are the Phase-17
OpenGL goldens from `tests/fixtures/golden/OpenGL/`.

## Why it is not in `ShadowDusk.slnx`

Intentional: it targets `net8.0-browser`, pulls KNI + the Blazor WASM workload,
and would otherwise drag browser-only build machinery into the core
cross-platform CI. It has its own empty `Directory.Build.props` to isolate it
from the repo-root props (which force `net8.0`, central package management, and
`TreatWarningsAsErrors`). Build it directly from this folder.

## Files

| File | Role |
|---|---|
| `ShaderFiddle.Web.csproj` | net8.0-browser Blazor WASM; refs KNI + `ShadowDusk.Wasm` |
| `Program.cs` | Blazor host |
| `Pages/Index.razor[.cs]` | the fiddle UI + compile/load loop + error panel |
| `ShaderFiddleGame.cs` | KNI WebGL `Game`; cat + effect render path |
| `WebShaderInputs.cs` | corpus list + by-name parameter values (ported) |
| `wwwroot/index.html` | Blazor shell + KNI 8.0.11 JS shims + render loop |
| `wwwroot/shadowdusk-dxc.js` / `wwwroot/shadowdusk-spirv-cross.js` | **host-side** ES-module stubs registered via `JSHost.ImportAsync` to satisfy `ShadowDusk.Wasm`'s `[JSImport]` contracts. They `throw` (mode 2 → Phase 100). Registering them is what makes the compile fail *gracefully* — calling an **unregistered** `[JSImport]` module aborts the whole .NET WASM runtime and crashes the page. |
| `wwwroot/cat.jpg` | the standard cat image |
| `wwwroot/shaders/OpenGL/*.mgfx` | precompiled mode-1 corpus bytes |
| `wwwroot/shaders/src/*.fx` | corpus sources shown in the editor |
