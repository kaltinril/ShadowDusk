#nullable enable

namespace ShadowDusk.Slang;

/// <summary>
/// Regex fragments for the HLSL resource TYPE names in slangc's <c>-target hlsl</c> emission,
/// shared by every scan of that text (<see cref="SlangcRegisterStripper"/>,
/// <see cref="SlangFx2TextureRespeller"/>).
/// </summary>
/// <remarks>
/// Each fragment matches a whole token and nothing looser. A prefix match
/// (<c>Texture\w*</c>) also takes every USER type whose name merely starts with "Texture":
/// a texture-free shader with <c>struct TextureRegion</c> was rejected on FNA as "a texture
/// passed as a function parameter", and a local <c>TextureSlot slots[2]</c> as "a texture
/// array" (issue #230 follow-up). slangc keeps a user struct's name and appends its own
/// <c>_N</c> suffix (<c>TextureRegion_0</c>), and <c>_</c> is a word character, so the
/// trailing <c>\b</c> also keeps a user type named <c>Texture2D_</c>-anything out.
/// </remarks>
internal static class SlangcResourceTypes
{
    /// <summary>
    /// A texture object type: <c>Texture1D</c>, <c>Texture2D</c>, <c>Texture3D</c>,
    /// <c>TextureCube</c>, their <c>Array</c>/<c>MS</c>/<c>MSArray</c> forms and the
    /// <c>RW</c> variants. The <c>&lt;...&gt;</c> element type is not part of this fragment.
    /// </summary>
    public const string Texture = """\b(?:RW)?Texture(?:1D|2D|3D|Cube)(?:MS)?(?:Array)?\b""";

    /// <summary><c>SamplerState</c> or <c>SamplerComparisonState</c>.</summary>
    public const string Sampler = """\bSampler(?:Comparison)?State\b""";
}
