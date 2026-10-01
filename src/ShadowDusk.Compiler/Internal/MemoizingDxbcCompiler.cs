#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// Per-compile memo over an <see cref="IDxbcShaderCompiler"/> (issue #255): an effect whose
/// techniques reuse an entry point asks the backend for the SAME request once per pass that
/// names it. MonoGame's stock <c>BasicEffect.fx</c> makes 64 vkd3d calls for 30 distinct
/// shaders, <c>SkinnedEffect.fx</c> 36 for 12, <c>EnvironmentMapEffect.fx</c> 32 for 8, and
/// every one of them is a full, single-threaded run of vkd3d's HLSL optimizer.
/// </summary>
/// <remarks>
/// <para><b>Why this cannot change output.</b> The key is every field of the request the
/// backend reads, and the backend is deterministic (the cross-host byte-identity manifest
/// depends on it), so a repeat request would have produced the identical blob, warnings, or
/// error. The caller still records one shader per pass exactly as before; only the redundant
/// native call is skipped. The cached <see cref="PlatformBlob"/> is immutable
/// (<see cref="ReadOnlyMemory{T}"/> bytes) and every consumer copies before patching.</para>
/// <para><b>Scope.</b> One instance lives for one pipeline run, so nothing is retained
/// between compiles and no cross-call state can leak. Not thread-safe; the pipeline compiles
/// its entry points sequentially.</para>
/// </remarks>
internal sealed class MemoizingDxbcCompiler : IDxbcShaderCompiler
{
    private readonly IDxbcShaderCompiler _inner;
    private readonly Dictionary<RequestKey, Result<PlatformBlob, ShaderError>> _results = new();

    public MemoizingDxbcCompiler(IDxbcShaderCompiler inner) => _inner = inner;

    /// <summary>How many requests reached the wrapped backend (test seam).</summary>
    internal int BackendCalls { get; private set; }

    public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestKey key = RequestKey.From(request);
        if (_results.TryGetValue(key, out Result<PlatformBlob, ShaderError> cached))
            return Task.FromResult(cached);
        return CompileAndStoreAsync(key, request, cancellationToken);
    }

    private async Task<Result<PlatformBlob, ShaderError>> CompileAndStoreAsync(
        RequestKey key, D3DCompileRequest request, CancellationToken cancellationToken)
    {
        BackendCalls++;
        Result<PlatformBlob, ShaderError> result =
            await _inner.CompileAsync(request, cancellationToken).ConfigureAwait(false);
        _results[key] = result;
        return result;
    }

    public Result<PlatformBlob, ShaderError> Compile(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        // A cache hit is still a point between entry points, where a cancelled compile stops.
        cancellationToken.ThrowIfCancellationRequested();
        RequestKey key = RequestKey.From(request);
        if (_results.TryGetValue(key, out Result<PlatformBlob, ShaderError> cached))
            return cached;

        BackendCalls++;
        Result<PlatformBlob, ShaderError> result = _inner.Compile(request, cancellationToken);
        _results[key] = result;
        return result;
    }

    /// <summary>Every <see cref="D3DCompileRequest"/> field a backend reads.</summary>
    private readonly record struct RequestKey(
        string HlslSource,
        string SourceFileName,
        string EntryPoint,
        ShaderStage Stage,
        bool EmbedDebugInfo,
        bool AllowWarnings,
        string? ProfileOverride)
    {
        public static RequestKey From(D3DCompileRequest r) => new(
            r.HlslSource, r.SourceFileName, r.EntryPoint, r.Stage,
            r.EmbedDebugInfo, r.AllowWarnings, r.ProfileOverride);
    }
}
