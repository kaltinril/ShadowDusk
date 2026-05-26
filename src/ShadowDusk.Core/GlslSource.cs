#nullable enable

namespace ShadowDusk.Core;

public readonly record struct GlslSource(string Text)
{
    public override string ToString() => Text;
}
