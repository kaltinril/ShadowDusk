#nullable enable

using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Core;

/// <summary>
/// Settings that control a single <see cref="IShaderCompiler.CompileAsync"/> call: the
/// target backend, how <c>#include</c> directives are resolved, debug output, the MGFX
/// container version, and (for DirectX) which DXBC backend to use.
/// </summary>
public sealed class CompilerOptions
{
    /// <summary>
    /// The platform backend to compile for. Defaults to <see cref="PlatformTarget.OpenGL"/>.
    /// Note this differs from the CLI's default profile (<c>DirectX_11</c>).
    /// </summary>
    public PlatformTarget Target { get; init; } = PlatformTarget.OpenGL;

    /// <summary>
    /// An optional, render-proven <see cref="CapabilityProfile"/> naming a full (runtime, format)
    /// contract: when set it selects the effect container/version <i>and</i> the GL
    /// <see cref="ShaderDialect"/>, overriding <see cref="Container"/> / <see cref="MgfxVersion"/>.
    /// Defaults to <see langword="null"/>, which reproduces today's behavior exactly (the container
    /// comes from <see cref="Container"/> / <see cref="MgfxVersion"/> and the dialect from
    /// <see cref="Target"/>). A profile is never required for correct output, only an additive,
    /// named convenience the runtime-detection helper also returns.
    /// </summary>
    public CapabilityProfile? Profile { get; init; }

    /// <summary>
    /// Optional custom resolver for <c>#include</c> directives. When <see langword="null"/>,
    /// includes are resolved relative to <see cref="SourceFileName"/> and
    /// <see cref="AdditionalIncludePaths"/>. Supply an in-memory resolver to compile without
    /// touching disk (e.g. in WASM/in-browser scenarios).
    /// </summary>
    public IIncludeResolver? IncludeResolver { get; init; }

    /// <summary>
    /// Additional directories searched, in order, when resolving <c>#include</c> directives.
    /// Equivalent to the CLI's <c>/I</c> flag.
    /// </summary>
    public IReadOnlyList<string> AdditionalIncludePaths { get; init; } = [];

    /// <summary>
    /// The logical source file name used for include resolution and for the file path
    /// reported in <see cref="ShaderError"/> diagnostics. Optional when compiling a string
    /// literal in memory.
    /// <para>
    /// <b>It can also reach the output bytes.</b> An MGFX v11 container stores a source-file
    /// string per shader, and <see cref="PlatformTarget.DirectX12"/> and
    /// <see cref="PlatformTarget.Vulkan"/> are always v11 (as is any target compiled with
    /// <see cref="MgfxVersion"/> <c>11</c>). Unless <see cref="EmbeddedSourceFileName"/> says
    /// otherwise, that string is this value exactly as passed, which is what <c>mgfxc</c>
    /// does with its own source argument: pass an absolute path and the compiled effect
    /// carries it, and changes with it. With <see cref="Debug"/> set, the same name also goes
    /// into the SPIR-V / DXIL debug information. MGFX v10, KNIFX and the FNA <c>fx_2_0</c>
    /// container store no source name, so <see cref="PlatformTarget.OpenGL"/>,
    /// <see cref="PlatformTarget.DirectX"/> and <see cref="PlatformTarget.Fna"/> output does
    /// not depend on it.
    /// </para>
    /// </summary>
    public string? SourceFileName { get; init; }

