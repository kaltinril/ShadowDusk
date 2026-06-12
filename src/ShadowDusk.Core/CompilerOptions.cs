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
    /// </summary>
    public string? SourceFileName { get; init; }

    /// <summary>
    /// When <see langword="true"/>, compiles with debug information enabled. Deliberately a
    /// no-op for <see cref="PlatformTarget.Fna"/>: MojoShader is stricter on fxc
    /// debug-style codegen, so the FNA path always compiles optimized — Debug can never
    /// produce a <c>.fxb</c> the FNA runtime rejects.
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>
    /// The MGFX container version to emit. Defaults to <c>10</c>, which loads across the
    /// supported MonoGame/KNI runtimes (the backwards-compatible choice). Ignored for
    /// <see cref="PlatformTarget.Fna"/>, whose output is the D3D9 fx_2_0 container, not MGFX.
    /// </summary>
    public int MgfxVersion { get; init; } = 10;

    /// <summary>
    /// Which backend compiles HLSL to SM5 DXBC when <see cref="Target"/> is
    /// <see cref="PlatformTarget.DirectX"/>. Defaults to the proven Windows-only
    /// d3dcompiler_47 oracle; set to <see cref="DxbcBackend.Vkd3d"/> for the
    /// cross-platform vkd3d-shader backend. Ignored for non-DirectX targets —
    /// <see cref="PlatformTarget.Fna"/> always uses vkd3d-shader (the same backend on every
    /// host, so output stays host-independent). On the browser/WASM host this option is
    /// OVERRIDDEN: <c>WasmShaderCompiler</c> always compiles DXBC via the vkd3d-shader
    /// WASM backend (there is no d3dcompiler_47 in a browser), so a browser DirectX
    /// compile matches a desktop compile with <see cref="DxbcBackend.Vkd3d"/>, not the
    /// desktop default.
    /// </summary>
    public DxbcBackend DxbcBackend { get; init; } = DxbcBackend.D3DCompiler;
}
