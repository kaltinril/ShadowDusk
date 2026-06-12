# ShaderFiddle.Web — ShadowDusk in-browser shader fiddle + export station

An XNA-Fiddle-style browser sample: paste (or upload) HLSL `.fx`, compile it
**entirely in the browser**, and see the standard ShadowDusk **cat** re-rendered
with that shader applied — no server, no `mgfxc`, no native toolchain on the
user's machine. Runtime is **KNI** (nkast's MonoGame fork) on
**Blazor WebAssembly + WebGL**.

It is also the **export station** (owner-directed, 2026-06-09): the *Export*
panel compiles the editor source in-browser for **OpenGL** (`.mgfx`),
**DirectX** (DX11 SM5 DXBC `.mgfx`), or **FNA** (fx_2_0 `.fxb`) and downloads
the artifact — **byte-identical to ShadowDusk's desktop output** for the same
source and target (for DirectX: a desktop compile using the vkd3d backend,
which the browser always uses). OpenGL stays the live render target; DirectX and FNA are
export-only (a browser cannot execute DXBC/D3D9 bytecode — they render in your
MonoGame WindowsDX / FNA game).

```
paste .fx ─▶ WasmShaderCompiler.CompileAsync(src, OpenGL)   ── faithful DXC→WASM, in-browser
          ─▶ byte[] .mgfx (MonoGame MGFX v10 / MojoShader)  ── MGFX binary format
          ─▶ new Effect(graphicsDevice, mgfxBytes)          ── real KNI WebGL Effect
          ─▶ SpriteBatch.Begin(effect:); Draw(cat); End();  ── applied over the cat
          ─▶ WebGL canvas
```

## Status — this sample uses the faithful product compiler

Both modes are live and use the **real ShadowDusk pipeline**:

| Path | What it is | State |
|---|---|---|
| **Mode 1** — load a precompiled `.mgfx` and render | the *Load a precompiled sample* dropdown | **Done.** Renders one of the 10 corpus shaders through a real KNI `Effect`. |
| **Mode 2** — compile `.fx` in-browser | the *Compile & Apply* button | **Done — faithful.** Compiles via `WasmShaderCompiler` (the **faithful pinned DXC→WASM → SPIR-V → SPIRV-Cross → MGFX** pipeline), then loads + renders the result. |

**No substitute compilers.** Mode 2 runs the same faithful frontend as the desktop
CLI — the in-browser SPIR-V is **byte-identical to desktop DXC**. The compiler ships
inside the `ShadowDusk.Wasm` NuGet package as self-registered Blazor static web
assets (`_content/ShadowDusk.Wasm/`); this sample adds only a `ProjectReference` and
**wires nothing** (no `JSHost.ImportAsync` — see `Pages/Index.razor.cs`). The first
*Compile & Apply* lazily downloads the ~17 MB `dxcompiler.wasm` once.

> The older Slang-wasm frontend (`wwwroot/shadowdusk-dxc.js` + `wwwroot/slang/`) is
> **dead, sample-only reference** kept for history — it is **not registered** and
> never runs. Slang was a *substitute* compiler (sample-only per THE PURPOSE); the
> faithful DXC→WASM module replaced it.

### Verified

- ✅ **Builds and publishes** clean (`dotnet build`, `dotnet publish -c Release`),
  `net8.0-browser` + BlazorWebAssembly SDK, referencing KNI (net8.0) and
  `ShadowDusk.Wasm` (net8.0-browser).
- ✅ **Faithful frontend byte-identical to desktop DXC** — the `.wasm-build` gates
  (`node-test-dxc-wasm.mjs`, `node-test-dxc-shim.mjs`) are 10/10 exact on the corpus.
- ✅ **Renders in real KNI WebGL** — a headless harness compiles + renders
  the corpus **10/10** pixel-equivalent (after the Dissolve slot-1 sampler pin and the
  `roundEven`→`floor(x+0.5)` WebGL1 lowering). KNI's forked `MGFXReader10` parses
  ShadowDusk's MGFX v10 directly — **no KNIFX-v11 writer needed**.
- ✅ **Live mode-2 compile confirmed in a real desktop browser.**

## Run it

```bash
cd samples/ShaderFiddle.Web
dotnet run                 # serves on http://localhost:5000
# then open the printed URL in a browser
```

or `./run.ps1` on Windows. First load downloads the .NET WASM runtime + KNI (no size
optimization here; deferred). **Prerequisite:** the faithful `dxcompiler.wasm` must be
restored into the package's `wwwroot/dxc/` — run `pwsh -File tools/restore.ps1` (or
`./tools/restore.sh`) once before building; the build fails loudly if it is missing.

On the page:
1. It boots showing the cat through the **Grayscale** sample (mode 1).
2. Pick other samples from the dropdown to load their precompiled `.mgfx`
   (Invert, TintShader, Sepia, Saturate, Pixelated, Scanlines, Fading, Dots,
   Dissolve), each with the by-name parameter values the desktop
   validation uses (`WebShaderInputs.SetParams`).
