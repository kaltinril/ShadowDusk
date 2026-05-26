#nullable enable

using FluentAssertions;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.HLSL.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

public sealed class ParameterListBuilderTests
{
    // -------------------------------------------------------------------------
    // Helpers — build canonical fake ReflectedEffect data
    // -------------------------------------------------------------------------

    private static VariableReflection MakeVar(string name, int offset, int size,
        EffectParameterClass cls = EffectParameterClass.Scalar,
        EffectParameterType type = EffectParameterType.Single,
        int rows = 1, int columns = 1, int elements = 0)
        => new()
        {
            Name           = name,
            StartOffset    = offset,
            SizeBytes      = size,
            ParameterClass = cls,
            ParameterType  = type,
            Rows           = rows,
            Columns        = columns,
            Elements       = elements,
        };

    private static ConstantBufferReflection MakeCBuffer(string name, int size,
        params VariableReflection[] variables)
        => new()
        {
            Name      = name,
            BindSlot  = 0,
            SizeBytes = size,
            Variables = variables,
        };

    private static TextureReflection MakeTexture(string name, int slot,
        TextureDimension dimension = TextureDimension.Texture2D)
        => new()
        {
            Name      = name,
            BindSlot  = slot,
            Dimension = dimension,
        };

    private static SamplerReflection MakeSampler(string name, int slot)
        => new()
        {
            Name     = name,
            BindSlot = slot,
        };

    private static ReflectedEffect MakeFakeEffect()
        => new()
        {
            ConstantBuffers = new[]
            {
                MakeCBuffer("Params", 32,
                    MakeVar("Scale",     offset: 0,  size: 4),
                    MakeVar("Color",     offset: 16, size: 16, cls: EffectParameterClass.Vector, columns: 4)),
            },
            Textures = new[]
            {
                MakeTexture("Albedo", slot: 0),
            },
            Samplers = new[]
            {
                MakeSampler("AlbedoSampler", slot: 0),
            },
            InputSignature   = Array.Empty<SignatureParameterReflection>(),
            OutputSignature  = Array.Empty<SignatureParameterReflection>(),
            Parameters       = Array.Empty<ParameterReflection>(),
        };

    // -------------------------------------------------------------------------
    // Output ordering
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_WithNullAnnotations_OrderIsCbufferVarsTextureSampler()
    {
        var effect = MakeFakeEffect();

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: null);

