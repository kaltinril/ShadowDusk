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
| **Mode 2** — compile `.fx` in-browser | the *Compile & Apply* button | **HLSL→SPIR-V frontend is now REAL** (`wwwroot/shadowdusk-dxc.js`, backed by **Slang** compiled to WebAssembly, `wwwroot/slang/`). The SPIR-V→GLSL stage (`wwwroot/shadowdusk-spirv-cross.js`) is wired separately. End-to-end mode-2 render also depends on the KNI MGFX-load path (see below). |

The in-browser HLSL→SPIR-V compiler uses **Slang** (`slang-wasm`, v2026.10),
not DXC: DXC has no maintained WASM build, while Slang ships a prebuilt browser
WASM that compiles HLSL syntax straight to SPIR-V. A desktop spike proved Slang's
SPIR-V flows through ShadowDusk's pure-managed `SpirvReflector` **unchanged**
(Grayscale + TintShader reflect identically to the DXC/`-fvk-use-dx-layout`
oracle for the SM3 PS corpus). See `wwwroot/slang/RESTORE.md` for version/source.

### What has and has not been verified

- ✅ **Builds and publishes** clean (`dotnet build`, `dotnet publish -c Release`),
  `net8.0-browser` + BlazorWebAssembly SDK, referencing both KNI (net8.0) and
  `ShadowDusk.Wasm` (net8.0-browser). All `index.html` JS/asset paths resolve in
  the publish output, **including `wwwroot/slang/*`** (the Slang WASM is staged
  into the publish `wwwroot/` and served as a static asset).
- ✅ **HLSL→SPIR-V frontend verified under node** (no browser): the real
  `shadowdusk-dxc.js` compiles a corpus pixel shader (Grayscale) to valid SPIR-V
  (1400 bytes, magic `0x07230203`), broken shaders throw the Slang diagnostic, and
  the output reflects cleanly through ShadowDusk's `SpirvReflector` — TintShader's
  `TintColor` cbuffer member matches the DXC `-fvk-use-dx-layout` oracle byte-for-byte.
- ⚠️ **Full mode-2 render not yet verified in a browser.** The dev environment that
  produced this has no browser, so the actual cat render is a **manual run** step
  (below) and a Phase 30 headless-browser CI item — not an automated check here.
- ⚠️ **Two DXC flags can't be forwarded to Slang's WASM API.** The `slang-wasm`
  embind surface only exposes `createSession(targetEnum)` (no `CompilerOptionEntry`
  pass-through), so `-fvk-use-dx-layout` and `-fvk-use-entrypoint-name` are dropped.
  Neither affects the SM3 PS corpus: Slang defaults to row-major (== `-Zpr`) and the
  lone `float4` cbuffer member packs identically; the SPIR-V entry point is named
  `main` (not `MainPS`), but `SpirvReflector` keys on types/decorations, not the
  entry name. Shaders with mixed scalar/matrix cbuffer packing could differ — track
  this if the corpus grows.
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
3. Click **Compile & Apply** to exercise the in-browser compile path. The
   HLSL→SPIR-V step now runs the real Slang WASM compiler; the SPIR-V→GLSL step
   and KNI MGFX load determine whether the final render appears (see the load
   risk above). On any failure the error panel shows the verbatim diagnostic.

## Verify the HLSL→SPIR-V frontend (Slang WASM)

The frontend (`wwwroot/shadowdusk-dxc.js`) is **node-testable without a browser**
— it is a plain ES module and Slang's WASM runs under node ≥ v18:

```bash
node - <<'EOF'
import { pathToFileURL } from 'node:url';
const url = pathToFileURL('samples/ShaderFiddle.Web/wwwroot/shadowdusk-dxc.js').href;
const { compileToSpirv, ensureReady } = await import(url);
await ensureReady();                                   // loads slang-wasm once
const hlsl = `
Texture2D SpriteTexture; SamplerState SpriteTextureSampler;
struct VSOut { float4 Position: POSITION; float4 Color: COLOR0; float2 UV: TEXCOORD0; };
float4 MainPS(VSOut i) : SV_Target {
  float4 c = SpriteTexture.Sample(SpriteTextureSampler, i.UV) * i.Color;
  c.rgb = (c.r + c.g + c.b) / 3.0f; return c; }`;
const spv = compileToSpirv(hlsl, ['-E','MainPS','-T','ps_5_0','-spirv',
  '-fvk-use-dx-layout','-auto-binding-space','1','-Zpr','-WX']);
const magic = (spv[0]|(spv[1]<<8)|(spv[2]<<16)|(spv[3]<<24))>>>0;
console.log('SPIR-V bytes', spv.length, 'magic 0x'+magic.toString(16)); // expect 0x7230203
EOF
```

This is the same `(hlslSource, args)` contract `ShadowDusk.Wasm`'s
`[JSImport("compileToSpirv","shadowdusk-dxc")]` calls. The emitted SPIR-V was
confirmed to reflect cleanly through the real `SpirvReflector` (textures,
samplers, and the `TintColor` cbuffer member all match the DXC oracle).

### Browser verification (manual, no automation here)

1. `dotnet run` (above); open the printed URL. The page boots in **mode 1** (cat
   via precompiled `.mgfx`) — the ~21 MB `slang-wasm.wasm` is **not** downloaded
   yet (the frontend loads it lazily, off the boot path).
2. Open DevTools → Network. Click **Compile & Apply**. On the **first** click you
   should see `slang/slang-wasm.wasm` fetched once (status "Compiling in-browser…"
   stays up during the download), then the compile runs.
3. **Expected with the current pieces:** the HLSL→SPIR-V step succeeds. Whether the
   cat re-renders depends on the SPIR-V→GLSL module and the KNI MGFX-load path; if
   either is incomplete the error panel shows the exact stage's diagnostic (the
   frontend never fakes success). Subsequent compiles reuse the cached compiler (no
   re-download).
4. To confirm the frontend in isolation in the browser console:
   `await (await import('./shadowdusk-dxc.js')).ensureReady()` should resolve, and
   a `compileToSpirv(src, ['-E','MainPS','-T','ps_5_0'])` call should return a
   `Uint8Array` beginning `03 02 23 07`.

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
| `wwwroot/shadowdusk-dxc.js` | **host-side** ES module registered via `JSHost.ImportAsync` for `ShadowDusk.Wasm`'s `[JSImport("compileToSpirv"/"ensureReady","shadowdusk-dxc")]` contract. The **real** HLSL→SPIR-V compiler, backed by Slang WASM (`wwwroot/slang/`); `ensureReady()` lazy-loads it, `compileToSpirv(hlsl,args)` returns SPIR-V bytes. Throws on failure (→ SD1900). |
| `wwwroot/slang/slang-wasm.{js,wasm,d.ts}` | Prebuilt **Slang v2026.10** WebAssembly compiler (HLSL→SPIR-V) + embind loader + TS types. Provenance & re-fetch recipe in `wwwroot/slang/RESTORE.md`. |
| `wwwroot/shadowdusk-spirv-cross.js` | **host-side** ES module for the `[JSImport(...,"shadowdusk-spirv-cross")]` SPIR-V→GLSL contract (owned separately). Registering it (even when stubbed) is what makes a failure *graceful* — calling an **unregistered** `[JSImport]` module aborts the whole .NET WASM runtime and crashes the page. |
| `wwwroot/cat.jpg` | the standard cat image |
| `wwwroot/shaders/OpenGL/*.mgfx` | precompiled mode-1 corpus bytes |
| `wwwroot/shaders/src/*.fx` | corpus sources shown in the editor |
