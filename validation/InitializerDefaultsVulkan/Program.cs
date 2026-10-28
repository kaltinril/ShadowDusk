using ShadowDusk.Core;
using ShadowDusk.Validation.InitializerDefaults;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return InitializerDefaultsProbe.Run("mg-vulkan", PlatformTarget.Vulkan, [EffectContainer.Mgfx], mgfxcOpenGlControl: false, skipFloat2x2: true);
    }
}