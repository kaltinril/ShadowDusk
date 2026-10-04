#nullable enable

using System.Text.Json;

namespace ShadowDusk.Validation.RaylibRoute;

/// <summary>
/// One render, handed from the orchestrator to an arm process through <c>jobs.json</c>. Both
/// arms read the SAME uniform values and the SAME source texels, so any pixel difference is
/// attributable to the shader each arm was given.
/// </summary>
/// <param name="Name">Output file stem.</param>
/// <param name="MgfxPath">ShadowDusk's OpenGL <c>.mgfx</c> for the MonoGame arm, or null to skip that arm.</param>
/// <param name="FragmentPath">The raylib fragment shader for the raylib arm, or null to skip that arm.</param>
/// <param name="Uniforms">Uniform name to value (1-4 floats), set by name on both arms.</param>
/// <param name="DrawTextures">
/// The unit-0 samplers (the texture the draw call binds; the MonoGame arm sets the parameter too),
/// as their MonoGame parameter candidates. See <see cref="ExtraTexture"/>.
/// </param>
/// <param name="ExtraTextures">
/// Every other sampler as (HLSL texture parameter, raylib uniform). Both arms bind these to a
/// SEPARATE copy of the source: MonoGame 3.8's GL backend stores sampler state on the texture
/// object, so one texture on two units leaks the second unit's wrap mode into later draws.
/// </param>
internal sealed record RenderJob(
    string Name,
    string? MgfxPath,
    string? FragmentPath,
    Dictionary<string, float[]> Uniforms,
    ExtraTexture[] DrawTextures,
    ExtraTexture[] ExtraTextures);

/// <summary>
/// A sampler: the names its MonoGame effect parameter can have, and its raylib uniform name.
/// The parameter is the HLSL texture's (<see cref="HlslTexture"/>), except for a legacy sampler
/// that binds no texture of its own (<c>sampler2D A;</c>), whose texture the SM4 rewrite
/// synthesizes and whose parameter carries the SAMPLER's name (<see cref="HlslSampler"/>), as
/// <c>mgfxc</c> names it. The MonoGame arm binds the first that exists.
/// </summary>
internal sealed record ExtraTexture(string HlslTexture, string RaylibUniform, string HlslSampler);

/// <summary>The work list plus the scene both arms draw.</summary>
internal sealed record JobFile(int Size, byte[] Tint, List<RenderJob> Jobs);

internal static class JobIo
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Write(string path, JobFile file) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json));

    public static JobFile Read(string path) =>
        JsonSerializer.Deserialize<JobFile>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"empty job file: {path}");

    /// <summary>The shared source image both arms sample (RGBA8, rows top-first).</summary>
    public static string SourcePath(string outDir) => Path.Combine(outDir, "source.rgba");

    public static string ImagePath(string outDir, string arm, string job) =>
        Path.Combine(outDir, $"{job}.{arm}.rgba");
}
