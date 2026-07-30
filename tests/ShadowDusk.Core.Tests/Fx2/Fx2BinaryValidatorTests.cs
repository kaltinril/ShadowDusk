#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests.Fx2;

/// <summary>
/// Calibrates <see cref="Fx2BinaryValidator"/> against REAL fxc.exe output — the fxc
/// goldens are ground truth, so if the validator rejects them the validator is wrong.
/// Corrupt-input tests patch copies of the golden bytes at offsets derived from the
/// annotated dumps in docs/fx2-binary-format.md §12 and tests/fixtures/golden/FNA/.
/// </summary>
public sealed class Fx2BinaryValidatorTests
{
    // -------------------------------------------------------------------------
    // Golden fixtures, embedded as base64 to keep unit tests disk-free.
    //
    // Provenance: byte-identical copies of tests/fixtures/golden/FNA/{minimal,textured}.fxb
    // (fxc.exe /T fx_2_0, Windows SDK 10.0.26100.0 — see the README there). Regenerate with
    //   [Convert]::ToBase64String([IO.File]::ReadAllBytes('tests/fixtures/golden/FNA/<f>.fxb'))
    // -------------------------------------------------------------------------

    // minimal.fx: 0 parameters; technique "T" / pass "P"; PixelShader = compile ps_2_0. 268 bytes.
    private const string MinimalFxbBase64 =
        "AQn//iwAAAAAAAAAAQAAAA8AAAAEAAAAAAAAAAAAAAAAAAAAAgAAAFAAAAACAAAAVAAAAAAAAAABAAAAAgAAAAIAAAAkAAAA" +
        "AAAAAAEAAAAcAAAAAAAAAAEAAACTAAAAAAAAAAgAAAAEAAAAAAAAAAEAAAAAAAAAAAAAAP////8AAAAAAAAAAIAAAAAAAv//" +
        "/v8UAENUQUIcAAAAIwAAAAAC//8AAAAAAAAAAAAAACAcAAAAcHNfMl8wAE1pY3Jvc29mdCAoUikgSExTTCBTaGFkZXIgQ29t" +
        "cGlsZXIgMTAuMQCrUQAABQAAD6AAAIA/AAAAAAAAAAAAAIA/AQAAAgAID4AAAOSg//8AAA==";

    // textured.fx: texture t; sampler s0 { Texture = <t>; MipFilter = LINEAR; }; tex2D. 544 bytes.
    private const string TexturedFxbBase64 =
        "AQn//sQAAAAAAAAABQAAAAQAAAAcAAAAAAAAAAAAAAABAAAAAgAAAHQAAAAKAAAABAAAAJQAAAAAAAAAAAAAAAIAAAAFAAAA" +
        "BAAAAAAAAAAAAAAAAAAAAAIAAAACAAAAAgAAAAAAAAAAAAAAAAAAAAEAAAABAAAAAgAAAKQAAAAAAQAAPAAAADgAAACrAAAA" +
        "AAEAAFQAAABQAAAAAwAAAHMwAAADAAAADwAAAAQAAAAAAAAAAAAAAAAAAAACAAAAUAAAAAIAAABUAAAAAgAAAAEAAAADAAAA" +
        "BAAAAAQAAAAYAAAAAAAAAAAAAAAkAAAAcAAAAAAAAAAAAAAAvAAAAAAAAAABAAAAtAAAAAAAAAABAAAAkwAAAAAAAACgAAAA" +
        "nAAAAAEAAAACAAAAAQAAAAAAAAAAAAAAAAAAAP////8AAAAAAAAAALgAAAAAAv///v8eAENUQUIcAAAASwAAAAAC//8BAAAA" +
        "HAAAAAAAACBEAAAAMAAAAAMAAAABAAAANAAAAAAAAABzMACrBAAMAAEAAQABAAAAAAAAAHBzXzJfMABNaWNyb3NvZnQgKFIp" +
        "IEhMU0wgU2hhZGVyIENvbXBpbGVyIDEwLjEAqx8AAAIAAACAAAADsB8AAAIAAACQAAgPoEIAAAMAAA+AAADksAAI5KABAAAC" +
        "AAgPgAAA5ID//wAA/////wEAAAAAAAAAAAAAAAEAAAACAAAAdAAAAA==";

