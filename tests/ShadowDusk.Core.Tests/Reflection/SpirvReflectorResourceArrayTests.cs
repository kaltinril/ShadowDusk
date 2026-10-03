#nullable enable

using Shouldly;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Issue #324: an ARRAY of textures or samplers (<c>Texture2D Tex[N]</c>, <c>SamplerState S[N]</c>)
/// is a <c>UniformConstant</c> variable whose pointee is an <c>OpTypeArray</c> of the resource
/// type. <see cref="SpirvReflector"/> used to match only a bare image or sampler type, so the
/// declaration vanished from the reflected effect while its siblings survived (a Vulkan
/// <c>Texture2D Tex[2]</c> reflected only its sampler). The reflector must report the array
/// with its element count. Pure: the modules are hand-built, no DXC.
/// </summary>
public sealed class SpirvReflectorResourceArrayTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void TextureArray_IsReflectedAsOneTextureWithItsElementCount(int elements)
    {
        byte[] spirv = ResourceModule(textureArrayLength: elements, samplerArrayLength: null);

        var result = new SpirvReflector().Reflect(spirv);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        TextureReflection tex = result.Value.Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.Dimension.ShouldBe(TextureDimension.Texture2D);
        tex.ArrayLength.ShouldBe(elements);
        tex.RawBinding.ShouldBe(32);
        tex.BindSlot.ShouldBe(0);

        SamplerReflection samp = result.Value.Samplers.ShouldHaveSingleItem();
        samp.Name.ShouldBe("TexSampler");
        samp.ArrayLength.ShouldBeNull("a single SamplerState is not an array");
    }

    [Fact]
    public void SingleTexture_HasNoArrayLength()
    {
        byte[] spirv = ResourceModule(textureArrayLength: null, samplerArrayLength: null);

        var result = new SpirvReflector().Reflect(spirv);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        result.Value.Textures.ShouldHaveSingleItem().ArrayLength.ShouldBeNull();
        result.Value.Samplers.ShouldHaveSingleItem().ArrayLength.ShouldBeNull();
    }

    [Fact]
    public void SamplerArray_IsReflectedAsOneSamplerWithItsElementCount()
    {
        byte[] spirv = ResourceModule(textureArrayLength: 2, samplerArrayLength: 2);

        var result = new SpirvReflector().Reflect(spirv);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        result.Value.Textures.ShouldHaveSingleItem().ArrayLength.ShouldBe(2);
        SamplerReflection samp = result.Value.Samplers.ShouldHaveSingleItem();
        samp.Name.ShouldBe("TexSampler");
        samp.ArrayLength.ShouldBe(2);
    }

    [Fact]
    public void NestedTextureArray_ReportsTheFlattenedElementCount()
    {
        // Texture2D Tex[2][3]: an array of arrays of images, 6 resource slots.
        byte[] spirv = ResourceModule(textureArrayLength: 2, samplerArrayLength: null, innerTextureArrayLength: 3);

        var result = new SpirvReflector().Reflect(spirv);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        result.Value.Textures.ShouldHaveSingleItem().ArrayLength.ShouldBe(6);
    }

    /// <summary>
    /// Hand-builds the smallest SPIR-V module that declares a 2D texture <c>Tex</c> (optionally
    /// an array of <paramref name="textureArrayLength"/> elements, optionally nested) and a
    /// sampler <c>TexSampler</c> (optionally an array), both <c>UniformConstant</c> with DXC's
    /// shifted Vulkan bindings (32, 33) in descriptor set 1.
    /// </summary>
    private static byte[] ResourceModule(int? textureArrayLength, int? samplerArrayLength, int? innerTextureArrayLength = null)
    {
        const ushort OpName = 5, OpTypeInt = 21, OpTypeFloat = 22, OpTypeImage = 25, OpTypeSampler = 26,
                     OpTypeArray = 28, OpTypePointer = 32, OpConstant = 43, OpVariable = 59, OpDecorate = 71;
        const uint BindingDecoration = 33, DescriptorSetDecoration = 34;
        const uint UniformConstant = 0;
        const uint Dim2D = 1;

        var words = new List<uint>();
        uint nextId = 1;
        uint Id() => nextId++;

        void Emit(ushort opcode, params uint[] operands)
        {
            words.Add((uint)(((operands.Length + 1) << 16) | opcode));
            words.AddRange(operands);
        }

        void EmitName(uint target, string name)
        {
            var literal = new List<uint>();
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
            for (int i = 0; i < utf8.Length + 1; i += 4)
            {
                uint word = 0;
                for (int b = 0; b < 4; b++)
                {
                    int index = i + b;
                    byte value = index < utf8.Length ? utf8[index] : (byte)0;
                    word |= (uint)value << (b * 8);
                }
                literal.Add(word);
            }
            Emit(OpName, [target, .. literal]);
        }

        // Header is patched with the final bound below.
        words.AddRange([0x07230203u, 0x00010000u, 0u, 0u, 0u]);

        uint floatId = Id(), intId = Id(), imageId = Id(), samplerId = Id();
        uint texVar = Id(), sampVar = Id();

        EmitName(texVar, "Tex");
        EmitName(sampVar, "TexSampler");
        Emit(OpDecorate, texVar, DescriptorSetDecoration, 1);
        Emit(OpDecorate, texVar, BindingDecoration, 32);
        Emit(OpDecorate, sampVar, DescriptorSetDecoration, 1);
        Emit(OpDecorate, sampVar, BindingDecoration, 33);

        Emit(OpTypeFloat, floatId, 32);
        Emit(OpTypeInt, intId, 32, 0);
        // OpTypeImage: result, sampled type, dim, depth, arrayed, MS, sampled (1 = with a sampler), format (0 = Unknown).
        Emit(OpTypeImage, imageId, floatId, Dim2D, 0, 0, 0, 1, 0);
        Emit(OpTypeSampler, samplerId);

        uint ArrayOf(uint element, int length)
        {
            uint lengthConst = Id();
            Emit(OpConstant, intId, lengthConst, (uint)length);
            uint arrayId = Id();
            Emit(OpTypeArray, arrayId, element, lengthConst);
            return arrayId;
        }

        uint texType = imageId;
        if (innerTextureArrayLength is { } inner)
            texType = ArrayOf(texType, inner);
        if (textureArrayLength is { } outer)
            texType = ArrayOf(texType, outer);

        uint sampType = samplerId;
        if (samplerArrayLength is { } samplerLength)
            sampType = ArrayOf(sampType, samplerLength);

        uint texPtr = Id(), sampPtr = Id();
        Emit(OpTypePointer, texPtr, UniformConstant, texType);
        Emit(OpTypePointer, sampPtr, UniformConstant, sampType);
        Emit(OpVariable, texPtr, texVar, UniformConstant);
        Emit(OpVariable, sampPtr, sampVar, UniformConstant);

        words[3] = nextId; // bound
        var bytes = new byte[words.Count * 4];
        for (int i = 0; i < words.Count; i++)
            System.BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }
}
