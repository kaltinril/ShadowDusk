#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Wasm;

// JS interop hooks ([JSImport]) for WASM-compiled DXC and SPIRV-Cross
// are added in a later phase; throwing here keeps the contract visible
// without silently returning a failure that callers might mishandle.
public sealed class WasmShaderCompiler : IShaderCompiler
{
    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("WASM compiler not yet implemented.");
}
