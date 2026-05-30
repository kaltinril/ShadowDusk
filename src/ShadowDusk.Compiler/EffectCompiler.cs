#nullable enable

using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Compiler;

public sealed class EffectCompiler : IShaderCompiler
{
    private readonly Func<IDxcShaderCompiler>? _dxcCompilerFactory;
    private readonly Func<ISpirvToGlslTranspiler>? _glslTranspilerFactory;

    public EffectCompiler(
        Func<IDxcShaderCompiler>? dxcCompilerFactory = null,
        Func<ISpirvToGlslTranspiler>? glslTranspilerFactory = null)
    {
        _dxcCompilerFactory    = dxcCompilerFactory;
        _glslTranspilerFactory = glslTranspilerFactory;
    }

    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        var pipeline = new CompilationPipeline(_dxcCompilerFactory, _glslTranspilerFactory);
        return pipeline.RunAsync(hlslSource, options, cancellationToken);
    }
}