3. Edit the source and click **Compile & Apply** to compile it in-browser (mode 2).
   On any failure the error panel shows the verbatim ShadowDusk diagnostic.
4. **Live parameters** — after a compile/load, the panel lists the effect's editable
   `float` scalar/vector parameters; edit them to drive the shader live. (A custom
   global like `float FishEyeAmount = 0.35;` shows up here defaulting to `0` — DXC
   doesn't bake a global's initializer into the bytes, so set it here, inline it as a
   literal, or `SetValue` it from code.)
5. **Reset (no shader)** drops the effect and shows the original cat.
6. **Export** — upload your own `.fx` (or use the editor source), pick a target row,
   and **Compile & Download**:
   - **OpenGL** → `<name>.mgfx` — MonoGame DesktopGL / KNI (the same target rendered
     live on the canvas).
   - **DirectX (DX11 SM5)** → `<name>.mgfx` — export-only; renders in your MonoGame
     WindowsDX game.
   - **FNA (fx_2_0)** → `<name>.fxb` — export-only; renders in your FNA game.

   All three run the same faithful pipeline as the desktop CLI (`WasmShaderCompiler`
   with the browser-injected DXC / SPIRV-Cross / vkd3d WASM backends).
   OpenGL and FNA bytes are identical to a desktop compile; DirectX bytes are
   identical to a desktop compile **using the vkd3d backend** (`DxbcBackend.Vkd3d` —
   the browser always compiles DXBC via vkd3d, while the desktop *default* is
   d3dcompiler_47, whose bytes differ). Compile errors appear in
   the same verbatim `file:line:col` error panel + editor squiggles; if the vkd3d
   WASM module is genuinely absent the DX/FNA rows fail loudly with **SD1902** (run
   `tools/restore.*` first). The first DX/FNA export fetches `vkd3d-shader.wasm`
   (~0.4 MB) once; the artifact name follows the selected sample / uploaded file and
   is editable.

## Verify the faithful frontend without a browser

The product frontend is the pinned **DirectXShaderCompiler compiled to WebAssembly**
(`.wasm-build/dxc-wasm-out/dxcompiler.{js,wasm}`, restored into the package). It is
node-testable — its SPIR-V is byte-identical to the desktop CLI on the corpus:

```bash
node .wasm-build/node-test-dxc-wasm.mjs    # raw module == desktop DXC, 10/10
node .wasm-build/node-test-dxc-shim.mjs    # the [JSImport] shim == desktop DXC, 10/10
```

The full report (pinned DXC commit, emscripten version, build recipe) is
`.wasm-build/DXC-WASM-BUILD.md`; the package-side restore is documented in
`src/ShadowDusk.Wasm/wwwroot/dxc/RESTORE.md`.

## How it mirrors the desktop validation

The render path is a direct port of `validation/Shared/EffectImageRenderer.cs`
(KNI keeps the `Microsoft.Xna.Framework` namespace, so the code is nearly
verbatim): `BlendState.Opaque` + `SamplerState.LinearClamp`, the SpriteBatch VS
**prime** so the PS-only effect inherits SpriteBatch's vertex shader, then an
`Immediate` batch with the effect. Slot-1 sampler state is pinned to
`LinearClamp` (Dissolve's second texture). Parameters are set **by name**
(`WebShaderInputs`, ported from `validation/Shared/ShaderInputs.cs`). The cat is
`samples/ShaderViewer/Content/cat.jpg`; the mode-1 `.mgfx` are the
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
| `Pages/Index.razor[.cs]` | the fiddle UI: compile/load loop, live-parameter panel, reset, error panel, export station (GL/DX/FNA compile + download, `.fx` upload) |
| `ShaderFiddleGame.cs` | KNI WebGL `Game`; cat + effect render path; parameter get/set |
| `WebShaderInputs.cs` | corpus list + by-name parameter values (ported) |
| `wwwroot/index.html` | Blazor shell + KNI 8.0.11 JS shims + render loop |
| `wwwroot/cat.jpg` | the standard cat image |
| `wwwroot/shaders/OpenGL/*.mgfx` | precompiled mode-1 corpus bytes |
| `wwwroot/shaders/src/*.fx` | corpus sources shown in the editor |
| `wwwroot/shadowdusk-dxc.js`, `wwwroot/slang/*` | **Dead sample-only Slang reference — NOT registered, never runs.** The faithful DXC→WASM frontend ships in the `ShadowDusk.Wasm` package and self-registers; these are kept only as a record of the prior substitute frontend. |
| `wwwroot/shadowdusk-spirv-cross.js` | Sample-only copy of the SPIR-V→GLSL shim; the product copy ships in the package and is self-registered. |
