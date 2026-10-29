using ShadowDusk.Core;
using ShadowDusk.Validation.VertexSemantics;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return VertexSemanticsProbe.Run("kni-gl", [EffectContainer.Mgfx, EffectContainer.Knifx]);
    }
}