#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The successful output of <see cref="IShaderCompiler.CompileAsync"/>: the compiled
/// <c>.mgfx</c> effect bytes together with the platform they were produced for. The bytes
/// can be written to a <c>.mgfx</c> file or fed directly to MonoGame's <c>Effect</c>.
/// </summary>
/// <param name="Target">The platform backend the effect was compiled for.</param>
/// <param name="Data">
/// The compiled <c>.mgfx</c> effect bytes, ready to load into MonoGame/KNI's <c>Effect</c>.
/// </param>
public sealed record CompiledShader(
    PlatformTarget Target,
    byte[] Data
);
