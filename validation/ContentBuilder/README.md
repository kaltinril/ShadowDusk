# validation/ContentBuilder — the MonoGame 3.8.5 Content Builder gate (Phase 63, issue #203)

**What it proves:** the `ShadowDusk.ContentPipeline` package works in the shape MonoGame 3.8.5 made
the template default — a C# `ContentBuilder` project the consumer owns — and that what comes out is
ShadowDusk's bytes in MonoGame's envelope, loadable through the unchanged `Content.Load<Effect>`,
rendering like the stock build. **A render gate AND a delivery-shape gate.**

One driver, one process, two halves:

1. **A real `ContentBuilder` subclass** (`MonoGame.Framework.Content.Pipeline` 3.8.5) builds the
   fixture set with **two content roots** — `stock` (MonoGame's own `EffectImporter`/`EffectProcessor`,
   the in-process 3.8.5 `mgfxc`-equivalent oracle, which writes MGFX **v11**) and `sd`
   (`ShadowDuskEffectImporter`/`ShadowDuskEffectProcessor` from the **library** project, passed as
   instances) — once per platform: `Windows` (Grayscale, VertexAndPixel, MultiTexture, SpriteEffect)
   and `DesktopGL` (the three GL-compilable ones; SpriteEffect is the known Phase 41 GAP-1 GL half).
   Per asset it asserts the `sd` payload is **byte-for-byte the `ShadowDuskCLI` binary's** for the
   platform's target, the `.xnb` envelope through the type id is **byte-for-byte the stock build's**,
   and the payloads **differ** (positive control).
   **`DesktopVK` and `WindowsDX12` passes** (Grayscale, VsTransformColorTexture: the fixtures with an
   `#if SM6` branch, which the stock 3.8.5 processor builds too) add the issue-#274 assertions. Those
   platforms are always MGFX v11, which stores a source-file string per shader, and the Builder hands
   the importer an absolute path. So per asset: every shader record's string is `<unknown>` and equals
   the stock build's; the `.xnb` names neither the source directory nor the file; the payload is the
   CLI's with **only** that string replaced (the CLI keeps writing the path it was passed, like
   `mgfxc`, which is asserted too); and a **second real Builder run from a different, deeper source
   directory** produces a byte-identical `.xnb`. Compile + byte assertions only (the render host is DX11).
2. **`Content.Load<Effect>` on MonoGame 3.8.5 WindowsDX** (3.8.5.1): both Windows `.xnb`s are
   loaded by asset name through a real `ContentManager`, rendered through the identical `SpriteBatch`
   path, and required to be **pixel-identical**. The render host must be 3.8.5 because the stock
   arm's payload is v11, which `validation/XnbContentLoad`'s 3.8.2.1105 runtime cannot load.

It is also the only place ShadowDusk's **real dependency graph is run through
`ContentBuilderHelper.LoadAssemblies`'s unguarded `Assembly.GetTypes()` scan** (the reason
`Vortice.Direct3D12` was dropped in Area A). `DependencyGraphScanTests` reproduces the walk under
`dotnet test`; this is the walk itself. The driver prints whether `Vortice.Direct3D12` is in the
AppDomain after the run (it must stay `False`).

## Run

```powershell
dotnet build src/ShadowDusk.Cli/ShadowDusk.Cli.csproj -c Release   # the byte-identity arm
dotnet run --project validation/ContentBuilder -c Release
```

or via `validation/run-windows-render-gates.ps1`, where it is **default-ON**. Windows + a DX11-capable
GPU. Not in `ShadowDusk.slnx` (it pulls MonoGame 3.8.5 and the content-pipeline tool packages).
Renders land in `validation/output-contentbuilder/` (gitignored).

## Measured (2026-09-09)

7/7 assets: Windows 4/4 payload == CLI `DirectX_11` + envelope == stock, DesktopGL 3/3 payload == CLI
`OpenGL` + envelope == stock, and 4/4 Windows assets `Content.Load<Effect>` + render **1,230,720 px
identical** to the stock 3.8.5 build on MonoGame 3.8.5.1 WindowsDX. The 3.8.2.1105-compiled pair
binds in the 3.8.5 process (the driver prints both versions).

**Issue #274 (2026-10-01):** 11/11 assets with the two v11 passes added (DesktopVK 2/2, WindowsDX12
2/2). Measured red before the fix (4 failures: the source-file string held the absolute path under
the temp work directory) and green after.

## Not covered here

- Rendering the `DesktopVK` / `WindowsDX12` Builder output: those passes are compile + byte
  assertions (the render host here is DX11), and only fixtures with an `#if SM6` branch have a stock
  oracle there (the stock 3.8.5 processor requires `vs_6_0`/`ps_6_0` for those targets). The payload
  bytes themselves are render-proven by the DX12 and Vulkan corpus gates. Recorded as a §7 gap in
  `docs/validation-matrix.md`.
- Linux/macOS: the Builder itself is cross-platform and the ShadowDusk arm would run there, but the
  render half needs a GL host on 3.8.5; the packed-package consumption on all three OSes is
  `pack-consume.yml`'s content-builder steps instead.
