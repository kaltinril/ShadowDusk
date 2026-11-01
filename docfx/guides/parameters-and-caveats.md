# Parameters & Caveats

A grab-bag of the practical things to know when compiling with ShadowDusk.

## The library vs. CLI default target

This bites people, so it's first:

| Surface | Default target |
|---|---|
| Library — <xref:ShadowDusk.Core.CompilerOptions.Target> | **`OpenGL`** |
| CLI — `mgfxc /Profile` | **`DirectX_11`** |

Always set the target explicitly. The CLI default matches `mgfxc` (`DirectX_11`); the library default favors the cross-platform OpenGL path.

## Global parameter initializers become the parameter's default

A global with an initializer, e.g.:

```hlsl
float FishEyeAmount = 0.35;
```

…carries `0.35` into the `.mgfx` (and `.knifx`) as the parameter's default value, on every target. The effect has that value the moment you load it, before any `SetValue`. DXC drops the initializer, so ShadowDusk reads it from your source (after macros, `#if` and any defines are applied) and writes it into the file itself.

This is deliberately **better than `mgfxc`**, not the same: `mgfxc`'s OpenGL build stores zero for every initializer, and its DirectX 11 build loses array elements and half of a `float2x2`. ShadowDusk keeps scalars, vectors, matrices, `int`, `bool` and every element of an array.

ShadowDusk evaluates literals, arithmetic, casts, constructors such as `float4(1, 0, 0, 1)`, brace lists, arrays, earlier `const` values and a few math functions. If an initializer is something else (a call to your own function, say), the compile still succeeds and warns with `SD0017` naming the variable; its default is then zero until you `SetValue` it. Setting parameters by name at runtime always works.

## Advanced texture intrinsics compile on Windows only (for now)

A small family of advanced **OpenGL-target** texture intrinsics — **3D-texture sampling (`tex3D`), explicit-LOD sampling (the `tex2Dlod` family), and gradient sampling (the `tex2Dgrad` family)** — currently compiles on the Windows DXC native but **fails to compile on the Linux/macOS DXC builds**, even though all hosts pin the same DXC version. If your `.fx` uses these and you compile on Linux/macOS, expect a compile error (a loud diagnostic, never a miscompile); compiling the same shader on Windows works. The ordinary texture corpus (`tex2D`, multi-texture, samplers, render targets, …) is unaffected and compiles identically on all three OSes. This per-OS divergence is a known, tracked gap under investigation — it will be fixed in the compiler, not papered over here.

## DirectX 12 must be compiled on Windows (for now)

DXC's DXIL validation-and-signing step runs through `dxil.dll`, which is **Windows-only**. A `DirectX12` compile on Linux or macOS therefore emits **unsigned DXIL**: it loads only with Windows Developer Mode enabled, and **retail D3D12 rejects it at pipeline-state creation**.

Unlike the intrinsics gap above, this one does **not** fail at compile time — the compile succeeds and emits the **`SD0214`** warning, so a build that ignores warnings will produce an artifact that is broken only on the consumer's machine. Build DX12 content on Windows until cross-platform signing ships. See [DirectX 12](../backends/directx12.md) for detail. Other targets (DX11, OpenGL, FNA) are unaffected.

## Vertex-stage texture fetch is rejected on the OpenGL target

Sampling a texture **in a vertex shader** (e.g. displacement mapping via `SampleLevel`/`tex2Dlod` in the VS) fails loudly with **`SD0210`** for `Target = OpenGL`. This is deliberate: neither MonoGame's nor KNI's OpenGL runtime can bind vertex textures — MonoGame's GL program link assigns texture units only for the *pixel* shader's sampler records (there is no GL `VertexTextures` apply path), and KNI's GL backend likewise sets only the pixel shader's sampler uniforms — so any compiled output would silently sample whatever happens to sit on texture unit 0 (typically rendering black). `mgfxc`'s own OpenGL build of such a shader fails too. A loud compile error is the only honest result. Move the fetch to the pixel stage, or pass the data through a uniform/vertex stream. The DirectX (11 and 12), Vulkan and FNA targets are unaffected by this GL-runtime limitation; vertex texture fetch is render-proven on DirectX 11, DirectX 12 and Vulkan.

