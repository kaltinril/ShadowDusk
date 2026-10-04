#nullable enable
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// A legacy <c>sampler2D A = sampler_state { Texture = &lt;T&gt;; };</c> whose texture is declared
/// somewhere the pre-parser's raw reading of the main file cannot see (an <c>#include</c>d header,
/// a macro), declared after the sampler, or not declared at all. <c>mgfxc</c> 3.8.4.1 compiles
/// every one of these on OpenGL and DirectX_11 with a single <c>T</c> parameter (measured
/// 2026-10-03). The undeclared-texture support briefly declared <c>T</c> from the raw reading,
/// which gave "redefinition" errors when a header or a macro already declared it; it now decides
/// on the preprocessed source.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StateBlockTextureDeclarationTests
{
    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #elif SM6
        #define PS_SHADERMODEL ps_6_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string Body = """

        float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * color; }
        technique Main { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } };
        """;

    public static IEnumerable<object[]> Cases()
    {
        var shapes = new (string Name, string Declarations, string Texture)[]
        {
            ("texture in an #include'd header", "#include \"tex.fxh\"\nsampler2D A = sampler_state { Texture = <Tex>; };", "Tex"),
            ("registered texture in an #include'd header", "#include \"texreg.fxh\"\nsampler2D A = sampler_state { Texture = <Tex>; };", "Tex"),
            ("texture declared by a macro", "#define DECL_TEX(x) Texture2D x##Tex;\nDECL_TEX(Mask)\nsampler2D A = sampler_state { Texture = <MaskTex>; };", "MaskTex"),
            ("legacy texture declared after the sampler", "sampler2D A = sampler_state { Texture = <Tex>; };\ntexture Tex;", "Tex"),
            ("Texture2D declared after the sampler", "sampler2D A = sampler_state { Texture = <Tex>; };\nTexture2D Tex;", "Tex"),
            ("texture declared nowhere", "sampler2D A = sampler_state { Texture = <Tex>; };", "Tex"),
            // MonoGame's own Macros.fxh spelling, and a scalar template argument.
            ("Texture2D<float4> in an #include'd header", "#include \"f4.fxh\"\nsampler2D A = sampler_state { Texture = <Tex>; };", "Tex"),
            ("Texture2D<float> in an #include'd header", "#include \"f1.fxh\"\nsampler2D A = sampler_state { Texture = <Tex>; };", "Tex"),
            // The sampler declared once per #if branch, each naming the undeclared texture: the
            // declaration must not land in the branch the target does not compile (both orders).
            ("one sampler per #if branch, OpenGL branch first",
             "#if OPENGL\nsampler2D A = sampler_state { Texture = <Tex>; MinFilter = Point; };\n#else\nsampler2D A = sampler_state { Texture = <Tex>; };\n#endif", "Tex"),
            ("one sampler per #if branch, OpenGL branch second",
             "#if !OPENGL\nsampler2D A = sampler_state { Texture = <Tex>; MinFilter = Point; };\n#else\nsampler2D A = sampler_state { Texture = <Tex>; };\n#endif", "Tex"),
        };
        foreach (var shape in shapes)
        foreach (PlatformTarget target in new[] { PlatformTarget.OpenGL, PlatformTarget.DirectX, PlatformTarget.DirectX12, PlatformTarget.Vulkan })
            yield return new object[] { shape.Name, shape.Declarations, shape.Texture, target };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StateBlockTexture_CompilesWithOneParameterOfTheTexturesName(
        string shape, string declarations, string texture, PlatformTarget target)
    {
        var result = await Compile(Header + declarations + Body, target, new InMemoryIncludeResolver(Headers));

        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? $"{shape} on {target}: " + string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.Parameters.Count(p => p.Name == texture && p.Type == 7).ShouldBe(1,
            $"{shape} on {target}: mgfxc emits one {texture} (Texture2D) parameter");
        reader.Samplers.Select(s => reader.Parameters[s.Parameter].Name).ShouldAllBe(n => n == texture);
    }

    private static readonly Dictionary<string, string> Headers = new(StringComparer.Ordinal)
    {
        ["tex.fxh"] = "Texture2D Tex;\n",
        ["texreg.fxh"] = "Texture2D Tex : register(t2);\n",
        ["f4.fxh"] = "Texture2D<float4> Tex;\n",
        ["f1.fxh"] = "Texture2D<float> Tex;\n",
        ["cube.fxh"] = "TextureCube Tex;\n",
        ["lower.fxh"] = "texture2D Tex;\n",
        ["arr.fxh"] = "Texture2DArray Tex;\n",
    };

    /// <summary>
    /// Header-declared texture types that the compile still cannot use with <c>tex2D</c>, for
    /// reasons that predate the undeclared-texture support and are unchanged by it (main fails
    /// identically, measured 2026-10-03; mgfxc 3.8.4.1 compiles all three): a <c>TextureCube</c>
    /// or <c>Texture2DArray</c> sampled through <c>.Sample(A, float2)</c> is a type error, and a
    /// lowercase <c>texture2D</c> in an <c>#include</c>d file reaches DXC unrewritten (DirectX 11
    /// compiles it). What these pin: the texture IS seen as declared, so no second declaration
    /// (no "redefinition") is ever added.
    /// </summary>
    public static IEnumerable<object[]> DeclaredButUnsampleableCases()
    {
        foreach (string header in new[] { "cube.fxh", "lower.fxh", "arr.fxh" })
        foreach (PlatformTarget target in new[] { PlatformTarget.OpenGL, PlatformTarget.DirectX, PlatformTarget.DirectX12, PlatformTarget.Vulkan })
            yield return new object[] { header, target };
    }

    [Theory]
    [MemberData(nameof(DeclaredButUnsampleableCases))]
    public async Task HeaderDeclaredTextureOfAnyType_IsNeverDeclaredASecondTime(string header, PlatformTarget target)
    {
        var result = await Compile(Header + $"#include \"{header}\"\nsampler2D A = sampler_state {{ Texture = <Tex>; }};" + Body,
                                   target, new InMemoryIncludeResolver(Headers));
        if (result.IsFailure)
        {
            string messages = string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage));
            messages.ShouldNotContain("redefinition", Case.Insensitive);
            messages.ShouldNotContain("already declared", Case.Insensitive);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The consumer's include resolver: the compile flattens the source more than once (the
    // compile, the preprocessed sampler views, SD0227's view on non-OpenGL targets), and every
    // pass must see the resolver's FIRST answer, with one call per include.

    private const string TwoSamplersWithInclude = Header + """
        #include "tex.fxh"
        sampler2D A = sampler_state { Texture = <Tex>; };
        sampler2D B : register(s1);
        float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * tex2D(B, uv); }
        technique Main { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } };
        """;

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task IncludeResolver_IsCalledOncePerInclude(PlatformTarget target)
    {
        var resolver = new CountingResolver(new InMemoryIncludeResolver(Headers), throwOnRepeat: false);
        var result = await Compile(TwoSamplersWithInclude, target, resolver);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        resolver.Calls.ShouldBe(1, $"{target}: one #include, one call");
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task IncludeResolver_ThatThrowsOnARepeatCall_StillCompiles(PlatformTarget target)
    {
        var resolver = new CountingResolver(new InMemoryIncludeResolver(Headers), throwOnRepeat: true);
        var result = await Compile(TwoSamplersWithInclude, target, resolver);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
    }

    private sealed class CountingResolver(IIncludeResolver inner, bool throwOnRepeat) : IIncludeResolver
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public int Calls { get; private set; }

        public Result<IncludeResolvedFile, ShaderError> Resolve(
            string includePath, string? includingFilePath, IReadOnlyList<string> additionalSearchPaths)
        {
            Calls++;
            if (!_seen.Add(includePath) && throwOnRepeat)
                throw new InvalidOperationException($"resolver asked for '{includePath}' twice");
            return inner.Resolve(includePath, includingFilePath, additionalSearchPaths);
        }
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> Compile(string source, PlatformTarget target, IIncludeResolver resolver)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        return await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            SourceFileName = "state-texture.fx",
            IncludeResolver = resolver,
        }, cts.Token);
    }
}
