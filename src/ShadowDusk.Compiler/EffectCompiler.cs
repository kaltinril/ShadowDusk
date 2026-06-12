#nullable enable

using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Compiler;

/// <summary>
/// The product's in-memory shader compiler and the default <see cref="IShaderCompiler"/>
/// implementation: HLSL <c>.fx</c> source in, <c>.mgfx</c> bytes out, on Linux, macOS, or
/// Windows with nothing but this library. Orchestrates the faithful pipeline —
/// HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL for OpenGL, and HLSL → vkd3d-shader → DXBC for
/// DirectX — and writes the MonoGame/KNI <c>.mgfx</c> container.
/// </summary>
/// <remarks>
/// Construct with the default constructor for normal use (<c>new EffectCompiler()</c>); the
/// optional factory parameters exist only to inject alternative pipeline components (e.g. the
/// pure-managed <c>SpirvReflector</c> for the WASM host). The output loads into MonoGame's
/// <c>Effect</c> and renders the same image as <c>mgfxc</c>'s, but is not byte-identical to it.
/// </remarks>
public sealed class EffectCompiler : IShaderCompiler
{
    private readonly Func<IDxcShaderCompiler>? _dxcCompilerFactory;
    private readonly Func<ISpirvToGlslTranspiler>? _glslTranspilerFactory;
    private readonly Func<IShaderReflector>? _reflectorFactory;
    private readonly Func<IDxbcShaderCompiler>? _dxbcCompilerFactory;

    /// <summary>
    /// Creates an <see cref="EffectCompiler"/>. Pass no arguments for the standard,
    /// self-contained desktop pipeline. The optional factories override individual pipeline
    /// components for advanced/host-specific scenarios.
    /// </summary>
    /// <param name="dxcCompilerFactory">
    /// Optional factory for the HLSL → SPIR-V DXC frontend. Defaults to the bundled
    /// (Vortice.Dxc) desktop DXC.
    /// </param>
    /// <param name="glslTranspilerFactory">
    /// Optional factory for the SPIR-V → GLSL transpiler. Defaults to the SPIRV-Cross-backed
    /// transpiler.
    /// </param>
    /// <param name="reflectorFactory">
    /// Optional factory for the shader reflector. When <see langword="null"/> the OpenGL path
    /// reflects from the native DXIL oracle; the WASM host injects the pure-managed
    /// <c>SpirvReflector</c> instead.
    /// </param>
    /// <param name="dxbcCompilerFactory">
    /// Optional factory for the HLSL → DXBC / D3D-bytecode backend (the DirectX and FNA
    /// targets). When <see langword="null"/> (the desktop default) the backend is selected
    /// from <see cref="CompilerOptions.DxbcBackend"/> for DirectX and is always the
    /// native vkd3d-shader backend for FNA — byte-for-byte the pre-existing behavior.
    /// The WASM host injects its browser vkd3d backend (<c>WasmVkd3dShaderCompiler</c>)
    /// here — the SAME pinned vkd3d 1.17, never a substitute compiler.
    /// </param>
    public EffectCompiler(
        Func<IDxcShaderCompiler>? dxcCompilerFactory = null,
        Func<ISpirvToGlslTranspiler>? glslTranspilerFactory = null,
        Func<IShaderReflector>? reflectorFactory = null,
        Func<IDxbcShaderCompiler>? dxbcCompilerFactory = null)
    {
        _dxcCompilerFactory    = dxcCompilerFactory;
        _glslTranspilerFactory = glslTranspilerFactory;
        // Default reflector is null → the OpenGL path reflects from the native
        // DXIL oracle (cross-platform: the reflection runs inside dxcompiler,
        // which Vortice.Dxc bundles per-RID). The WASM/browser path injects
        // SpirvReflector explicitly (WasmShaderCompiler). Leaving this null is
        // load-bearing: SpirvReflectionByteIdentityTests uses `new EffectCompiler()`
        // as its DXIL baseline arm, so defaulting it to SpirvReflector would
        // silently make that keystone DXIL≡SPIR-V test compare SPIR-V to itself.
        _reflectorFactory      = reflectorFactory;
        _dxbcCompilerFactory   = dxbcCompilerFactory;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A thin asynchronous shell over the same synchronous pipeline core
    /// <see cref="Compile"/> runs (one implementation — output is byte-identical by
    /// construction). The compile is offloaded to the thread pool so the caller's thread
    /// is never blocked by the native compiler work.
    /// </remarks>
    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Compile(hlslSource, options, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// On desktop no prior <see cref="InitializeAsync"/> is required: the native
    /// compilers (DXC via Vortice.Dxc, SPIRV-Cross, vkd3d-shader, d3dcompiler_47) load
    /// lazily on first use, synchronously, inside this call.
    /// </remarks>
    public Result<CompiledShader, ShaderError[]> Compile(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        var pipeline = new CompilationPipeline(
            _dxcCompilerFactory, _glslTranspilerFactory, _reflectorFactory, _dxbcCompilerFactory);
        return pipeline.Run(hlslSource, options, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Effectively a no-op on desktop: the native compiler libraries load on first use
    /// and that load is itself synchronous, so <see cref="Compile"/> needs no prior
    /// warm-up here. The method exists so consumers can call the same
    /// <c>await compiler.InitializeAsync(); … compiler.Compile(...)</c> pattern against
    /// any <see cref="IShaderCompiler"/> — on the browser/WASM host
    /// (<c>WasmShaderCompiler</c>) the warm-up is real and required.
    /// </remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;
}
