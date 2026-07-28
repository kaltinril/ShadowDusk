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

    var runTask = runner.RunAsync(cliArgs, cts.Token);

    // Cooperative cancellation is only observed at managed stage boundaries; a wedged
    // NATIVE compiler call never reaches one, so the watchdog CTS alone cannot stop it
    // (bug-hunt 2026-07-27 M17). Give the cooperative path a grace window past the
    // watchdog, then hard-exit: process teardown is the only thing that can end a hung
    // native compile, and for a CLI that is exactly the right tool.
    var winner = await Task.WhenAny(runTask, Task.Delay(timeout + TimeSpan.FromSeconds(30)));
    if (winner != runTask)
    {
        var hangError = new ShaderError(
            File: cliArgs.SourceFile,
            Line: 0,
            Column: 0,
            Code: "X0007",
            Message: $"Compilation timed out after {timeout.TotalMinutes:0} minutes and the native compiler did not respond to cancellation; aborting");
        Console.Error.WriteLine(MgcbErrorFormatter.Format(hangError));
        return 1;
    }

    var compileResult = await runTask;
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
    // X0099 is "a bug if a consumer ever sees it" (docs/error-codes.md) — so the full
    // exception (type + stack), not just the message, is the actionable payload the bug
    // report needs. Release builds used to print Message only, which left nothing to
    // locate the fault with (bug-hunt 2026-07-27 N14).
    var internalError = new ShaderError(
        File: "",
        Line: 0,
        Column: 0,
        Code: "X0099",
        Message: ex.ToString());
    Console.Error.WriteLine(MgcbErrorFormatter.Format(internalError));
    return 1;
}
