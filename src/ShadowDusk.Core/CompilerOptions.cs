#nullable enable

namespace ShadowDusk.Core;

public sealed class CompilerOptions
{
    public PlatformTarget Target { get; init; } = PlatformTarget.OpenGL;
}
