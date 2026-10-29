using ShadowDusk.Core;
using ShadowDusk.Validation.VertexSemantics;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return VertexSemanticsProbe.Run("mg-gl", [EffectContainer.Mgfx]);
    }
}