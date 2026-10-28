using ShadowDusk.Core;
using ShadowDusk.Validation.NonSquareMatrix;

internal static class Program
{
    // Args: optional label=dir pairs of prebuilt <Case>.mgfx files to render alongside the in-tree
    // ShadowDusk build. With none, the committed real mgfxc 3.8.5 /Profile:Vulkan builds are added.
    [STAThread]
    private static int Main(string[] args) =>
        NonSquareMatrixProbe.Run("mg-vulkan", PlatformTarget.Vulkan,
            args.Length > 0 ? args : [$"mgfxc-vk={NonSquareMatrixProbe.CommittedGoldens("Vulkan")}"]);
}