        // Expect: Scale, Color (cbuffer vars), Albedo (texture), AlbedoSampler (sampler)
        parameters.Should().HaveCount(4);
        parameters[0].Name.Should().Be("Scale");
        parameters[1].Name.Should().Be("Color");
        parameters[2].Name.Should().Be("Albedo");
        parameters[3].Name.Should().Be("AlbedoSampler");
    }

    [Fact]
    public void Build_MultipleTextures_OrderedByBindSlot()
    {
        var effect = new ReflectedEffect
        {
            ConstantBuffers = Array.Empty<ConstantBufferReflection>(),
            Textures = new[]
            {
                MakeTexture("NormalMap", slot: 1),
                MakeTexture("Albedo",    slot: 0),
            },
            Samplers         = Array.Empty<SamplerReflection>(),
            InputSignature   = Array.Empty<SignatureParameterReflection>(),
            OutputSignature  = Array.Empty<SignatureParameterReflection>(),
            Parameters       = Array.Empty<ParameterReflection>(),
        };

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: null);

        parameters.Should().HaveCount(2);
        parameters[0].Name.Should().Be("Albedo",    because: "slot 0 comes before slot 1");
        parameters[1].Name.Should().Be("NormalMap", because: "slot 1 comes after slot 0");
    }

    [Fact]
    public void Build_MultipleSamplers_OrderedByBindSlot()
    {
        var effect = new ReflectedEffect
        {
            ConstantBuffers = Array.Empty<ConstantBufferReflection>(),
            Textures        = Array.Empty<TextureReflection>(),
            Samplers = new[]
            {
                MakeSampler("SamplerB", slot: 1),
                MakeSampler("SamplerA", slot: 0),
            },
            InputSignature  = Array.Empty<SignatureParameterReflection>(),
            OutputSignature = Array.Empty<SignatureParameterReflection>(),
            Parameters      = Array.Empty<ParameterReflection>(),
        };

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: null);

        parameters[0].Name.Should().Be("SamplerA");
        parameters[1].Name.Should().Be("SamplerB");
    }

    // -------------------------------------------------------------------------
    // Annotation merging
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_WithMatchingAnnotation_AttachesToParameter()
    {
        var effect = MakeFakeEffect();

        var annotation = new ParameterAnnotation
        {
            ParameterName = "Scale",
            Entries = new[]
            {
                new AnnotationEntry("string", "UIWidget", "slider", SourceSpan.Unknown),
            },
            Span = SourceSpan.Unknown,
        };

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: new[] { annotation });

        var scaleParam = parameters.Single(p => p.Name == "Scale");
        scaleParam.Annotations.Should().NotBeNullOrEmpty();
        scaleParam.Annotations!.Should().ContainSingle(a => a.Name == "UIWidget");
    }

    [Fact]
    public void Build_WithNonMatchingAnnotation_OtherParametersHaveNoAnnotations()
    {
        var effect = MakeFakeEffect();

        var annotation = new ParameterAnnotation
        {
            ParameterName = "Scale",
            Entries = new[]
            {
                new AnnotationEntry("float", "UIMin", "0.0", SourceSpan.Unknown),
            },
            Span = SourceSpan.Unknown,
        };

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: new[] { annotation });

        var colorParam = parameters.Single(p => p.Name == "Color");
        colorParam.Annotations.Should().BeNullOrEmpty();
    }

    // -------------------------------------------------------------------------
    // Null / empty annotation list
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_NullAnnotations_AllParameterAnnotationsAreNullOrEmpty()
    {
        var effect = MakeFakeEffect();

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: null);

        parameters.Should().AllSatisfy(p =>
            p.Annotations.Should().BeNullOrEmpty());
    }

    [Fact]
    public void Build_EmptyAnnotationList_AllParameterAnnotationsAreNullOrEmpty()
    {
        var effect = MakeFakeEffect();

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: Array.Empty<ParameterAnnotation>());

        parameters.Should().AllSatisfy(p =>
            p.Annotations.Should().BeNullOrEmpty());
    }

    // -------------------------------------------------------------------------
    // Empty cbuffer
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_EmptyCbuffer_ProducesNoParametersForThatCbuffer()
    {
        var effect = new ReflectedEffect
        {
            ConstantBuffers = new[]
            {
                new ConstantBufferReflection
                {
                    Name      = "Empty",
                    BindSlot  = 0,
                    SizeBytes = 0,
                    Variables = Array.Empty<VariableReflection>(),
                },
            },
            Textures        = Array.Empty<TextureReflection>(),
            Samplers        = Array.Empty<SamplerReflection>(),
            InputSignature  = Array.Empty<SignatureParameterReflection>(),
            OutputSignature = Array.Empty<SignatureParameterReflection>(),
            Parameters      = Array.Empty<ParameterReflection>(),
        };

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: null);

        parameters.Should().BeEmpty(because: "an empty cbuffer contributes no parameters");
    }

    // -------------------------------------------------------------------------
    // No resources at all
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_NoResourcesAtAll_ReturnsEmptyList()
    {
        var effect = new ReflectedEffect
        {
            ConstantBuffers = Array.Empty<ConstantBufferReflection>(),
            Textures        = Array.Empty<TextureReflection>(),
            Samplers        = Array.Empty<SamplerReflection>(),
            InputSignature  = Array.Empty<SignatureParameterReflection>(),
            OutputSignature = Array.Empty<SignatureParameterReflection>(),
            Parameters      = Array.Empty<ParameterReflection>(),
        };

        var parameters = ParameterListBuilder.Build(effect, fxAnnotations: null);

        parameters.Should().BeEmpty();
    }
}
