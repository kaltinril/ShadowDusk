using ShadowDusk.Core;
using ShadowDusk.Validation.InitializerDefaults;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ShadowDusk.Validation.Dx.DxHeadlessRasterizer.PinIfRequested();
        return InitializerDefaultsProbe.Run("mg-dx", PlatformTarget.DirectX, [EffectContainer.Mgfx], mgfxcOpenGlControl: false);
    }
}