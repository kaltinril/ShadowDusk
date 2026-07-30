#nullable enable

using Shouldly;
using ShadowDusk.Cli;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Core.Tests;

public sealed class ArgumentParserTests
{
    // -------------------------------------------------------------------------
    // Valid positional args — defaults
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ValidPositionalArgs_ReturnsSuccessWithDefaults()
    {
        var result = ArgumentParser.Parse(["Shader.fx", "Out.mgfx"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SourceFile.ShouldBe("Shader.fx");
        result.Value.OutputFile.ShouldBe("Out.mgfx");
        result.Value.Platform.ShouldBe(PlatformTarget.DirectX);
        result.Value.Debug.ShouldBeFalse();
        result.Value.IncludePaths.ShouldBeEmpty();
        result.Value.MgfxVersion.ShouldBe(10);
        result.Value.Profile.ShouldBeNull();
    }

    // -------------------------------------------------------------------------
    // /Defines: (mgfxc parity) and missing-value flags — bug-hunt 2026-07-27 M9/N12
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DefinesFlag_CollectsMacros()
    {
        // /Defines: is a real mgfxc flag MGCB's EffectProcessor forwards; it used to
        // fall into the unknown-flag ignore, silently dropping the macros (M9).
        var result = ArgumentParser.Parse(
            ["Shader.fx", "Out.mgfx", "/Defines:SKINNED=1;HIGH_QUALITY", "/Defines:EXTRA=abc"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Defines.ShouldBe(new[]
        {
            new ShadowDusk.Core.Preprocessor.UserDefine("SKINNED", "1"),
            new ShadowDusk.Core.Preprocessor.UserDefine("HIGH_QUALITY"), // bare name -> "1"
            new ShadowDusk.Core.Preprocessor.UserDefine("EXTRA", "abc"),
        }, ignoreOrder: true);
    }

    [Theory]
    [InlineData("--target-runtime")]
    [InlineData("--mgfx-version")]
    [InlineData("/I")]
    public void Parse_ValuedFlagWithNoValue_FailsLoudlyWithX0009(string flag)
    {
        // A required-value flag ending the argument list used to be silently ignored,
        // compiling with the DEFAULT — the wrong artifact with exit 0 (N12).
        var result = ArgumentParser.Parse(["Shader.fx", "Out.mgfx", flag]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0009");
        result.Error.Message.ShouldContain(flag, Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // --target-runtime flag (maps a friendly name to a proven CapabilityProfile)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("monogame-gl", "MonoGameGL_3_8_2")]
    [InlineData("monogame-dx", "MonoGameDX_SM5")]
    [InlineData("monogame-gl-v11", "MonoGameGL_3_8_5")]
    [InlineData("kni-knifx", "KniGL_4_02")]
    [InlineData("fna", "Fna_Fx2")]
    public void Parse_TargetRuntime_MapsToProfile(string name, string expectedProfileName)
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--target-runtime", name]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Profile.ShouldNotBeNull();
        result.Value.Profile!.Name.ShouldBe(expectedProfileName);
    }

    [Fact]
    public void Parse_TargetRuntime_SlashColonForm_Works()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/target-runtime:kni-knifx"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Profile.ShouldBe(CapabilityProfile.KniGL_4_02);
    }

    [Fact]
    public void Parse_TargetRuntime_EqualsForm_Works()
    {
        // The '=' form used to fall through to the silent unknown-flag branch, so this
        // compiled with the DEFAULT profile and exit 0 — the wrong artifact, no diagnostic.
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--target-runtime=kni-knifx"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Profile.ShouldBe(CapabilityProfile.KniGL_4_02);
    }

    [Fact]
    public void Parse_TargetRuntime_EqualsForm_UnknownValue_StillFailsLoudly()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--target-runtime=nope"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0008");
    }

    [Fact]
    public void Parse_TargetRuntime_Unknown_FailsWithX0008()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--target-runtime", "nope"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0008");
    }

    // -------------------------------------------------------------------------
    // Profile flag
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ProfileOpenGL_SlashPrefix_ReturnsPlatformOpenGL()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Profile:OpenGL"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Platform.ShouldBe(PlatformTarget.OpenGL);
    }

