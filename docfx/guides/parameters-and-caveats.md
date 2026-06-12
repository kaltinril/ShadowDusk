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

## Advanced texture intrinsics compile on Windows only (for now)

A small family of advanced **OpenGL-target** texture intrinsics — **3D-texture sampling (`tex3D`), explicit-LOD sampling (the `tex2Dlod` family), and gradient sampling (the `tex2Dgrad` family)** — currently compiles on the Windows DXC native but **fails to compile on the Linux/macOS DXC builds**, even though all hosts pin the same DXC version. If your `.fx` uses these and you compile on Linux/macOS, expect a compile error (a loud diagnostic, never a miscompile); compiling the same shader on Windows works. The ordinary texture corpus (`tex2D`, multi-texture, samplers, render targets, …) is unaffected and compiles identically on all three OSes. This per-OS divergence is a known, tracked gap under investigation — it will be fixed in the compiler, not papered over here.

## Vertex-stage texture fetch is rejected on the OpenGL target

Sampling a texture **in a vertex shader** (e.g. displacement mapping via `SampleLevel`/`tex2Dlod` in the VS) fails loudly with **`SD0210`** for `Target = OpenGL`. This is deliberate: MonoGame 3.8.2's OpenGL runtime cannot bind vertex textures at all — its GL program link assigns texture units only for the *pixel* shader's sampler records, and there is no GL `VertexTextures` apply path — so any compiled output would silently sample whatever happens to sit on texture unit 0 (typically rendering black). A loud compile error is the only honest result. Move the fetch to the pixel stage, or pass the data through a uniform/vertex stream. The DirectX and FNA targets are unaffected by this MonoGame-GL-runtime limitation.

## Uniform types on the OpenGL target (cbuffers, arrays, staged limits)

Free uniforms, named `cbuffer`s (including one shared by both shader stages, or several in one stage), and **array uniforms** (`float4 Colors[4]`, `float4x4 Bones[N]`, `float`/`float2`/`float3` arrays) are fully modelled: array parameters expose their elements in `Effect.Parameters` — `Parameters["Colors"].SetValue(Vector4[])` and `Parameters["Colors"].Elements[i]` work exactly as with `mgfxc` output, on every target.

Two uniform shapes are **rejected loudly** (`SD0210`) on the OpenGL target rather than silently miscompiled (staged scope, Phase 43C):

- **`int` / `bool` (and `intN`/`boolN`) uniforms** — MojoShader models these in separate `{vs,ps}_uniforms_ivec4`/`_bool` register sets that ShadowDusk does not emit yet. Use a `float`-typed uniform and cast inside the shader.
- **Non-`float4x4` matrices (`float3x3`, `float2x2`, …) and `struct` uniforms** — pad the matrix to `float4x4` or split it into vectors.

Both shapes compiled silently into broken GLSL before Phase 43C (failing only at `Effect`-load time inside the game); the loud error is the honest replacement until the shapes are modelled.

## DirectX uses `vkd3d-shader`, not DXC

For `Target = DirectX`, ShadowDusk emits DXBC (SM ≤ 5) — what MonoGame's DX11 runtime loads — via `vkd3d-shader` (cross-platform) or `d3dcompiler_47` (Windows-only oracle), selected by <xref:ShadowDusk.Core.CompilerOptions.DxbcBackend>. **DXC is not used for DX11** because it only emits SM6 DXIL. See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md).

## MGFX format version

ShadowDusk produces **MGFX v10** (<xref:ShadowDusk.Core.CompilerOptions.MgfxVersion> / CLI `--mgfx-version`). v10 is the one effect container that **both MonoGame (3.8.x DesktopGL/WindowsDX) and KNI (Reach and HiDef) load** — MonoGame reads it directly and KNI reads it as its supported migration format. ShadowDusk does not emit the older v9 (pre-3.8.2) format.

`--mgfx-version` / <xref:ShadowDusk.Core.CompilerOptions.MgfxVersion> sets the format-version **byte** in the header. It is an escape hatch, **defaults to 10, and 10 is the value ShadowDusk produces and validates** — there is no need to set it. A runtime checks that byte and **rejects a version it does not recognise**, which is exactly why v10 (the universally-loaded version) is the default. Setting the byte to another value does not by itself produce that runtime's newer container layout, so leave it on 10.

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
if (result.IsFailure)
    foreach (var e in result.Error)
        Console.WriteLine($"{e.File}({e.Line},{e.Column}): {e.Code}: {e.Message}");
```

This works identically on desktop and **in the browser** — as of 0.2.0 the WASM path reports the same `Line`/`Column` as desktop, so an in-browser editor (e.g. a KNI/Blazor shader fiddle) can highlight the offending line. It's a real compile (not a parse-only pass), so it surfaces exactly what the shipping pipeline would reject.
