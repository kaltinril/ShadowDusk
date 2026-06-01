#nullable enable

using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Compiler;

public sealed class EffectCompiler : IShaderCompiler
{
    private readonly Func<IDxcShaderCompiler>? _dxcCompilerFactory;
    private readonly Func<ISpirvToGlslTranspiler>? _glslTranspilerFactory;
    private readonly Func<IShaderReflector>? _reflectorFactory;

    public EffectCompiler(
        Func<IDxcShaderCompiler>? dxcCompilerFactory = null,
        Func<ISpirvToGlslTranspiler>? glslTranspilerFactory = null,
        Func<IShaderReflector>? reflectorFactory = null)
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
    }

    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        var pipeline = new CompilationPipeline(_dxcCompilerFactory, _glslTranspilerFactory, _reflectorFactory);
        return pipeline.RunAsync(hlslSource, options, cancellationToken);
    }
}
