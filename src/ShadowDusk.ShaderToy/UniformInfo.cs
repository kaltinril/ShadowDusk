namespace ShadowDusk.ShaderToy;

/// <summary>Static metadata for the predefined ShaderToy uniform set, used by both the emitter
/// (to recognize references and translate <c>iChannelN</c> samples) and the harness generator
/// (to emit only the referenced uniforms as HLSL globals).</summary>
internal static class UniformInfo
{
    /// <summary>The non-channel scalar/vector/array uniforms, in a stable declaration order.</summary>
    public static readonly IReadOnlyList<string> ScalarUniforms = new[]
    {
        "iResolution",
        "iTime",
        "iTimeDelta",
        "iFrame",
        "iFrameRate",
        "iMouse",
        "iDate",
        "iSampleRate",
        "iChannelTime",
        "iChannelResolution",
    };

    /// <summary>The four texture channels.</summary>
    public static readonly IReadOnlyList<string> ChannelUniforms = new[]
    {
        "iChannel0",
        "iChannel1",
        "iChannel2",
        "iChannel3",
    };

    /// <summary>The HLSL global declaration line for a scalar/vector/array uniform.</summary>
    public static string HlslDeclaration(string name) => name switch
    {
        "iResolution" => "float3 iResolution;",
        "iTime" => "float iTime;",
        "iTimeDelta" => "float iTimeDelta;",
        "iFrame" => "int iFrame;",
        "iFrameRate" => "float iFrameRate;",
        "iMouse" => "float4 iMouse;",
        "iDate" => "float4 iDate;",
        "iSampleRate" => "float iSampleRate;",
        "iChannelTime" => "float iChannelTime[4];",
        "iChannelResolution" => "float3 iChannelResolution[4];",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a scalar uniform."),
    };

    /// <summary>True if <paramref name="name"/> is any predefined ShaderToy uniform.</summary>
    public static bool IsUniform(string name) =>
        ScalarUniforms.Contains(name) || ChannelUniforms.Contains(name);

    /// <summary>True if <paramref name="name"/> is one of the texture channels.</summary>
    public static bool IsChannel(string name) => ChannelUniforms.Contains(name);
}
