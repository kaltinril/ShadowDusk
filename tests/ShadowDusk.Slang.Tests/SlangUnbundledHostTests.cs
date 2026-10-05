#nullable enable

using System.Runtime.InteropServices;
using ShadowDusk.Core;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #286: on a host ShadowDusk.Slang bundles no slangc for (win-arm64 is the one CI
/// measures, in <c>.github/workflows/win-arm64.yml</c>), the real <see cref="SlangCompiler"/>
/// with its real host probe must fail loudly with <c>SD0620</c> naming the host's RID and
/// asking for an issue, never crash and never fall back to a different compiler. Skips on
/// a bundled host, where <see cref="SlangToolPathTests"/> covers the same reason text
/// through the explicit seam.
/// </summary>
public sealed class SlangUnbundledHostTests(ITestOutputHelper output)
{
    private const string ValidPixelShader = """
        [shader("fragment")]
        float4 MainPS() : SV_Target { return float4(1, 0, 0, 1); }
        """;

    [UnbundledSlangHostFact]
    public void RealHost_Unbundled_ReturnsSD0620_NamingTheRid()
    {
        string hostRid = RuntimeInformation.RuntimeIdentifier;
        output.WriteLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture} RuntimeIdentifier={hostRid}");
        if (Environment.GetEnvironmentVariable("SHADOWDUSK_EXPECT_PROCESS_ARCH") is { Length: > 0 } arch)
            RuntimeInformation.ProcessArchitecture.ToString().ShouldBe(arch);

        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "p.slang" };
        var result = new SlangCompiler().Compile(ValidPixelShader, options);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        output.WriteLine($"{error.Code}: {error.Message}");
        error.Code.ShouldBe("SD0620");
        error.File.ShouldBe("p.slang");
        error.Message.ShouldContain($"this host ({hostRid}) is not one of them", Case.Sensitive);
        error.Message.ShouldContain("https://github.com/kaltinril/ShadowDusk/issues", Case.Sensitive);
        SlangToolPath.IsSupportedOnThisPlatform.ShouldBeFalse();
        SlangToolPath.Resolve().ShouldBeNull();
    }
}

/// <summary>Skips unless this process is a host ShadowDusk.Slang bundles no slangc for.</summary>
public sealed class UnbundledSlangHostFactAttribute : FactAttribute
{
    public UnbundledSlangHostFactAttribute()
    {
        if (SlangToolPath.CurrentRid is not null)
            Skip = $"this host ({RuntimeInformation.RuntimeIdentifier}) is a bundled slangc RID; " +
                   "the unbundled-host path runs on win-arm64 in .github/workflows/win-arm64.yml (issue #286)";
    }
}
