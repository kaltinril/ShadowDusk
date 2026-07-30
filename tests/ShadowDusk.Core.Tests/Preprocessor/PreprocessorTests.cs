#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;
using HlslPreprocessor = ShadowDusk.Core.Preprocessor.Preprocessor;

namespace ShadowDusk.Core.Tests.Preprocessor;

public sealed class PreprocessorTests
{
    private static HlslPreprocessor CreatePreprocessor() => new HlslPreprocessor();

    private static MacroSet DirectXMacros => PlatformMacros.For(PlatformTarget.DirectX);
    private static MacroSet OpenGLMacros  => PlatformMacros.For(PlatformTarget.OpenGL);

    // -------------------------------------------------------------------------
    // 6.1 — Basic macro injection
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_DirectX_OutputStartsWithMacroDefineBlock()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = preprocessor.Flatten(
            "float4 main() { return 0; }",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("#define MGFX 1", Case.Sensitive);
        result.Value.Text.ShouldContain("#define HLSL 1", Case.Sensitive);
        result.Value.Text.ShouldContain("#define SM4 1", Case.Sensitive);
    }

    [Fact]
    public void Flatten_OpenGL_OutputContainsOpenGLMacros()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = preprocessor.Flatten(
            "float4 main() { return 0; }",
            originalFilePath: "root.fx",
            macros: OpenGLMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("#define MGFX 1", Case.Sensitive);
        result.Value.Text.ShouldContain("#define GLSL 1", Case.Sensitive);
        result.Value.Text.ShouldContain("#define OPENGL 1", Case.Sensitive);
    }

    [Fact]
    public void Flatten_AnyPlatform_OutputContainsLineDirectiveBeforeShaderBody()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string shaderBody = "float marker_token;";

