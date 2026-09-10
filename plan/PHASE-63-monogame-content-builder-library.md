# Phase 63 — MonoGame 3.8.5 Content Builder: the importer/processor pair as a library (issue #203)

**Track:** Delivery shapes. Additive; **no existing output byte changes** — the same
`ShadowDuskEffectImporter`/`ShadowDuskEffectProcessor` making the same `EffectCompiler.Compile` call,
delivered in a shape a consumer's own C# can reference.

**Status:** 🔵 **Planned (2026-09-09).** Investigated **by measurement** against the real
`MonoGame.Framework.Content.Pipeline` 3.8.5 package, the `v3.8.5` source, the 3.8.5 project
templates, and a real Content Builder probe project driving the worktree's own plugin; nothing
implemented yet. The investigation found **two product defects that outrank the feature** and shape
its plan: a hard blocker in ShadowDusk's dependency graph (§2.3, Area A) and a silent wrong-artifact
bug in the *shipped* MGCB plugin on MonoGame 3.8.5 (§2.4, Area B).

**Depends on:** [Phase 29](DONE/PHASE-29-mgcb-content-processor-plugin.md) (the pair exists and is
byte-identical to the CLI), [Phase 60](DONE/PHASE-60-xnb-content-output.md) (the `.xnb` envelope
knowledge and the `validation/XnbContentLoad` driver pattern this phase's gate mirrors),
[Phase 52](DONE/PHASE-52-monogame-3.8.5-support.md) Area E (the finding that the 3.8.5 Builder has
no external-tool seam, so an in-process importer/processor is the only route).

**Blocks:** nothing.

**Gated on:** **Area A.** Until ShadowDusk's dependency graph survives the Builder's assembly scan,
*neither* the existing tools-only package *nor* a new library package runs inside a 3.8.5 Content
Builder at all (§2.3). Area A is a change to a shipped library (`ShadowDusk.HLSL`) and carries the
full pre-merge bar.

> **The request** — [issue #203](https://github.com/kaltinril/ShadowDusk/issues/203), aitorciki,
> 2026-08-30: *"Starting with MonoGame 3.8.5, the legacy MGCB is being replaced with the new Content
> Builder Project pattern … moving away from the `.mgcb` file and replacing it with regular C# code
> … Afaict, ShadowDusk has all the required bits to integrate with this pattern in the form of
> `ShadowDuskEffectImporter` and `ShadowDuskEffectProcessor`, but these aren't published as a
> library today since `ShadowDusk.MgcbPlugin` is tools-only. Would it be possible to export the
> importer and processor to this end?"*
>
> ```csharp
> using Microsoft.Xna.Framework.Content.Pipeline;
> using Microsoft.Xna.Framework.Content.Pipeline.Processors;
> using MonoGame.Framework.Content.Pipeline.Builder;
> var builder = new Builder();
> builder.Run(args);
> return builder.FailedToBuild > 0 ? -1 : 0;
> public class Builder : ContentBuilder
> {
>     public override IContentCollection GetContentCollection()
>     {
>         var content = new ContentCollection();
>         content.Include<WildcardRule>("Effects/*.fx");
>         content.Include<WildcardRule>("Font/*.spritefont");
>         content.Include<WildcardRule>("Sounds/*.ogg", new OggImporter(), new SoundEffectProcessor());
>         content.Include("splash-screen.png");
>         return content;
>     }
> }
> ```

---

## 1. Where this came from, and why it matters

MonoGame 3.8.5 made the **Content Builder project** the default content story for new projects: the
`MonoGame.Templates.CSharp` 3.8.5 package ships `MonoGame.ContentBuilder.CSharp` (short name
`mgcb`, a console project) and every `*.StartKit.CB.*` template wires the game to it through a
`BuildContent.targets` import. The `.mgcb` route is not gone (`dotnet-mgcb` 3.8.5 is still
published), but it is now the legacy one.

The Builder is **ordinary C# the consumer owns**, referencing `MonoGame.Framework.Content.Pipeline`
directly. To use ShadowDusk from it, the consumer needs our importer and processor as a
**compile-time reference** — `new ShadowDuskEffectImporter()` — which a tools-only package cannot
give them: `ShadowDusk.MgcbPlugin` deliberately ships no `lib/` because MGCB's `/reference:` needs
a self-contained directory and because a content-pipeline plugin must never flow into the game
assembly. Both reasons are still right *for MGCB*; neither applies to a Content Builder project,
which is a separate console executable and is exactly the kind of project a normal library package
is for.

Phase 52 Area E predicted this: *"being ordinary C# the consumer owns, it is a better place to call
ShadowDusk from than the `.mgcb` file ever was."* This phase is that sentence made real. It is the
**fourth delivery shape** (§4): the `.mgcb` plugin, the CLI, direct `.xnb` output, and now the pair
as a library in the consumer's own Builder.

---

## 2. What is established by measurement (2026-09-09) — do not re-derive this

Everything below was measured, not transcribed. Probe project and commands: Appendix A.

### 2.1 The real 3.8.5 Content Builder API

**The package.** `MonoGame.Framework.Content.Pipeline` 3.8.5 ships `lib/net8.0` only (plus
`runtimes/{linux-arm64,linux-x64,osx,win-arm64,win-x64}`). Its nuspec declares dependencies on
`AssimpNetter`, `BCnEncoder.Net`, `LibKTX`, `Microsoft.VisualBasic`, `MonoGame.Library.FreeType`,
`MonoGame.Library.MojoShader`, `MonoGame.Tool.{Basisu,Crunch,Dxc 1.8.2505.11,FFmpeg,FFprobe}`,
`SharpDX`, `SharpDX.D3DCompiler`, `System.CommandLine 2.0.0-beta4`, `YamlDotNet` — and **no
`MonoGame.Framework`**, although the assembly references `MonoGame.Framework, Version=3.8.5.0`
(the csproj takes it as a `PrivateAssets=All` project reference). A console project referencing
only the pipeline package dies at first touch with
`FileNotFoundException: Could not load file or assembly 'MonoGame.Framework, Version=3.8.5.0'`;
the template supplies it through `MonoGame.Framework.Native` (`PrivateAssets=All`). This is the
same quirk `project_facts.md` records for 3.8.2.1105, unchanged.

**The template** (`MonoGame.ContentBuilder.CSharp/MGNamespace.csproj`, verbatim):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.Content.Pipeline" Version="3.8.5" />
    <PackageReference Include="MonoGame.Framework.Native" Version="3.8.5">
      <PrivateAssets>All</PrivateAssets>
    </PackageReference>
    <PackageReference Include="MonoGame.Library.FreeType" Version="2.13.2.*" />
    <PackageReference Include="MonoGame.Library.MojoShader" Version="1.0.0.*" />
    <PackageReference Include="MonoGame.Tool.Basisu" Version="2.0.2.*" />
    <PackageReference Include="MonoGame.Tool.Crunch" Version="1.0.4.*" />
    <PackageReference Include="MonoGame.Tool.Dxc" Version="1.8.2505.*" />
    <PackageReference Include="MonoGame.Tool.FFmpeg" Version="7.0.0.*" />
    <PackageReference Include="MonoGame.Tool.FFprobe" Version="7.0.0.*" />
  </ItemGroup>
</Project>
```

The game project does `<Import Project="..\X.Content\BuildContent.targets" />`, whose `BuildContent`
target (`BeforeTargets="BeforeCompile"`) MSBuilds the Builder project and `Exec`s
`bin\Debug\<Builder> build -p $(MonoGamePlatform) -s <Builder>/Assets -o $(ProjectDir)$(OutputPath) -i $(ProjectDir)$(IntermediateOutputPath)`,
with `CustomErrorRegularExpression="\[E\] .+"`. **`$(MonoGamePlatform)` is the `TargetPlatform`
enum name** (`DesktopGL`, `DesktopVK`, `WindowsDX12`, `Android`, `iOS`, and `Windows` for WindowsDX).

**The surface** (`MonoGame.Framework.Content.Pipeline.Builder`, from reflection over the shipped
assembly, cross-checked against `v3.8.5` source):

```csharp
public abstract class ContentBuilder
{
    public ContentBuilderParams Parameters { get; set; }          // = new()
    public ContentBuildLogger   Logger     { get; set; }          // = ContentBuilderLogger (internal, console, "[I]/[W]/[E]" prefixes)
    public virtual IContentCache ContentCache { get; init; }      // = ContentCache
    public uint FailedToBuild    { get; private set; }
    public uint SucceededToBuild { get; private set; }
    public abstract IContentCollection GetContentCollection();    // called ONCE, after LoadAssemblies()
    public bool Run(ContentBuilderParams parameters);
    public void Run(string[] args) => Run(ContentBuilderParams.Parse(args));
    public string  BuildAndWriteContent(string relativeSrcPath, ContentInfo contentInfo, string? relativeDstPath = null, ContentProcessorContext? parentContext = null);
    public object? BuildAndLoadContent (string relativeSrcPath, ContentInfo contentInfo, string? relativeDstPath = null, ContentProcessorContext? parentContext = null);
}

public interface IContentCollection { bool GetContentInfo(string filePath, out List<ContentInfo> contentInfos); }

public class ContentCollection : IContentCollection
{
    public void SetContentRoot(string contentRoot);                                   // default "Content"; prefixes output paths; several roots may coexist
    public void Include(string inputPath, IContentImporter? contentImporter = null, IContentProcessor? contentProcessor = null);
    public void Include(string inputPath, string outputPath, IContentImporter? contentImporter = null, IContentProcessor? contentProcessor = null);
    public void IncludeCopy(string inputPath, string? outputPath);
    public void Exclude(string excludePath);
    public void Include<T>(string includePattern, IContentImporter? contentImporter = null, IContentProcessor? contentProcessor = null) where T : ContentRule, new();
    public void Include<T>(string includePattern, Func<string,string> outputPath, IContentImporter? contentImporter = null, IContentProcessor? contentProcessor = null) where T : ContentRule, new();
    public void IncludeCopy<T>(string includePattern, Func<string,string>? outputPath = null) where T : ContentRule, new();
    public void Exclude<T>(string excludePattern) where T : ContentRule, new();
}

public abstract class ContentRule { public string Pattern { get; init; } public abstract bool IsMatch(string filePath); }
public class WildcardRule : ContentRule   // Microsoft.VisualBasic LikeOperator.LikeString(filePath, Pattern, CompareMethod.Binary) - relative path, '/' separators
public class RegexRule    : ContentRule   // Regex.IsMatch(filePath, Pattern)

public class ContentInfo(string contentRoot = "", bool shouldBuild = true, IContentImporter? importer = null, IContentProcessor? processor = null, Func<string,string>? outputPath = null)
{ public string ContentRoot; public bool ShouldBuild; public IContentImporter? Importer; public IContentProcessor? Processor; public string GetOutputPath(string); public int MakeBuildHash(); }

public class ContentBuilderParams
{
    public ContentBuilderMode Mode { get; set; }          // None | Builder | Server; None = help was shown, NOTHING is built
    public string WorkingDirectory { get; set; }          // Directory.GetCurrentDirectory()
    public string SourceDirectory { get; set; }           // "Content"     (+ RootedSourceDirectory)
    public string OutputDirectory { get; set; }           // "bin/Content" (+ RootedOutputDirectory)
    public string IntermediateDirectory { get; set; }     // "obj/Content" (+ RootedIntermediateDirectory)
    public TargetPlatform Platform { get; set; }          // DesktopGL
    public GraphicsProfile GraphicsProfile { get; set; }  // HiDef
    public bool CompressContent { get; set; }             // false
    public LogLevel LogLevel { get; set; }                // Info
    public bool Rebuild { get; set; }                     // false
    public bool SkipClean { get; set; }                   // false
    public List<ContentServer> Servers { get; set; }
    public static ContentBuilderParams Parse(params string[] args);
}
```

**`Run(args)` grammar** (System.CommandLine): subcommands **`build`** (`--rebuild`, `--skip-clean`)
and **`server`**; global options `--workingDir`, `--src|-s`, `--output|-o`, `--intermediate|-i`,
`--platform|-p <TargetPlatform name>`, `--graphics-profile|-g`, `--compress`, `--loglevel|-l`.
**There is no `--config`/build-configuration argument at all.** With no arguments the parser shows
help, `Mode` stays `None`, `Run` returns `false` having built nothing, and the template's
`return builder.FailedToBuild > 0 ? -1 : 0` exits **0** — an argument-less run "succeeds" silently.
The template's own `Program.cs` covers this by constructing a `ContentBuilderParams { Mode = Builder, … }`
when `args` is empty.

**Importer/processor discovery** (`ContentBuilderHelper.LoadAssemblies()`, called first thing in
`Run`, before `GetContentCollection`):

1. Takes `AppDomain.CurrentDomain.GetAssemblies()`, then for every one **recursively
   `Assembly.Load`s every referenced assembly** whose name does not start with `System.`
   (load failures swallowed by an empty `catch`).
2. For every assembly now in the domain: **`a.GetTypes()` — unguarded** — and for each concrete
   type implementing `IContentImporter`, records it once per extension in its
   `[ContentImporter]` attribute (a type without the attribute gets `".*"`); for each
   `IContentProcessor`, records it. The importer list is sorted by extension length, longest first.
3. When an `Include` carries **no instance**: importer = first list entry whose extension the file
   name ends with (case-insensitive); processor = first recorded processor whose **`Type.Name`
   equals the chosen importer attribute's `DefaultProcessor`**. When an `Include` carries instances,
   they are used as-is (step 3 is skipped; steps 1-2 still run).

There is no `/reference:`. Anything the Builder's own project references (transitively) is scanned.

**The context our processor receives** (`ContentBuilderProcessorContext`, internal):
`TargetPlatform` = `Parameters.Platform`; `TargetProfile` = `Parameters.GraphicsProfile`;
**`BuildConfiguration` = `""` always** (so `DebugMode.Auto` optimizes; a consumer wanting debug sets
`DebugMode = EffectProcessorDebugMode.Debug` on the instance); `Parameters` = an empty
`OpaqueDataDictionary` (**processor parameters are C# properties on the instance**, not a
dictionary); `SourceIdentity` = a *relative* path; `Logger` = the builder's; `AddDependency` and
`AddOutputFile` feed the per-asset file cache; `ProjectDirectory` = the rooted source directory.
The importer is handed the **absolute** path (`Path.Combine(RootedSourceDirectory, relativePath)`).
`Convert`/`BuildAsset` overloads that take *names* throw `NotSupportedException`; only the
instance-taking overloads work.

**Caching.** The per-asset cache key hashes the importer's and processor's public properties through
YamlDotNet (`ContentInfo.MakeBuildHash`), so changing `Defines`/`ShaderProfile`/… on our processor
invalidates the asset; `#include`s registered via `AddDependency` are tracked as file dependencies.
`build --rebuild` bypasses the cache.

**Writing.** `ContentCompiler.Compile(stream, processedObject, Platform, GraphicsProfile,
CompressContent, RootedOutputDirectory, outputDir)` — the same `ContentCompiler` + `EffectWriter`
MGCB uses, so a `CompiledEffectContent` from our processor lands in an `.xnb` MonoGame's own writer
produced. The type-reader manifest string in a 3.8.5-written `.xnb` is
`Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework, Version=3.8.5.0, Culture=neutral, PublicKeyToken=null`
(Phase 60's `XnbWriter` emits the 3.8.4.1 form; both load, per Phase 60's finding that the version
is inert).

**The stock 3.8.5 `EffectProcessor`** compiles in-process through bundled DXC 1.8.2505 + MojoShader
(no external tool), maps platforms with
`Windows → DirectX_11; iOS|Android|DesktopGL|MacOSX|RaspberryPi|Web → OpenGL; DesktopVK → Vulkan; WindowsDX12|XboxOne|XboxSeries → DirectX_12`,
and **writes MGFX v11** (`EffectObject.writer.cs`: `internal const int Version = 11`). Consequence for
the gate (Area D): the in-process oracle arm's `.xnb` loads only on a 3.8.5 runtime, so the render
half must run on MonoGame 3.8.5 (the `validation/MonoGameV11` / `validation/VsDrivenDx12` precedent),
not the 3.8.2.1105 `validation/XnbContentLoad` uses.

### 2.2 The existing pair works in the pattern, as-is — once the scan in §2.3 is out of the way

A scratch Content Builder project (Appendix A: the 3.8.5 template's csproj, `ProjectReference` to
the worktree's `ShadowDusk.MgcbPlugin.csproj`, one `Builder : ContentBuilder` with two content
roots — `stock` via `new EffectImporter(), new EffectProcessor()` and `sd` via
`new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor()` — over `Grayscale.fx`,
`VertexAndPixel.fx`, `MultiTexture.fx`, `SpriteEffect.fx`), run with the §2.3 scan bypassed:

| `-p` | ShadowDusk arm | payload vs `ShadowDuskCLI /Profile:` | `.xnb` envelope vs stock 3.8.5 | platform byte |
|---|---|---|---|---|
| `Windows` | 4/4 built | `DirectX_11`: **byte-identical**, 4/4 | header + manifest **identical**; payload differs (v10 vs stock's v11) | `w` |
| `DesktopGL` | 3/4 built; `SpriteEffect.fx` fails `SD0010` | `OpenGL`: **byte-identical**, 3/3 | identical / differs | `d` |
| `DesktopVK` | 3/4 "built" | **equals the `OpenGL` output** — the wrong artifact; see §2.4 | identical / differs | `V` |
| `WindowsDX12` | 0/4, `SD0501` "platform 'WindowsDX12' is not supported" | — | — | — |

- The `SpriteEffect.fx` failure on GL is the known Phase 41 GAP-1 GL half (macro-defined
  techniques, `SD0010`), identical through the CLI; it is why `validation/MgcbPlugin` runs that
  fixture on Windows only.
- **The 3.8.2.1105-compiled assembly binds in the 3.8.5 process** (`typeof(ContentBuilder).Assembly`
  = 3.8.5.0; the plugin's reference = 3.8.2.1105): .NET Core binds a lower-versioned reference to a
  higher loaded assembly. The reverse would not (a 3.8.5-compiled plugin cannot load into MGCB
  3.8.4.1), which is the argument for keeping the floor (§3 C3).
- **Both a `net8.0` and a `net10.0` Builder project work** (the template is `net9.0`).
- **`PluginNativeLibraryResolver` is not needed in this shape, and is harmless.** With the shim
  unsubscribed (reflection removes its handler before `Run`), Windows and DesktopGL build
  identically: the consumer's own executable gets `runtimes/win-x64/native/{dxcompiler,dxil,spirv-cross}.dll`
  and `libvkd3d-shader-1.dll` through normal NuGet asset flow, and `AppContext.BaseDirectory` *is*
  the consumer's bin, so the existing loaders find them first.
- **Extension auto-discovery picks MonoGame's stock pair, silently.** With both loaded,
  `Include<WildcardRule>("Effects/*.fx")` (no instances) built every file through
  `EffectImporter`/`EffectProcessor` — bytes equal to the stock arm. Both register `.fx`; the tie
  order is the unstable `List.Sort` on equal extension lengths. **Passing instances is the route
  and the docs must say so**; there is no way for ShadowDusk to "win" the extension.
- `Defines`, `DebugMode`, `IncludeDirs`, `ShaderProfile`, `MgfxVersion`, `DxbcBackend` are set as
  C# properties (`new ShadowDuskEffectProcessor { Defines = "FOO=1" }`) and participate in the cache
  hash.

### 2.3 BLOCKER: the Builder's unguarded `Assembly.GetTypes()` scan crashes on `Vortice.Direct3D12`

Before the workaround, every run — every platform, shim or no shim — died before touching a shader:

```
Unhandled exception. System.Reflection.ReflectionTypeLoadException: Unable to load one or more of the requested types.
Could not load type 'Union' from assembly 'Vortice.Direct3D12, Version=3.5.0.0, ...' because it contains an
object field at offset 0 that is incorrectly aligned or overlapped by a non-object field.
   at System.Reflection.RuntimeModule.GetTypes(RuntimeModule module)
   at MonoGame.Framework.Content.Pipeline.Builder.ContentBuilderHelper.LoadAssemblies()
   at MonoGame.Framework.Content.Pipeline.Builder.ContentBuilder.Run(ContentBuilderParams parameters)
```

Measured facts:

- **Exactly one type in the whole graph fails:** a sweep that `Assembly.GetTypes()`s every managed
  DLL in the probe's output directory (41 assemblies, dependencies resolved from the same directory)
  reports **only `Vortice.Direct3D12`**, and within it **only
  `Vortice.Direct3D12.VersionedDeviceRemovedExtendedData+Union`** (explicit layout; `Dred_1_0 :
  DeviceRemovedExtendedData` and `Dred_1_1 : DeviceRemovedExtendedData1` both at offset 0, and the
  DRED structs carry managed references). The other eight `Union` nested types in the assembly load
  fine.
- **Not fixed by a version bump:** the pinned 3.5.0 and the newest 3.8.3 (2026-09) fail identically.
- **Why Phase 29 never saw it:** MGCB's `PipelineManager.ResolveAssemblies` scans **only the
  `/reference:`d assemblies**, inside a `try { … a.GetTypes() … } catch (Exception) { LogWarning; continue; }`.
  The new `ContentBuilderHelper.LoadAssemblies` scans **every assembly in the AppDomain plus their
  transitive references**, with **no guard**. Same package, two scanners, one of them strict.
- **It is on a shipping path in ShadowDusk.** `ShadowDusk.HLSL` references `Vortice.Direct3D12` for
  one file, `Reflection/DxilReflectionExtractor.cs` (318 lines; consumes `ID3D12ShaderReflection`,
  `ID3D12ShaderReflectionConstantBuffer`, `ID3D12ShaderReflectionVariable`, `ID3D12ShaderReflectionType`,
  `ShaderDescription`, `InputBindingDescription`, `ConstantBufferDescription`,
  `ShaderVariableDescription`, `ShaderTypeDescription`), used by `CompilationPipeline` for the
  DXIL-oracle reflection of the **desktop OpenGL, DirectX 11 and DirectX 12 paths** (line ~333; the
  SPIR-V reflector is taken only for Vulkan, WASM and Android) and for **DirectX 12 vertex-input
  reflection** (line ~2318, `DxilVertexInputReflector`). The probe confirms the split: after a
  `Windows` build `Vortice.Direct3D12` is *not* loaded (DXBC reflection is the pure-managed
  `RdefReader`); after a `DesktopGL` build it is.
- **`Vortice.Dxc` itself does not depend on `Vortice.Direct3D12`** (its only references are
  `SharpGen.Runtime` and `SharpGen.Runtime.COM`), so dropping the assembly does not mean dropping DXC.
- **No upstream report exists** (GitHub issue search, 2026-09-09) and `develop`'s
  `ContentBuilderHelper.cs` (last touched 2026-07-16) is unchanged.

Consequence: **until ShadowDusk's graph is `GetTypes()`-clean, no ShadowDusk package can be used
from a 3.8.5 Content Builder**, tools-only or library. This is Area A and it gates the phase.

### 2.4 BUG in the shipped MGCB plugin: MonoGame renumbered `TargetPlatform` in 3.8.5

| value | 3.8.2.1105 (what `MgcbPlatformMap` is compiled against) | 3.8.5 |
|---|---|---|
| 0-11 | `Windows, Xbox360, iOS, Android, DesktopGL, MacOSX, NativeClient, RaspberryPi, PlayStation4, PlayStation5, XboxOne, Switch` | same |
| 12 | `Stadia` | **`Web`** |
| 13 | `Web` | **`DesktopVK`** |
| 14 | — | **`WindowsDX12`** |
| 15 | — | **`XboxSeries`** |

`MgcbPlatformMap.FromTargetPlatform` switches on the enum *members*, i.e. on the compile-time
numbers. Measured on the **real `dotnet-mgcb` 3.8.5** with the built plugin (`.mgcb` route, the one
that ships today), and identically in the Builder probe:

| MonoGame 3.8.5 `/platform:` | plugin sees | result |
|---|---|---|
| `Web` (12) | `Stadia` | **`SD0501` "platform 'Web' is not supported … Supported MGCB platforms: … Web …"** — a loud false refusal whose message lists the platform as supported |
| `DesktopVK` (13) | `Web` → OpenGL | **"Build 1 succeeded"**: an `.xnb` with platform byte `V` whose payload is **byte-identical to the CLI's `OpenGL` output** — a GLSL effect for a Vulkan runtime, silently |
| `WindowsDX12` (14) | (unmapped) | `SD0501` — loud, but DirectX 12 is a rung-4 target ShadowDusk has |
| `DesktopGL`, `Windows` | correct | correct (the only two platforms `validation/MgcbPlugin` exercises) |

This **contradicts `project_facts.md`** ("3.8.2.1105 … is a floor, not a pin: the plugin contract has
been stable across 3.8.x"): the *type* contract is stable; the **enum numbering is not**, and the
existing gate could not see it because it runs the tool-manifest `dotnet-mgcb` 3.8.4.1 (old
numbering) on two platforms whose numbers did not move. It is also the seamlessness directive's
worst case — the consumer flips nothing and gets a broken artifact.

**The fix is a name-based map** (Area B): switch on `context.TargetPlatform.ToString()`. In a 3.8.5
process the loaded enum type *is* 3.8.5's, so value 13 stringifies to `"DesktopVK"`; on 3.8.2.1105
it stringifies to `"Web"`. Names are what `/platform:` and `-p` parse and what `MonoGamePlatform`
carries, so they are the actual contract. A name map can name `DesktopVK`, `WindowsDX12` and
`XboxSeries` without compiling against 3.8.5, keeps the 3.8.2.1105 floor, and lets DirectX 12 and
Vulkan be **derived from the platform** on 3.8.5 — the seamless pattern — with `ShaderProfile`
staying as the escape hatch for pre-3.8.5 MGCB, which cannot name them.

### 2.5 Why the tools-only package cannot be the answer

`ShadowDusk.MgcbPlugin` has no `lib/`, so `using ShadowDusk.MgcbPlugin;` in a Builder project does
not compile; `SuppressDependenciesWhenPacking` means no ShadowDusk/Vortice dependency edges flow;
`DevelopmentDependency` is the wrong signal for a project that needs runtime assets. Hand-pointing a
`<Reference>` at `tools/net8.0/any/ShadowDusk.MgcbPlugin.dll` would leave the Builder's output
without the natives and without `ShadowDusk.Compiler`'s own assets, i.e. the exact failure the
tools layout exists to prevent inside MGCB. And the csproj comment's reason for excluding `lib/` —
never flow into the game assembly — does not describe a Builder, which is a separate executable that
already carries `MonoGame.Framework.Content.Pipeline` and hundreds of MB of MonoGame tool natives.

---

## 3. Areas

### Area A — unblock: make ShadowDusk's dependency graph survive `Assembly.GetTypes()` (prerequisite; gates the phase)

- **A1 (recommended). Own the `ID3D12ShaderReflection` interop and drop `Vortice.Direct3D12` from
  `ShadowDusk.HLSL`.** `IDxcUtils.CreateReflection` is in `Vortice.Dxc` (kept); the result is a COM
  object whose wrapper *type* is the only thing `Vortice.Direct3D12` contributes. Replace the nine
  consumed types with ShadowDusk-owned declarations over `SharpGen.Runtime` (already in the graph
  via `Vortice.Dxc`): the four reflection interfaces' vtable slots we call (`GetDesc`,
  `GetConstantBufferByIndex`, `GetResourceBindingDesc`, `GetInputParameterDesc`,
  `GetVariableByIndex`, `GetType`, `GetMemberTypeByIndex`, …) and the five `D3D12_*_DESC` structs,
  blittable. Same DXC, same reflection calls, same pinned native — a lookup path, never a substitute
  compiler. **Acceptance is zero byte change**: every golden, the cross-host byte-identity manifest,
  and the DX12 gates must be green unchanged. It also removes `Vortice.Direct3D12` and
  `Vortice.DXGI` from every consumer's graph. The `DxbcBackend.D3DCompiler` oracle keeps its
  `Vortice.D3DCompiler` reference (it passes the scan).
- **A2 (alternative, not recommended first).** Route desktop OpenGL/DX11 and DX12 reflection through
  the pure-managed `SpirvReflector` (Phase 19 proved it byte-identical to the DXIL oracle for GL;
  Android already auto-selects it) and `SpirvVertexInputReflector` (Vulkan already uses it), deleting
  `DxilReflectionExtractor` outright. Costs: DX12 would need a parallel `-spirv` compile purely for
  reflection (slower builds, and HLSL that compiles to DXIL but not SPIR-V would start failing), and
  DX12's `includeSamplerParameters = false` special case plus `DxilVertexInputReflector` semantics
  would need re-proving against the real `mgfxc` DX12 golden. Keep as the fallback if A1's interop
  turns out larger than sized.
- **A3 (rejected).** Isolating the DXIL reflector in a separately, lazily loaded assembly so no
  static reference reaches `Vortice.Direct3D12` before the scan. It works only by load *order*
  (the scan runs once at `Run`; a consumer that compiles anything before `Run` crashes again), and
  it is a trimming/AOT smell.
- **A4. Upstream, in parallel, never as a substitute.** (i) MonoGame: an issue + PR making
  `ContentBuilderHelper.LoadAssemblies` guard `GetTypes()` the way its own `PipelineManager` does
  (`catch (ReflectionTypeLoadException e) → e.Types.Where(t => t is not null)`), so *any*
  dependency with one unloadable type stops taking the whole Builder down; (ii) Vortice: the
  `VersionedDeviceRemovedExtendedData.Union` layout. Neither helps a consumer on 3.8.5 today.
- **A5. Regression guard.** A test that reproduces the scan exactly — load every assembly
  `ShadowDusk.Compiler` transitively references (the same `Assembly.Load` walk, skipping `System.*`)
  and `GetTypes()` each — so a future dependency cannot re-introduce the failure silently.
  Integration-tagged if it must load from the build output; it is the check that turns "we removed
  Vortice.Direct3D12" into "the graph is scan-clean".

### Area B — fix the `TargetPlatform` map (ships in the plugin whether or not Area C ships)

- **B1.** `MgcbPlatformMap.FromTargetPlatform` keys on **`platform.ToString()`**, not on enum members.
  Unit-test both numberings by feeding the raw values `(TargetPlatform)12/13/14/15` and the names
  side by side, so the test fails if anyone reverts to member matching.
- **B2.** Map `DesktopVK → PlatformTarget.Vulkan`, `WindowsDX12 → PlatformTarget.DirectX12`
  (DX12 content must still be built on Windows for DXIL signing; the existing `SD0214` path applies);
  `XboxSeries` joins the console `SD0501` set; `Stadia` stays refused. `ShaderProfile` remains the
  documented escape hatch for MGCB < 3.8.5.
- **B3.** `SD0501`'s message stops listing the offending name as supported (build the list from the
  map, not a literal).
- **B4.** `validation/MgcbPlugin` gains a **3.8.5 `dotnet-mgcb` arm** (fetched by `PackageDownload`
  the way `validation/ForwardCompat` takes its runtimes; the tool-manifest 3.8.4.1 arm stays) with
  `/platform:Web`, `DesktopVK`, `WindowsDX12` cases asserting the *target* the payload actually is
  (CLI `OpenGL` / `Vulkan` / `DirectX_12` byte-identity), and a negative case that a `V`-byte `.xnb`
  never carries an OpenGL payload again. `MgcbPluginByteIdentityTests` gets the same three
  platforms through `FakeContentProcessorContext`.
- **B5.** `project_facts.md`: correct the two "floor / contract stable" lines to say the type
  contract is stable and the enum numbering is not; `docs/error-codes.md` `SD0501` text; the plugin
  README/guide platform table gains the two new rows. This is a bug in a shipped package found
  while chasing the stated goal — CLAUDE.md says fix it, not ask; it is listed here because this
  wave is documentation-only, and it should ship **ahead of** the rest of the phase if the phase
  slips.

### Area C — the library package: `ShadowDusk.ContentPipeline`

- **C1. Shape: a NEW `lib/net8.0` package, not `lib/` added to `ShadowDusk.MgcbPlugin`.** The
  tools-only package's three properties (`IncludeBuildOutput=false`, `SuppressDependenciesWhenPacking`,
  `DevelopmentDependency`) are each load-bearing for MGCB and each wrong for a library consumer, who
  needs real dependency edges so `ShadowDusk.Compiler` and the natives flow transitively. One
  package cannot be both. Sketch:

  ```xml
  <!-- src/ShadowDusk.ContentPipeline/ShadowDusk.ContentPipeline.csproj -->
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net8.0</TargetFramework>   <!-- single-TFM like MgcbPlugin: Content.Pipeline itself is net8.0-only; a net9/net10 Builder references it fine (measured) -->
      <AssemblyName>ShadowDusk.ContentPipeline</AssemblyName>
      <RootNamespace>ShadowDusk.ContentPipeline</RootNamespace>
      <PackageId>ShadowDusk.ContentPipeline</PackageId>
      <IsPackable>true</IsPackable>
      <DevelopmentDependency>true</DevelopmentDependency>  <!-- build-time signal only; does not remove assets from the Builder's own output -->
    </PropertyGroup>
    <ItemGroup>
      <!-- ONE copy of the logic: the same five files the plugin compiles. -->
      <Compile Include="..\ShadowDusk.MgcbPlugin\ShadowDuskEffectImporter.cs"     Link="ShadowDuskEffectImporter.cs" />
      <Compile Include="..\ShadowDusk.MgcbPlugin\ShadowDuskEffectProcessor.cs"    Link="ShadowDuskEffectProcessor.cs" />
      <Compile Include="..\ShadowDusk.MgcbPlugin\MgcbPlatformMap.cs"              Link="MgcbPlatformMap.cs" />
      <Compile Include="..\ShadowDusk.MgcbPlugin\RecordingIncludeResolver.cs"     Link="RecordingIncludeResolver.cs" />
      <Compile Include="..\ShadowDusk.MgcbPlugin\PluginNativeLibraryResolver.cs"  Link="PluginNativeLibraryResolver.cs" />
      <Compile Include="..\ShadowDusk.Cli\MgcbErrorFormatter.cs"                  Link="Cli\MgcbErrorFormatter.cs" />
    </ItemGroup>
    <ItemGroup>
      <!-- A REAL dependency edge, deliberately: the Builder project must see the same contract types. -->
      <PackageReference Include="MonoGame.Framework.Content.Pipeline" />
      <ProjectReference Include="..\ShadowDusk.Compiler\ShadowDusk.Compiler.csproj" />
    </ItemGroup>
  </Project>
  ```

  `packages.lock.json` per the repo convention; `README.md` + icon like the other packages;
  `PluginNativeLibraryResolver` stays (measured harmless, and it keeps the files identical).
- **C2. Namespace: move the five types to `ShadowDusk.ContentPipeline`** (the assembly name
  `ShadowDusk.MgcbPlugin.dll` and the `tools/net8.0/any/` path are untouched — they are the `.mgcb`
  contract). Measured safe: MGCB's `PipelineManager` resolves `/importer:`/`/processor:` by
  **`type.Name`** (simple name) and the Builder matches `DefaultProcessor` the same way, so
  `samples/mgcb/Content/Content.ShadowDusk.mgcb` and every consumer `.mgcb` keep working
  byte-for-byte. Cost: the DocFX API namespace moves (no released consumer references the C#
  namespace today — that is the whole issue). Fallback if the owner prefers zero churn: keep
  `ShadowDusk.MgcbPlugin` as the namespace in both packages; nothing else changes.
- **C3. Floor: `MonoGame.Framework.Content.Pipeline` 3.8.2.1105, the same single `PackageVersion`
  as the plugin.** Our code uses only the `ContentImporter`/`ContentProcessor` contract, never the
  `Builder` namespace, so nothing needs 3.8.5 to compile; the 3.8.2.1105-compiled assembly binds
  in a 3.8.5 process (measured); a Builder project necessarily references 3.8.5 itself (the
  `Builder` namespace exists only there), so NuGet unifies upward regardless; and a 3.8.5 floor
  would exclude custom `PipelineManager`-based hosts on older versions for no gain. The enum
  renumbering is handled by Area B, not by the floor.
- **C4. `NoMonoGameInProductLibrariesTests`.** `NoSrcProjectReferencesMonoGame` exempts a
  two-element set `{ ShadowDusk.MgcbPlugin.csproj, ShadowDusk.ContentPipeline.csproj }` by name with
  the reason for each. `MgcbPluginTakesNoRuntimeMonoGameAssets` stays exactly as is (the plugin
  still must be compile-only + private). A new `ContentPipelinePackageDeclaresMonoGameDependency`
  pins the *opposite* for the library: a plain reference with **neither** `PrivateAssets="all"` nor
  `IncludeAssets="compile"`, because a Builder consumer needs the dependency edge and the runtime
  assets. A fourth test pins that the library's `<Compile Include>` set equals the plugin's `.cs`
  file set, so the "one copy of the logic" promise cannot drift.
- **C5. Facts/decisions.** `project_facts.md`: "ONLY project allowed to reference MonoGame" becomes
  "the two build-time delivery-shape projects (`MgcbPlugin`, compile-only + private; `ContentPipeline`,
  a real dependency) are the only ones", the package count becomes **nine**, and a line records why
  the exception does not weaken the guard: the concern was MonoGame reaching a *game* through a
  ShadowDusk *compiler* package, and a Content Builder is build-time tooling like MGCB — the shipped
  compiler libraries (`Core/HLSL/GLSL/Compiler/ShaderToy/Wasm`) stay MonoGame-free.
  `project_decisions.md`: "chose a second, library-shaped package (not `lib/` in the tools package,
  not a namespace-only export) because the three tools-only properties are each required by MGCB
  and each wrong for a library consumer"; "chose the name-based platform map because MonoGame
  renumbered the enum".
