using Microsoft.Xna.Framework.Graphics;
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
            Runtime = "kni-dx",
            Target = PlatformTarget.DirectX,
            Containers = [EffectContainer.Mgfx, EffectContainer.Knifx],
            MgfxcGoldenDir = "mgfxc-3.8.4.1-DirectX_11",
            KniXnbDir = "DirectX",
            ExpectedRuntimeVersion = version,
            // Issue #204: KNI carries the WARP knob on PresentationParameters, not MonoGame's static
            // (see validation/KniWinFormsDX). Opt-in, so a local run renders on the real GPU.
            ConfigureDevice = gdm =>
            {
                if (Environment.GetEnvironmentVariable("SHADOWDUSK_DX_WARP") == "1")
                    gdm.PreparingDeviceSettings += (_, e) =>
                        e.GraphicsDeviceInformation.PresentationParameters.UseDriverType =
                            PresentationParameters.DriverType.FastSoftware;
            },
        });
    }
}