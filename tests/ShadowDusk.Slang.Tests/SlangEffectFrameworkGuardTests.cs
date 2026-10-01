#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Pure tests (no disk, no process) for the HLSL Effect rejection (<c>SD0626</c>, issue #231).
/// Like the entry-stage and SM6 rejections it depends only on the source, so it must fire
/// BEFORE the slangc host lookup on every OS; the injected locator fails the test if reached.
/// The measurement that motivates it, against MonoGame's real effects, is in
/// <c>SlangMonoGameEffectsTests</c>.
/// </summary>
public sealed class SlangEffectFrameworkGuardTests
{
    private static SlangCompiler.SlangcLocation MustNotBeReached() =>
        throw new InvalidOperationException(
            "the slangc host/native lookup ran before the Effect-framework rejection");

    private static ShaderError Reject(string source, PlatformTarget target = PlatformTarget.DirectX)
    {
        var compiler = new SlangCompiler(downstreamCompiler: null, MustNotBeReached);
        var result = compiler.Compile(source, new CompilerOptions { Target = target, SourceFileName = "effect.fx" });
        result.IsFailure.ShouldBeTrue();
        return result.Error.Single();
    }

    // Construct name, then the construct as planted source text.
    public static IEnumerable<object[]> PlantedConstructs()
    {
        yield return ["technique", "technique Basic" + "\n" + "{ pass P0 { } }"];
        yield return ["VertexShader = compile", "        VertexShader = compile vs_3_0 VS();"];
        yield return ["TECHNIQUE(...) macro", "TECHNIQUE( Basic, VS, PS );"];
        yield return ["DECLARE_TEXTURE(...) macro", "DECLARE_TEXTURE(Texture, 0);"];
        yield return ["DECLARE_TEXTURE(...) macro", "DECLARE_CUBEMAP(Env, 1);"];
        yield return ["BEGIN_CONSTANTS macro", "BEGIN_CONSTANTS"];
    }

    public static IEnumerable<object[]> PlantedConstructsOnEveryTarget()
    {
        PlatformTarget[] targets =
        [
            PlatformTarget.DirectX, PlatformTarget.OpenGL, PlatformTarget.Vulkan,
            PlatformTarget.DirectX12, PlatformTarget.Fna,
        ];
        foreach (object[] p in PlantedConstructs())
        {
            foreach (PlatformTarget t in targets)
                yield return [p[0], p[1], t];
        }
    }

    [Theory]
    [MemberData(nameof(PlantedConstructsOnEveryTarget))]
    public void EffectConstruct_RejectedWithSD0626_AtItsExactLine_BeforeTheHostLookup(
        string construct, string planted, PlatformTarget target)
    {
        // Lines 1-5 are drift: a block comment that itself spells the other constructs, and a
        // line comment. They must not match and must not move the line. The construct is line 6.
        string[] drift =
        [
            "// header",
            "/* technique Decoy",
            "   TECHNIQUE( A, B, C ); VertexShader = compile vs_3_0 VS();",
            "   BEGIN_CONSTANTS DECLARE_TEXTURE(T, 0) */",
            "// technique AlsoDecoy {",
        ];
        string source = string.Join('\n', drift) + '\n' + planted + '\n';

        ShaderError error = Reject(source, target);

        error.Code.ShouldBe("SD0626");
        error.File.ShouldBe("effect.fx");
        error.Line.ShouldBe(6);
        error.Column.ShouldBe(1);
        error.Message.ShouldContain(construct, Case.Sensitive);
        error.Message.ShouldContain(".fx route", Case.Sensitive);
    }

    [Fact]
    public void SlangSourceThatOnlyMentionsEffectWordsInComments_IsNotRejected()
    {
        const string source = """
            // technique Foo { pass P0 { VertexShader = compile vs_3_0 VS(); } }
            /* TECHNIQUE( A, B, C ); DECLARE_TEXTURE(T, 0); BEGIN_CONSTANTS */
            [shader("fragment")]
            float4 MainPS() : SV_Target
            {
                return 1;
            }
            """;
        // Passing the guard AND the entry scan means the host lookup is reached: report it
        // as unsupported so the test needs no slangc.
        var compiler = new SlangCompiler(
            downstreamCompiler: null,
            () => new SlangCompiler.SlangcLocation("test host", null));

        var result = compiler.Compile(source, new CompilerOptions { SourceFileName = "ok.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0620");
    }
}
