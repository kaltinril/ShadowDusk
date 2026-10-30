using ShadowDusk.Core;
using ShadowDusk.Validation.KniEffectTest;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return KniEffectTestProbe.Run(new ProbeConfig
        {
            Runtime = "mg-vk",
            Target = PlatformTarget.Vulkan,
            Containers = [EffectContainer.Mgfx],
            MgfxcGoldenDir = "mgfxc-3.8.5-Vulkan",
        });
    }
}