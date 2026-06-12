#nullable enable

using System.Text;
using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.ImageTests.GlContext;
using ShadowDusk.ImageTests.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Phase 34 — GL texture breadth (cube maps, 3D/volume, explicit-LOD/gradient).
///
/// <para>Two evidence rungs beyond the rewriter/integration unit tests:</para>
/// <list type="number">
///   <item><b>Cube-map cross-validation (same-backend oracle, rung 3).</b>
///   ShadowDusk's cube-map <c>.mgfx</c> is checked against the mgfxc
///   <c>EnvironmentMapEffect.mgfx</c> golden: BOTH emit the legacy
///   <c>samplerCube ps_s{k}</c> + <c>textureCube(ps_s{k}, …)</c> form AND BOTH
///   carry sampler-type byte <c>1</c> (MonoGame <c>SamplerType.SamplerCube</c>).
///   This is GL↔GL fidelity against the reference compiler.</item>
///   <item><b>Real-driver GLSL compile + link (rung 3).</b> The cube/3D/LOD/grad
///   fragment GLSL ShadowDusk emits is compiled AND linked in the live GL 3.3
///   Compatibility context the image suite uses. This catches exactly the silent
///   break Phase 33 guarded against — a <c>texture2D()</c> call on a non-2D
///   sampler is an invalid-overload <i>compile</i> error, so a clean compile/link
///   proves the dimension-specific builtins are valid.</item>
/// </list>
///
/// <para><b>What is NOT done here (honest scope):</b> a full pixel-equivalence
/// render of a cube/3D scene in real MonoGame/KNI. The image harness'
/// <see cref="ShaderSceneRenderer"/> is hardwired to 2D textures
/// (<c>TextureTarget.Texture2D</c>, a single 2D quad) — a cube/3D render scene
/// (6 cube faces / a volume, a direction-vector VS) is non-trivial new harness
/// work. The cube golden cross-val (rung 3, same-backend) plus the real-driver
/// compile/link is the strongest evidence achievable in-env without that scene;
/// rung-4 render validation in real KNI HiDef/Reach is carried forward.</para>
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)] // shared GL fixture; see GlContextCollection
public sealed class Phase34TextureBreadthTests
{
    private readonly GlContextFixture  _fixture;
    private readonly ITestOutputHelper _output;

