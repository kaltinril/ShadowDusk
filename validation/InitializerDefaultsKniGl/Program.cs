using ShadowDusk.Core;
using ShadowDusk.Validation.InitializerDefaults;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return InitializerDefaultsProbe.Run("kni-gl", PlatformTarget.OpenGL, [EffectContainer.Mgfx, EffectContainer.Knifx], mgfxcOpenGlControl: true, knifxReachRows: true);
    }
}