        var result = preprocessor.Flatten(
            shaderBody,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();

        var text = result.Value.Text;
        var lineDirectiveIndex = text.IndexOf("#line 1 \"root.fx\"", StringComparison.Ordinal);
        var shaderBodyIndex    = text.IndexOf("marker_token", StringComparison.Ordinal);

        lineDirectiveIndex.ShouldBeGreaterThanOrEqualTo(0, customMessage: "a #line reset directive must appear before the shader body");
        lineDirectiveIndex.ShouldBeLessThan(shaderBodyIndex, customMessage: "the #line directive must precede the first shader token");
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    public void Flatten_KnownPlatform_Succeeds(PlatformTarget platform)
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = preprocessor.Flatten(
            "float dummy;",
            originalFilePath: "shader.fx",
            macros: PlatformMacros.For(platform),
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // 6.2 — Single #include
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_SingleInclude_InlinesIncludedContent()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float c;"
        });
        const string source = "float a;\n#include \"common.fxh\"\nfloat b;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("float c;", Case.Sensitive);
    }

    [Fact]
    public void Flatten_SingleInclude_NoIncludeDirectivesRemainInOutput()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float c;"
        });
        const string source = "float a;\n#include \"common.fxh\"\nfloat b;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldNotContain("#include", Case.Sensitive);
    }

    [Fact]
    public void Flatten_SingleInclude_LineDirectivesWrapIncludedContent()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float c;"
        });
        const string source = "float a;\n#include \"common.fxh\"\nfloat b;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        // There must be a #line directive referencing common.fxh to mark where the included content starts
        result.Value.Text.ShouldContain("common.fxh", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // 6.3 — Nested #include
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_NestedInclude_InlinesTransitiveContent()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["a.fxh"] = "#include \"b.fxh\"\nfloat from_a;",
            ["b.fxh"] = "float from_b;"
        });
        const string source = "#include \"a.fxh\"\nfloat root_var;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("float from_b;", Case.Sensitive);
        result.Value.Text.ShouldContain("float from_a;", Case.Sensitive);
        result.Value.Text.ShouldContain("float root_var;", Case.Sensitive);
    }

    [Fact]
    public void Flatten_NestedInclude_NoIncludeDirectivesRemainInOutput()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["a.fxh"] = "#include \"b.fxh\"\nfloat from_a;",
            ["b.fxh"] = "float from_b;"
        });
        const string source = "#include \"a.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldNotContain("#include", Case.Sensitive);
    }

    [Fact]
    public void Flatten_NestedInclude_LineDirectivesReflectIncludeChain()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["a.fxh"] = "#include \"b.fxh\"",
            ["b.fxh"] = "float deep;"
        });
        const string source = "#include \"a.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("b.fxh", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // 6.4 — Circular include (direct self-reference)
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_DirectCircularInclude_ReturnsFailure()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["self.fx"] = "#include \"self.fx\"\nfloat x;"
        });

        var result = preprocessor.Flatten(
            "#include \"self.fx\"",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Flatten_DirectCircularInclude_ReturnsCircularIncludeKind()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["self.fx"] = "#include \"self.fx\"\nfloat x;"
        });

        var result = preprocessor.Flatten(
            "#include \"self.fx\"",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.Error.Kind.ShouldBe(ShaderErrorKind.CircularInclude);
    }

    [Fact]
    public void Flatten_DirectCircularInclude_ErrorRequestedPathReferencesCircularFile()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["self.fx"] = "#include \"self.fx\"\nfloat x;"
        });

        var result = preprocessor.Flatten(
            "#include \"self.fx\"",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.Error.RequestedPath!.ShouldContain("self.fx", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // 6.5 — Circular include (transitive A→B→A)
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_TransitiveCircularInclude_ReturnsCircularIncludeError()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["a.fxh"] = "#include \"b.fxh\"\nfloat from_a;",
            ["b.fxh"] = "#include \"a.fxh\"\nfloat from_b;"
        });

        var result = preprocessor.Flatten(
            "#include \"a.fxh\"",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsFailure.ShouldBeTrue();
        result.Error.Kind.ShouldBe(ShaderErrorKind.CircularInclude);
    }

    // -------------------------------------------------------------------------
    // 6.6 — #pragma once
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_PragmaOnce_IncludedContentAppearsOnlyOnce()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["header.fxh"] = "#pragma once\nfloat x;"
        });
        // Include header.fxh twice from the same root
        const string source = "#include \"header.fxh\"\n#include \"header.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();

        // Count occurrences of the guarded content
        var text = result.Value.Text;
        var firstIndex  = text.IndexOf("float x;", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("float x;", firstIndex + 1, StringComparison.Ordinal);

        firstIndex.ShouldBeGreaterThanOrEqualTo(0, customMessage: "the content must appear at least once");
        secondIndex.ShouldBeLessThan(0, customMessage: "#pragma once must prevent a second inclusion");
    }

    [Fact]
    public void Flatten_PragmaOnce_IncludedFromDifferentFilesAppearsOnlyOnce()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["shared.fxh"] = "#pragma once\nfloat shared_val;",
            ["a.fxh"]      = "#include \"shared.fxh\"\nfloat a_val;",
            ["b.fxh"]      = "#include \"shared.fxh\"\nfloat b_val;"
        });
        const string source = "#include \"a.fxh\"\n#include \"b.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();

        var text = result.Value.Text;
        var firstIndex  = text.IndexOf("float shared_val;", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("float shared_val;", firstIndex + 1, StringComparison.Ordinal);

        firstIndex.ShouldBeGreaterThanOrEqualTo(0);
        secondIndex.ShouldBeLessThan(0, customMessage: "shared.fxh has #pragma once and must not be emitted twice even via different include paths");
    }

    // -------------------------------------------------------------------------
    // 6.7 — #pragma warning pass-through
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_PragmaWarning_AppearsUnchangedInOutput()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string pragmaLine = "#pragma warning(disable: 3571)";

        var result = preprocessor.Flatten(
            $"float dummy;\n{pragmaLine}\nfloat dummy2;",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain(pragmaLine, Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // 6.8 — Unknown #pragma pass-through
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_UnknownPragma_AppearsUnchangedInOutput()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string pragmaLine = "#pragma custom_thing";

        var result = preprocessor.Flatten(
            $"float dummy;\n{pragmaLine}\nfloat dummy2;",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain(pragmaLine, Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // 6.9 — Missing include error
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_MissingInclude_ReturnsFailure()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string source =
            "float a;\n" +
            "float b;\n" +
            "float c;\n" +
            "float d;\n" +
            "#include \"missing.fxh\"\n" +
            "float e;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Flatten_MissingInclude_ReturnsIncludeNotFoundKind()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string source =
            "float a;\n" +
            "float b;\n" +
            "float c;\n" +
            "float d;\n" +
            "#include \"missing.fxh\"\n" +
            "float e;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.Error.Kind.ShouldBe(ShaderErrorKind.IncludeNotFound);
    }

    [Fact]
    public void Flatten_MissingInclude_ErrorContainsIncludingFilePath()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string source = "#include \"missing.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.Error.IncludingFilePath.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Flatten_MissingInclude_ErrorLineNumberMatchesIncludeDirectiveLine()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        // The #include is on line 5 (1-based)
        const string source =
            "float a;\n" +
            "float b;\n" +
            "float c;\n" +
            "float d;\n" +
            "#include \"missing.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.Error.IncludingLineNumber.ShouldBe(5);
    }

    [Fact]
    public void Flatten_MissingInclude_ErrorRequestedPathMatchesIncludePath()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string source = "#include \"missing.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.Error.RequestedPath.ShouldBe("missing.fxh");
    }

    [Fact]
    public void Flatten_MissingInclude_ErrorSearchedPathsIsNonEmpty()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string source = "#include \"missing.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.Error.SearchedPaths.ShouldNotBeNull();
        result.Error.SearchedPaths.ShouldNotBeEmpty();
    }

    // -------------------------------------------------------------------------
    // 6.10 — DxcMacroFlags round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_DirectX_DxcMacroFlagsMatchPlatformMacrosToDxcFlags()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        var macros = PlatformMacros.For(PlatformTarget.DirectX);

        var result = preprocessor.Flatten(
            "float dummy;",
            originalFilePath: "root.fx",
            macros: macros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DxcMacroFlags
            .ShouldBe(macros.ToDxcFlags());
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    public void Flatten_AnyPlatform_DxcMacroFlagsRoundTripMatchesMacroSet(PlatformTarget platform)
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        var macros = PlatformMacros.For(platform);

        var result = preprocessor.Flatten(
            "float dummy;",
            originalFilePath: "root.fx",
            macros: macros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DxcMacroFlags.ShouldBe(macros.ToDxcFlags());
    }

    [Fact]
    public void Flatten_PreprocessedSource_OriginalFilePathPreserved()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());

        var result = preprocessor.Flatten(
            "float dummy;",
            originalFilePath: "my/shader.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.OriginalFilePath.ShouldBe("my/shader.fx");
    }

    // -------------------------------------------------------------------------
    // 6.11 — Line number preservation after #include
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_SingleInclude_LineDirectiveResumesRootFileAfterInclude()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["hdr.fxh"] = "float from_hdr;"
        });
        // root.fx:
        //   line 1: float a;
        //   line 2: #include "hdr.fxh"
        //   line 3: float b;
        const string source = "float a;\n#include \"hdr.fxh\"\nfloat b;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();

        // After the included content there must be a #line directive resuming root.fx
        // at line 3 (the line after the #include directive).
        result.Value.Text.ShouldContain("#line 3 \"root.fx\"", Case.Sensitive);
    }

    [Fact]
    public void Flatten_MultilineIncludedHeader_LineDirectiveAfterIncludePointsToCorrectLine()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            // Header has 3 lines of content
            ["hdr.fxh"] = "float x;\nfloat y;\nfloat z;"
        });
        // root.fx:
        //   line 1: float pre;
        //   line 2: #include "hdr.fxh"
        //   line 3: float post;
        const string source = "float pre;\n#include \"hdr.fxh\"\nfloat post;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();

        // The resume directive after the 3-line header must point to line 3 of root.fx
        result.Value.Text.ShouldContain("#line 3 \"root.fx\"", Case.Sensitive);
    }

    [Fact]
    public void Flatten_IncludedContentComesAfterLineDirectiveForIncludedFile()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["hdr.fxh"] = "float included_token;"
        });
        const string source = "float a;\n#include \"hdr.fxh\"\nfloat b;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();

        var text = result.Value.Text;
        var lineDirectiveForHeader = text.IndexOf("hdr.fxh", StringComparison.Ordinal);
        var includedTokenIndex     = text.IndexOf("included_token", StringComparison.Ordinal);

        lineDirectiveForHeader.ShouldBeLessThan(includedTokenIndex, customMessage: "the #line directive for the included file must precede the included content");
    }

    // -------------------------------------------------------------------------
    // Diamond includes are legal (cycle detection is an include STACK, not a
    // visited set): a → {b, c} → common must flatten — fxc/mgfxc accept it —
    // while true cycles still fail SD0002.
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_DiamondInclude_Succeeds_AndIncludesCommonTwice()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["b.fxh"]      = "#include \"common.fxh\"\nfloat from_b;",
            ["c.fxh"]      = "#include \"common.fxh\"\nfloat from_c;",
            ["common.fxh"] = "float common_token;",
        });
        const string source = "#include \"b.fxh\"\n#include \"c.fxh\"\nfloat root_var;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "a.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue($"a diamond include is not a cycle; error: {(result.IsFailure ? result.Error.Message : "<none>")}");

        var text = result.Value.Text;
        text.ShouldContain("from_b", Case.Sensitive);
        text.ShouldContain("from_c", Case.Sensitive);
        // No #pragma once / guards → the textual inclusion happens twice, exactly
        // like fxc's preprocessor (guards, when present, are evaluated by DXC later).
        CountOccurrences(text, "common_token").ShouldBe(2);
    }

    [Fact]
    public void Flatten_DiamondInclude_WithPragmaOnce_IncludesCommonOnce()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["b.fxh"]      = "#include \"common.fxh\"\nfloat from_b;",
            ["c.fxh"]      = "#include \"common.fxh\"\nfloat from_c;",
            ["common.fxh"] = "#pragma once\nfloat common_token;",
        });
        const string source = "#include \"b.fxh\"\n#include \"c.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "a.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        CountOccurrences(result.Value.Text, "common_token").ShouldBe(1);
    }

    [Fact]
    public void Flatten_SelfInclude_StillFailsAsCircular()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["self.fxh"] = "#include \"self.fxh\"\nfloat x;",
        });

        var result = preprocessor.Flatten(
            "#include \"self.fxh\"",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0002");
    }

    [Fact]
    public void Flatten_MutualInclude_StillFailsAsCircular()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["a.fxh"] = "#include \"b.fxh\"\nfloat from_a;",
            ["b.fxh"] = "#include \"a.fxh\"\nfloat from_b;",
        });

        var result = preprocessor.Flatten(
            "#include \"a.fxh\"",
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0002");
    }

    [Fact]
    public void Flatten_RepeatedSiblingInclude_IsNotACycle()
    {
        // The same header included twice from the SAME file is textual duplication
        // (fxc semantics), not a cycle.
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["common.fxh"] = "float common_token;",
        });
        const string source = "#include \"common.fxh\"\n#include \"common.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        CountOccurrences(result.Value.Text, "common_token").ShouldBe(2);
    }

    // -------------------------------------------------------------------------
    // Comment-aware directive scanning: a directive inside a comment is TEXT,
    // not an instruction (the input still carries comments — FxPreParser's
    // stripped output preserves them).
    // -------------------------------------------------------------------------

    [Fact]
    public void Flatten_IncludeInsideBlockComment_IsIgnored()
    {
        var preprocessor = CreatePreprocessor();
        // "ghost.fxh" is NOT in the resolver: if the commented directive were
        // honored, Flatten would fail with SD0001.
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string source = "float a;\n/*\n#include \"ghost.fxh\"\n*/\nfloat b;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue($"a commented-out #include must not be processed; error: {(result.IsFailure ? result.Error.Message : "<none>")}");
        // The comment passes through verbatim (DXC strips it later).
        result.Value.Text.ShouldContain("#include \"ghost.fxh\"", Case.Sensitive);
    }

    [Fact]
    public void Flatten_PragmaOnceInsideBlockComment_IsIgnored()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            // The commented '#pragma once' must NOT register hdr.fxh as include-once.
            ["hdr.fxh"] = "/*\n#pragma once\n*/\nfloat hdr_token;",
        });
        const string source = "#include \"hdr.fxh\"\n#include \"hdr.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        CountOccurrences(result.Value.Text, "hdr_token").ShouldBe(2);
    }

    [Fact]
    public void Flatten_IncludeWithTrailingComment_IsStillProcessed()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["hdr.fxh"] = "float hdr_token;",
        });
        const string source = "#include \"hdr.fxh\" // the usual helpers";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("hdr_token", Case.Sensitive);
    }

    [Fact]
    public void Flatten_IncludeAfterClosedBlockComment_IsProcessed()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["hdr.fxh"] = "float hdr_token;",
        });
        const string source = "/* leading note */ #include \"hdr.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("hdr_token", Case.Sensitive);
    }

    [Fact]
    public void Flatten_CommentOpenerInsideStringLiteral_DoesNotSwallowLaterIncludes()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>
        {
            ["hdr.fxh"] = "float hdr_token;",
        });
        // The "/*" inside the string must not open a block comment, or the real
        // #include on the next line would be silently skipped.
        const string source = "string s = \"/*\";\n#include \"hdr.fxh\"";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("hdr_token", Case.Sensitive);
    }

    [Fact]
    public void Flatten_LineCommentedInclude_IsIgnored()
    {
        var preprocessor = CreatePreprocessor();
        var resolver = new InMemoryIncludeResolver(new Dictionary<string, string>());
        const string source = "// #include \"ghost.fxh\"\nfloat a;";

        var result = preprocessor.Flatten(
            source,
            originalFilePath: "root.fx",
            macros: DirectXMacros,
            includeResolver: resolver,
            additionalPaths: []);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldContain("// #include \"ghost.fxh\"", Case.Sensitive);
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