    /// <summary>
    /// The source-file string stored <b>inside</b> the compiled effect, for the containers
    /// that have one (MGFX v11: always for <see cref="PlatformTarget.DirectX12"/> and
    /// <see cref="PlatformTarget.Vulkan"/>, and for any target compiled with
    /// <see cref="MgfxVersion"/> <c>11</c>). The runtime only ever shows it in shader error
    /// messages; it has no effect on rendering.
    /// <para>
    /// Defaults to <see langword="null"/>, which stores <see cref="SourceFileName"/> exactly
    /// as passed (what <c>mgfxc</c> does), or <c>&lt;unknown&gt;</c> when that is
    /// <see langword="null"/> too. Only <see langword="null"/> falls back: any other value is
    /// stored verbatim, so an empty string stores an empty string. Set it when the name the compiler needs is not a name
    /// the output should carry: a build tool that compiles from absolute paths can keep
    /// <see cref="SourceFileName"/> absolute, so <c>#include</c> resolution and
    /// <see cref="ShaderError"/> locations stay exact, while the effect records a stable
    /// string and stops changing with the checkout directory. ShadowDusk's own MonoGame
    /// content processor, and the CLI when it writes an <c>.xnb</c>, set it to
    /// <c>&lt;unknown&gt;</c>, which is what MonoGame's stock <c>EffectProcessor</c> writes.
    /// </para>
    /// <para>
    /// This never changes diagnostics, include resolution, or the debug information emitted
    /// under <see cref="Debug"/>, all of which use <see cref="SourceFileName"/>. Ignored by
    /// the containers with no such field (MGFX v10, KNIFX, FNA <c>fx_2_0</c>).
    /// </para>
    /// </summary>
    public string? EmbeddedSourceFileName { get; init; }

    /// <summary>
    /// When <see langword="true"/>, compiles with debug information enabled. Deliberately a
    /// no-op for <see cref="PlatformTarget.Fna"/>: MojoShader is stricter on fxc
    /// debug-style codegen, so the FNA path always compiles optimized — Debug can never
    /// produce a <c>.fxb</c> the FNA runtime rejects.
    /// <para>
    /// Debug output depends only on the source and these options, never on the files on the
    /// host's disk: the source text embedded in SPIR-V debug information
    /// (<see cref="PlatformTarget.Vulkan"/>, and the SPIR-V behind
    /// <see cref="PlatformTarget.OpenGL"/>) is the text ShadowDusk compiled, on every host.
    /// </para>
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>
    /// The MGFX container version to emit. Defaults to <c>10</c>, which loads across the
    /// supported MonoGame/KNI runtimes (the backwards-compatible choice). Ignored for
    /// <see cref="PlatformTarget.Fna"/>, whose output is the D3D9 fx_2_0 container, not MGFX.
    /// Also ignored when <see cref="Container"/> is <see cref="EffectContainer.Knifx"/>.
    /// </summary>
    public int MgfxVersion { get; init; } = 10;

    /// <summary>
    /// Which effect container to emit. Defaults to <see cref="EffectContainer.Mgfx"/>
    /// (MGFX v10), which loads on every MonoGame/KNI runtime, the seamless default.
    /// Set <see cref="EffectContainer.Knifx"/> to opt into KNI's additive KNIFX v11
    /// container (KNI v4.02+). This is a non-required escape hatch / additive target,
    /// never needed for correct v10 output. Ignored for <see cref="PlatformTarget.Fna"/>.
    /// </summary>
    public EffectContainer Container { get; init; } = EffectContainer.Mgfx;

    /// <summary>
    /// Which backend compiles HLSL to SM5 DXBC when <see cref="Target"/> is
    /// <see cref="PlatformTarget.DirectX"/>. Defaults to <see cref="DxbcBackend.Vkd3d"/>,
    /// the cross-platform shipping backend: it runs on every desktop OS and is
    /// host-independent, so a default DirectX compile produces the same bytes on
    /// Linux, macOS, and Windows. Set <see cref="DxbcBackend.D3DCompiler"/> to opt in
    /// to the Windows-only d3dcompiler_47 correctness oracle (it hard-fails off
    /// Windows). Ignored for non-DirectX targets — <see cref="PlatformTarget.Fna"/>
    /// always uses vkd3d-shader. On the browser/WASM host this option is moot:
    /// <c>WasmShaderCompiler</c> always compiles DXBC via the vkd3d-shader WASM backend
    /// (there is no d3dcompiler_47 in a browser), which matches the desktop default.
    /// </summary>
    public DxbcBackend DxbcBackend { get; init; } = DxbcBackend.Vkd3d;

