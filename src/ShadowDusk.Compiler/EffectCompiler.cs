#nullable enable

using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler;

public sealed class EffectCompiler : IShaderCompiler
{
    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        var pipeline = new CompilationPipeline();
        return pipeline.RunAsync(hlslSource, options, cancellationToken);
    }
}