    private static byte[] MinimalGolden() => Convert.FromBase64String(MinimalFxbBase64);
    private static byte[] TexturedGolden() => Convert.FromBase64String(TexturedFxbBase64);

    // -------------------------------------------------------------------------
    // Calibration: minimal.fxb (fxc ground truth) parses clean
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_MinimalGolden_Succeeds()
    {
        var effect = Fx2BinaryValidator.Parse(MinimalGolden());

        effect.Parameters.ShouldBeEmpty();
        effect.ObjectCount.ShouldBe(2); // reserved index 0 + the pixel-shader object
        effect.SamplerTextureMap.ShouldBeEmpty();

        var technique = effect.Techniques.ShouldHaveSingleItem();
        technique.Name.ShouldBe("T");
        var pass = technique.Passes.ShouldHaveSingleItem();
        pass.Name.ShouldBe("P");

        var state = pass.States.ShouldHaveSingleItem();
        state.Operation.ShouldBe(147); // PIXELSHADER
        state.ObjectIndex.ShouldBe(1);

        var shader = effect.Shaders.ShouldHaveSingleItem();
        shader.Stage.ShouldBe(ShaderStage.Pixel);
        shader.VersionToken.ShouldBe(0xFFFF0200); // ps_2_0
        shader.CtabConstantNames.ShouldBeEmpty();
        shader.BytecodeLength.ShouldBe(0x80);
    }

    // -------------------------------------------------------------------------
    // Calibration: textured.fxb (fxc ground truth) parses clean
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_TexturedGolden_Succeeds()
    {
        var effect = Fx2BinaryValidator.Parse(TexturedGolden());

        effect.ObjectCount.ShouldBe(4);
        effect.Parameters.Count().ShouldBe(2);

        var texture = effect.Parameters[0];
        texture.Name.ShouldBe("t");
        texture.Class.ShouldBe(4); // OBJECT
        texture.Type.ShouldBe(5);  // TEXTURE
        texture.SamplerStates.ShouldBeEmpty();

        var sampler = effect.Parameters[1];
        sampler.Name.ShouldBe("s0");
        sampler.Class.ShouldBe(4);
        sampler.Type.ShouldBe(10); // fxc writes the undimensioned SAMPLER for `sampler s0`

        sampler.SamplerStates.Count().ShouldBe(2);
        var textureState = sampler.SamplerStates[0];
        textureState.Operation.ShouldBe(164); // Texture
        textureState.ObjectIndex.ShouldBe(2);
        var mipFilterState = sampler.SamplerStates[1];
        mipFilterState.Operation.ShouldBe(171); // MipFilter
        mipFilterState.DwordValue.ShouldBe((uint?)(2));  // LINEAR

        effect.SamplerTextureMap.Count().ShouldBe(1);
        effect.SamplerTextureMap["s0"].ShouldBe("t");

        var pass = effect.Techniques.ShouldHaveSingleItem()
            .Passes.ShouldHaveSingleItem();
        var state = pass.States.ShouldHaveSingleItem();
        state.Operation.ShouldBe(147);
        state.ObjectIndex.ShouldBe(3);

        var shader = effect.Shaders.ShouldHaveSingleItem();
        shader.Stage.ShouldBe(ShaderStage.Pixel);
        shader.VersionToken.ShouldBe(0xFFFF0200);
        shader.CtabConstantNames.ShouldBe(new[] { "s0" });
        shader.BytecodeLength.ShouldBe(0xB8);
    }

    // -------------------------------------------------------------------------
    // Ignored fields — the validator must read-and-discard, never assert
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_IgnoredCountsHeaderDword2_AnyValueStillParses()
    {
        // Counts-header dword 2 (the "shader count") is read and discarded by MojoShader;
        // it sits at abs 0xD4 in textured.fxb (counts header at 8 + pool_size 0xC4 = 0xCC).
        var bytes = TexturedGolden();
        bytes[0xD4] = 0xFF;
        bytes[0xD5] = 0xFF;

        var effect = Fx2BinaryValidator.Parse(bytes);

        effect.Parameters.Count().ShouldBe(2);
        effect.Techniques.ShouldHaveSingleItem();
    }

