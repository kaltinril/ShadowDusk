#nullable enable

using System.Reflection;
using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// PURE unit tests for the per-run DXBC/D3D-bytecode memo (issue #255): a repeated request
/// reaches the backend once, any field that differs reaches it again, and the key cannot
/// silently fall behind <see cref="D3DCompileRequest"/>.
/// </summary>
public sealed class MemoizingDxbcCompilerTests
{
    private sealed class CountingCompiler : IDxbcShaderCompiler
    {
        public int Calls { get; private set; }

        public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
            D3DCompileRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Compile(request, cancellationToken));

        public Result<PlatformBlob, ShaderError> Compile(
            D3DCompileRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            // Distinct bytes per call, so a test can tell a cached result from a fresh one.
            return Result<PlatformBlob, ShaderError>.Ok(
                new PlatformBlob(BlobKind.Dxbc, new byte[] { (byte)Calls }));
        }
    }

    private static D3DCompileRequest Request(
        string source = "float4 PS() : SV_Target { return 1; }",
        string entry = "PS",
        ShaderStage stage = ShaderStage.Pixel,
        string? profile = null,
        bool debug = false,
        bool allowWarnings = true,
        string file = "a.fx") => new()
    {
        HlslSource = source,
        SourceFileName = file,
        EntryPoint = entry,
        Stage = stage,
        ProfileOverride = profile,
        EmbedDebugInfo = debug,
        AllowWarnings = allowWarnings,
    };

    [Fact]
    public void RepeatedRequest_ReachesTheBackendOnce_AndReturnsTheSameBlob()
    {
        var inner = new CountingCompiler();
        var memo = new MemoizingDxbcCompiler(inner);

        var first = memo.Compile(Request());
        // A separate but equal request object: what the pipeline builds for the next pass.
        var second = memo.Compile(Request());

        inner.Calls.ShouldBe(1);
        memo.BackendCalls.ShouldBe(1);
        second.Value.Bytes.ToArray().ShouldBe(first.Value.Bytes.ToArray());
    }

    [Fact]
    public async Task AsyncAndSyncShareTheMemo()
    {
        var inner = new CountingCompiler();
        var memo = new MemoizingDxbcCompiler(inner);

        await memo.CompileAsync(Request());
        memo.Compile(Request());
        await memo.CompileAsync(Request());

        inner.Calls.ShouldBe(1);
    }

    public static TheoryData<string> DifferingFields() =>
        new() { "source", "entry", "stage", "profile", "debug", "allowWarnings", "file" };

    [Theory]
    [MemberData(nameof(DifferingFields))]
    public void AnyDifferingField_IsADifferentCompile(string field)
    {
        var inner = new CountingCompiler();
        var memo = new MemoizingDxbcCompiler(inner);
        memo.Compile(Request());

        D3DCompileRequest other = field switch
        {
            "source"        => Request(source: "float4 PS() : SV_Target { return 0; }"),
            "entry"         => Request(entry: "PS2"),
            "stage"         => Request(stage: ShaderStage.Vertex),
            "profile"       => Request(profile: "ps_3_0"),
            "debug"         => Request(debug: true),
            "allowWarnings" => Request(allowWarnings: false),
            "file"          => Request(file: "b.fx"),
            _               => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var result = memo.Compile(other);

        inner.Calls.ShouldBe(2);
        result.Value.Bytes.ToArray().ShouldBe(new byte[] { 2 });
    }

    [Fact]
    public void Key_CoversEveryRequestProperty()
    {
        // If D3DCompileRequest grows a field a backend reads, the memo key must grow with it,
        // or two requests that compile differently would share one cached result.
        string[] properties = typeof(D3DCompileRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        properties.ShouldBe(new[]
        {
            nameof(D3DCompileRequest.AllowWarnings),
            nameof(D3DCompileRequest.EmbedDebugInfo),
            nameof(D3DCompileRequest.EntryPoint),
            nameof(D3DCompileRequest.HlslSource),
            nameof(D3DCompileRequest.ProfileOverride),
            nameof(D3DCompileRequest.SourceFileName),
            nameof(D3DCompileRequest.Stage),
        }, "a new D3DCompileRequest property must be added to MemoizingDxbcCompiler's RequestKey");
    }

    [Fact]
    public void CancelledToken_ThrowsEvenOnACacheHit()
    {
        var memo = new MemoizingDxbcCompiler(new CountingCompiler());
        memo.Compile(Request());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => memo.Compile(Request(), cts.Token));
    }
}
