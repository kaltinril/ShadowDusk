using System.Reflection;
using ShadowDusk.Core;
using ShadowDusk.Validation.KniEffectTest;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string version = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "KniVersion").Value!;
        return KniEffectTestProbe.Run(new ProbeConfig
        {
            Runtime = "kni-gl",
            Target = PlatformTarget.OpenGL,
            Containers = [EffectContainer.Mgfx, EffectContainer.Knifx],
            MgfxcGoldenDir = "mgfxc-3.8.4.1-OpenGL",
            KniXnbDir = "OpenGL",
            ExpectedRuntimeVersion = version,
        });
    }
}