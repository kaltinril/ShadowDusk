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

TimeSpan timeout = TimeSpan.FromMinutes(5);
using var cts = new CancellationTokenSource(timeout);

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
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // The watchdog above fired — report a real timeout instead of an opaque
    // X0099 "The operation was canceled."
    var timeoutError = new ShaderError(
        File: cliArgs.SourceFile,
        Line: 0,
        Column: 0,
        Code: "X0007",
        Message: $"Compilation timed out after {timeout.TotalMinutes:0} minutes");
    Console.Error.WriteLine(MgcbErrorFormatter.Format(timeoutError));
    return 1;
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