    public Phase34TextureBreadthTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    private static async Task<byte[]> CompileFixtureAsync(string relativeFxPath, CancellationToken ct)
    {
        string fxPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", relativeFxPath);
        File.Exists(fxPath).Should().BeTrue($".fx fixture must exist at {fxPath}");
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var options = new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        };
        var result = await new EffectCompiler().CompileAsync(source, options, ct);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure
                ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
                : "compile must succeed");
        return result.Value.Data;
    }

    // ---------------------------------------------------------------------
    // 1. Cube-map cross-validation against the mgfxc golden (same-backend).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CubeMap_MatchesMgfxcGolden_LegacyFormAndSamplerTypeByte()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // mgfxc golden: EnvironmentMapEffect has a 2D Texture (slot 0) + a cube
        // EnvironmentMap (slot 1). Confirm the ORACLE itself first.
        string goldenPath = Path.Combine(
            AppContext.BaseDirectory, "fixtures", "golden", "OpenGL", "EnvironmentMapEffect.mgfx");
        File.Exists(goldenPath).Should().BeTrue(
            $"the mgfxc cube golden must exist at {goldenPath} (the same-backend oracle)");
        byte[] golden = await File.ReadAllBytesAsync(goldenPath, cts.Token);

        var goldenSamplers = SamplerTableDecoder.Decode(golden);
        goldenSamplers.Should().Contain(s => s.Type == 1,
            because: "mgfxc encodes the cube sampler with SamplerType.SamplerCube (byte 1)");
        string goldenAscii = Ascii(golden);
        goldenAscii.Should().Contain("samplerCube ps_s",
            because: "mgfxc's own cube golden declares samplerCube ps_s{k}");
        goldenAscii.Should().Contain("textureCube(ps_s",
            because: "mgfxc samples the cube via textureCube(ps_s{k}, …)");

        // ShadowDusk's cube fixture must produce the SAME backend form + type byte.
        byte[] sd = await CompileFixtureAsync("examples/ExCubeSamplerHidef.fx", cts.Token);

        var sdSamplers = SamplerTableDecoder.Decode(sd);
        sdSamplers.Should().ContainSingle();
        sdSamplers[0].Type.Should().Be(1,
            because: "ShadowDusk must encode the cube sampler with the SAME SamplerType byte as mgfxc (1)");

        string sdAscii = Ascii(sd);
        sdAscii.Should().Contain("uniform samplerCube ps_s0;",
            because: "same legacy decl form as the mgfxc cube golden");
        sdAscii.Should().Contain("textureCube(ps_s0,",
            because: "same dimension-specific builtin as the mgfxc cube golden");

        _output.WriteLine(
            $"cube golden sampler types = [{string.Join(", ", goldenSamplers.Select(s => s.Type))}]; " +
            $"ShadowDusk cube sampler type = {sdSamplers[0].Type}");
    }

    [Fact]
    public async Task VolumeTexture_SamplerTypeByte_IsVolume()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] sd = await CompileFixtureAsync("examples/ExVolumeTextureHidef.fx", cts.Token);

        var samplers = SamplerTableDecoder.Decode(sd);
        samplers.Should().ContainSingle();
        samplers[0].Type.Should().Be(2,
            because: "a 3D texture maps to MonoGame SamplerType.SamplerVolume (byte 2)");
    }

    [Theory]
    [InlineData("examples/ExSampleLevelHidef.fx")]
    [InlineData("examples/ExSampleGradHidef.fx")]
    public async Task LodGrad_SamplerTypeByte_StaysTwoD(string fx)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] sd = await CompileFixtureAsync(fx, cts.Token);

        var samplers = SamplerTableDecoder.Decode(sd);
        samplers.Should().ContainSingle();
        samplers[0].Type.Should().Be(0,
            because: "LOD/grad sampling still uses a 2D sampler (SamplerType.Sampler2D, byte 0)");
    }

    // ---------------------------------------------------------------------
    // 2. Real-driver GLSL compile + link (rung 3): the dimension-specific
    //    builtins must be valid GLSL in the live driver. A texture2D() on a
    //    non-2D sampler is an invalid-overload COMPILE error — so a clean
    //    compile+link proves the emitted form is correct.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("examples/ExCubeSamplerHidef.fx",   "textureCube")]
    [InlineData("examples/ExVolumeTextureHidef.fx", "texture3D")]
    [InlineData("examples/ExSampleLevelHidef.fx",   "texture2DLod")]  // Phase 43 F7: legacy name + guarded header
    [InlineData("examples/ExSampleGradHidef.fx",    "texture2DGrad")] // Phase 43 F7: legacy name + guarded header
    public async Task EmittedGlsl_CompilesAndLinks_InRealDriver(string fx, string expectedBuiltin)
    {
        if (_fixture.IsSkipped)
        {
            _output.WriteLine(_fixture.SoftSkipLine);
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] sd = await CompileFixtureAsync(fx, cts.Token);

        // Extract the PS GLSL (these fixtures are PS-only → null VS) and pair it with
        // the MojoShader passthrough VS, which supplies vTexCoord0 as a vec4 varying —
        // the cube/3D PS reads vTexCoord0.xyz (a 3-component direction), exactly what
        // a cube/3D sample needs.
        GlslShaderPair pair = GlslShaderExtractor.Extract(sd);
        pair.FragmentSource.Should().Contain(expectedBuiltin,
            because: $"{fx} must emit the dimension-specific builtin {expectedBuiltin}");

        string vs = pair.VertexSource ?? PassthroughVertexShader.PickFor(pair.FragmentSource);

        using (_fixture.MakeContextCurrent())
        {
            // GlslShaderProgram.Compile throws GlslCompileException on a compile OR link
            // failure, embedding the driver info-log. A texture2D(non-2D sampler) overload
            // error would surface HERE. Success == the legacy dimension-specific builtin
            // is valid in the real driver.
            using var program = GlslShaderProgram.Compile(_fixture.Gl, vs, pair.FragmentSource);
            program.Handle.Should().NotBe(0u);
        }

        _output.WriteLine($"{fx}: emitted {expectedBuiltin}; PS compiled + linked OK in the real driver.");
    }

    private static string Ascii(byte[] mgfx) =>
        Encoding.ASCII.GetString(mgfx.Select(b => (b >= 9 && b <= 126) ? b : (byte)' ').ToArray());

    /// <summary>
    /// Minimal decoder for the per-shader sampler tables of a MonoGame <c>.mgfx</c>
    /// (mgfxc or ShadowDusk), reading just the sampler-type byte for every sampler in
    /// every shader blob. Mirrors <c>validation/decode_mgfx.py</c> and MonoGame's
    /// <c>Shader.cs</c> reader for the fields it must skip.
    /// </summary>
    private static class SamplerTableDecoder
    {
        public readonly record struct SamplerRecord(byte Type, byte TextureSlot, byte SamplerSlot, string Name);

        public static IReadOnlyList<SamplerRecord> Decode(byte[] mgfx)
        {
            using var ms = new MemoryStream(mgfx);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

            br.ReadUInt32();             // "MGFX"
            br.ReadByte();               // version
            br.ReadByte();               // profile
            br.ReadInt32();              // effect key

            // Constant buffers.
            int cbCount = br.ReadInt32();
            for (int i = 0; i < cbCount; i++)
            {
                br.ReadString();         // name
                br.ReadInt16();          // size
                int paramCount = br.ReadInt32();
                for (int j = 0; j < paramCount; j++)
                {
                    br.ReadInt32();      // param index
                    br.ReadUInt16();     // offset
                }
            }

            var samplers = new List<SamplerRecord>();

            // Shaders.
            int shaderCount = br.ReadInt32();
            for (int i = 0; i < shaderCount; i++)
            {
                br.ReadBoolean();        // isVertexShader
                int byteLen = br.ReadInt32();
                br.ReadBytes(byteLen);   // bytecode / GLSL

                int samplerCount = br.ReadByte();
                for (int s = 0; s < samplerCount; s++)
                {
                    byte type = br.ReadByte();
                    byte texSlot = br.ReadByte();
                    byte sampSlot = br.ReadByte();
                    if (br.ReadBoolean()) // hasState
                    {
                        br.ReadBytes(3); // AddressU/V/W
                        br.ReadBytes(4); // BorderColor RGBA
                        br.ReadByte();   // Filter
                        br.ReadInt32();  // MaxAnisotropy
                        br.ReadInt32();  // MaxMipLevel
                        br.ReadSingle(); // MipMapLevelOfDetailBias
                    }
                    string name = br.ReadString();
                    br.ReadByte();       // parameter index
                    samplers.Add(new SamplerRecord(type, texSlot, sampSlot, name));
                }

                int cbIndexCount = br.ReadByte();
                br.ReadBytes(cbIndexCount);

                int attrCount = br.ReadByte();
                for (int a = 0; a < attrCount; a++)
                {
                    br.ReadString();     // name
                    br.ReadByte();       // usage
                    br.ReadByte();       // index
                    br.ReadInt16();      // location
                }
            }

            return samplers;
        }
    }
}
