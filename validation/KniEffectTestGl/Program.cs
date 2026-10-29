using ShadowDusk.Core;
using ShadowDusk.Validation.KniEffectTest;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return KniEffectTestProbe.Run(new ProbeConfig
        {
            Runtime = "mg-gl",
            Target = PlatformTarget.OpenGL,
            Containers = [EffectContainer.Mgfx],
            MgfxcGoldenDir = "mgfxc-3.8.4.1-OpenGL",
        });
    }
}