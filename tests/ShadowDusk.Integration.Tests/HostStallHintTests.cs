#nullable enable

using ShadowDusk.HLSL.Dxc;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #312: the stall hint a test host writes about itself must keep reporting the two
/// things a stalled macOS run needs: the fork gate's reader/writer state (read by reflection,
/// so a rename in <c>DxcForkGate</c> would otherwise silently blank it) and the thread picture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HostStallHintTests
{
    [Fact]
    public void Snapshot_ReportsTheForkGateAndTheThreads()
    {
        // Touch the gate's type so the assembly that holds it is loaded in this process.
        _ = typeof(DxcShaderCompiler);

        string snapshot = HostStallHint.Snapshot(TimeSpan.FromMinutes(3));

        snapshot.ShouldContain("thread pool: ", Case.Sensitive);
        snapshot.ShouldContain("DxcForkGate: installed=", Case.Sensitive);
        snapshot.ShouldContain("readers(compiles in the native call)=", Case.Sensitive);
        snapshot.ShouldContain("waitingWriters(forks blocked in the atfork handler)=", Case.Sensitive);
        snapshot.ShouldNotContain("update HostStallHint", Case.Sensitive);
        snapshot.ShouldContain("process: CPU ", Case.Sensitive);
    }
}
