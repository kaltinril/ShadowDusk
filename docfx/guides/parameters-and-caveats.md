# Parameters & Caveats

A grab-bag of the practical things to know when compiling with ShadowDusk.

## The library vs. CLI default target

This bites people, so it's first:

| Surface | Default target |
|---|---|
| Library — <xref:ShadowDusk.Core.CompilerOptions.Target> | **`OpenGL`** |
| CLI — `mgfxc /Profile` | **`DirectX_11`** |

Always set the target explicitly. The CLI default matches `mgfxc` (`DirectX_11`); the library default favors the cross-platform OpenGL path.

## Global parameter initializers are not baked in

A global with an initializer, e.g.:

```hlsl
float FishEyeAmount = 0.35;
```

…does **not** carry `0.35` into the `.mgfx`. DXC (unlike `fxc`/`mgfxc`) drops global cbuffer initializers, so the stored default is `0`. The effect renders as identity until you either:

- `SetValue` the parameter from code after loading the `Effect`, or
- inline the value as a literal in the shader instead of a named global.

This is a known fidelity gap versus `mgfxc` for the *initializer* specifically (it did not affect the validated SM3 corpus). Setting parameters by name at runtime is the recommended pattern regardless.

## DirectX uses `vkd3d-shader`, not DXC

For `Target = DirectX`, ShadowDusk emits DXBC (SM ≤ 5) — what MonoGame's DX11 runtime loads — via `vkd3d-shader` (cross-platform) or `d3dcompiler_47` (Windows-only oracle), selected by <xref:ShadowDusk.Core.CompilerOptions.DxbcBackend>. **DXC is not used for DX11** because it only emits SM6 DXIL. See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md).

## MGFX format version

Output defaults to **MGFX v10** (<xref:ShadowDusk.Core.CompilerOptions.MgfxVersion> / CLI `--mgfx-version`). v10 loads in MonoGame 3.8.x DesktopGL/WindowsDX and KNI (Reach and HiDef). Valid values are **10** and **11** only — ShadowDusk does not emit the older v9 (pre-3.8.2) format. Stay on v10 for broad backward compatibility; only change it if you have a specific runtime that requires v11.

The version is **not** forward/backward tolerant: a runtime checks the byte and **rejects a mismatch** — a v11 `.mgfx` will not load in a v10 runtime, and v10 is what every shipping MonoGame 3.8.x / KNI build reads today. That's why v10 is the default; pick v11 only when you know the target runtime requires it.

## `.mgfx` vs `.xnb`

ShadowDusk produces a raw **`.mgfx`** blob — the compiled effect — **not** an `.xnb`. The `.xnb` is the Content Pipeline's *container* that wraps a `.mgfx` (along with a type-reader header), and it's what `Content.Load<Effect>("name")` reads. The raw `.mgfx` is what the `new Effect(graphicsDevice, mgfxBytes)` constructor reads directly.

- Loading effects with **`new Effect(gd, bytes)`** → use ShadowDusk's `.mgfx` as-is.
- Loading via **`Content.Load<Effect>`** → you need an `.xnb`. Build the `.fx` through MGCB (see [MGCB Content Pipeline](mgcb-content-pipeline.md)) so the pipeline wraps ShadowDusk's `.mgfx`, or wrap the bytes yourself.

This matches `mgfxc`, which also emits the raw effect and leaves `.xnb` wrapping to the content pipeline. See [Choosing a Target](choosing-a-target.md#mgfx-vs-xnb).

## KNI HiDef / WebGL2

A single ShadowDusk `.mgfx` loads in both KNI **Reach** (WebGL1) and **HiDef** (WebGL2 / GLSL ES 3.00) — no profile flag, no separate build. KNI's runtime converts the legacy GLSL to ES 3.00 at load, and ShadowDusk emits the `#define`-aliased fragment output that converter expects. HiDef shader loading needs a recent KNI (the release that added the runtime converter); Reach and desktop GL have no version requirement. After upgrading ShadowDusk, **recompile your `.fx`** — an old `.mgfx` keeps the old output.

## Includes

Pass extra include search paths with the CLI `/I <path>` (repeatable) or the library's <xref:ShadowDusk.Core.CompilerOptions.AdditionalIncludePaths> / a custom <xref:ShadowDusk.Core.Preprocessor.IIncludeResolver>. Missing or circular includes fail loudly with `SD0001` / `SD0002` diagnostics.

## Errors fail loudly

Compilation returns `Result<CompiledShader, ShaderError[]>`. Each <xref:ShadowDusk.Core.ShaderError> carries the file, line, column, code, and message exactly as the underlying compiler emitted them; `FxcFormattedMessage` renders the MGCB-parseable form. No exceptions are thrown for expected shader failures.

## Validating a `.fx` (no render needed)

Because failures come back as data, `CompileAsync` doubles as a **validator/linter**: compile the source, ignore the `.mgfx` bytes on success, and read `ShaderError[]` on failure for the line, column, and message. No graphics device, no `Effect` load, no render required — handy for IDEs, build checks, and shader linters.

```csharp
var result = await compiler.CompileAsync(fxSource, options);
if (result.IsError)
    foreach (var e in result.Error)
        Console.WriteLine($"{e.File}({e.Line},{e.Column}): {e.Code}: {e.Message}");
```

This works identically on desktop and **in the browser** — as of 0.2.0 the WASM path reports the same `Line`/`Column` as desktop, so an in-browser editor (e.g. a KNI/Blazor shader fiddle) can highlight the offending line. It's a real compile (not a parse-only pass), so it surfaces exactly what the shipping pipeline would reject.
