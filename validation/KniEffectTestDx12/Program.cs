using ShadowDusk.Core;
using ShadowDusk.Validation.KniEffectTest;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ShadowDusk.Validation.Dx.DxHeadlessRasterizer.PinIfRequested();
        return KniEffectTestProbe.Run(new ProbeConfig
        {
            Runtime = "mg-dx12",
            Target = PlatformTarget.DirectX12,
            Containers = [EffectContainer.Mgfx],
            MgfxcGoldenDir = "mgfxc-3.8.5-DirectX_12",
        });
    }
}