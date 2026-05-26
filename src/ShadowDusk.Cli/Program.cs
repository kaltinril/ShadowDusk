#nullable enable

using ShadowDusk.Cli;
using ShadowDusk.Core;

var parseResult = ArgumentParser.Parse(args);
if (parseResult.IsFailure)
{
    Console.Error.WriteLine(ArgumentParser.UsageText);
    Console.Error.WriteLine(MgcbErrorFormatter.Format(parseResult.Error));
    return 1;
}

CliArguments cliArgs = parseResult.Value;

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

try
{
    PipelineRunner runner = PipelineRunnerFactory.Create(cliArgs);

    var compileResult = await runner.RunAsync(cliArgs, cts.Token);
    if (compileResult.IsFailure)
    {
        foreach (string line in MgcbErrorFormatter.FormatAll(compileResult.Error))
            Console.Error.WriteLine(line);
        return 1;
    }

    return 0;
}
catch (Exception ex)
{
    var internalError = new ShaderError(
        File: "",
        Line: 0,
        Column: 0,
        Code: "X0099",
#if DEBUG
        Message: ex.ToString()
#else
        Message: ex.Message
#endif
    );
    Console.Error.WriteLine(MgcbErrorFormatter.Format(internalError));
    return 1;
}