    [Fact]
    public void Parse_ProfileOpenGL_DashPrefix_ReturnsPlatformOpenGL()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--Profile:OpenGL"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Platform.ShouldBe(PlatformTarget.OpenGL);
    }

    [Fact]
    public void Parse_ProfileDirectX_Default_ReturnsPlatformDirectX()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Platform.ShouldBe(PlatformTarget.DirectX);
    }

    [Fact]
    public void Parse_ProfileCaseInsensitive_ReturnsPlatformOpenGL()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Profile:opengl"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Platform.ShouldBe(PlatformTarget.OpenGL);
    }

    [Fact]
    public void Parse_ProfileFNA_ReturnsPlatformFna()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.fxb", "/Profile:FNA"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Platform.ShouldBe(PlatformTarget.Fna);
    }

    [Fact]
    public void Parse_ProfileFnaCaseInsensitive_ReturnsPlatformFna()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.fxb", "/Profile:fna"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Platform.ShouldBe(PlatformTarget.Fna);
    }

    // -------------------------------------------------------------------------
    // Debug flag
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DebugFlag_SlashPrefix_ReturnsTrueDebug()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Debug"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Debug.ShouldBeTrue();
    }

    [Fact]
    public void Parse_DebugFlag_DashPrefix_ReturnsTrueDebug()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--Debug"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Debug.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // Include path flag
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_IncludePath_SingleSlash_ReturnsIncludePath()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/I", "include/"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IncludePaths.ShouldHaveSingleItem().ShouldBe("include/");
    }

    [Fact]
    public void Parse_IncludePath_ColonForm_ReturnsIncludePath()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/I:include/"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IncludePaths.ShouldHaveSingleItem().ShouldBe("include/");
    }

    [Fact]
    public void Parse_IncludePath_Repeatable_ReturnsMultiplePaths()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/I", "a", "/I", "b"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IncludePaths.ShouldBe(new[] {"a", "b"});
    }

    // -------------------------------------------------------------------------
    // mgfx-version flag
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MgfxVersion10_ReturnsMgfxVersion10()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--mgfx-version", "10"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MgfxVersion.ShouldBe(10);
    }

    [Fact]
    public void Parse_MgfxVersion11_ReturnsMgfxVersion11()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--mgfx-version", "11"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MgfxVersion.ShouldBe(11);
    }

    [Fact]
    public void Parse_MgfxVersionInvalid_ReturnsFailureWithCodeX0005()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--mgfx-version", "99"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0005");
    }

    // -------------------------------------------------------------------------
    // Missing required arguments
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MissingSourceFile_ReturnsFailureWithCodeX0003()
    {
        // Only one positional arg — treated as source, but no output file
        var result = ArgumentParser.Parse(["Out.mgfx"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0003");
    }

    [Fact]
    public void Parse_MissingBothFiles_ReturnsFailureWithCodeX0003()
    {
        var result = ArgumentParser.Parse([]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0003");
    }

    // -------------------------------------------------------------------------
    // POSIX absolute paths — must NOT be misread as "/Opt" options (Linux/macOS)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PosixAbsolutePaths_AreParsedAsPositionals()
    {
        // On Linux/macOS the source/output paths start with '/', like an mgfxc option.
        // They must be parsed as positionals, not silently dropped as unknown flags.
        var result = ArgumentParser.Parse(
            ["/home/runner/work/ShadowDusk/tests/fixtures/shaders/Grayscale.fx",
             "/tmp/out/Grayscale.mgfx"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SourceFile.ShouldBe("/home/runner/work/ShadowDusk/tests/fixtures/shaders/Grayscale.fx");
        result.Value.OutputFile.ShouldBe("/tmp/out/Grayscale.mgfx");
    }

    [Fact]
    public void Parse_PosixAbsolutePaths_WithProfileFlag_StillParsesBoth()
    {
        var result = ArgumentParser.Parse(
            ["/abs/src/Shader.fx", "/abs/out/Shader.mgfx", "/Profile:OpenGL"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SourceFile.ShouldBe("/abs/src/Shader.fx");
        result.Value.OutputFile.ShouldBe("/abs/out/Shader.mgfx");
        result.Value.Platform.ShouldBe(PlatformTarget.OpenGL);
    }

    [Fact]
    public void Parse_IncludePath_ColonForm_WithPosixAbsoluteValue_Preserved()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/I:/usr/local/include"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IncludePaths.ShouldHaveSingleItem().ShouldBe("/usr/local/include");
    }

    // -------------------------------------------------------------------------
    // Unknown flags — forward compatibility
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_UnknownFlagIgnored_ReturnsSuccess()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "--future-flag", "value"]);

        result.IsSuccess.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // Unsupported platforms
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_UnsupportedPlatform_PS4_ReturnsFailureWithCodeX0010()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Profile:PlayStation4"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0010");
    }

    [Fact]
    public void Parse_UnsupportedPlatform_XboxOne_ReturnsFailureWithCodeX0010()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Profile:XboxOne"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0010");
    }

    // -------------------------------------------------------------------------
    // Unknown profile string
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_UnknownProfile_ReturnsFailureWithCodeX0004()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Profile:DOS"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0004");
    }

    [Fact]
    public void Parse_UnknownProfile_ErrorListsFnaAsValidProfile()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Profile:DOS"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("FNA", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Extensionless POSIX paths — historically misread as unknown '/'-flags and
    // silently dropped; now positional ('/'-token is a flag only when its name is
    // a known flag, or it carries a ':' value like future mgfxc flags).
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ExtensionlessPosixPaths_AreParsedAsPositionals()
    {
        var result = ArgumentParser.Parse(["/data", "/out"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SourceFile.ShouldBe("/data");
        result.Value.OutputFile.ShouldBe("/out");
    }

    [Fact]
    public void Parse_UnknownSlashFlagWithColonValue_IsStillIgnoredForForwardCompat()
    {
        // A future mgfxc flag shape ("/Defines:FOO=1") keeps being tolerated.
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/Defines:FOO=1"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SourceFile.ShouldBe("S.fx");
        result.Value.OutputFile.ShouldBe("O.mgfx");
    }

    // -------------------------------------------------------------------------
    // /DxbcBackend escape hatch (default: vkd3d — the cross-platform backend)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_NoDxbcBackendFlag_DefaultsToVkd3d()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DxbcBackend.ShouldBe(DxbcBackend.Vkd3d);
    }

    [Fact]
    public void Parse_DxbcBackendD3DCompiler_OptsIntoTheOracle()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/DxbcBackend:d3dcompiler"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DxbcBackend.ShouldBe(DxbcBackend.D3DCompiler);
    }

    [Fact]
    public void Parse_DxbcBackendVkd3d_CaseInsensitive()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/dxbcbackend:VKD3D"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DxbcBackend.ShouldBe(DxbcBackend.Vkd3d);
    }

    [Fact]
    public void Parse_DxbcBackendInvalid_ReturnsFailureWithCodeX0006()
    {
        var result = ArgumentParser.Parse(["S.fx", "O.mgfx", "/DxbcBackend:fxc"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0006");
        result.Error.Message.ShouldContain("vkd3d", Case.Sensitive);
        result.Error.Message.ShouldContain("d3dcompiler", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // /Defines (bug-hunt 2026-07-27 M9): mgfxc's real flag, previously silently
    // swallowed by the unknown-flag rule.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_Defines_ParsesNameValuePairsAndBareNames()
    {
        var result = ArgumentParser.Parse(
            ["Shader.fx", "Out.mgfx", "/Defines:SKINNED=1;HIGH_QUALITY,BONES=4"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Defines.ShouldBe(new[] {
            new UserDefine("SKINNED", "1"),
            new UserDefine("HIGH_QUALITY"),
            new UserDefine("BONES", "4")});
    }

    [Fact]
    public void Parse_Defines_Repeatable_Accumulates()
    {
        var result = ArgumentParser.Parse(
            ["Shader.fx", "Out.mgfx", "/Defines:A=1", "/Defines:B=two"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Defines.ShouldBe(new[] {
            new UserDefine("A", "1"),
            new UserDefine("B", "two")});
    }

    [Fact]
    public void Parse_NoDefines_YieldsEmptyList()
    {
        var result = ArgumentParser.Parse(["Shader.fx", "Out.mgfx"]);

        result.IsSuccess.ShouldBeTrue();
        (result.Value.Defines ?? []).ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // DirectX_12 in the profile surface (bug-hunt 2026-07-27 D1)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_DirectX12Profile_Accepted()
    {
        var result = ArgumentParser.Parse(["Shader.fx", "Out.mgfx", "/Profile:DirectX_12"]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Platform.ShouldBe(PlatformTarget.DirectX12);
    }

    [Fact]
    public void Parse_UnknownProfile_ErrorListsDirectX12()
    {
        var result = ArgumentParser.Parse(["Shader.fx", "Out.mgfx", "/Profile:Nonsense"]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0004");
        result.Error.Message.ShouldContain("DirectX_12", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // X0009 (bug-hunt 2026-07-27 N12): a flag that requires a value but reaches
    // the end of the argument list used to be silently ignored — the compile ran
    // with the DEFAULT and exit 0.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("--mgfx-version")]
    [InlineData("--target-runtime")]
    [InlineData("/I")]
    public void Parse_ValuedFlagAtEndOfArgs_FailsLoudlyWithX0009(string flag)
    {
        var result = ArgumentParser.Parse(["Shader.fx", "Out.mgfx", flag]);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("X0009");
        result.Error.Message.ShouldContain(flag, Case.Sensitive);
    }
}
