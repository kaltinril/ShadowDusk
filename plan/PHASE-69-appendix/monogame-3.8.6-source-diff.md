# MonoGame 3.8.6: source diff against 3.8.5 / 3.8.5.1, read for ShadowDusk

**Researched 2026-10-31.** Source: `https://github.com/MonoGame/MonoGame`, blobless clone, tags
`v3.8.5` (2026-07-15), `v3.8.5.1` (2026-08-14), `v3.8.6` (GitHub release 2026-10-07; nuget.org
packages stamped 2026-10-07/08; blog post
[monogame.net/blog/2026-10-26-3.8.6-release-2026](https://monogame.net/blog/2026-10-26-3.8.6-release-2026/)).
The 3.8.6 release notes are the GitHub release body (`Full Changelog: v3.8.5.1...v3.8.6`).

**Tag topology.** `v3.8.5` is an ancestor of `v3.8.6`; `v3.8.5.1` is **not**. It is a hotfix
branch of 11 cherry-picks (Vulkan NormalizedShort SNORM, Vulkan RT sync, DX12 ring-buffer and
CommandListPool cleanup, WindowsDX RenderTargetCube MSAA, RT retention, the
`ContentBuilderProcessorContext` relative-OutputDirectory fix, and so on). All 11 are also on
`v3.8.6` as separate commits. `git diff v3.8.5 v3.8.6` covers 528 files (+20,361/-9,876, 58
commits). `git diff v3.8.5.1 v3.8.6` covers 509 files. Most of the line count is the
`[Pipeline] Warning Cleanup 1000` refactor (#9050): nullable annotations, primary constructors,
and file-scoped namespaces across `MonoGame.Framework.Content.Pipeline` and `mgfxc`. That refactor
causes the one real break below.

Labels: **[source]** means verified by reading the tag's source. **[binary]** means verified
against the published nupkg or ShadowDusk's own built DLLs. **[inferred]** means reasoned but not
measured.

---

## Impact on ShadowDusk (summary, ranked)

### BREAK: fix before claiming 3.8.6

1. **`ShadowDuskEffectImporter` throws `MissingMethodException` on MonoGame 3.8.6's content
   pipeline**, in both `ShadowDusk.MgcbPlugin` (`dotnet mgcb` 3.8.6) and `ShadowDusk.ContentPipeline`
   (a 3.8.6 Content Builder). **[source + binary, measured]**
   - **Cause.** #9050 turned `ContentIdentity` into a primary-constructor class
     (`ContentIdentity(string sourceFilename = "", string? sourceTool = "", string? fragmentIdentifier = null)`,
     `MonoGame.Framework.Content.Pipeline/ContentIdentity.cs:17` @ v3.8.6). That removed the
     `()`, `(string)` and `(string, string)` overloads. Only `.ctor(string, string, string)`
     remains in IL.
   - **Our call site.** `ShadowDuskEffectImporter.cs:48` calls
     `new ContentIdentity(fullPath, "ShadowDusk")`. Compiled against 3.8.2.1105, this is a
     MemberRef to `.ctor(string, string)` (signature blob `20-02-01-0E-0E`, read from the Release
     DLLs built 2026-10-29).
   - **Measurement.** A scratch probe loaded the real nupkg `MonoGame.Framework.Content.Pipeline.dll`
     3.8.5 and 3.8.6 plus ShadowDusk's Release plugin and library DLLs, then resolved every
     `Microsoft.Xna.*` MemberRef and invoked `Import()`:

     | Content.Pipeline | MonoGame MemberRefs resolved | `Import()` |
     |---|---|---|
     | 3.8.5 | 17/17 | OK, returns `EffectContent` |
     | 3.8.6 | 16/17 | `MissingMethodException: Method not found: 'Void Microsoft.Xna.Framework.Content.Pipeline.ContentIdentity..ctor(System.String, System.String)'` |

   - **The processor is unaffected.** `ShadowDuskEffectProcessor` binds completely on 3.8.6.
     The attribute constructors bind too: `ContentImporterAttribute(string[])` and
     `ContentProcessorAttribute()`.
   - **Who hits it.** Both documented routes use our importer: the MgcbPlugin README's
     `/importer:ShadowDuskEffectImporter`, and the ContentPipeline README's
     `new ShadowDuskEffectImporter()`. So every 3.8.6 consumer who follows our docs gets a failed
     content build. The consumer is not choosing 3.8.6 Content.Pipeline on purpose:
     `ShadowDusk.ContentPipeline` declares `>= 3.8.2.1105`, and a 3.8.6 Builder project
     references 3.8.6. This breaks the seamless directive.
   - **Workaround (not a fix).** Use MonoGame's stock `EffectImporter` with
     `ShadowDuskEffectProcessor`.
   - **Fix.** Call the three-argument constructor, `new ContentIdentity(fullPath, "ShadowDusk", null)`.
     It exists on every version from 3.8.2.1105 through 3.8.6, so the floor is unchanged.
   - **Guard.** Add a test that resolves every `Microsoft.Xna.*` MemberRef of both plugin
     assemblies against each supported `MonoGame.Framework.Content.Pipeline` (3.8.2.1105,
     3.8.4.1, 3.8.5, 3.8.6), using the same technique as the probe (`Module.ResolveMember` over
     the 0x0A table inside an `AssemblyLoadContext`). Today nothing can see this class of break
     until a real host runs.
   - **Same refactor, other plugins.** #9050 also removed `ContentImporterAttribute(string)`,
     the single-extension constructor (`ContentImporterAttribute.cs:13` @ v3.8.6, now only
     `(params string[])`). `PipelineManager` instantiates importer attributes
     (`GetCustomAttributes`) outside its try/catch, so a third-party plugin with a one-extension
     importer compiled against ≤3.8.5 crashes `mgcb` 3.8.6. Ours passes two extensions
     (`".fx", ".fxh"`), binds `(string[])`, and was measured OK. Upstream has no issue filed for
     either removal (searched 2026-10-31).

### NEEDS VALIDATION: no format change, but new or changed runtime behaviour

2. **New target: `DesktopGL4`, a native C++ OpenGL 4.1-core runtime (#9491).** It ships in new
   packages `MonoGame.Runtime.{Windows,Linux,Mac}.DesktopGL4` 3.8.6 on top of
   `MonoGame.Framework.Native`. It loads the classic **OpenGL-profile (byte 0) MGFX**: the same
   `.mgfx` our `PlatformTarget.OpenGL` emits, and the same `Effects/OpenGL` set MonoGame's own
   tests use. But it parses the GLSL in a new C++ path with **stricter, name-based contracts**
   than managed DesktopGL (§3.3). Any contract mismatch is a `MGGL_FAIL`, which is
   `MG_GENERATE_TRAP(); abort();`, a process crash and not an exception. It also compiles our
   no-`#version` GLSL verbatim in a **core** profile (4.1, falling back to 3.1). That is
   driver-dependent for `mgfxc`'s output too.
   - **What it means for us.** This is additive and seamless (same artifact). It needs a new
     rung-4 driver (`DesktopGL4` vs `mgfxc` OpenGL golden), and the
     `docs/validation-matrix.md` / `project_facts.md` cells per the support-surface rule.
3. **Managed DesktopGL changed how it applies an effect.** It derives a `FragmentOutputMask`
   from a GLSL substring scan (`gl_FragColor` / `gl_FragData[n]`) and masks MRT draw buffers to
   it (#9509). It also retries MSAA context creation (#9565) and disables the scissor test
   during `Clear` (#9501). ShadowDusk's GLSL uses `gl_FragColor`/`gl_FragData[N]` exactly like
   MojoShader, so this should be neutral. Re-run the GL gates and the ForwardCompat sweep with
   3.8.6 appended.
4. **DX12 and Vulkan runtimes: no container or shader-path change, but behavioural fixes.**
   - Vulkan: `NormalizedShort2/4` now `SNORM` instead of `SSCALED`; render-target
     layout/barrier/subpass-dependency rework; swapchain sizing.
   - DX12: `device->uniforms` cleared every frame; uniforms re-dirtied after
     `GetBackBufferData` / `Texture_GetData`, our read-back path.
   - Both: vertex textures and samplers re-dirtied every frame (#9516); two-sided stencil (#9505).
   - **Why re-run.** Our DX12/Vulkan gates use read-back and run under the Khronos validation
     layer (any layer message fails), so re-run them on 3.8.6 rather than assume.
5. **`mgfxc` 3.8.6 as an oracle.**
   - Unchanged: still writes **MGFX v11**, profile ids are the same, the tool is still
     `dotnet-mgfxc` (net8.0).
   - Bundled-tool bumps: `MonoGame.Tool.Dxc` 1.8.2505.11 → **1.9.2607.1** (different
     DXIL/SPIR-V bytes from the oracle), SharpDX 4.0.1 → 4.2.0, MojoShader 1.0.0.5 → 1.0.0.7.
     The MojoShader bump is build-script-only, so no GLSL change is expected [inferred].
   - DX12 profile: new refusal `Sample name is empty for {fn} slot {n}` for a texture with no
     resolvable sampler.
   - Parser errors are now formatted `file,(line,col) : msg`, a cosmetic regression from
     `file(line,col)`.
   - Only matters if the oracle pin moves off 3.8.4.1.
6. **Content Builder template refresh.** `MonoGame.Templates` moved: `MonoGame.Tool.Dxc 1.9.2607.*`,
   build-output path resolution, `UseCurrentRuntimeIdentifier`, and Assimp package split. The
   blog asks existing Builder projects to re-copy the template. `validation/ContentBuilder` pins
   3.8.5 / 3.8.5.1 and needs a 3.8.6 arm. That arm is exactly the one that would have caught
   item 1.

### NO IMPACT (verified empty or append-only diffs)

- **MGFX loader.** `Graphics/Effect/**` is unchanged v3.8.5→v3.8.6. `MGFXMinVersion = 10`,
  `MGFXVersion = 11`, and the header and profile checks are identical. **Our default v10
  output is still accepted.**
- **Unchanged shader and effect-reading files.** `Graphics/Shader/Shader.cs`, `ConstantBuffer*.cs`
  (common, DirectX, OpenGL), `Shader.DirectX.cs`, `Shader.Native.cs`, `VertexInputLayout.Native.cs`,
  `EffectReader.cs`, `ContentTypeReaderManager.cs` and `ContentReader.cs` have empty diffs.
- **Unchanged `mgfxc` writers.** `ShaderData.writer.cs` and `ConstantBufferData.writer.cs` have
  empty diffs. `EffectObject.writer.cs` changes only nullable annotations.
- **Containers.** Profile bytes are still OpenGL 0, DX11 1, DX12 2, Vulkan 80. The DX12
  `0xB00B00` + two-int wrapper is unchanged in the writer and in `MGG_Shader_Create`. Vulkan
  `SlotOffset = 32` is unchanged. The Vulkan native shader/descriptor path has whitespace-only
  changes.
- **XNB.** Version byte 5 (4 accepted) is unchanged. The platform whitelist only gains `'U'`
  (Switch 2). `CompiledEffectContentWriter` still names
  `Microsoft.Xna.Framework.Content.EffectReader, <MonoGame.Framework full name>`.
- **`TargetPlatform`.** Append-only (`Switch2` after `XboxSeries`), so no renumbering this time.
  Our name-based `MgcbPlatformMap` is unaffected. `Switch2` is unmapped and gets our loud
  `SD0501` (correct).
- **TFMs.** Content.Pipeline, `mgcb`, `mgfxc` and DesktopGL are net8.0; WindowsDX is
  net8.0-windows; Native is netstandard2.1; Android is net8.0-android. No new minimum SDK.

---

## 1. MGFX loader (`Effect.cs`)

**[source]** `git diff --stat v3.8.5 v3.8.6 -- MonoGame.Framework/Graphics/Effect` is **empty**.
At v3.8.6, `MonoGame.Framework/Graphics/Effect/Effect.cs`:

```csharp
32:            public const int MGFXVersion = 11;
38:            public const int MGFXMinVersion = 10;
168:            header.Version = (int)effectCode[index++];
169:            header.Profile = (int)effectCode[index++];
175:            if (header.Version < MGFXHeader.MGFXMinVersion) throw ... "needs to be rebuilt."
177:            if (header.Version > MGFXHeader.MGFXVersion)   throw ... "for a newer release"
180:            if (header.Profile != Shader.Profile)           throw ... "different platform!"
315:                _shaders[s] = new Shader(GraphicsDevice, header.Version, reader);
```

- v10 is still accepted. There is no v12 and no new header field.
- Parameter, technique, pass, shader, sampler and constant-buffer reading is byte-for-byte the
  3.8.5 code, because `Shader.cs`, `ConstantBuffer.cs` and `EffectParameter*.cs` all have empty
  diffs.
- The only adjacent change is `ShaderStage` gaining `Count` (#9548,
  `Graphics/Shader/ShaderStage.cs:23`). It is not serialized.

## 2. DX12 and Vulkan containers (native runtime)

**[source]**

**Headers and C# glue.**
- `native/monogame/include/mg_effect.h` only adds built-in-effect symbol macros (`_xb_`, `_xs_`,
  `_ogl_`).
- `api_MGG.h` changes a few signatures:
  - `ResizeSwapchain` now returns `mgbyte`.
  - `DrawIndexedInstanced` gains `baseInstance`.
  - New `MGG_GraphicsDevice_GetBackBufferMultiSampleCount`, `MGG_RenderTarget_WrapNativeHandle`
    and `MGG_GraphicsDevice_GetNativeHandles`.
  - `MGG_Shader_Create`, `MGG_InputLayout_Create` and `SetConstantBuffer` are unchanged.
- `api_structs.h` adds caps booleans, two-sided-stencil fields in `MGG_DepthStencilState_Info`,
  and `MGP_NativeGraphicsHandles`. None of these touch the shader blob.

**DX12** (`native/monogame/directx12/MGG_DX12.cpp` @ v3.8.6).
- The profile is unchanged on Windows:
  ```cpp
  645  #if defined(MG_XBOXONE)   caps.ShaderProfile = 21;
  651  #elif defined(MG_XBOXSERIES) caps.ShaderProfile = 22;   // was 21 for both Xbox in 3.8.5
  653  #else caps.ShaderProfile = 2;
  ```
- The wrapper is unchanged:
  ```cpp
  2340 if ((*(mgint*)bytecode) == 0xB00B00) { skip id; maxSamplerSlot = int; maxTextureSlot = int; }
       else { maxSamplerSlot = 15; maxTextureSlot = 15; }
  ```
- The `mgfxc` side still writes it (`ShaderProfile.DirectX12.cs:426`, `writer.Write((uint)0xB00B00)`).
- Behavioural diffs:
  - `MGDX_PrepareNextFrame` now does `memset(device->uniforms, 0, ...)`.
  - `GetBackBufferData` and `Texture_GetData` now set `uniformsDirty = 0xFFFFFFFF` (#9516/#9526).
  - Two-sided stencil: when `twoSidedStencilMode` is off, `BackFace = FrontFace` (#9505).
  - Ring-buffer and command-list cleanup.
- The managed `ConstantBuffer.PlatformApply` (Native) always calls `SetConstantBuffer`, so the
  per-frame clear is safe for any effect, ours or `mgfxc`'s.

**Vulkan** (`native/monogame/vulkan/MGG_Vulkan.cpp`).
- No hunk touches `MGG_Shader_Create`, descriptor-set layout, or SPIR-V reflection.
- Hunks around `MGVK_UpdateDescriptors`, `SetConstantBuffer`, `MGVK_CreatePipeline`,
  `MGVK_UpdatePipeline` and `MGVK_Buffer_Create` are whitespace only.
- Real changes:
  - `ToVkFormat(NormalizedShort2/4)`: `R16G16[B16A16]_SSCALED` → `_SNORM`.
  - Render-target layout tracking via `optimal_layout`, plus a second subpass dependency
    (`VK_SUBPASS_EXTERNAL`, color write → shader read).
  - The `ResolveRenderTargets` barrier now starts from `SHADER_READ_ONLY_OPTIMAL`.
  - Swapchain-sizing and `VK_SUBOPTIMAL_KHR` handling.
  - Two-sided stencil, `baseInstance`, and external-handle RTs.
- `mgfxc` Vulkan profile: still `SlotOffset = 32` (`ShaderProfile.Vulkan.cs:112`,
  `-fvk-t-shift/-fvk-s-shift 32 all`). The descriptor table and attribute table are written as
  before. The diff is nullable/null-guard rewrites only, so the `SlotOffset` behaviour ShadowDusk
  documents is unchanged.
- Matrix and constant-buffer upload: the managed `ConstantBuffer.SetParameter` and
  `ConstantBufferData.Vulkan.cs` layout math are unchanged. The latter changes only from
  `.Value` to plain properties. The non-square-matrix behaviour from issue #423 is therefore
  still the 3.8.5 behaviour [source; not re-measured].

## 3. DX11 and OpenGL shader loading

### 3.1 DX11 (WindowsDX)

**[source]** `Shader.DirectX.cs` and `ConstantBuffer.DirectX.cs` have **empty diffs**.
`GraphicsDevice.DirectX.cs` only adds render-target-retention `try/finally` blocks and MSAA cube
face resolve (#9488, #9490). The SharpDX package moves 4.0.1 → 4.2.0, and the stale
`Microsoft.NETCore.App 2.1.30` / `System.Net.Http` / `X509Certificates` package refs are gone
(#9504). Validation lock files will churn.

### 3.2 Managed OpenGL (DesktopGL)

**[source]**
- `Shader.OpenGL.cs` @ v3.8.6:20-67 adds `FragmentOutputMask`, computed by `Contains`-scanning
  the GLSL for `gl_FragColor`, `gl_FragData[0]` … `gl_FragData[7]`.
- `ShaderProgramCache` stores the mask per program.
- `GraphicsDevice.OpenGL.cs:914` `ApplyRenderTargetDrawBuffers` sets `GL.DrawBuffers` with
  `DrawBuffersEnum.None` for render targets the pixel shader does not write. A mask of 0 means
  "all", so it is safe. It runs at RT bind (`:979`) and at program change (`:1030`).
- Desktop only (`#if !GLES`).
- Also new: `RetryMSAAIfNeeded` (`:259`, `:348`) and scissor disabled during `Clear`
  (`:438-500`).
- Attribute and uniform binding is unchanged: by MGFX names via `glGetAttribLocation` and
  `glGetUniformLocation`.

ShadowDusk emits `gl_FragColor` (single target) or `gl_FragData[N]` (rewriter rule 5,
`docs/glsl-uniform-naming.md`), which is the same spelling as MojoShader. The mask therefore
matches `mgfxc`'s for the same shader [inferred: re-measure on the GL corpus + MRT cases].

### 3.3 NEW native OpenGL backend (`DesktopGL4`): its effect contract

**[source]** `native/monogame/opengl/MGG_OpenGL.cpp` (3,779 new lines) @ v3.8.6.
- It reports `caps.ShaderProfile = OpenGLShaderProfile = 0` (lines 218, 1949), so it loads
  standard OpenGL-profile MGFX.
- MonoGame's own tests use the `Effects/OpenGL` set for `DESKTOPGL4` (`Tests/Runner/Utility.cs:360`).
- Built-in effects are compiled with the stock `mgfxc` OpenGL profile into `*.ogl.mgfxo.h`
  (`build/BuildShaders/BuildShadersOGLTask.cs`).
- The managed side is the unchanged `Shader.Native.cs`: it passes the raw GLSL bytes to
  `MGG_Shader_Create` and builds the input layout from the MGFX attribute table in table order
  (`VertexInputLayout.Native.cs:14-54`: `elements[i]` = vertex element matching `inputs[i]`'s
  usage+index).

The C++ side then applies these rules. **None of them read the MGFX attribute names, sampler
names, or attribute `location` fields.**

| Contract | Code @ v3.8.6 | Consequence for a ShadowDusk `.mgfx` |
|---|---|---|
| Vertex input *i* is the GLSL attribute **`vs_v{i}`**. | `GetAttributeName` :482-488. `InputLayout_Create` sets `binding.attributeIndex = elementIndex` (:3568). The link step does `GetAttribLocation("vs_v{index}")` (:843-848). | The MGFX attribute table's order must equal the `k` in `vs_v{k}`. |
| Attribute count = how many of `vs_v0, vs_v1, …` appear **contiguously** as substrings of the source. | `CountSequentialShaderInputs` :591-597 | If the table has more entries than contiguous `vs_vN` in the GLSL (a gap, or a dead input kept in the table), the draw hits `MGGL_FAIL("Invalid shader attribute index")`, which aborts the process. Managed GL would just get location −1 and skip. |
| Constant buffer slot *s* maps to the *s*-th of `{stage}_uniforms_bool`, `_ivec4`, `_vec4` **present in the source**, in that order. Upload is `glUniform4fv/4iv(location, sizeInBytes/16, mapped)`. | `Shader_Create` :3619-3628; `SetConstantBuffer` :2483-2525; `GetConstantRegisterCount` requires `size % 16 == 0` | We emit only `{vs,ps}_uniforms_vec4` (one record per stage), so slot 0 is `vec4`. A cbuffer record whose stage GLSL lacks the array would hit `MGGL_FAIL("Invalid constant buffer slot")`, an abort. |
| Sampler for slot *s* is the uniform **`{vs,ps}_s{s}`**. Pixel unit = *s*, vertex unit = 16+*s*. | `GetSamplerName` :491-497; program link :850-865 | Matches our `ps_s{k}` with `TextureSlot = k`. **Vertex textures are "exposed but intentionally disabled"** on GL4 (PR #9491 text). |
| `posFixup` (`vec4`) is set per draw: `(1, +1, 0, 0)` for the backbuffer, `(1, −1, 0, 0)` for a render target. **No half-pixel `.zw`.** | `ApplyPosFixup` :645-661 | We inject the same `posFixup` contract. The behaviour is the same for both compilers. |
| Fragment-output mask by substring (`gl_FragColor`, `gl_FragData[n]`). | `GetFragmentOutputMask` :599-631 | Same rule as §3.2. |
| GLSL compiled **verbatim** with no `#version` injection, in a **core** context. SDL asks for `SDL_GL_CONTEXT_PROFILE_CORE`, trying 4.1 down to 3.1. | `MGP_sdl.cpp:745-752, 807-809`; `Shader_Create` :3633-3651 | Our GLSL (no `#version`, `attribute`/`varying`/`texture2D`/`gl_FragColor`, plus an `#ifdef GL_ES` precision header) is in the same dialect family as `mgfxc`'s MojoShader output. Whether a core-profile driver accepts version-less GLSL is driver-dependent: Mesa core rejects GLSL 1.10 [inferred]. MonoGame's own CI runs only the `Storage` test category for DesktopGL4, so graphics on Mesa are unproven upstream too. |

Failures are `[[noreturn]] MGGL_Fail` → `MG_Print_StdError; MG_GENERATE_TRAP(); abort();`
(:252-259). A validation driver must run each case out-of-process, or treat a crash as a red
result.

How a consumer selects GL4: `MonoGame.Framework.Native` 3.8.6 (netstandard2.1) plus
`MonoGame.Runtime.<OS>.DesktopGL4`. No `TargetPlatform` or XNB platform byte was added. The
content side uses `DesktopGL` (`'d'`, OpenGL profile) [inferred from the tests and the build
task; there is no DesktopGL4 template in this release].

## 4. What `mgfxc` 3.8.6 emits

**[source]**

**Format.** `Tools/MonoGame.Effect.Compiler/Effect/EffectObject.writer.cs:15-16`:

```csharp
private const string Header = "MGFX";
internal const int Version = 11;
```

| Tag | `Version` |
|---|---|
| v3.8.4.1 | 10 |
| v3.8.5 | 11 |
| v3.8.5.1 | 11 |
| v3.8.6 | 11 |

- The writer diff is nullable `!` only. `ShaderData.writer.cs` and `ConstantBufferData.writer.cs`
  are unchanged.
- **Options.** `Options.cs` @ v3.8.6 has only `SourceFile`, `OutputFile`, `Profile`, `Debug` and
  `Defines`. There is **no flag to request v10**.
- **Tool package.** `dotnet-mgfxc` 3.8.6 is still published (DotnetTool, `tools/net8.0/any/mgfxc.dll`
  [binary]).
- **Profiles.** `OpenGL` 0, `DirectX_11` 1, `DirectX_12` 2 and `Vulkan` 80 are unchanged.
  `DirectX12ShaderProfile` gains a `protected (string name, byte formatId)` constructor and a
  virtual `RunTool` for private console subclasses.
  - `ShaderProfile.ForPlatform`: `XboxOne`/`XboxSeries` no longer map to `DirectX_12`. They fall
    through to `FromName(platform.ToString())`, which **now throws** when no profile matches
    (`ShaderProfile.cs:59,86`).
- **DX12 pairing** (`ShaderProfile.DirectX12.cs` ~:376). After the name and slot sampler lookup,
  a new `if (string.IsNullOrEmpty(sampler.samplerName)) throw new Exception($"Sample name is empty for {shaderFunction} slot {textureSlot}.")`.
  A texture with no sampler at its register (for example `.Load`-only) now fails `mgfxc`
  DX12. Earlier versions wrote an empty-named sampler.
- **Bundled tools** (`MonoGame.Effect.Compiler.csproj`, mirrored in the Content.Pipeline nuspec
  [binary]): `MonoGame.Tool.Dxc` 1.8.2505.11 → **1.9.2607.1**, `MonoGame.Library.MojoShader`
  1.0.0.5 → 1.0.0.7, SharpDX/D3DCompiler 4.0.1 → 4.2.0. The MojoShader repo compare
  `1.0.0.5...1.0.0.7` touches only `.github/workflows`, `build/*Task.cs` and the `buildscripts`
  submodule (Docker Linux build, stripping), with no MojoShader source. The GLSL text is
  therefore expected to be identical [inferred]. The DXC bump changes `mgfxc`'s DX12/Vulkan
  bytes.
- **Diagnostics.** The FX-parser error format changed from `"{0}({1},{2}) : {3}"` to
  `$"{error.File},({error.Line},{error.Column}) : {error.Message}"` (`ShaderResult.cs:81`). The
  extra comma looks like an upstream regression. It only matters to tools that parse `mgfxc`
  parser errors.

## 5. Content pipeline

**[source]** unless marked.

- **`TargetPlatform`** (`MonoGame.Framework.Content.Pipeline/TargetPlatform.cs`). Order at v3.8.6:
  `Windows, Xbox360, iOS, Android, DesktopGL, MacOSX, NativeClient, RaspberryPi, PlayStation4,
  PlayStation5, XboxOne, Switch, Web, DesktopVK, WindowsDX12, XboxSeries, Switch2`. This is
  **append-only** relative to 3.8.5. The `"Linux"`/`"WindowsGL"` legacy converter is unchanged.
  The pipeline-side XNB char table (`ContentWriter.cs:38-54`) appends `'U'`.
- **`ContentImporter<T>` / `ContentProcessor<TIn,TOut>`.** These gained `where T/TOutput : notnull`.
  That is a nullable-annotation constraint and not enforced at type load, so our 3.8.2.1105-built
  subclasses load [binary: attributes instantiate and the processor binds on 3.8.6].
  The explicit public parameterless constructors were removed in favour of the implicit ones:
  `ContentItem`, `EffectContent`, `EffectImporter`, and `ContentProcessorAttribute`. Same
  signature, so binary-compatible. On the abstract `ContentImporterContext` the implicit
  constructor is `protected`.
- **Removed overloads (binary breaks).**
  - `ContentIdentity()`, `(string)`, `(string, string)`: **this breaks ShadowDusk** (see Summary §1).
  - `ContentImporterAttribute(string)`: breaks other plugins only.
  - `ContentProcessorContext.BuildAndLoadAsset(ref, processorName)`, `BuildAsset(ref, processorName)`
    and `Convert(input, processorName)` are now `[Obsolete]` but still present.
- **`EffectContent`, `CompiledEffectContent`, `EffectProcessor`.**
  - `CompiledEffectContent(byte[] effectCode)` is now a primary constructor. Same IL signature;
    `GetEffectCode()` is unchanged.
  - `EffectContent.EffectCode` now defaults to `""`.
  - Stock `EffectProcessor` throws if `input.Identity == null`, rethrows after logging on
    `ShaderCompilerException`, and logs through `Logger.Log(LogLevel.Warning, …)`.
  - `ContentBuildLogger` is **unchanged**, so our `LogWarning(string, ContentIdentity, string,
    object[])` call binds [binary].
- **Code-centric Content Builder** (`Builder/`). Only four files changed:
  - `ContentBuilder.BuildAndWriteContent` now returns `string?`.
  - `ContentBuilderProcessorContext.OutputDirectory` → `RootedOutputDirectory` (#9498, also in
    3.8.5.1). The other members are primary-constructor rewrites.
  - `ContentBuilderHelper` only adds `InvariantCulture` parsing. Its unguarded
    `Assembly.GetTypes()` scan (the Phase 63 blocker) is **unchanged**.
  - `IContentCollection`, `ContentBuilderParams` and `ContentInfo` are unchanged.
- **`dotnet mgcb` `PipelineManager`.** The diff is nullable and logging rewrites. Assembly load
  and `GetTypes` stay inside try/catch, but the `GetCustomAttributes(ContentImporterAttribute)`
  loop stays outside it (see §1, third-party note).
- **Content.Pipeline csproj.** It no longer links `ReflectionHelpers.cs`, `AssemblyHelper.cs`
  or `ContentExtensions.cs` (all internal). `TreatWarningsAsErrors` is on.
- **Templates** (`external/MonoGame.Templates` 7332503 → 90600c8). The Content Builder
  template's `BuildContent.targets` now resolves the Builder executable from MSBuild
  `TargetOutputs` instead of `bin\Debug`, and quotes paths. Its csproj gains
  `UseCurrentRuntimeIdentifier`, `MonoGame.Tool.Dxc 1.9.2607.*` and `AssimpNetter`/
  `MonoGame.Library.Assimp`. The template TFM stays net9.0.

## 6. XNB / `ContentManager`

**[source]**
- `MonoGame.Framework/Content/ContentManager.cs`: the only diff is `'G'` relabelled "Windows
  DirectX 12" and `'U'` (Nintendo Switch 2) appended to `targetPlatformIdentifiers` (:37-55).
- XNB version: `if (version != 5 && version != 4) throw new ContentLoadException("Invalid XNB version")`
  (:505-512), unchanged.
- `ContentTypeReaderManager.cs`, `ContentReader.cs` and `ContentReaders/EffectReader.cs`: **empty
  diffs**.
- The changed readers are Texture2D/3D/Cube, VertexBuffer and VertexDeclaration. None are on our
  path.
- `XnbWriter`'s platform bytes (`d`, `w`, `V`, `G`, …) and the
  `Microsoft.Xna.Framework.Content.EffectReader` manifest remain valid.

## 7. Packaging (nuget.org, checked 2026-10-31)

**[binary]**

| Package | 3.8.6 | Notes |
|---|---|---|
| `MonoGame.Framework.DesktopGL` | ✅ | net8.0; deps unchanged (OpenAL 1.24.3.4, SDL 2.32.10.2, NVorbis 0.10.4) |
| `MonoGame.Framework.WindowsDX` | ✅ | net8.0-windows; SharpDX 4.2.0 |
| `MonoGame.Framework.Native` | ✅ | netstandard2.1 |
| `MonoGame.Runtime.Windows.DX12` | ✅ | |
| `MonoGame.Runtime.{Windows,Linux,Mac}.Vulkan` | ✅ | |
| **`MonoGame.Runtime.{Windows,Linux,Mac}.DesktopGL4`** | ✅ **NEW** (first published 3.8.6-preview.1) | netstandard2.1, native `mgruntime` (win-x64/arm64, linux, osx) |
| `MonoGame.Framework.Content.Pipeline` | ✅ | net8.0; deps: Dxc 1.9.2607.1, MojoShader 1.0.0.7, SharpDX 4.2.0, AssimpNetter 6.0.5 + **new** `MonoGame.Library.Assimp` 6.0.5.1, FreeType 2.13.2.5, … |
| `dotnet-mgcb`, `dotnet-mgfxc` | ✅ | DotnetTool, `tools/net8.0/any` |
| `MonoGame.Content.Builder.Task` | ✅ | |
| `MonoGame.Framework.Android`, `.iOS` | ✅ | Android source TFM still `net8.0-android` (#9531 fixes net10 builds) |
| `MonoGame.Tool.Dxc` | 1.9.2607.1 | the only 1.9.x on the feed |

No package was removed. No TFM moved to net9/net10. The only new required package is the
DesktopGL4 runtime, and only for that new target.

## 8. Other release-note items touching effects, shaders and content

- **Native OpenGL backend, DesktopGL4** (#9491). See §3.3. Per the blog it is a step towards a
  WebGL2 platform in 3.8.7. For 3.9 the blog announces "a new unified shader compiler", which is
  a future watch item for the MGFX format.
- **Vulkan/DX12 fixes** (#9464, #9484, #9445, #9442, #9447, #9505, #9526, #9516). See §2.
- **Mask DesktopGL MRT outputs** (#9509); **scissor off during Clear** (#9501); **MSAA fallback**
  (#9565). See §3.2.
- **`SpriteBatch`**: doc-comment change only. **`BasicEffect` and the built-in effects**: `.fx`
  sources unchanged. The native builds add `.ogl/.xb/.xs` headers.
- **Content Builder** is now "the recommended approach" (blog). This raises the stakes of
  Summary §1.
- Not relevant to ShadowDusk: StorageContainer (native platforms), Switch 2, Xbox GDK
  suspend/resume, native GPU handles and external RT wrapping, Span texture APIs, vector maths,
  FBX orientation, and the Docker-built Linux natives.

---

## Suggested follow-ups (for the phase doc)

1. **Fix + guard Summary §1.**
   - Use the three-argument `ContentIdentity` constructor.
   - Add the MemberRef-binding test against every supported Content.Pipeline version.
   - Add a 3.8.6 arm to `validation/ContentBuilder` and `validation/MgcbPlugin`.
   - Consider filing upstream: #9050 removed public overloads in a patch release.
2. **Version sweep.** Append 3.8.6 to `validation/ForwardCompat` (expect pass: loader unchanged).
   Then re-run the GL / DX11 / DX12 / Vulkan gates on 3.8.6 runtimes.
3. **New `DesktopGL4` driver** (rung 4 vs the `mgfxc` OpenGL golden on the same runtime).
   - Watch the `vs_v{i}` = table-index rule, the contiguous-attribute count, the
     cbuffer-slot-by-presence rule, and the process abort on mismatch.
   - Windows/NVIDIA first. Mesa core-profile behaviour is unknown for `mgfxc` output too.
   - Register it in validation-matrix §6 and §7, and in project_facts.
4. **Oracle pin** stays at 3.8.4.1 unless deliberately moved. If moved, record the DXC 1.9 byte
   drift and the new DX12 "Sample name is empty" refusal.
