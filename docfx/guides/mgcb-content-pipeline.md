# MGCB Content Pipeline

> [!IMPORTANT]
> **This page used to document a "Tier-1 `PATH` override" as the shipping MGCB integration. It does not work.**
> Measured on 2026-07-28 against `dotnet mgcb` **3.8.2.1105, 3.8.4.1, and 3.8.5**: with a real `mgfxc`
> executable placed first on `PATH`, a `.mgcb` content build **never invoked it once** and still produced a
> valid `.xnb`. **MGCB compiles `.fx` in-process** — it does not shell out to an external `mgfxc` — so
> there is nothing for a `PATH` alias to intercept. MonoGame 3.8.5's new code-centric Content Builder is a
> C# project over `MonoGame.Framework.Content.Pipeline` and has no external-tool seam either.
>
> **What to do instead — see [Using ShadowDusk with MGCB today](#using-shadowdusk-with-mgcb-today).**

## Why the override cannot work

MGCB's `EffectProcessor` compiles effects inside the build tool's own process (via SharpDX `D3DCompiler`
through 3.8.4.1, and bundled DXC + MojoShader native tool packages from 3.8.5). No `mgcb` or
`MonoGame.Framework.Content.Pipeline` assembly in any of those versions even contains the string `mgfxc`;
`MonoGame.Content.Builder.Task`'s MSBuild targets only ever exec `mgcb`. The `mgfxc` tool still exists — at
3.8.5 it moved into its own `dotnet-mgfxc` package — but MGCB does not call it.

The measurement was taken on Windows. The Linux/macOS behaviour is inferred from the identical package
payloads rather than run directly.

## Using ShadowDusk with MGCB today

ShadowDusk's CLI is still a faithful [drop-in `mgfxc` replacement](dropin-mgfxc.md) — the issue is purely
that MGCB has no hook to call it through. Two routes work now:

**1. Compile shaders with ShadowDusk directly, and let MGCB copy the result.** Build your `.fx` to `.mgfx`
with the ShadowDusk CLI as a pre-build step, then have the content project copy that `.mgfx` instead of
processing the `.fx`:

```sh
ShadowDuskCLI Content/MyEffect.fx Content/MyEffect.mgfx /Profile:OpenGL
```

```
#begin MyEffect.mgfx
/copy:MyEffect.mgfx
```

**2. Skip the content pipeline for shaders entirely** — compile at runtime and hand the bytes to `Effect`,
which is [what the library is for](../getting-started/overview.md):

```csharp
var result = await new EffectCompiler().CompileAsync(File.ReadAllText("MyEffect.fx"),
    new CompilerOptions { Target = PlatformTarget.OpenGL });
var effect = new Effect(GraphicsDevice, result.Value.Data);
```

Native in-process MGCB integration is tracked as the (unimplemented) content-processor plugin — see
[the stub note below](#the-mgcb-plugin-is-a-stub-future).

## The `.mgcb` `Profile` ↔ ShadowDusk mapping

MGCB passes the platform via `/Profile:`. ShadowDusk understands the MonoGame profile names:

| MGCB `/Profile:` | ShadowDusk target |
|---|---|
| `DirectX_11` | DirectX (DXBC SM5) |
| `DirectX_12` | DirectX 12 (SM6 DXIL), validated for MonoGame `WindowsDX12` (KNI has no DirectX 12 platform). **Build DX12 content on Windows** — DXIL signing needs the Windows-only `dxil.dll`, or the output is unsigned and retail D3D12 rejects it (`SD0214`); see [DirectX 12](../backends/directx12.md) |
| `OpenGL` | OpenGL / DesktopGL (GLSL) |
| `Vulkan` | Vulkan (SM6 SPIR-V), validated for MonoGame `DesktopVK` (KNI has no Vulkan platform) |

Unsupported console profiles (`PlayStation4`, `XboxOne`, `Switch`) fail loudly with exit code 1, just as a portable tool should.

These profile names are what the [ShadowDusk CLI](../cli/index.md) accepts, so they apply to the direct-compile
route above just as they did to the `mgfxc` invocation MGCB was believed to make.

## A worked sample

The repository's [`samples/mgcb`](../samples/mgcb.md) sample is a standard MonoGame content project over the
shader corpus. Note that its shaders build through **MGCB's own in-process compiler**, not ShadowDusk — see
the correction at the top of this page.

## The MGCB plugin is a stub (future)

A dedicated `ShadowDusk.MgcbPlugin` content-processor NuGet is **scaffolded but not implemented** — it is a stub with no working processor today. Since the `PATH` override turns out not to fire, this plugin is the only route to *native* in-process MGCB integration, which raises its priority from convenience to the real gap; it is still unimplemented, so do not expect a shipping package. Track its status in the [Contributing Guide](../contributing/index.md) and `plan/PHASE-29-mgcb-content-processor-plugin.md`.
