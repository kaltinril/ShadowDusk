using ShadowDusk.Core;
using ShadowDusk.Validation.InitializerDefaults;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return InitializerDefaultsProbe.Run("kni-dx", PlatformTarget.DirectX, [EffectContainer.Mgfx, EffectContainer.Knifx], mgfxcOpenGlControl: false, knifxReachRows: true);
    }
}