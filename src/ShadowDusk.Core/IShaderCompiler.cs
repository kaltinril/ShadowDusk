#nullable enable

namespace ShadowDusk.Core;

public interface IShaderCompiler
{
    Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default);
}