## The `compile <target>` profile and the `#if OPENGL` header

A technique pass names its shaders with `compile <target> Entry()`, where `<target>` is the shader profile:

```hlsl
pass P0
{
    VertexShader = compile VS_SHADERMODEL MainVS();
    PixelShader  = compile PS_SHADERMODEL MainPS();
}
```

`VS_SHADERMODEL` / `PS_SHADERMODEL` are not built-in — they come from the standard MonoGame cross-platform header, which picks the right profile per backend:

```hlsl
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif
```

The header expresses one split twice: the **profile** tokens (legacy SM3 on OpenGL, the feature-level-9 SM4 forms on DirectX) and the **position semantic** (`SV_POSITION` is a real Shader Model 4+ system-value on the DirectX side, so it is used as-is; on the SM3 OpenGL side it is mapped back to the D3D9-era `POSITION`).

ShadowDusk validates the `compile` target exactly as `mgfxc`/`fxc` do: after macro expansion the token must resolve to a recognized profile. If it does not, the compile fails loudly with **`SD0013`** instead of silently falling back to SM3. The two ways to trip it:

- A **typo** in the profile token (`compile A MainPS();`).
- A **`*_SHADERMODEL` macro that is not defined** — most often because the `#if OPENGL … #else …` header above was removed or never added. Add the header (or `#define` the profile directly, e.g. `compile ps_3_0 MainPS();`).

This is the same input `mgfxc` rejects with `unrecognized compiler target`; ShadowDusk surfaces it at compile time so the shader cannot silently ship broken.

### `SD0015` — a real profile that is still too low for the DirectX target

A profile can be perfectly recognized and *still* be one the target's reference compiler refuses. MonoGame's `DirectX_11` shader profile accepts **only**:

```
vs_4_0_level_9_1   vs_4_0_level_9_3   vs_4_0   vs_4_1   vs_5_0
ps_4_0_level_9_1   ps_4_0_level_9_3   ps_4_0   ps_4_1   ps_5_0
```

Anything else — every SM 1–3 profile, `*_4_0_level_9_0`, and (counter-intuitively) **every SM6 profile such as `ps_6_0`** — makes `mgfxc /Profile:DirectX_11` fail with *"Invalid profile 'ps_3_0'. Pixel shader 'MainPS' must be SM 4.0 level 9.1 or higher!"*. ShadowDusk reports the same condition as **`SD0015`**.

This is the shape you hit when a shader written for DesktopGL or FNA — `PixelShader = compile ps_3_0 MainPS();` with no cross-platform header — is built for `WindowsDX`. **The fix is the header above:** add the `#if OPENGL … #else …` block so the DirectX arm defines the `*_4_0_level_9_1` pair. Nothing else in the shader needs to change.

The floor is **per target**, and only the DirectX one is enforced today: OpenGL caps at SM 3.0 and Vulkan requires exactly `*_6_0`, so a profile that is wrong for DirectX is often exactly right for another target. Do not "fix" an `SD0015` by raising the profile for every backend.

**KNIFX on DirectX follows KNI, not `mgfxc`.** KNI's own DirectX pipeline also accepts `vs_2_0`, `ps_2_0`, `vs_3_0` and `ps_3_0`: it compiles them as `*_4_0_level_9_1` and `*_4_0_level_9_3`. A KNIFX DirectX compile does the same, so an XNA-era `compile ps_2_0` effect builds for KNI WinForms.DX11 with no header change. `ps_3_0` records shader model 3.0, so it loads on HiDef but not on Reach, exactly as KNI's own build. The profiles KNI refuses (`ps_2_a`, `ps_2_b`, the `*_sw` profiles, SM1, `*_4_0_level_9_0`, SM6) are still `SD0015`. Two DirectX gaps that affected these effects are fixed as of 0.23.0:

