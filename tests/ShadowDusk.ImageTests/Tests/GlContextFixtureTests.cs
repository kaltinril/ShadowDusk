#nullable enable

using Shouldly;
using ShadowDusk.ImageTests.GlContext;
using Silk.NET.OpenGL;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Issue #345 checks on the live fixture. The window must belong to a thread
/// the fixture owns: Windows destroys a window when its creating thread exits,
/// and a thread-pool creator thread was retired mid-run, after which every
/// make-current failed with "The handle is invalid". The project's runtime
/// config also retires idle pool threads after 100 ms, so every GL test in this
/// assembly re-proves the same thing.
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)]
public sealed class GlContextFixtureTests
{
    private readonly GlContextFixture _fixture;
    private readonly ITestOutputHelper _output;

    public GlContextFixtureTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    [Fact]
    public void Window_IsOwnedByAFixtureThreadThatIsStillAlive()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }

        _fixture.IsOwnerThreadAlive.ShouldBeTrue(
            "the window's creating thread must live as long as the fixture, or Windows destroys the window");
    }

    [Fact]
    public void Context_ClaimedOnThreadsThatThenExit_StaysUsable()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }

        for (int i = 0; i < 3; i++)
        {
            GLEnum error = GLEnum.InvalidValue;
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using (_fixture.MakeContextCurrent())
                    {
                        _fixture.Gl.ClearColor(0f, 0f, 0f, 1f);
                        error = _fixture.Gl.GetError();                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            worker.Start();
            worker.Join(TimeSpan.FromSeconds(60)).ShouldBeTrue("claiming the context must never block");

            failure.ShouldBeNull();
            error.ShouldBe(GLEnum.NoError);
        }
    }
}
