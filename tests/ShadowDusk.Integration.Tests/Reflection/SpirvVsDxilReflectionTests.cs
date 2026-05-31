#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Equivalence gate for the pure-managed <see cref="SpirvReflector"/> (Phase 19, WASM).
///
/// <para>For each Phase 17 PS-only corpus shader, the pixel entry point is compiled
/// to BOTH:</para>
/// <list type="number">
///   <item>DXIL (DirectX target) → reflected by the trusted native
///         <see cref="DxilReflectionExtractor"/> oracle, and</item>
///   <item>SPIR-V (OpenGL target) → reflected by the new managed
///         <see cref="SpirvReflector"/>.</item>
/// </list>
///
/// <para>The two <see cref="ReflectedEffect"/>s must agree on every field that drives
/// <c>.mgfx</c> output: each constant buffer's name / size and each variable's
/// name, offset, size, class, type, rows, columns, elements; each texture's name,
/// bind slot, dimension; each sampler's name and bind slot.</para>
///
/// <para>Both sides are preprocessed with the SAME (OpenGL) platform macros, exactly
/// as <c>CompilationPipeline</c> does for the OpenGL path: it compiles the
/// OpenGL-preprocessed text to DirectX (for DXIL reflection) and to OpenGL (for
/// SPIR-V) from the identical source, so the two reflections describe the same
/// shader.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class SpirvVsDxilReflectionTests
{
    private readonly ITestOutputHelper _output;

    public SpirvVsDxilReflectionTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] s_corpus =
    {
        "Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
        "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve",
    };

    public static IEnumerable<object[]> Corpus() =>
        s_corpus.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task SpirvReflection_MatchesDxilOracle(string fixtureStem)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        // --- Read + FX9 pre-parse + preprocess (OpenGL macros, as the GL path does) ---
        string fxPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", fixtureStem + ".fx");
        File.Exists(fxPath).Should().BeTrue($".fx fixture must exist at {fxPath}");

        string source = await File.ReadAllTextAsync(fxPath, ct);

        var parsed = FxPreParser.Parse(source, fxPath);
        parsed.IsSuccess.Should().BeTrue(
            because: parsed.IsFailure ? parsed.Error.Message : "FX pre-parse must succeed");
        FxParseResult fx = parsed.Value;

        fx.Techniques.Should().NotBeEmpty();
        ShadowDusk.HLSL.Ast.PassInfo pass = fx.Techniques.SelectMany(t => t.Passes).First();
        pass.PixelEntryPoint.Should().NotBeNull(because: "corpus shaders are PS-only");
        string psEntry = pass.PixelEntryPoint!;

        var preprocessor = new Preprocessor();
        var pre = preprocessor.Flatten(
            fx.StrippedHlsl,
            fxPath,
            PlatformMacros.For(PlatformTarget.OpenGL),
            new FileSystemIncludeResolver(),
            Array.Empty<string>());
        pre.IsSuccess.Should().BeTrue(
            because: pre.IsFailure ? pre.Error.Message : "preprocess must succeed");
        string hlsl = pre.Value.Text;

        // --- (a) Oracle: DXIL → DxilReflectionExtractor (+ ParameterListBuilder) ---
        ReadOnlyMemory<byte> dxil = await CompileAsync(hlsl, fxPath, psEntry, PlatformTarget.DirectX, ct);
        var oracleResult = new DxilReflectionExtractor().Extract(dxil, ct);
        oracleResult.IsSuccess.Should().BeTrue(
            because: oracleResult.IsFailure ? oracleResult.Error.Message : "DXIL reflection must succeed");
        ReflectedEffect oracle = oracleResult.Value;

        // --- (b) Subject: SPIR-V → SpirvReflector ---
        ReadOnlyMemory<byte> spirv = await CompileAsync(hlsl, fxPath, psEntry, PlatformTarget.OpenGL, ct);
        var subjectResult = new SpirvReflector().Reflect(spirv);
        subjectResult.IsSuccess.Should().BeTrue(
            because: subjectResult.IsFailure ? subjectResult.Error.Message : "SPIR-V reflection must succeed");
        ReflectedEffect subject = subjectResult.Value;

        DumpDiff(fixtureStem, oracle, subject);

        // --- Equivalence on .mgfx-driving fields ---
        AssertConstantBuffersEquivalent(oracle, subject);
        AssertTexturesEquivalent(oracle, subject);
        AssertSamplersEquivalent(oracle, subject);
    }

    private static void AssertConstantBuffersEquivalent(ReflectedEffect oracle, ReflectedEffect subject)
    {
        subject.ConstantBuffers.Should().HaveCount(oracle.ConstantBuffers.Count,
            "SPIR-V and DXIL must report the same number of constant buffers");

        var oracleByName = oracle.ConstantBuffers.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (ConstantBufferReflection sub in subject.ConstantBuffers)
        {
            oracleByName.Should().ContainKey(sub.Name);
            ConstantBufferReflection ora = oracleByName[sub.Name];

            sub.SizeBytes.Should().Be(ora.SizeBytes, $"cbuffer '{sub.Name}' size must match");

            var oraVars = ora.Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
            sub.Variables.Should().HaveCount(ora.Variables.Count,
                $"cbuffer '{sub.Name}' variable count must match");

            foreach (VariableReflection sv in sub.Variables)
            {
                oraVars.Should().ContainKey(sv.Name);
                VariableReflection ov = oraVars[sv.Name];

                sv.StartOffset.Should().Be(ov.StartOffset,    $"'{sub.Name}.{sv.Name}' StartOffset");
                sv.SizeBytes.Should().Be(ov.SizeBytes,        $"'{sub.Name}.{sv.Name}' SizeBytes");
                sv.ParameterClass.Should().Be(ov.ParameterClass, $"'{sub.Name}.{sv.Name}' ParameterClass");
                sv.ParameterType.Should().Be(ov.ParameterType,   $"'{sub.Name}.{sv.Name}' ParameterType");
                sv.Rows.Should().Be(ov.Rows,                  $"'{sub.Name}.{sv.Name}' Rows");
                sv.Columns.Should().Be(ov.Columns,            $"'{sub.Name}.{sv.Name}' Columns");
                sv.Elements.Should().Be(ov.Elements,          $"'{sub.Name}.{sv.Name}' Elements");
            }
        }
    }

    private static void AssertTexturesEquivalent(ReflectedEffect oracle, ReflectedEffect subject)
    {
        subject.Textures.Should().HaveCount(oracle.Textures.Count, "texture count must match");

        var oraByName = oracle.Textures.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (TextureReflection st in subject.Textures)
        {
            oraByName.Should().ContainKey(st.Name);
            TextureReflection ot = oraByName[st.Name];
            st.BindSlot.Should().Be(ot.BindSlot,   $"texture '{st.Name}' BindSlot");
            st.Dimension.Should().Be(ot.Dimension, $"texture '{st.Name}' Dimension");
        }
    }

    private static void AssertSamplersEquivalent(ReflectedEffect oracle, ReflectedEffect subject)
    {
        subject.Samplers.Should().HaveCount(oracle.Samplers.Count, "sampler count must match");

        var oraByName = oracle.Samplers.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (SamplerReflection ss in subject.Samplers)
        {
            oraByName.Should().ContainKey(ss.Name);
            SamplerReflection os = oraByName[ss.Name];
            ss.BindSlot.Should().Be(os.BindSlot, $"sampler '{ss.Name}' BindSlot");
        }
    }

    private void DumpDiff(string stem, ReflectedEffect oracle, ReflectedEffect subject)
    {
        _output.WriteLine($"=== {stem} ===");
        _output.WriteLine("ORACLE (DXIL):");
        Describe(oracle);
        _output.WriteLine("SUBJECT (SPIR-V):");
        Describe(subject);
    }

    private void Describe(ReflectedEffect e)
    {
        foreach (var cb in e.ConstantBuffers)
        {
            _output.WriteLine($"  cbuffer {cb.Name} size={cb.SizeBytes} slot={cb.BindSlot}");
            foreach (var v in cb.Variables)
                _output.WriteLine($"    {v.Name} off={v.StartOffset} size={v.SizeBytes} " +
                                  $"class={v.ParameterClass} type={v.ParameterType} " +
                                  $"r={v.Rows} c={v.Columns} elems={v.Elements}");
        }
        foreach (var t in e.Textures)
            _output.WriteLine($"  texture {t.Name} slot={t.BindSlot} dim={t.Dimension}");
        foreach (var s in e.Samplers)
            _output.WriteLine($"  sampler {s.Name} slot={s.BindSlot}");
    }

    private static async Task<ReadOnlyMemory<byte>> CompileAsync(
        string hlsl, string fileName, string entryPoint, PlatformTarget platform, CancellationToken ct)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = fileName,
            EntryPoint     = entryPoint,
            Stage          = ShaderStage.Pixel,
            Platform       = platform,
            // Mirror the pipeline: the DXIL reflection compile tolerates warnings;
            // the SPIR-V compile is the authoritative compile but warnings here are
            // not the focus of a reflection-equivalence test.
            Options        = new DxcCompileOptions { AllowWarnings = true },
        };
        var result = await compiler.CompileAsync(request, ct);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }
}
