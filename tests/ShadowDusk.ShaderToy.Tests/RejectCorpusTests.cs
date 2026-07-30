using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Drives every <c>corpus/reject/*.glsl</c> through the converter and asserts it fails loudly: no
/// <c>.fx</c>, at least one <see cref="DiagnosticSeverity.Error"/>, and a plausible located error.
/// Where the reject README names a specific reason, the diagnostic must mention it (so a regression
/// that rejects for the wrong reason is also caught).
/// </summary>
public sealed class RejectCorpusTests
{
    public static IEnumerable<object[]> RejectShaders() =>
        CorpusLocator.GlslFiles(CorpusLocator.RejectDir)
            .Select(p => new object[] { Path.GetFileName(p), p });

    [Theory]
    [MemberData(nameof(RejectShaders))]
    public void RejectShader_FailsWithLocatedError(string fileName, string path)
    {
        _ = fileName; // surfaced in the test display name only.
        string glsl = File.ReadAllText(path);

        ConvertResult result = ShaderToyConverter.Convert(glsl);

        result.Success.ShouldBeFalse(string.Format("'{0}' contains an out-of-scope construct", fileName));
        result.Fx.ShouldBeNull();

        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        errors.ShouldNotBeEmpty("a rejected shader must emit at least one Error diagnostic");

        // At least one error must carry a plausible 1-based source location.
        errors.ShouldContain(
            e => e.Line > 0 && e.Column > 0, "at least one error should point at a real line/column in the source");
    }

    [Theory]
    [MemberData(nameof(RejectShaders))]
    public void RejectReason_MentionsTheNamedConstruct(string fileName, string path)
    {
        string glsl = File.ReadAllText(path);
        ConvertResult result = ShaderToyConverter.Convert(glsl);

        string allText = string.Join(
            " ",
            result.Diagnostics.Select(d => $"{d.Message} {d.Construct}"));

        // Per the reject README, each shader's only out-of-scope construct has a specific reason.
        // Map the filename to a keyword the diagnostic message/construct must mention.
        string expectedKeyword = Path.GetFileNameWithoutExtension(fileName) switch
        {
            "nested_struct" => "struct",
            "unsized_array" => "Unsized",
            "array_nonconst_size" => "constant",
            "unmappable_intrinsic" => "roundEven",
            "second_entry_cubemap" => "Cubemap",
            "switch_fallthrough" => "fall-through",
            "stage_in_noncoord_referenced" => "Undeclared",
            "macro_paste" => "##",
            "unknown_intrinsic" => "texelFetch",
            "unknown_global" => "RENDERSIZE",
            "custom_uniform_sampler3d" => "sampler",
            "custom_uniform_bad_type" => "mat2x3",
            "global_unsupported_type" => "double",
            "pp_include" => "#include",
            "main_no_output" => "fragment output",
            "intrinsic_texturecube" => "textureCube",
            "texture_cubemap_coord" => "CUBEMAP",
            "feedback_lastframe" => "getLastFrameColor",
            "gl_fragdepth_builtin" => "gl_FragDepth",
            "host_specific_uniform" => "host-provided value",
            "host_template_placeholder" => "host-template placeholder",
            "sampler_param" => "sampler2D",
            _ => string.Empty,
        };

        if (expectedKeyword.Length > 0)
        {
            allText.ShouldContain(
                expectedKeyword, Shouldly.Case.Insensitive, string.Format(
                "the diagnostic for '{0}' should mention its specific rejection reason", fileName));
        }
    }
}
