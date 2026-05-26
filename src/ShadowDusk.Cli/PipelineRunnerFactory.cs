#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.GLSL;

namespace ShadowDusk.Cli;

internal static class PipelineRunnerFactory
{
    public static PipelineRunner Create(CliArguments args)
    {
        var includeResolver = new FileSystemIncludeResolver();
        var preprocessor    = new Preprocessor();
        var mgfxWriter      = new MgfxWriter();
        var glslTranspiler  = new SpirvCrossGlslTranspiler();

        return new PipelineRunner(includeResolver, preprocessor, mgfxWriter, glslTranspiler);
    }
}
