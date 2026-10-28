using ShadowDusk.Core;
using ShadowDusk.Validation.InitializerDefaults;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return InitializerDefaultsProbe.Run("mg-gl", PlatformTarget.OpenGL, [EffectContainer.Mgfx], mgfxcOpenGlControl: true);
    }
}