- **Legacy samplers' textures take `mgfxc`'s texture slots on DirectX 11.** fxc (and so `mgfxc` and KNI's pipeline) gives a legacy sampler's texture (`sampler2D A;`, a `sampler_state` block, read with `tex2D`) the `tN` slot of the shader's first read of it, so a `texture` declared above the `sampler … : register(s0)` SpriteBatch's sprite reaches no longer lands on `t0`. ShadowDusk checks each such shader after its compile and, when the slots differ, recompiles it with each texture pinned to `mgfxc`'s slot. When it cannot prove the order (a read under a condition fxc might fold away, a texture declared through a macro, a legacy sampler passed to a function, and the other shapes listed under `SD0230` in the [diagnostic codes](../diagnostics.md)), it keeps the declaration-order slots earlier releases used and warns **`SD0230`**; declaring samplers and their textures in slot order (`s0` first) keeps such a shader correct.
- **`half` globals compile on DirectX** as the `float` declaration, as `mgfxc` builds them (see [`half` uniforms are `float` uniforms](#half-uniforms-are-float-uniforms) below).

ShadowDusk compiles every DirectX 11 shader at SM5, so it is more lenient than KNI's fxc compile at `*_4_0_level_9_1` / `_9_3`: an effect that builds here can still fail in KNI's own content pipeline if it exceeds the feature-level-9 instruction or dependent-read limits, reads `VPOS` or `VFACE`, uses `ddx`/`ddy` at `ps_2_0`, or writes the profile in upper case (`PS_2_0`, which KNI's case-sensitive check refuses).

### KNIFX: the shader model KNI checks

A `.knifx` container records each shader's model, and KNI's runtime refuses a shader whose model is above the graphics profile's ceiling (Reach 2.0, HiDef 3.0, FL10_0 4.0, FL11_0 5.0). ShadowDusk records the model the way KNI's own content pipeline does for the feature-level-9 profiles: `*_4_0_level_9_1` is **2.0** and `*_4_0_level_9_3` is **3.0**, so the standard MonoGame header (`#define PS_SHADERMODEL ps_4_0_level_9_1`) loads on Reach and HiDef. Writing `ps_4_0` (no feature level) records 4.0, which KNI only accepts from `FL10_0` up, exactly as it does for its own build. `ps_2_0` and `ps_3_0` record 2.0 and 3.0 on both targets. SM6 profiles (`vs_6_x`/`ps_6_x`) are refused with `SD0015` on the KNIFX container on OpenGL as on DirectX, matching KNI's own pipeline: KNI's runtime refuses a shader recording 6.0 on every graphics profile ([issue #439](https://github.com/kaltinril/ShadowDusk/issues/439)). Use `*_4_0_level_9_x`, `*_4_0`, `*_4_1`, `*_5_0` or `*_2_0`/`*_3_0`; the GLSL does not depend on the profile. A few other profiles KNI's own pipeline refuses (`*_4_0_level_9_0`, `ps_2_a`, the `*_sw` profiles, SM1) still compile on the OpenGL KNIFX target and record 4.0, 3.0 or 1.x (on DirectX they are `SD0015`).

## Uniform types on the OpenGL target (cbuffers, arrays, staged limits)

Free uniforms, named `cbuffer`s (including one shared by both shader stages, or several in one stage), and **array uniforms** (`float4 Colors[4]`, `float4x4 Bones[N]`, `float`/`float2`/`float3` arrays) are fully modelled: array parameters expose their elements in `Effect.Parameters` — `Parameters["Colors"].SetValue(Vector4[])` and `Parameters["Colors"].Elements[i]` work exactly as with `mgfxc` output, on every target.

Two uniform shapes are **rejected loudly** (`SD0210`) on the OpenGL target rather than silently miscompiled (a staged scope — these shapes are not modelled on the GL path yet):

- **`int` / `bool` (and `intN`/`boolN`) uniforms** — MojoShader models these in separate `{vs,ps}_uniforms_ivec4`/`_bool` register sets that ShadowDusk does not emit yet. Use a `float`-typed uniform and cast inside the shader.
- **Non-`float4x4` matrices (`float3x3`, `float2x2`, …) and `struct` uniforms** — pad the matrix to `float4x4` or split it into vectors.

The loud error is the honest result until these shapes are modelled — far better than the silent broken GLSL they would otherwise produce (which fails only at `Effect`-load time inside the game).

## `half` uniforms are `float` uniforms

An XNA-era uniform declared with a `half` type (`half4 Tint = half4(1, 0.5, 0, 1);`, or any `half`, `halfN`, `halfNxM` global or cbuffer member) compiles as the `float` declaration on DirectX 11, DirectX 12, Vulkan, OpenGL and KNIFX: a `Single` parameter of the same shape, with the same default, set with the same `SetValue` overload. That is what `mgfxc`'s DirectX 11 build does (fxc's backwards-compatibility flag). `half` locals and function parameters compile everywhere.

The one exception is **FNA**: its reference compiler, `fxc /T fx_2_0`, refuses a `half` global (`X3650 ... global variables cannot use the 'half' type`), so ShadowDusk refuses it too, with vkd3d's `E5002` at the declaration. Declare the uniform as `float`.

## DirectX uses `vkd3d-shader`, not DXC

For `Target = DirectX`, ShadowDusk emits DXBC (SM ≤ 5) — what MonoGame's DX11 runtime loads — via `vkd3d-shader` (cross-platform) or `d3dcompiler_47` (Windows-only oracle), selected by <xref:ShadowDusk.Core.CompilerOptions.DxbcBackend>. **DXC is not used for DX11** because it only emits SM6 DXIL. See [DirectX DXBC (vkd3d) Path](../architecture/directx-dxbc-vkd3d.md).

## Effect container: MGFX v10 (default), and opt-in MGFX v11 / KNIFX v11 / KNIFX v12

ShadowDusk produces **MGFX v10** by default (<xref:ShadowDusk.Core.CompilerOptions.MgfxVersion> defaults to 10; CLI `--mgfx-version`). v10 is the one effect container that **both MonoGame (3.8.x DesktopGL/WindowsDX) and KNI (Reach and HiDef) load** — MonoGame reads it directly and KNI reads it as its supported migration format. It is the **seamless default**: you never set a flag to get correct output, and it loads on every MonoGame 3.8.1.263+ and KNI runtime. 3.8.1.263 is the *measured* floor: every stable `MonoGame.Framework.DesktopGL` release was probed and 3.8.0.1641 is the one that rejects v10 (its loader predates the format). ShadowDusk does not emit the older v9 format.

Two **opt-in, experimental** newer containers are available for consumers targeting newer runtimes. Both are **additive** — the v10 default is unchanged, and you only get them if you ask:

- **MGFX v11** — set <xref:ShadowDusk.Core.CompilerOptions.MgfxVersion> `= 11` (CLI `--mgfx-version 11`). A faithful MonoGame v11 container for **MonoGame 3.8.5+**. v11 adds two per-shader diagnostic strings (the source file + entry point, used only in shader error messages, MonoGame PR #8813); it renders identically to v10. The source-file string is <xref:ShadowDusk.Core.CompilerOptions.SourceFileName> exactly as you pass it (what `mgfxc` does with its own argument), so an absolute path ends up in the effect; set <xref:ShadowDusk.Core.CompilerOptions.EmbeddedSourceFileName> to store a different string while diagnostics keep the real path. ShadowDusk's MGCB plugin and Content Builder processor store `<unknown>`, as MonoGame's stock processor does. DirectX 12 and Vulkan are always v11, so this applies to them without any opt-in. Render-proven in real MonoGame 3.8.5. *(v11 stays opt-in: v10 loads in every MonoGame from 3.8.1.263 onward and in KNI, whereas v11 requires 3.8.5+.)*
- **KNIFX v11** — set <xref:ShadowDusk.Core.CompilerOptions.Container> `= EffectContainer.Knifx`. KNI's newer KNIFX container for **KNI v4.02+**, render-proven in real KNI v4.2.9001. `MgfxVersion` is ignored when `Container == Knifx`; `Container` is ignored for the `Fna` target (always D3D9 fx_2_0).
- **KNIFX v12** — use <xref:ShadowDusk.Core.CapabilityProfile.KniGL_4_03> (CLI `--target-runtime kni-knifx-v12`). The same KNIFX body under container version 12, for **KNI 4.3.x only** (4.2.x rejects it); it avoids KNI 4.3's "older version" warning on v11. Render-proven in real KNI 4.3.9001.
  One parameter difference follows KNI's own pipeline: in `.knifx` a legacy sampler that binds no texture (`sampler s0;`, `sampler2D S : register(s0);`, or a `sampler_state` without `Texture =`) is **no effect parameter**, so `effect.Parameters["s0"]` is `null`. Bind the texture through the device (`GraphicsDevice.Textures[n] = texture`; SpriteBatch already binds unit 0), or declare the texture in the `.fx` (`Texture2D Tex; sampler2D s0 = sampler_state { Texture = (Tex); };`) and set `Parameters["Tex"]`, which works in both containers. The default MGFX v10 container keeps `mgfxc`'s parameter named after the sampler.

To pick a whole target (backend + container) with one value instead of setting these separately, set `CompilerOptions.Profile` to a `CapabilityProfile` (e.g. `CapabilityProfile.KniGL_4_02`), or use the CLI `--target-runtime` flag — a profile overrides `Target` / `Container` / `MgfxVersion`. See [Choosing a Target](choosing-a-target.md).

> **Important:** `--mgfx-version 11` writes a correct, full v11 body (not merely a bumped header byte over a v10 body). Still, **leave the default (v10) unless you specifically target a newer runtime and want its container** — v10 loads everywhere.

## Anonymous passes and techniques are named `""`

A `pass { ... }` or `technique { ... }` written without a name is named with the empty string, as `mgfxc`, KNI's pipeline and `fxc` name it, in every container (MGFX, KNIFX, FNA's fx_2_0): `EffectPass.Name` and `EffectTechnique.Name` read `""`. Look an anonymous pass up by index (`technique.Passes[0]`, or iterate `technique.Passes`), or give it a name in the `.fx`. Releases before 0.23.0 synthesized `P0`, `P1`, ... for anonymous passes (FNA reported an anonymous technique's name as `null`), so `technique.Passes["P0"]` now returns `null`. Two anonymous techniques in one effect, and an anonymous pass beside a pass named `P0`, compile, as they do with `mgfxc`.

## `.mgfx` vs `.xnb`

ShadowDusk's compiled effect is a raw **`.mgfx`** blob. The `.xnb` is the Content Pipeline's *container* that wraps a `.mgfx` (along with a type-reader header), and it's what `Content.Load<Effect>("name")` reads. The raw `.mgfx` is what the `new Effect(graphicsDevice, mgfxBytes)` constructor reads directly. ShadowDusk produces **either**:

- Loading effects with **`new Effect(gd, bytes)`** → use ShadowDusk's `.mgfx` as-is.
- Loading via **`Content.Load<Effect>`** → you need an `.xnb`, and ShadowDusk writes it directly: call `result.Value.ToXnb()`, or give the CLI an `.xnb` output path (the extension is the whole switch). The platform byte is derived from your target and the payload inside is the same `.mgfx` bytes. No MGCB involved — though for builds that already run MGCB for other content, the [MGCB Content Pipeline](mgcb-content-pipeline.md) plugin route also works.

(`mgfxc` itself only ever emits the raw effect and leaves `.xnb` wrapping to the content pipeline — the direct route is one thing ShadowDusk does that it doesn't.) See [Choosing a Target](choosing-a-target.md#mgfx-vs-xnb).

Proven at rung 4 on **MonoGame** (WindowsDX and DesktopGL), **KNI** (4.2.9001 and 4.3.9001, MGFX v10 and KNIFX payloads) and **FNA** (26.06): a real `ContentManager.Load<Effect>` on the ShadowDusk-written file renders pixel-identical to the reference build on every one of them (`validation/XnbContentLoad`, `XnbContentLoadGl`, `KniXnbContentLoad`, and the `.xnb` arm of `FnaValidation`).

**KNI ≤ 4.2.9001 cannot load a stock MonoGame-mgcb effect `.xnb`** — its `ContentTypeReaderManager` throws `FileLoadException: The given assembly name was invalid.` on the type-reader name mgcb writes (KNI 4.3.9001 catches it). ShadowDusk's `.xnb` loads on both, because it carries the XNA-4.0 reader name (`Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, …`), which is what KNI's own content pipeline and XNA write and the only name every MonoGame, KNI and FNA version resolves. One string everywhere; nothing to select. The CLI default profile is `DirectX_11`, so **pass `/Profile:OpenGL` for a KNI or DesktopGL game** — the wrong-profile messages, and what each means, are tabulated in [Drop-in `mgfxc`](dropin-mgfxc.md#replacing-the-content-pipeline-entirely-xnb-output).

## KNI HiDef / WebGL2

A single ShadowDusk `.mgfx` loads in both KNI **Reach** (WebGL1) and **HiDef** (WebGL2 / GLSL ES 3.00) — no profile flag, no separate build. KNI's runtime converts the legacy GLSL to ES 3.00 at load, and ShadowDusk emits the `#define`-aliased fragment output that converter expects. HiDef shader loading needs a recent KNI (the release that added the runtime converter); Reach and desktop GL have no version requirement. After upgrading ShadowDusk, **recompile your `.fx`** — an old `.mgfx` keeps the old output.

## Includes

Pass extra include search paths with the CLI `/I <path>` (repeatable) or the library's <xref:ShadowDusk.Core.CompilerOptions.AdditionalIncludePaths> / a custom <xref:ShadowDusk.Core.Preprocessor.IIncludeResolver>. Missing or circular includes fail loudly with `SD0001` / `SD0002` diagnostics.

## Errors fail loudly

Compilation returns `Result<CompiledShader, ShaderError[]>`. Each <xref:ShadowDusk.Core.ShaderError> carries the file, line, column, code, and message exactly as the underlying compiler emitted them; `FxcFormattedMessage` renders the MGCB-parseable form. No exceptions are thrown for expected shader failures.

## Extremely deep expressions

The native compilers (DXC, vkd3d-shader, SPIRV-Cross) recurse once per nesting level of the source, and a native stack overflow cannot be reported as a `ShaderError`: it ends the process. ShadowDusk therefore runs every native compiler call on its own worker thread with a 64 MB stack, whatever thread (and stack size) your code calls `Compile`/`CompileAsync` from. Measured on Windows x64, that moves the ceiling from about 2,000 terms in one additive chain (`x + x + ...`) to about 94,400, and `else if` chains and call chains of tens of thousands compile with time as the only limit. Only the native call moves: your `IIncludeResolver` and the rest of the pipeline run on the calling thread. Source deeper than even that still exits the process, since the overflow cannot be detected beforehand (see `project_facts.md` for the measured ceilings); bracketed nesting is capped earlier by DXC's own clean `-fbracket-depth` (256) diagnostic.

## Validating a `.fx` (no render needed)

Because failures come back as data, `CompileAsync` doubles as a **validator/linter**: compile the source, ignore the `.mgfx` bytes on success, and read `ShaderError[]` on failure for the line, column, and message. No graphics device, no `Effect` load, no render required — handy for IDEs, build checks, and shader linters.

```csharp
var result = await compiler.CompileAsync(fxSource, options);
if (result.IsFailure)
    foreach (var e in result.Error)
        Console.WriteLine($"{e.File}({e.Line},{e.Column}): {e.Code}: {e.Message}");
```

This works identically on desktop and **in the browser** — the WASM path reports the same `Line`/`Column` as desktop, so an in-browser editor (e.g. a KNI/Blazor shader fiddle) can highlight the offending line. It's a real compile (not a parse-only pass), so it surfaces exactly what the shipping pipeline would reject.