    /// <summary>
    /// User preprocessor macros injected into the compile after the platform macros —
    /// the library-level equivalent of mgfxc's <c>/Defines:</c> flag (which the CLI maps
    /// here). They ride through both macro renderers (the <c>#define</c> prepend and the
    /// DXC <c>-D</c> flags), so every backend sees them. Empty by default.
    /// </summary>
    public IReadOnlyList<Preprocessor.UserDefine> Defines { get; init; } = [];

    /// <summary>
    /// Internal seam for the real-slangc route (issues #302, #340): the names of the sampler
    /// arrays slangc emitted as the sampler half of a combined-sampler ARRAY the author declared
    /// (<c>Sampler2D T[N]</c> becomes <c>Texture2D T_texture_0[N]</c>, renamed to <c>T</c>, plus
    /// <c>SamplerState T_sampler_0[N]</c>). Those are one author resource, lowered, not an author's
    /// <c>SamplerState S[N]</c>, so the DirectX sampler-array refusal (<c>SD0224</c>) skips them and
    /// the texture half carries the array diagnostics (<c>SD0221</c>, <c>SD0222</c>), giving the same
    /// one-parameter table the hand-written <c>Texture2D T[N]; SamplerState S;</c> gets. Empty for
    /// every <c>.fx</c> compile. Not a consumer setting: an author never needs it for correct output.
    /// </summary>
    internal IReadOnlyCollection<string> SamplerArraysFromCombinedSamplers { get; init; } = [];

    /// <summary>
    /// Internal test seam (issue #358): when <see langword="true"/>, the DirectX and FNA targets
    /// call their D3D-bytecode backend directly instead of through the per-run memo
    /// (<c>MemoizingDxbcCompiler</c>, issue #255), so a test can compile the same effect with and
    /// without the memo and compare the bytes. <see langword="false"/> (the memo) for every real
    /// compile. Not a consumer setting: the memo cannot change output, it only skips repeat calls.
    /// </summary>
    internal bool BypassDxbcMemo { get; init; }

    /// <summary>
    /// Returns a copy with <see cref="Target"/> replaced by <paramref name="graphicsTarget"/>,
    /// preserving every other setting. The pipeline uses this to apply a
    /// <see cref="CapabilityProfile.GraphicsTarget"/> (a profile fully specifies its output
    /// backend, so a set <see cref="Profile"/> determines the backend).
    /// </summary>
    public CompilerOptions WithGraphicsTarget(PlatformTarget graphicsTarget) =>
        Copy(graphicsTarget, SamplerArraysFromCombinedSamplers);

    /// <summary>
    /// Returns a copy with <see cref="SamplerArraysFromCombinedSamplers"/> replaced, preserving every
    /// other setting (the real-slangc route's seam; see that property).
    /// </summary>
    internal CompilerOptions WithSamplerArraysFromCombinedSamplers(IReadOnlyCollection<string> names) =>
        Copy(Target, names);

    private CompilerOptions Copy(PlatformTarget target, IReadOnlyCollection<string> samplerArraysFromCombinedSamplers) => new()
    {
        Target                 = target,
        Profile                = Profile,
        IncludeResolver        = IncludeResolver,
        AdditionalIncludePaths = AdditionalIncludePaths,
        SourceFileName         = SourceFileName,
        EmbeddedSourceFileName = EmbeddedSourceFileName,
        Debug                  = Debug,
        MgfxVersion            = MgfxVersion,
        Container              = Container,
        DxbcBackend            = DxbcBackend,
        // Every property above MUST be copied — "preserving every other setting" is the
        // documented contract, and this is the pipeline's own normalization step, so a
        // dropped property silently changes what gets compiled. Defines was missed once:
        // `--target-runtime monogame-gl /Defines:X` compiled with X undefined, and
        // ValidateAsync (which calls this per target) reported on a different source than
        // CompileAsync would produce. A round-trip test pins this.
        Defines                = Defines,
        SamplerArraysFromCombinedSamplers = samplerArraysFromCombinedSamplers,
        BypassDxbcMemo         = BypassDxbcMemo,
    };
}
