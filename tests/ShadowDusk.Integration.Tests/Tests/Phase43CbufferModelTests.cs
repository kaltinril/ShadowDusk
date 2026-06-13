#nullable enable

using System.Text;
using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 43C (F4/F5/F6) — the GL cbuffer/array model, structurally validated against
/// the mgfxc 3.8.2.1105 goldens.
///
/// <para><b>The model (pinned by the goldens, mgfxc is the spec):</b></para>
/// <list type="bullet">
///   <item><b>F4 — shared VS+PS cbuffer:</b> a PER-STAGE record each
///   (<c>vs_uniforms_vec4</c> AND <c>ps_uniforms_vec4</c>), the stage's shader binding
///   its record by index. (The SkinnedEffect golden additionally pins that several
///   records may share a NAME.) Pre-43C ShadowDusk deduped across stages into one
///   <c>ps_uniforms_vec4</c> record while the VS GLSL read <c>vs_uniforms_vec4[]</c> —
///   VS uniforms silently read zero.</item>
///   <item><b>F5 — multiple same-stage cbuffers:</b> ONE merged record covering all
///   members in declaration order (MojoShader's single-register-file-per-stage model;
///   the MultiCbuffer golden: TintA@0, TintB@16, MixAmount@32, size 48).</item>
///   <item><b>F6 — arrays:</b> element stride one register for vec types, four for
///   mat4 (ArrayUniform golden: Colors@0/Weights@64, size 128; ArrayUniformVs golden:
///   Bones@0/PosOffsets@128, size 160), and the parameter carries N RECURSIVE element
///   sub-records (empty name/semantic, parent shape, zero-data leaf) on every
///   target.</item>
/// </list>
///
/// <para><b>Pinned divergence (render-proven equivalent, see validation/CbufferModel):</b>
/// mgfxc's per-stage records contain only the constants fxc kept for that stage
/// (SharedCbuffer golden: vs size 64 = WVP only, ps size 16 = DiffuseColor only),
/// while ShadowDusk's carry the cbuffer's FULL declared layout per stage (vs AND ps
/// size 80, both members listed). Both are self-consistent — each .mgfx's offsets
/// agree with its own GLSL — and parameters are set by name, so the runtime behaviour
/// is identical; only unused registers are uploaded additionally.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class Phase43CbufferModelTests
{
    private readonly ITestOutputHelper _output;

    public Phase43CbufferModelTests(ITestOutputHelper output) => _output = output;

    private static async Task<MgfxBlobReader> CompileAsync(string stem, PlatformTarget target, CancellationToken ct)
    {
        string fxPath = TestHelpers.FixturePath(stem + ".fx");
        string source = await File.ReadAllTextAsync(fxPath, ct);
        var result = await new EffectCompiler().CompileAsync(
            source, new CompilerOptions { Target = target, SourceFileName = fxPath }, ct);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure
                ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : $"{stem} must compile for {target}");
        return MgfxBlobReader.Parse(result.Value.Data);
    }

    private static MgfxBlobReader ParseGolden(string profileDir, string stem)
    {
        string path = Path.Combine(
            FindRepoRoot(), "tests", "fixtures", "golden", profileDir, stem + ".mgfx");
        File.Exists(path).Should().BeTrue($"mgfxc golden must exist at {path}");
        return MgfxBlobReader.Parse(File.ReadAllBytes(path));
    }

    private static string Ascii(byte[] blob) =>
        Encoding.ASCII.GetString(blob.Select(b => (b >= 9 && b <= 126) ? b : (byte)' ').ToArray());

    /// <summary>name → offset map for a cb record, resolved through the parameter list.</summary>
    private static Dictionary<string, int> OffsetsByName(MgfxBlobReader reader, MgfxConstantBufferRecord cb)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < cb.ParameterIndices.Count; i++)
            map[reader.Parameters[cb.ParameterIndices[i]].Name] = cb.ParameterOffsets[i];
        return map;
    }

    // ------------------------------------------------------------------ F4 ----

    [Fact]
    public async Task SharedCbuffer_EmitsPerStageRecords_VsArrayIsBindable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        MgfxBlobReader subject = await CompileAsync("SharedCbuffer", PlatformTarget.OpenGL, cts.Token);
        MgfxBlobReader golden  = ParseGolden("OpenGL", "SharedCbuffer");

        // mgfxc's model: one record per stage. The golden has exactly
        // {ps_uniforms_vec4, vs_uniforms_vec4}; so must we.
        golden.ConstantBuffers.Select(c => c.Name).Should().BeEquivalentTo(
            new[] { "ps_uniforms_vec4", "vs_uniforms_vec4" },
            because: "the golden pins mgfxc's per-stage record model");
        subject.ConstantBuffers.Select(c => c.Name).Should().BeEquivalentTo(
            new[] { "ps_uniforms_vec4", "vs_uniforms_vec4" },
            because: "a shared cbuffer must yield a record per stage (F4) — the cross-stage " +
                     "dedup left the VS array unbindable and VS uniforms silently zero");

        // Each stage's shader binds ITS record (by index), exactly like the golden.
        foreach (MgfxShaderRecord shader in subject.Shaders)
        {
            shader.ConstantBufferIndices.Should().HaveCount(1);
            string expected = shader.IsVertex ? "vs_uniforms_vec4" : "ps_uniforms_vec4";
            subject.ConstantBuffers[shader.ConstantBufferIndices[0]].Name.Should().Be(expected,
                $"the {(shader.IsVertex ? "VS" : "PS")} must bind its own stage's record");
        }

        // The VS record and the VS GLSL share one allocation: WorldViewProjection at
        // offset 0, and the declared array length == record size / 16. (The golden
        // agrees on the used member's offset; sizes differ by the pinned
        // full-declared-layout divergence in the class doc.)
        MgfxConstantBufferRecord vsRecord = subject.ConstantBuffers.Single(c => c.Name == "vs_uniforms_vec4");
        MgfxShaderRecord vsShader = subject.Shaders.Single(s => s.IsVertex);
        OffsetsByName(subject, vsRecord)["WorldViewProjection"].Should().Be(0);
        OffsetsByName(golden, golden.ConstantBuffers.Single(c => c.Name == "vs_uniforms_vec4"))
            ["WorldViewProjection"].Should().Be(0, "golden agreement on the used VS member's offset");

        string vsGlsl = Ascii(vsShader.Bytecode);
        vsGlsl.Should().Contain($"uniform vec4 vs_uniforms_vec4[{vsRecord.Size / 16}];",
            because: "the VS GLSL must declare the exact array the record describes (the F4 smoking gun)");

        // PS side likewise.
        MgfxConstantBufferRecord psRecord = subject.ConstantBuffers.Single(c => c.Name == "ps_uniforms_vec4");
        OffsetsByName(subject, psRecord).Should().ContainKey("DiffuseColor");
        string psGlsl = Ascii(subject.Shaders.Single(s => !s.IsVertex).Bytecode);
        psGlsl.Should().Contain($"uniform vec4 ps_uniforms_vec4[{psRecord.Size / 16}];");
    }

    // ------------------------------------------------------------------ F5 ----

    [Fact]
    public async Task MultiCbuffer_MergesIntoOneRecord_MatchingGoldenLayoutExactly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        MgfxBlobReader subject = await CompileAsync("MultiCbuffer", PlatformTarget.OpenGL, cts.Token);
        MgfxBlobReader golden  = ParseGolden("OpenGL", "MultiCbuffer");

        // ONE merged ps record (no member of either cbuffer is unused, so layout
        // matches the golden EXACTLY: TintA@0, TintB@16, MixAmount@32, size 48).
        subject.ConstantBuffers.Should().HaveCount(1);
        golden.ConstantBuffers.Should().HaveCount(1, "the golden pins mgfxc's merge model");

        MgfxConstantBufferRecord sub = subject.ConstantBuffers[0];
        MgfxConstantBufferRecord gold = golden.ConstantBuffers[0];
        sub.Name.Should().Be("ps_uniforms_vec4");
        sub.Size.Should().Be(gold.Size);
        OffsetsByName(subject, sub).Should().BeEquivalentTo(OffsetsByName(golden, gold));

        // No raw std140 block may survive in the GLSL (the F5 Effect-load failure),
        // and the merged array must carry the record's register count.
        string glsl = Ascii(subject.ShaderBlobs[0]);
        glsl.Should().NotContain("std140");
        glsl.Should().NotContain("type_");
        glsl.Should().Contain($"uniform vec4 ps_uniforms_vec4[{sub.Size / 16}];");
    }

    [Fact]
    public async Task MultiCbufferVs_MergesIntoOneVsRecord_MatchingGoldenLayoutExactly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        MgfxBlobReader subject = await CompileAsync("MultiCbufferVs", PlatformTarget.OpenGL, cts.Token);
        MgfxBlobReader golden  = ParseGolden("OpenGL", "MultiCbufferVs");

        MgfxConstantBufferRecord sub  = subject.ConstantBuffers.Single();
        MgfxConstantBufferRecord gold = golden.ConstantBuffers.Single();
        sub.Name.Should().Be("vs_uniforms_vec4");
        gold.Name.Should().Be("vs_uniforms_vec4");
        sub.Size.Should().Be(gold.Size);
        OffsetsByName(subject, sub).Should().BeEquivalentTo(OffsetsByName(golden, gold),
            because: "both cbuffers' members must fold into one vs register space in declaration order");

        string vsGlsl = Ascii(subject.Shaders.Single(s => s.IsVertex).Bytecode);
        vsGlsl.Should().NotContain("std140");
        vsGlsl.Should().Contain($"uniform vec4 vs_uniforms_vec4[{sub.Size / 16}];");
    }

    // ------------------------------------------------------------------ F6 ----

    [Theory]
    [InlineData("ArrayUniform",   PlatformTarget.OpenGL,  "OpenGL")]
    [InlineData("ArrayUniform",   PlatformTarget.DirectX, "DirectX_11")]
    [InlineData("ArrayUniformVs", PlatformTarget.OpenGL,  "OpenGL")]
    [InlineData("ArrayUniformVs", PlatformTarget.DirectX, "DirectX_11")]
    public async Task ArrayParameters_CarryElementSubRecords_MatchingGoldenRecursively(
        string stem, PlatformTarget target, string goldenDir)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        MgfxBlobReader subject = await CompileAsync(stem, target, cts.Token);
        MgfxBlobReader golden  = ParseGolden(goldenDir, stem);

        var subjectByName = subject.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (MgfxParameterRecord gold in golden.Parameters.Where(p => p.ElementCount > 0))
        {
            _output.WriteLine($"{stem}/{target}: comparing array parameter '{gold.Name}' " +
                              $"({gold.ElementCount} elements)");
            subjectByName.Should().ContainKey(gold.Name);
            AssertParameterTreeEqual(subjectByName[gold.Name], gold, gold.Name);
        }

        // At least one array parameter must exist in the golden, or the test is vacuous.
        golden.Parameters.Should().Contain(p => p.ElementCount > 0,
            "the fixture exists to pin the array model");
    }

    [Fact]
    public async Task ArrayUniform_GlRecord_PacksElementsAtRegisterStride_MatchingGolden()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        MgfxBlobReader subject = await CompileAsync("ArrayUniform", PlatformTarget.OpenGL, cts.Token);
        MgfxBlobReader golden  = ParseGolden("OpenGL", "ArrayUniform");

        MgfxConstantBufferRecord sub  = subject.ConstantBuffers.Single();
        MgfxConstantBufferRecord gold = golden.ConstantBuffers.Single();
        sub.Name.Should().Be("ps_uniforms_vec4");
        sub.Size.Should().Be(gold.Size, "float4[4] = 4 registers, float[4] = 4 registers (stride 16)");
        OffsetsByName(subject, sub).Should().BeEquivalentTo(OffsetsByName(golden, gold));

        string glsl = Ascii(subject.ShaderBlobs[0]);
        glsl.Should().Contain($"uniform vec4 ps_uniforms_vec4[{sub.Size / 16}];");
        glsl.Should().NotContain("_Globals",
            because: "no reference to the deleted SPIRV-Cross block may survive (the F6 failure)");
    }

    [Fact]
    public async Task ArrayUniformVs_GlRecord_Mat4ArrayStrideFour_MatchingGolden()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        MgfxBlobReader subject = await CompileAsync("ArrayUniformVs", PlatformTarget.OpenGL, cts.Token);
        MgfxBlobReader golden  = ParseGolden("OpenGL", "ArrayUniformVs");

        MgfxConstantBufferRecord sub  = subject.ConstantBuffers.Single();
        MgfxConstantBufferRecord gold = golden.ConstantBuffers.Single();
        sub.Name.Should().Be("vs_uniforms_vec4");
        sub.Size.Should().Be(gold.Size, "float4x4[2] = 8 registers + float4[2] = 2 registers");
        OffsetsByName(subject, sub).Should().BeEquivalentTo(OffsetsByName(golden, gold),
            because: "Bones@0 and PosOffsets@128 — mat4 array elements stride FOUR registers");

        // The VS GLSL reads BOTH elements of both arrays at the packed offsets. The mat4
        // elements are reconstructed TRANSPOSED (issue #70): the registers become the
        // matrix ROWS, so each transposed-matrix column is vec4(reg[a].c, reg[b].c, …).
        string vsGlsl = Ascii(subject.Shaders.Single(s => s.IsVertex).Bytecode);
        vsGlsl.Should().Contain(
            "vec4(vs_uniforms_vec4[0].x, vs_uniforms_vec4[1].x, vs_uniforms_vec4[2].x, vs_uniforms_vec4[3].x)",
            because: "Bones[0] sits at registers 0-3, reconstructed transposed");
        vsGlsl.Should().Contain(
            "vec4(vs_uniforms_vec4[4].x, vs_uniforms_vec4[5].x, vs_uniforms_vec4[6].x, vs_uniforms_vec4[7].x)",
            because: "Bones[1] sits at registers 4-7 (mat4 array elements stride FOUR), transposed");
        vsGlsl.Should().Contain("vs_uniforms_vec4[8]", because: "PosOffsets[0] sits at register 8");
        vsGlsl.Should().Contain("vs_uniforms_vec4[9]", because: "PosOffsets[1] sits at register 9");
    }

    /// <summary>
    /// Recursive exact comparison of a parameter and its element/member sub-records
    /// against the golden — the shape MonoGame's recursive ReadParameters consumes.
    /// </summary>
    private static void AssertParameterTreeEqual(MgfxParameterRecord sub, MgfxParameterRecord gold, string path)
    {
        sub.Class.Should().Be(gold.Class,     $"{path}.Class");
        sub.Type.Should().Be(gold.Type,       $"{path}.Type");
        sub.Rows.Should().Be(gold.Rows,       $"{path}.Rows");
        sub.Columns.Should().Be(gold.Columns, $"{path}.Columns");
        sub.Semantic.Should().Be(gold.Semantic, $"{path}.Semantic");
        sub.ElementCount.Should().Be(gold.ElementCount, $"{path}.ElementCount");
        sub.MemberCount.Should().Be(gold.MemberCount,   $"{path}.MemberCount");
        for (int i = 0; i < gold.Elements.Count; i++)
        {
            sub.Elements[i].Name.Should().Be(gold.Elements[i].Name,
                $"{path}[{i}].Name (mgfxc gives elements EMPTY names)");
            AssertParameterTreeEqual(sub.Elements[i], gold.Elements[i], $"{path}[{i}]");
        }
        for (int i = 0; i < gold.Members.Count; i++)
            AssertParameterTreeEqual(sub.Members[i], gold.Members[i], $"{path}.{gold.Members[i].Name}");
    }

    // ------------------------------------------------- staged-scope loud fails ----

    [Theory]
    [InlineData("examples/ExIntUniformMember.fx", "integer/boolean uniforms")]
    [InlineData("examples/ExMat3UniformMember.fx", "float4x4")]
    public async Task UnmodeledUniformMember_FailsLoudly_SD0210_NeverInvalidGlslWithExit0(
        string fx, string expectedFragment)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await TestHelpers.CompileFixtureAsync(fx, "OpenGL", ct: cts.Token);

        result.ExitCode.Should().NotBe(0,
            because: "an unmodelled uniform member previously shipped invalid GLSL (a reference " +
                     "to the deleted block) with exit code 0, failing only at Effect-load time");
        result.Stderr.Should().Contain("SD0210");
        result.Stderr.Should().Contain(expectedFragment,
            because: "the diagnostic must name the actual limitation");
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (ShadowDusk.slnx) above the test output directory.");
    }
}
