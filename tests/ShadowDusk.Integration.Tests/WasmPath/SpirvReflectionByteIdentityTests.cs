#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.WasmPath;

/// <summary>
/// Keystone byte-transparency proof for Phase 19 (WASM).
///
/// <para>The OpenGL compilation path can reflect from EITHER the native DXIL
/// <c>ID3D12ShaderReflection</c> oracle (desktop default) OR the pure-managed
/// <see cref="SpirvReflector"/> (browser/WASM, where no native reflection library
/// exists). This test proves the two reflection SOURCES yield <b>byte-identical</b>
/// <c>.mgfx</c> output for the Phase 17 PS-only corpus.</para>
///
/// <para>For each corpus shader the same source is compiled twice with the SAME native
/// DXC + SPIRV-Cross binaries — only the reflection source is swapped:</para>
/// <list type="number">
///   <item><c>bytesA</c>: default <see cref="EffectCompiler"/> → DXIL reflection.</item>
///   <item><c>bytesB</c>: <c>EffectCompiler(reflectorFactory: () =&gt; new SpirvReflector())</c>
///         → SPIR-V reflection.</item>
/// </list>
///
/// <para>Byte-equality (<c>bytesB == bytesA</c>) demonstrates the reflection-source swap
/// is transparent, i.e. the WASM path (which uses <see cref="SpirvReflector"/>) produces
/// the same <c>.mgfx</c> as the CLI, modulo DXC/SPIRV-Cross binary version.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class SpirvReflectionByteIdentityTests
{
    private readonly ITestOutputHelper _output;

    public SpirvReflectionByteIdentityTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] s_corpus =
    {
        "Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
        "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve",
    };

    public static IEnumerable<object[]> Corpus() =>
        s_corpus.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task SpirvReflection_ProducesByteIdenticalMgfx_ToDxilReflection(string fixtureStem)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;

        string fxPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", fixtureStem + ".fx");
        File.Exists(fxPath).Should().BeTrue($".fx fixture must exist at {fxPath}");

        string source = await File.ReadAllTextAsync(fxPath, ct);

        var options = new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        };

        // (A) Desktop default: native DXC + SPIRV-Cross, reflection sourced from DXIL.
        var defaultCompiler = new EffectCompiler();
        var resultA = await defaultCompiler.CompileAsync(source, options, ct);
        resultA.IsSuccess.Should().BeTrue(
            because: resultA.IsFailure
                ? string.Join("; ", resultA.Error.Select(e => $"{e.Code}: {e.Message}"))
                : "DXIL-reflection compile must succeed");
        byte[] bytesA = resultA.Value.Data;

        // (B) WASM path: native DXC + SPIRV-Cross UNCHANGED, ONLY reflection swapped to SPIR-V.
        var spirvCompiler = new EffectCompiler(reflectorFactory: () => new SpirvReflector());
        var resultB = await spirvCompiler.CompileAsync(source, options, ct);
        resultB.IsSuccess.Should().BeTrue(
            because: resultB.IsFailure
                ? string.Join("; ", resultB.Error.Select(e => $"{e.Code}: {e.Message}"))
                : "SPIR-V-reflection compile must succeed");
        byte[] bytesB = resultB.Value.Data;

        _output.WriteLine($"{fixtureStem}: DXIL={bytesA.Length} bytes, SPIRV={bytesB.Length} bytes");
        if (!bytesB.SequenceEqual(bytesA))
        {
            int firstDiff = -1;
            int min = Math.Min(bytesA.Length, bytesB.Length);
            for (int i = 0; i < min; i++)
            {
                if (bytesA[i] != bytesB[i]) { firstDiff = i; break; }
            }
            _output.WriteLine(
                $"DIVERGENCE: lenA={bytesA.Length} lenB={bytesB.Length} firstDiffOffset={firstDiff}" +
                (firstDiff >= 0 ? $" A=0x{bytesA[firstDiff]:X2} B=0x{bytesB[firstDiff]:X2}" : ""));
        }

        // The reflection-source swap MUST be byte-transparent. If a shader is NOT
        // byte-identical, this assertion fails honestly (see the // DIVERGENCE: log
        // above) rather than being weakened.
        bytesB.Should().Equal(bytesA,
            because: "swapping reflection from DXIL to SPIR-V must produce byte-identical .mgfx");
    }
}
