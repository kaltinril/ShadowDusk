#nullable enable

namespace ShadowDusk.Cli;

internal static class PipelineRunnerFactory
{
    public static PipelineRunner Create(CliArguments args) => new PipelineRunner();
}
