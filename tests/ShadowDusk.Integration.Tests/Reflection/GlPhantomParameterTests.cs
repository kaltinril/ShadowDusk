#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Regression tests for the issue-#187 phantom-parameter class, fixed by the synthesized
/// GL backing step in <c>CompilationPipeline</c> (full record:
/// <c>plan/DONE/ISSUE-187-gl-phantom-parameter-compile-fidelity.md</c>).
///
/// <para><b>The class.</b> Desktop OpenGL reflects from a companion DXC compile targeting
/// DirectX SM6 DXIL, while the shipped GLSL comes from a separate <c>-spirv</c> compile.
/// The two can apply different dead-code elimination: <c>GradientToy.fx</c>'s pixel shader
/// computes <c>fragCoord = uv * iResolution.xy</c> then <c>fragCoord / iResolution.xy</c>,
/// an algebraic identity DXC's SPIR-V backend cancels (dropping the then-unused
/// <c>$Globals</c> cbuffer from the SPIR-V entirely) but the DXIL companion — like real
/// fxc/mgfxc — keeps. Unfixed, the .mgfx carried <c>Parameters["iResolution"]</c> with no
/// cbuffer record behind it: <c>SetValue</c> wrote into the parameter's CPU-side data and
/// nothing ever consumed it. The pipeline now completes the reflection→backing join:
/// a reflected numeric parameter absent from the rewriter's uniform layout gets a
/// synthesized register slot, cbuffer membership, and a covering
/// <c>uniform vec4 {vs,ps}_uniforms_vec4[N];</c> declaration — the same
/// parameter→cbuffer→declared-array chain the mgfxc golden has, and the same shape real
/// mgfxc's DirectX profile ships for any declared-but-unused uniform.</para>
///
/// <para><b>Why the assertion is structural, not textual.</b> This test originally asserted
/// <c>allGlsl.ShouldContain(p.Name)</c> — which can never pass for ANY correctly-backed GL
/// value parameter, including in the mgfxc golden itself: GL packs every non-sampler
/// uniform into the <c>{vs,ps}_uniforms_vec4[]</c> register arrays, so the literal
/// parameter name does not appear in healthy GLSL (the golden's own pixel shader contains
/// <c>ps_uniforms_vec4</c>/<c>ps_c0</c> and no literal <c>iResolution</c>). Backing is
/// therefore asserted through the records MonoGame actually consumes: cbuffer membership,
/// a shader referencing that cbuffer, and a declared array covering the recorded size.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class GlPhantomParameterTests
{
    private readonly ITestOutputHelper _output;

    public GlPhantomParameterTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task GradientToy_OpenGL_ReflectionHasNoPhantomParameters()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        string fxPath = TestHelpers.FixturePath("shadertoy/GradientToy.fx");
        File.Exists(fxPath).ShouldBeTrue($".fx fixture must exist at {fxPath}");
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = fxPath },
            ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
                ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : "compile must succeed");

        MgfxBlobReader mgfx = MgfxBlobReader.Parse(result.Value.Data);

        // Name parity with the mgfxc golden (the drop-in bar): the parameter must exist.
        mgfx.Parameters.Count.ShouldBe(1);
        mgfx.Parameters[0].Name.ShouldBe("iResolution");

        // Every numeric parameter is register-backed (the general #187 criterion).
        AssertNumericParametersBacked(mgfx, "GradientToy.fx (OpenGL)", _output);

        // Pin the specific golden-parity shape the synthesized backing must reproduce
        // (tests/fixtures/golden/OpenGL/GradientToy.mgfx carries exactly this):
        // one ps_uniforms_vec4 cbuffer, 16 bytes, iResolution (param 0) at offset 0,
        // referenced by the pixel shader.
        mgfx.ConstantBuffers.Count.ShouldBe(1);
        MgfxConstantBufferRecord cb = mgfx.ConstantBuffers[0];
        cb.Name.ShouldBe("ps_uniforms_vec4");
        cb.Size.ShouldBe(16);
        cb.ParameterIndices.ShouldBe(new[] { 0 });
        cb.ParameterOffsets.ShouldBe(new[] { 0 });

        MgfxShaderRecord ps = mgfx.Shaders.Single(s => !s.IsVertex);
        ps.ConstantBufferIndices.ShouldBe(new[] { 0 });
        Encoding.UTF8.GetString(ps.Bytecode)
            .ShouldContain("uniform vec4 ps_uniforms_vec4[1];", Case.Sensitive);
    }

    /// <summary>
    /// The three fixture-pinned sub-shapes of the #187 synthesis path, each guarding a
    /// defect the adversarial review found (or proved reachable) in the first cut:
    /// a NON-SQUARE MATRIX phantom must be sized by the runtime's transposed write model
    /// (Columns registers — Rows under-allocates and MonoGame throws on the first
    /// Apply); a phantom in a DERIVATIVE-using shader must get its declaration inserted
    /// after the whole <c>#extension</c> + <c>#ifdef GL_ES</c> prologue (strict ESSL
    /// front ends reject it earlier); and a phantom whose stage ALSO has live uniforms
    /// must RESIZE the existing declaration rather than insert a second one.
    /// </summary>
    [Theory]
    [InlineData("examples/ExPhantomNonSquareMatrix.fx", "PhantomM", 64, "0", "uniform vec4 ps_uniforms_vec4[4];", "")]
    [InlineData("examples/ExPhantomDerivativeUniform.fx", "GhostResolution", 16, "0", "uniform vec4 ps_uniforms_vec4[1];", "#extension GL_OES_standard_derivatives")]
    [InlineData("examples/ExPhantomSecondCbufferFold.fx", "GhostOffset", 32, "0,16", "uniform vec4 ps_uniforms_vec4[2];", "")]
    [InlineData("examples/ExPhantomTexLodUniform.fx", "GhostScale", 16, "0", "uniform vec4 ps_uniforms_vec4[1];", "#if __VERSION__ >= 300")]
    public async Task PhantomSubShapes_OpenGL_AreBackedWithCorrectFootprint(
        string fixture, string phantomName, int expectedCbSize, string expectedOffsets,
        string expectedDeclaration, string expectedPrologueMarker)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        string fxPath = TestHelpers.FixturePath(fixture);
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = fxPath },
            ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
                ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : "compile must succeed");

        MgfxBlobReader mgfx = MgfxBlobReader.Parse(result.Value.Data);
        AssertNumericParametersBacked(mgfx, $"{fixture} (OpenGL)", _output);

        mgfx.Parameters.Select(p => p.Name).ShouldContain(phantomName);
        mgfx.ConstantBuffers.Count.ShouldBe(1);
        mgfx.ConstantBuffers[0].Size.ShouldBe(expectedCbSize);
        string.Join(",", mgfx.ConstantBuffers[0].ParameterOffsets)
            .ShouldBe(expectedOffsets, "synthesized offsets must append after the live registers");

        MgfxShaderRecord ps = mgfx.Shaders.Single(s => !s.IsVertex);
        string glsl = Encoding.UTF8.GetString(ps.Bytecode);
        glsl.ShouldContain(expectedDeclaration, Case.Sensitive);
        // Exactly one declaration (the resize path must never leave two), and it must
        // sit after the ENTIRE leading preprocessor prologue: after every #extension
        // line (GLSL ES requires #extension before non-preprocessor tokens; Mesa
        // desktop hard-errors on a mid-shader one), after the #ifdef GL_ES precision
        // block's #endif (ESSL needs a default float precision before the first
        // float-typed global in a fragment shader), and after the balanced TexLod
        // '#if __VERSION__ >= 300 … #endif' header when present.
        Regex.Matches(glsl, @"^uniform vec4 ps_uniforms_vec4\[\d+\];", RegexOptions.Multiline)
            .Count.ShouldBe(1);
        int declIndex = glsl.IndexOf(expectedDeclaration, StringComparison.Ordinal);
        int lastExtension = glsl.LastIndexOf("#extension", StringComparison.Ordinal);
        if (lastExtension >= 0)
            declIndex.ShouldBeGreaterThan(lastExtension);
        if (glsl.Contains("#ifdef GL_ES"))
            declIndex.ShouldBeGreaterThan(glsl.IndexOf("#endif", StringComparison.Ordinal));
        // The marker rows exist to pin a prologue interaction; assert the marker
        // UNCONDITIONALLY so the pin fails loudly (instead of passing vacuously) if a
        // rewriter or DXC-pin change ever stops emitting that prologue or stops folding
        // the fixture's uniform.
        if (expectedPrologueMarker.Length > 0)
        {
            int markerIndex = glsl.IndexOf(expectedPrologueMarker, StringComparison.Ordinal);
            markerIndex.ShouldBeGreaterThanOrEqualTo(0,
                $"the fixture exists to pin the '{expectedPrologueMarker}' prologue interaction; " +
                "if the prologue is gone the pin is vacuous and the fixture needs rethinking");
            declIndex.ShouldBeGreaterThan(markerIndex);
            if (expectedPrologueMarker.StartsWith("#if", StringComparison.Ordinal))
                declIndex.ShouldBeGreaterThan(
                    glsl.IndexOf("#endif", markerIndex, StringComparison.Ordinal),
                    "the declaration must land after the balanced prologue block's own #endif");
        }
    }

    /// <summary>
    /// Corpus-wide tripwire for the #187 class: DXC-folds-what-fxc-keeps is a family, and
    /// this sweep turns any future member from a silent divergence into a red test. Every
    /// fixture that compiles for OpenGL must give every reflected numeric parameter real
    /// register backing. Fixtures that do not compile for GL are skipped (the corpus
    /// deliberately contains non-GL and deliberate-error fixtures, each covered by its own
    /// tests); a floor on the compiled count keeps this sweep from rotting into a vacuous
    /// pass if compilation ever breaks en masse.
    /// </summary>
    [Fact]
    public async Task Corpus_OpenGL_EveryNumericParameterHasRegisterBacking()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        string shadersRoot = TestHelpers.FixturePath("");
        var fixtures = Directory
            .EnumerateFiles(shadersRoot, "*.fx", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        fixtures.ShouldNotBeEmpty();

        int compiled = 0;
        int skipped = 0;
        bool sawGradientToy = false;

        foreach (string fxPath in fixtures)
        {
            string rel = Path.GetRelativePath(shadersRoot, fxPath).Replace('\\', '/');
            string source = await File.ReadAllTextAsync(fxPath, ct);

            var result = await new EffectCompiler().CompileAsync(
                source,
                new CompilerOptions
                {
                    Target = PlatformTarget.OpenGL,
                    SourceFileName = fxPath,
                    IncludeResolver = new FileSystemIncludeResolver(),
                },
                ct);

            if (result.IsFailure)
            {
                skipped++;
                continue;
            }

            compiled++;
            if (rel == "shadertoy/GradientToy.fx")
                sawGradientToy = true;

            MgfxBlobReader mgfx = MgfxBlobReader.Parse(result.Value.Data);
            AssertNumericParametersBacked(mgfx, $"{rel} (OpenGL)", _output);
        }

        _output.WriteLine($"swept {fixtures.Count} fixtures: {compiled} compiled, {skipped} skipped (non-GL/deliberate-error corpus members)");

        // The founding member of the phantom class must actually be in the swept set, and
        // the floor pins the exact compiled count so a pipeline regression cannot silently
        // reclassify fixtures from compiled to skipped (a skipped fixture is unswept).
        // UPDATE THE FLOOR when adding corpus fixtures: 108 = the 104 GL-compiling
        // fixtures at the time of the #187 fix + the four ExPhantom* fixtures it added.
        sawGradientToy.ShouldBeTrue("shadertoy/GradientToy.fx must compile for OpenGL and be swept");
        compiled.ShouldBeGreaterThanOrEqualTo(108);
    }

    /// <summary>
    /// The structural #187 criterion: every Scalar/Vector/Matrix parameter must be
    /// reachable by MonoGame's upload chain — a cbuffer record lists its index at an
    /// offset whose buffer covers the RUNTIME'S write footprint (MonoGame uploads a
    /// Matrix transposed, writing <c>ColumnCount</c> 16-byte rows — an undersized record
    /// is an <c>ArgumentException</c> on the first <c>EffectPass.Apply</c>, no SetValue
    /// needed), a shader references that cbuffer, and EVERY referencing shader's GLSL
    /// declares the cbuffer-named <c>uniform vec4 …[N]</c> array covering the recorded
    /// size. (Object parameters are never cbuffer-backed by design — MonoGame binds them
    /// through the sampler table — and Struct parameters are out of scope, matching the
    /// pipeline.)
    /// </summary>
    private static void AssertNumericParametersBacked(
        MgfxBlobReader mgfx, string context, ITestOutputHelper output)
    {
        // Per-cbuffer overlap check: no two members' write spans may intersect. A
        // synthesized slot mis-offset INSIDE the buffer (overlapping a live parameter)
        // corrupts the live parameter's data at upload time while every size-only
        // check stays green — the one mutation the final audit found surviving.
        for (int cbIdx = 0; cbIdx < mgfx.ConstantBuffers.Count; cbIdx++)
        {
            MgfxConstantBufferRecord cb = mgfx.ConstantBuffers[cbIdx];
            var spans = new List<(int Start, int End, string Name)>();
            for (int pos = 0; pos < cb.ParameterIndices.Count; pos++)
            {
                MgfxParameterRecord member = mgfx.Parameters[cb.ParameterIndices[pos]];
                int start = cb.ParameterOffsets[pos];
                spans.Add((start, start + FootprintBytes(member), member.Name));
            }
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 1; i < spans.Count; i++)
                spans[i].Start.ShouldBeGreaterThanOrEqualTo(spans[i - 1].End,
                    $"{context}: cbuffer '{cb.Name}' members '{spans[i - 1].Name}' " +
                    $"[{spans[i - 1].Start},{spans[i - 1].End}) and '{spans[i].Name}' " +
                    $"[{spans[i].Start},…) overlap — uploads would corrupt each other.");
        }

        for (int k = 0; k < mgfx.Parameters.Count; k++)
        {
            MgfxParameterRecord p = mgfx.Parameters[k];
            // EffectParameterClass: 0 Scalar, 1 Vector, 2 Matrix, 3 Object, 4 Struct.
            if (p.Class > 2)
                continue;

            int footprintBytes = FootprintBytes(p);

            // A parameter is backed when at least one listing cbuffer is referenced by
            // a shader AND every referencing shader of EVERY listing cbuffer declares a
            // covering array — first-backing-wins would let a second, broken record
            // (e.g. the other stage's) hide behind a healthy first one.
            bool anyBacked = false;
            bool anyViolation = false;
            var trail = new StringBuilder();

            for (int cbIdx = 0; cbIdx < mgfx.ConstantBuffers.Count; cbIdx++)
            {
                MgfxConstantBufferRecord cb = mgfx.ConstantBuffers[cbIdx];
                int pos = cb.ParameterIndices.ToList().IndexOf(k);
                if (pos < 0)
                    continue;
                trail.Append($"[cb{cbIdx} '{cb.Name}' offset={cb.ParameterOffsets[pos]} size={cb.Size}]");

                // The buffer must cover the runtime's write for this parameter — the
                // issue-#187 review's non-square-matrix crash class.
                (cb.ParameterOffsets[pos] + footprintBytes).ShouldBeLessThanOrEqualTo(cb.Size,
                    $"{context}: parameter '{p.Name}' (class={p.Class} rows={p.Rows} " +
                    $"cols={p.Columns} elements={p.ElementCount}) sits at offset " +
                    $"{cb.ParameterOffsets[pos]} of cbuffer '{cb.Name}' (size {cb.Size}), " +
                    $"but MonoGame's ConstantBuffer upload writes {footprintBytes} bytes " +
                    "for it — an undersized record throws ArgumentException on the first " +
                    "EffectPass.Apply.");

                var referencing = mgfx.Shaders
                    .Where(s => s.ConstantBufferIndices.Contains(cbIdx))
                    .ToList();
                if (referencing.Count == 0)
                    continue;

                bool allDeclare = true;
                foreach (MgfxShaderRecord s in referencing)
                {
                    string glsl = Encoding.UTF8.GetString(s.Bytecode);
                    Match decl = Regex.Match(
                        glsl,
                        $@"^uniform vec4 {Regex.Escape(cb.Name)}\[(\d+)\];",
                        RegexOptions.Multiline);
                    trail.Append($"[shader{s.Index} decl={(decl.Success ? decl.Value : "MISSING")}]");
                    if (!decl.Success || int.Parse(decl.Groups[1].Value) * 16 < cb.Size)
                        allDeclare = false;
                }
                if (allDeclare)
                    anyBacked = true;
                else
                    anyViolation = true;
            }

            bool backed = anyBacked && !anyViolation;
            if (!backed)
                output.WriteLine($"{context}: parameter '{p.Name}' unbacked; trail: {trail}");

            backed.ShouldBeTrue(
                $"{context}: numeric parameter '{p.Name}' (index {k}) has no register " +
                "backing — no cbuffer record lists it, no shader references a listing " +
                "cbuffer, or a referencing shader's GLSL never declares a covering " +
                "{vs,ps}_uniforms_vec4[N] array. SetValue on it would write nowhere " +
                "(the issue-#187 phantom-parameter class).");
        }
    }

    /// <summary>
    /// Register-granular footprint of what MonoGame/KNI write for a parameter:
    /// Matrix → ColumnCount 16-byte rows per element (the transposed upload);
    /// Scalar/Vector → one register per row (= 1); arrays → once per element.
    /// </summary>
    private static int FootprintBytes(MgfxParameterRecord p)
    {
        int perElementRegisters = p.Class == 2 ? Math.Max(1, (int)p.Columns) : Math.Max(1, (int)p.Rows);
        return perElementRegisters * Math.Max(1, p.ElementCount) * 16;
    }
}
