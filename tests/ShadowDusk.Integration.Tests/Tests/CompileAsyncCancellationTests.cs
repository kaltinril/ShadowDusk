#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Review of PR #376 (issue #373): a compile cancelled AFTER it started, on the real pipeline,
/// leaves <c>CompileAsync</c>'s task CANCELED (never faulted) and carries the traced message.
/// The token is cancelled from inside the pipeline, by the include resolver, so the
/// cancellation is observed at the next check, before the first native compile call.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompileAsyncCancellationTests
{
    private const string Source = """
        #include "colour.fxh"
        float4 MainPS(float4 c : COLOR0) : SV_TARGET { return c * Tint; }
        technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
        """;

    private sealed class CancellingResolver(CancellationTokenSource cts) : IIncludeResolver
    {
        public Result<IncludeResolvedFile, ShaderError> Resolve(
            string includePath, string? includingFilePath, IReadOnlyList<string> additionalSearchPaths)
        {
            cts.Cancel();
            return Result<IncludeResolvedFile, ShaderError>.Ok(
                new IncludeResolvedFile(includePath, "float4 Tint;\n"));
        }
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task CancelledMidCompile_TaskIsCanceled_AndTheMessageIsTraced(PlatformTarget target)
    {
        using var cts = new CancellationTokenSource();
        var options = new CompilerOptions
        {
            Target = target,
            SourceFileName = "cancel.fx",
            IncludeResolver = new CancellingResolver(cts),
        };

        Task<Result<CompiledShader, ShaderError[]>> task = new EffectCompiler().CompileAsync(Source, options, cts.Token);
        Task onlyOnCanceled = task.ContinueWith(
            _ => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnCanceled, TaskScheduler.Default);

        OperationCanceledException ex = await AwaitCancellation(task);

        cts.IsCancellationRequested.ShouldBeTrue("the resolver ran, so the compile had started");
        task.Status.ShouldBe(TaskStatus.Canceled);
        ex.CancellationToken.ShouldBe(cts.Token);
        ex.Message.ShouldContain("The compile was cancelled after ", Case.Sensitive);
        await onlyOnCanceled.WaitAsync(TestBudget.Compile);
        onlyOnCanceled.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    /// <summary>
    /// Awaits <paramref name="task"/> itself and returns the exception the AWAIT throws.
    /// <c>Should.ThrowAsync</c> is not used: for a canceled task it builds its own
    /// "A task was canceled." exception, hiding the one a caller's await actually sees.
    /// </summary>
    private static async Task<OperationCanceledException> AwaitCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException ex)
        {
            return ex;
        }

        throw new Shouldly.ShouldAssertException($"the task completed with status {task.Status} instead of being cancelled");
    }
}
