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