    // (The goldens themselves already exercise two more ignored fields: fxc writes 0x100
    // in the sampler-state index dword and 0xFFFFFFFF/0 in large-object element_index.)

    // -------------------------------------------------------------------------
    // Corrupt input: header
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_FlippedHeaderToken_Throws()
    {
        var bytes = TexturedGolden();
        bytes[3] ^= 0xFF; // 0xFEFF0901 -> 0x01FF0901

        var act = () => Fx2BinaryValidator.Parse(bytes);

        Should.Throw<Fx2ValidationException>(act).Message.ShouldContain("not an Effects Framework binary", Case.Sensitive);
    }

    [Fact]
    public void Parse_XnaWrapperToken_Throws()
    {
        // We never emit the XNA4 wrapper (0xBCF00BCF), so the validator must reject it.
        var bytes = TexturedGolden();
        bytes[0] = 0xCF;
        bytes[1] = 0x0B;
        bytes[2] = 0xF0;
        bytes[3] = 0xBC;

        var act = () => Fx2BinaryValidator.Parse(bytes);

        Should.Throw<Fx2ValidationException>(act).Message.ShouldContain("XNA4 wrapper", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // Corrupt input: truncation at every region boundary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(7)]     // shorter than the 8-byte header
    [InlineData(100)]   // inside the data pool (pool_size 0xC4 exceeds whats left)
    [InlineData(0x130)] // inside the object records
    [InlineData(0x150)] // inside the embedded shader blob (large record A's data)
    [InlineData(543)]   // missing the final pad byte of large record B's "t\0" data
    public void Parse_Truncated_Throws(int length)
    {
        var bytes = TexturedGolden().AsSpan(0, length).ToArray();

        var act = () => Fx2BinaryValidator.Parse(bytes);

        Should.Throw<Fx2ValidationException>(act);
    }

    // -------------------------------------------------------------------------
    // Corrupt input: targeted patches (offsets per the annotated dump)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ObjectIndexOutOfRange_Throws()
    {
        // Texture parameter `t`'s value blob is the dword at pool 0x18 (abs 0x20):
        // object index 1. Patch to 0xFF — out of range for object_count 4.
        var bytes = TexturedGolden();
        bytes[0x20] = 0xFF;

        var act = () => Fx2BinaryValidator.Parse(bytes);

        Should.Throw<Fx2ValidationException>(act).Message.ShouldContain("object index 255 out of range", Case.Sensitive);
    }

    [Fact]
    public void Parse_PassStateOpNotFnaHonored_Throws()
    {
        // The pass's PIXELSHADER state op (147, abs 0x114) patched to 4 (ALPHATESTENABLE)
        // — a real D3D9 render state, but one FNA's runtime throws on.
        var bytes = TexturedGolden();
        bytes[0x114] = 0x04;

        var act = () => Fx2BinaryValidator.Parse(bytes);

        Should.Throw<Fx2ValidationException>(act).Message.ShouldContain("pass state op 4", Case.Sensitive);
    }

    [Fact]
    public void Parse_SamplerStateOpFnaThrows_Throws()
    {
        // s0's MipFilter sampler-state op (171, abs 0x8C) patched to 168 (BorderColor) —
        // one of the four sampler ops FNA throws NotImplementedException on.
        var bytes = TexturedGolden();
        bytes[0x8C] = 0xA8;

        var act = () => Fx2BinaryValidator.Parse(bytes);

        Should.Throw<Fx2ValidationException>(act).Message.ShouldContain("op 168", Case.Sensitive);
    }

    [Fact]
    public void Parse_CtabConstantNameWithoutParameter_Throws()
    {
        // The embedded shader's CTAB constant name "s0" lives at abs 0x188. Renaming it
        // to "z0" leaves no effect parameter with the identical name — release-mode
        // memory corruption in MojoShader, so the validator must reject it.
        var bytes = TexturedGolden();
        bytes[0x188] = (byte)'z';

        var act = () => Fx2BinaryValidator.Parse(bytes);

        Should.Throw<Fx2ValidationException>(act).Message.ShouldContain("CTAB constant 'z0'", Case.Sensitive);
    }
}
