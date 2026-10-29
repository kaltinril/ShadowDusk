using ShadowDusk.Core;
using ShadowDusk.Validation.NonSquareMatrix;

internal static class Program
{
    // Args: optional label=dir pairs of prebuilt <Case>.mgfx files to render alongside the in-tree
    // ShadowDusk build. With none, the committed real mgfxc 3.8.5 /Profile:DirectX_11 builds are added.
    [STAThread]
    private static int Main(string[] args)
    {
        ShadowDusk.Validation.Dx.DxHeadlessRasterizer.PinIfRequested();
        return NonSquareMatrixProbe.Run("mg-dx", PlatformTarget.DirectX,
            args.Length > 0 ? args : [$"mgfxc-dx={NonSquareMatrixProbe.CommittedGoldens("DirectX_11")}"]);
    }
}