- **C6. Consumer surface** (package README + the site guide): the template csproj plus
  `<PackageReference Include="ShadowDusk.ContentPipeline" />`, and

  ```csharp
  using ShadowDusk.ContentPipeline;
  content.Include<WildcardRule>("Effects/*.fx", new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor());
  ```

  with three measured notes: **pass the instances** (auto-discovery picks MonoGame's pair);
  `DebugMode` defaults to optimized because the Builder has no build configuration; the target
  follows `-p` / `$(MonoGamePlatform)`, including `DesktopVK` and `WindowsDX12` once Area B lands.
- **C7. Release mechanics for a ninth package.** `release.yml`: add the project to the desktop
  pack loop and bump the count assertion (`Expected 7 desktop .nupkg` → 8) plus a presence gate that
  `lib/net8.0/ShadowDusk.ContentPipeline.dll` exists and the nuspec carries the
  `MonoGame.Framework.Content.Pipeline` dependency; `pack-consume.yml`: pack it alongside the four
  (its consume step is Area D2); `RELEASING.md` package table and every "eight" (reserve the
  nuget.org id at first publish); `.claude/skills/release/SKILL.md` "eight" mentions; `CHANGELOG.md`
  header sentence ("All eight `ShadowDusk.*` packages").

### Area D — evidence (rung 4, and the packaged shape)

- **D1. `validation/ContentBuilder` (Windows; GPU as `XnbContentLoad` needs; not in
  `ShadowDusk.slnx`).** One driver, two phases in one process:
  1. A real `ContentBuilder` subclass (`MonoGame.Framework.Content.Pipeline` **3.8.5**,
     `ManagePackageVersionsCentrally=false` or `VersionOverride`, the template's package list) builds
     the fixture set (`Grayscale`, `VertexAndPixel`, `MultiTexture`, `SpriteEffect` on Windows;
     the GL-compilable three on DesktopGL) through **both** content roots — `stock`
     (`EffectImporter`/`EffectProcessor`, the in-process 3.8.5 `mgfxc`-equivalent oracle) and `sd`
     (ShadowDusk's pair, **from the `ShadowDusk.ContentPipeline` project**, not the plugin) — by
     calling `Run(new ContentBuilderParams { Mode = Builder, Platform = …, … })` per platform.
  2. Asserts per asset: the `sd` payload is byte-identical to the `ShadowDuskCLI` binary's
     (`/Profile:` from the platform); the envelope through the type id is byte-identical to the
     `stock` `.xnb`; the payloads differ (positive control); then loads **both** through a real
     `ContentManager.Load<Effect>(assetName)` on **MonoGame 3.8.5** (`MonoGame.Framework.WindowsDX`
     3.8.5.1 for the Windows arm — required because the oracle's payload is MGFX v11 and does not
     load on 3.8.2.1105) and requires **pixel-identical** renders through the `XnbContentLoad`
     `SpriteBatch` path. A `DesktopVK`/`WindowsDX12` arm is added once Area B lands (rendered on
     `validation/VsDrivenVulkan` / `VsDrivenDx12`'s runtimes, the oracle arm subject to the
     mgfxc-side `ps_6_0` floor).

  **`docs/validation-matrix.md` §6 row (text):**
  > **MonoGame 3.8.5 Content Builder — the importer/processor pair as a LIBRARY, in a REAL `ContentBuilder`** ([Phase 63](../plan/PHASE-63-monogame-content-builder-library.md), [issue #203](https://github.com/kaltinril/ShadowDusk/issues/203)). **A render gate AND a delivery-shape gate.** A real `ContentBuilder` subclass over `MonoGame.Framework.Content.Pipeline` 3.8.5 builds each fixture twice in one run — once through MonoGame's stock `EffectImporter`/`EffectProcessor` (the in-process 3.8.5 oracle, MGFX v11) and once through `ShadowDusk.ContentPipeline`'s `ShadowDuskEffectImporter`/`ShadowDuskEffectProcessor` passed as instances — then asserts the ShadowDusk payload is byte-for-byte the `ShadowDuskCLI` binary's, the `.xnb` envelope is byte-for-byte the stock build's, the payloads differ, and **both `.xnb`s load through a real `ContentManager.Load<Effect>` on MonoGame 3.8.5 and render pixel-identical**. Also the proof that ShadowDusk's dependency graph survives the Builder's unguarded `Assembly.GetTypes()` scan (§2.3 of the phase doc), which no `dotnet test` can see. Not in CI: needs a GPU and a 3.8.5 runtime. | `validation/ContentBuilder` (self-asserting; the pure halves are `MgcbPlatformMapTests` and the scan-clean guard) | ❌ driver (GPU + 3.8.5) / ✅ the pure halves — **default-ON in `run-windows-render-gates.ps1`** | `dotnet run -c Release --project validation/ContentBuilder`, or the gate script |

  **`run-windows-render-gates.ps1` slot** (after the XNB direct-writer gate, default ON):
  ```powershell
  # MonoGame 3.8.5 Content Builder (Phase 63, issue #203). A REAL ContentBuilder subclass builds
  # the fixtures through MonoGame's stock pair AND through ShadowDusk.ContentPipeline's, then
  # Content.Load<Effect>s both on MonoGame 3.8.5 and requires pixel-identical renders. It is also
  # the only place the Builder's unguarded Assembly.GetTypes() scan is exercised against our real
  # dependency graph - a dependency that fails type-load takes the consumer's whole build down.
  $gates.Add(@{
      Name   = 'MonoGame 3.8.5 Content Builder (Phase 63: ShadowDusk.ContentPipeline pair vs stock pair, real ContentBuilder + Content.Load<Effect> on 3.8.5)'
      Action = {
          Invoke-Checked 'dotnet' @('build', 'src/ShadowDusk.Cli/ShadowDusk.Cli.csproj', '-c', 'Release')
          Invoke-Checked 'dotnet' @('run', '--project', 'validation/ContentBuilder', '-c', 'Release')
      }
  })
  ```
- **D2. Packed-package consumption (`pack-consume` shape).** Pack `ShadowDusk.ContentPipeline` into
  the local feed with the four it depends on; a scratch Builder project (the template csproj +
  `<PackageReference Include="ShadowDusk.ContentPipeline" Version="<smoke>" />`, restored from
  the feed) builds `Grayscale.fx` for `DesktopGL` and `Windows` through the ShadowDusk pair and the
  payload is compared to the CLI's. This is the check that the dependency edges and the natives
  actually flow to a cold consumer. Add it as a job in `pack-consume.yml` (label-gated like the
  rest; Linux is fine for this arm — see D3) and to `tools/verify-*.ps1` for local use.
- **D3. CI on Linux.** The **ShadowDusk arm** of D1/D2 (build + CLI byte-identity, no render) runs
  on `ubuntu` today: our natives ship for `linux-x64`, and the Builder itself is cross-platform.
  The **stock oracle arm** needs `MonoGame.Tool.Dxc` + `MonoGame.Library.MojoShader` `linux-x64`
  natives (both packages ship them; unverified here) and the **render** needs a GL context (the
  `validation-render.yml` llvmpipe lane) plus `MonoGame.Framework.DesktopGL` 3.8.5. Plausible, not
  measured; the Windows gate stays authoritative and the Linux lane is a follow-up, recorded as
  such in the §6 row when it lands.
- **D4. In-suite (pure, `dotnet test`).** `MgcbPlatformMapTests` (both numberings, B1); the
  scan-clean guard (A5); `NoMonoGameInProductLibrariesTests` additions (C4). The real-3.8.5
  in-process Builder is deliberately **not** dragged into `ShadowDusk.slnx` (it would pull 3.8.5 and
  the MonoGame tool natives into every `dotnet test`); it lives in `validation/`.

### Area E — support surfaces (same PR; the CLAUDE.md list, with what changes in each)

| Surface | Change |
|---|---|
| `README.md` | "Delivery shapes" gains the fifth shape (Content Builder library) with the two-line consumer snippet; the tree listing gains `ShadowDusk.ContentPipeline/`; the plugin's platform table gains `DesktopVK`/`WindowsDX12` (Area B) |
| `docs/the-purpose.md` | item 4 "Build-time delivery shapes" names the third package; the host × target matrix row for build-time shapes |
| `docs/validation-matrix.md` | §6 row (D1 text above) + a §6 note on the `pack-consume` arm; §7 gap rows for the Linux lane (D3) and the DesktopVK/DX12 Builder arms; §1 cells unchanged (same bytes) |
| `docs/repository-layout.md` | `src/ShadowDusk.ContentPipeline/`, `validation/ContentBuilder/`; the MgcbPlugin comment's "ONLY src/ project allowed a MonoGame reference" |
| `docs/pipeline-overview.puml` + regenerate `docfx/images/pipeline-overview.svg` | the "Delivery shapes" line (168-169) gains **Content Builder library** |
| `docfx/guides/mgcb-content-pipeline.md` | a new section for the 3.8.5 Content Builder (or a sibling guide `content-builder.md` linked from `toc.yml`); the platform table (Area B); the note that auto-discovery picks MonoGame's pair |
| `docfx/getting-started/overview.md`, `docfx/index.md` | delivery-shape tables |
| `docfx/getting-started/installation.md`, `docfx/guides/choosing-a-target.md`, `docfx/contributing/validation.md`, `docfx/glossary.md` | package list; the `.xnb` routes paragraph; the new driver; "Content Builder" glossary entry |
| `docfx/samples/mgcb.md` + `samples/mgcb` | optionally a `Content.ShadowDusk.Builder` project beside the two `.mgcb`s |
| `src/ShadowDusk.MgcbPlugin/README.md`, `src/ShadowDusk.ContentPipeline/README.md` | plugin README points at the library for Builder users; the library README is the C6 surface |
| XML doc-comments | `ShadowDuskEffectProcessor`'s class remarks (`ShaderProfile` "the only way to reach DX12/Vulkan" becomes "for MGCB before 3.8.5"); `MgcbPlatformMap` |
| `project_facts.md` / `project_decisions.md` | C5 and B5 |
| `plan/plan.md` | this row; on completion move to `DONE/` |
| `CHANGELOG.md` | `[Unreleased]`: Added (package), Fixed (Area B — the DesktopVK wrong-artifact and the Web false refusal on MonoGame 3.8.5), Changed (Area A — `Vortice.Direct3D12` dropped), and the header's "eight" |
| `RELEASING.md`, `.claude/skills/release/SKILL.md`, `.github/workflows/{release,pack-consume}.yml` | C7 |
| `CLAUDE.md` | the gate-command comment lists the new gate; the target table is unchanged |
| `docs/error-codes.md` | `SD0501` text (B3) |

---

## 4. Relationship to Phase 29 and Phase 60 — all routes stay; this is the fourth

| Route | Who it is for | What the consumer touches |
|---|---|---|
| **MGCB plugin** ([Phase 29](DONE/PHASE-29-mgcb-content-processor-plugin.md), `ShadowDusk.MgcbPlugin`, tools-only) | a team on a `.mgcb` (any MonoGame 3.8.1+ MGCB, KNI users on MonoGame's MGCB) | one `/reference:` line + importer/processor names |
| **CLI** | scripts, `/copy:` pipelines, anything that really invokes `mgfxc` | a build step |
| **Direct `.xnb`** ([Phase 60](DONE/PHASE-60-xnb-content-output.md), `CompiledShader.ToXnb()`) | a consumer who wants MonoGame's tooling out of the picture | nothing in the source tree |
| **Content Builder library** (this phase, `ShadowDusk.ContentPipeline`) | a 3.8.5+ team on the Content Builder project, which is the template default now | one `PackageReference` + passing two instances in `GetContentCollection` |

Nothing supersedes anything: the plugin remains the only route into `.mgcb`/MGCB, direct `.xnb`
remains the only MGCB-free route, and the library is the only route into a Builder. The same five
source files and the same `EffectCompiler.Compile` back all of the MGCB-family routes, which is what
makes the byte-identity claims cheap to keep true (C4's file-set test) and why Area B's fix reaches
both packages at once.

---

## 5. Acceptance

- [ ] **A (blocker):** `Vortice.Direct3D12` no longer in `ShadowDusk.HLSL`'s graph; full `dotnet test`
      green on both TFMs with **zero** golden or cross-host-manifest byte changes; the Windows render
      gates green unchanged; the scan-clean guard (A5) in the suite; upstream MonoGame + Vortice
      issues filed and linked here.
- [ ] **B:** name-based `MgcbPlatformMap`; `DesktopVK → Vulkan`, `WindowsDX12 → DirectX12`; `SD0501`
      text fixed; `validation/MgcbPlugin` 3.8.5 arm proves `Web`/`DesktopVK`/`WindowsDX12` produce
      the right payload; `project_facts.md` corrected.
- [ ] **C:** `ShadowDusk.ContentPipeline` packs as `lib/net8.0` with a real
      `MonoGame.Framework.Content.Pipeline >= 3.8.2.1105` dependency and `ShadowDusk.Compiler`
      transitive; `NoMonoGameInProductLibrariesTests` updated per C4; nine packages at one version
      through `release.yml`.
- [ ] **D1 (rung 4):** `validation/ContentBuilder` green — payload == CLI, envelope == stock 3.8.5,
      `Content.Load<Effect>` on MonoGame 3.8.5 pixel-identical to the stock build; default-ON in the
      gate script; §6 row written.
- [ ] **D2:** the packed package consumed cold from a local feed in a scratch Builder builds the
      fixture with CLI-identical bytes (`pack-consume.yml` job + local script).
- [ ] **E:** every surface in the Area E table updated in the same PR; SVG regenerated.
- [ ] No existing output byte moves for any consumer of any existing route.

## 6. Non-goals

- Replacing or deprecating the `.mgcb` plugin, the CLI route, or direct `.xnb` output (§4).
- A ShadowDusk-provided `ContentBuilder` subclass or content-collection helper. The consumer owns
  their Builder; ShadowDusk provides the two instances. (Revisit only if a real consumer asks for
  e.g. a one-call `content.IncludeShadowDuskEffects("Effects/*.fx")` extension — cheap, but it
  would be the only Builder-API coupling in the package and would move the floor to 3.8.5.)
- Winning extension auto-discovery over MonoGame's stock pair. Not possible by design (§2.2).
- The Builder's `server` mode. Same importer/processor, nothing to do; untested and unclaimed.
- KNI. KNI ships neither MGCB nor a Content Builder; a KNI team on MonoGame's Builder gets the same
  v10 `.mgfx` and is covered by the same proof.
- Any change to what the pair *compiles*: the Phase 41 GAP-1 GL half (`SD0010`) stays a library
  gap, identical through every route.

## 7. Open questions

- **OQ1. Does a 3.8.2.1105-compiled importer/processor bind inside a 3.8.5 Builder process?
  ANSWERED: yes** (§2.2, measured; the reverse direction would not, which is why the floor stays).
- **OQ2. Is `PluginNativeLibraryResolver` needed in the library shape? ANSWERED: no, and harmless**
  (§2.2; unsubscribed, both platforms build identically). Kept so the files stay one copy.
- **OQ3. Which importer does the Builder pick for `.fx` when both are loaded? ANSWERED: MonoGame's,
  silently** (§2.2). Instances are the route; documented as such.
- **OQ4. Does the consumer's Builder project need anything beyond the pipeline package?
  ANSWERED: yes**, a `MonoGame.Framework` provider (`MonoGame.Framework.Native`, as the template
  does), because the pipeline package references but does not depend on it (§2.1).
- **OQ5. Does the existing pair work in the pattern as-is? ANSWERED: the pair, yes — the graph, no**
  (§2.2 vs §2.3). The blocker is one `Vortice.Direct3D12` struct and the Builder's unguarded scan.
- **OQ6. Is Area A byte-transparent? EXPECTED yes, to be proven by the goldens**: same DXC, same
  `IDxcUtils::CreateReflection`, same vtable calls, only the managed declarations change. A single
  byte of drift fails the existing suite.
- **OQ7. Can the gate run in CI on Linux? PARTLY** (D3): the ShadowDusk build + byte-identity arm
  yes; the stock-oracle and render arms plausible on llvmpipe with 3.8.5 DesktopGL but unmeasured.
- **OQ8. Namespace: move or keep? RECOMMENDED move to `ShadowDusk.ContentPipeline`** (C2; measured
  safe because both MonoGame lookups are by simple type name). Owner call; the fallback costs
  nothing but a slightly odd `using`.
- **OQ9. Should Area B ship as a hotfix ahead of the package?** Recommended yes: it is a silent
  wrong-artifact bug on the current stable MonoGame in a released package, independent of Areas A
  and C, and small. The owner decides the release cut.
- **OQ10. Floor 3.8.2.1105 or 3.8.5 for the library? ANSWERED by C3: 3.8.2.1105** — nothing we
  compile needs 3.8.5, binding upward is measured, and the enum problem is solved by names, not by
  the floor.

---

## Appendix A — the probe (reusable by the implementation wave)

All paths under the session scratchpad `…/scratchpad/issue203/`. The MonoGame source used is a
sparse clone of `https://github.com/MonoGame/MonoGame` at tag **`v3.8.5`** (`git clone --depth 1
--branch v3.8.5 --filter=blob:none --sparse`, then `git sparse-checkout set
MonoGame.Framework.Content.Pipeline Tools/MonoGame.Effect.Compiler`). The templates were read from
`MonoGame.Templates.CSharp` 3.8.5 fetched with a `<PackageDownload Include="MonoGame.Templates.CSharp" Version="[3.8.5]" />`
project; `dotnet-mgcb` 3.8.5 the same way.

**`cb/cb.csproj`** — the 3.8.5 template's project list verbatim, `net8.0`, plus the thing under test:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>   <!-- also run as net10.0: identical result -->
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.Content.Pipeline" Version="3.8.5" />
    <PackageReference Include="MonoGame.Framework.Native" Version="3.8.5"><PrivateAssets>All</PrivateAssets></PackageReference>
    <PackageReference Include="MonoGame.Library.FreeType" Version="2.13.2.3" />
    <PackageReference Include="MonoGame.Library.MojoShader" Version="1.0.0.5" />
    <PackageReference Include="MonoGame.Tool.Basisu" Version="2.0.2.1" />
    <PackageReference Include="MonoGame.Tool.Crunch" Version="1.0.4.7" />
    <PackageReference Include="MonoGame.Tool.Dxc" Version="1.8.2505.11" />
    <PackageReference Include="MonoGame.Tool.FFmpeg" Version="7.0.0.10" />
    <PackageReference Include="MonoGame.Tool.FFprobe" Version="7.0.0.10" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="<worktree>\src\ShadowDusk.MgcbPlugin\ShadowDusk.MgcbPlugin.csproj" />
  </ItemGroup>
</Project>
```

`cb/Assets/Effects/` holds `Grayscale.fx`, `VertexAndPixel.fx`, `MultiTexture.fx`, `SpriteEffect.fx`
and every `tests/fixtures/shaders/*.fxh` (SpriteEffect includes `Macros.fxh`). A `nuget.config`
with only nuget.org keeps the repo's feeds out of it.

**`cb/Program.cs`** (the `--hide-d3d12` branch is the §2.3 workaround, probe-only: it hides
`Vortice.Direct3D12.dll` until `GetContentCollection` runs — the first virtual call after the scan —
then serves it on demand; `--no-shim` measures OQ2; `--auto` measures OQ3):

```csharp
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using MonoGame.Framework.Content.Pipeline.Builder;
using ShadowDusk.MgcbPlugin;

// cb build -p <Platform> -s Assets -o out/<Platform> -i obj/<Platform> [--hide-d3d12] [--no-shim] [--auto]
bool noShim = args.Contains("--no-shim"), auto = args.Contains("--auto"), hide = args.Contains("--hide-d3d12");
args = args.Where(a => a != "--no-shim" && a != "--auto" && a != "--hide-d3d12").ToArray();

string bin = AppContext.BaseDirectory;
string d3d12 = Path.Combine(bin, "Vortice.Direct3D12.dll"), hidden = Path.Combine(bin, "hidden", "Vortice.Direct3D12.dll");
if (hide)
{
    Directory.CreateDirectory(Path.GetDirectoryName(hidden)!);
    if (File.Exists(d3d12)) File.Move(d3d12, hidden, overwrite: true);
    AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        name.Name == "Vortice.Direct3D12" && Builder.ScanDone ? ctx.LoadFromAssemblyPath(hidden) : null;
}

var sdImporter  = new ShadowDuskEffectImporter();   // static ctors register PluginNativeLibraryResolver here
var sdProcessor = new ShadowDuskEffectProcessor();
if (noShim)
{
    var t = typeof(ShadowDuskEffectImporter).Assembly.GetType("ShadowDusk.MgcbPlugin.PluginNativeLibraryResolver")!;
    var m = t.GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static)!;
    AssemblyLoadContext.Default.ResolvingUnmanagedDll -=
        (Func<Assembly, string, IntPtr>)Delegate.CreateDelegate(typeof(Func<Assembly, string, IntPtr>), m);
}

var builder = new Builder(sdImporter, sdProcessor, auto);
try { builder.Run(args); }
finally { if (hide && File.Exists(hidden)) File.Move(hidden, d3d12, overwrite: true); }
Console.WriteLine($"[probe] succeeded={builder.SucceededToBuild} failed={builder.FailedToBuild}");
Console.WriteLine($"[probe] Content.Pipeline in process: {typeof(ContentBuilder).Assembly.GetName().Version}");
Console.WriteLine($"[probe] MgcbPlugin compiled against: {typeof(ShadowDuskEffectImporter).Assembly.GetReferencedAssemblies().First(a => a.Name == "MonoGame.Framework.Content.Pipeline").Version}");
Console.WriteLine($"[probe] Vortice.Direct3D12 loaded now: {AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Vortice.Direct3D12")}");
return builder.FailedToBuild > 0 ? -1 : 0;

public class Builder(IContentImporter sdImporter, IContentProcessor sdProcessor, bool auto) : ContentBuilder
{
    public static bool ScanDone;
    public override IContentCollection GetContentCollection()
    {
        ScanDone = true;
        var c = new ContentCollection();
        c.SetContentRoot("stock");
        c.Include<WildcardRule>("Effects/*.fx", new EffectImporter(), new EffectProcessor());
        c.SetContentRoot("sd");
        if (auto) c.Include<WildcardRule>("Effects/*.fx");
        else      c.Include<WildcardRule>("Effects/*.fx", sdImporter, sdProcessor);
        return c;
    }
}
```

**Commands and what they produced** (worktree natives restored with `tools/restore.ps1`; plugin
and CLI built `-c Release` first):

```
dotnet build -c Release
bin/Release/cb.exe build -p Windows     -s Assets -o out/Windows     -i obj/Windows                 # -> ReflectionTypeLoadException (§2.3), every platform, with or without --no-shim
bin/Release/cb.exe build --rebuild -p Windows     -s Assets -o out/Windows     -i obj/Windows     --hide-d3d12   # 8 succeeded
bin/Release/cb.exe build --rebuild -p DesktopGL   -s Assets -o out/DesktopGL   -i obj/DesktopGL   --hide-d3d12   # 7 succeeded, SpriteEffect SD0010
bin/Release/cb.exe build --rebuild -p DesktopVK   -s Assets -o out/DesktopVK   -i obj/DesktopVK   --hide-d3d12   # sd arm "succeeds" with OpenGL bytes (§2.4); stock arm refuses ps_4_0_level_9_1 fixtures
bin/Release/cb.exe build --rebuild -p WindowsDX12 -s Assets -o out/WindowsDX12 -i obj/WindowsDX12 --hide-d3d12   # sd arm SD0501
bin/Release/cb.exe build --rebuild -p Windows   ... --hide-d3d12 --no-shim                                        # 8 succeeded (OQ2)
bin/Release/cb.exe build --rebuild -p DesktopGL ... --hide-d3d12 --auto -l Debug                                  # "[D] Importer: EffectImporter / Processor: EffectProcessor" for every file (OQ3)
```

**`xnbcheck`** (a 60-line console tool, `Program.cs` in the scratchpad): parses each
`out/<Platform>/sd/Effects/*.xnb` with the `validation/MgcbPlugin` `XnbEffect.Parse` logic, runs
`ShadowDuskCLI <fixture> <tmp>.mgfx /Profile:<profile>` for each candidate profile, and prints
platform byte, MGFX version byte, SHA-256 prefix, `IDENTICAL`/`different` per profile, and the
header/manifest comparison against `out/<Platform>/stock/…`. Its output is the §2.2 table.

**`gettypes`** (a 40-line console tool): `LoadFromAssemblyPath` + `GetTypes()` over every DLL in a
directory with a `Resolving` handler for that directory, printing the loader exceptions — the §2.3
sweep. A `meta` mode uses `System.Reflection.Metadata` to list an assembly's references and the
fields/offsets of every nested `Union`; a `types` mode loads each `…+Union` by name to find the one
that fails.

**`mgcb385/*.mgcb`** — the §2.4 measurement on the `.mgcb` route:

```
/outputDir:bin_<P>
/intermediateDir:obj_<P>
/platform:<P>
/config:
/profile:Reach
/compress:False

/reference:<worktree>/src/ShadowDusk.MgcbPlugin/bin/Release/net8.0/ShadowDusk.MgcbPlugin.dll

#begin <abs>/Grayscale.fx
/importer:ShadowDuskEffectImporter
/processor:ShadowDuskEffectProcessor
/build:<abs>/Grayscale.fx;Grayscale
```

run as `dotnet ~/.nuget/packages/dotnet-mgcb/3.8.5/tools/net8.0/any/mgcb.dll /@:<P>.mgcb` for
`Web`, `DesktopVK`, `WindowsDX12`, `DesktopGL`.

## Appendix B — the reflection dump

`reflect/` is a `net8.0` console referencing `MonoGame.Framework.Content.Pipeline` 3.8.5 **and**
`MonoGame.Framework.DesktopGL` 3.8.5 (without the latter it cannot even start — OQ4). It enumerates
every exported type in the `MonoGame.Framework.Content.Pipeline.Builder` namespace with
constructors, properties, methods (generic constraints and default values included), fields, the
`ContentProcessorContext` members, the `TargetPlatform` values, every type deriving
`ContentProcessorContext` or implementing `IContentCollection`, and the assembly's references. §2.1
is its output, condensed. The 3.8.2.1105 enum was read from the cached package with
`[System.Reflection.Assembly]::LoadFile(...)` + `[Enum]::GetNames` in PowerShell.
