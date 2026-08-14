#nullable enable

using System.Diagnostics;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Cli;

/// <summary>
/// The CLI's <c>.xnb</c> output mode (Phase 60, issue #199).
///
/// <para>Two things are worth pinning here and nowhere else. <b>The trigger is the output
/// extension</b> (Phase 60 OQ4) — no ShadowDusk-specific switch, because a flag a consumer must
/// set to get correct output is what the standing seamlessness directive forbids. And
/// <b>the payload inside the <c>.xnb</c> is byte-identical to the plain <c>.mgfx</c> the same
/// invocation would have written</b> (C2), which is the property that keeps the new delivery
/// shape from quietly becoming a second compile path.</para>
///
/// <para>The container's fidelity to real MGCB and the rung-4 <c>Content.Load&lt;Effect&gt;</c>
/// proof live in <c>validation/XnbContentLoad</c>; neither can run under <c>dotnet test</c>
/// (no <c>dotnet-mgcb</c>, no GPU).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliXnbOutputTest : IClassFixture<CliBinaryFixture>
{
    private readonly CliBinaryFixture _fixture;
    private static readonly string FixturesDir = FindFixturesDir();

    public CliXnbOutputTest(CliBinaryFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("OpenGL", 'd')]
    [InlineData("DirectX_11", 'w')]
    public async Task XnbOutputPath_WrapsTheSameBytesThePlainCompileWouldWrite(
        string profile, char expectedPlatform)
    {
        string source = Path.Combine(FixturesDir, "shaders", "Grayscale.fx");
        string id     = Guid.NewGuid().ToString("N");
        string mgfx   = Path.Combine(Path.GetTempPath(), $"Grayscale_{id}.mgfx");
        string xnb    = Path.Combine(Path.GetTempPath(), $"Grayscale_{id}.xnb");

        try
        {
            (int mgfxExit, _, _) = await RunCliAsync(source, mgfx, $"/Profile:{profile}");
            (int xnbExit,  _, _) = await RunCliAsync(source, xnb,  $"/Profile:{profile}");

            mgfxExit.ShouldBe(0);
            xnbExit.ShouldBe(0);

            byte[] mgfxBytes = await File.ReadAllBytesAsync(mgfx);
            byte[] xnbBytes  = await File.ReadAllBytesAsync(xnb);

            // The .xnb is a container: same invocation, same payload, wrapped.
            xnbBytes.Length.ShouldBeGreaterThan(mgfxBytes.Length,
                "the .xnb must be the .mgfx plus a container, never a different compile");

            xnbBytes.AsSpan(0, 3).ToArray().ShouldBe([(byte)'X', (byte)'N', (byte)'B']);
            ((char)xnbBytes[3]).ShouldBe(expectedPlatform);

            // C2: byte-identical payload. Derived from the file itself, not assumed from a
            // fixed offset — a manifest change would otherwise silently pass.
            byte[] payload = ExtractPayload(xnbBytes);
            payload.ShouldBe(mgfxBytes,
                "the payload inside the .xnb must be byte-identical to the .mgfx the same "
                + "invocation writes — one writer, one pipeline, no second code path");
        }
        finally
        {
            foreach (string f in new[] { mgfx, xnb })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task NonXnbExtension_IsPassedThroughUnwrapped()
    {
        // The extension trigger must not fire on anything else: `.mgfx` (and any other name a
        // consumer picks) still gets raw effect bytes, so no existing build changes shape.
        string source = Path.Combine(FixturesDir, "shaders", "Grayscale.fx");
        string output = Path.Combine(Path.GetTempPath(), $"Grayscale_{Guid.NewGuid():N}.mgfx");

        try
        {
            (int exit, _, _) = await RunCliAsync(source, output, "/Profile:OpenGL");
            exit.ShouldBe(0);

            byte[] bytes = await File.ReadAllBytesAsync(output);
            bytes.AsSpan(0, 3).ToArray().ShouldNotBe([(byte)'X', (byte)'N', (byte)'B']);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>Reads the length-prefixed effect payload out of an uncompressed XNB.</summary>
    private static byte[] ExtractPayload(byte[] xnb)
    {
        int i = 10;
        int readerCount = Read7BitEncodedInt(xnb, ref i);
        for (int r = 0; r < readerCount; r++)
        {
            int nameLength = Read7BitEncodedInt(xnb, ref i);
            i += nameLength + 4;
        }

        Read7BitEncodedInt(xnb, ref i);   // shared-resource count
        Read7BitEncodedInt(xnb, ref i);   // type id

        int payloadLength = BitConverter.ToInt32(xnb, i);
        i += 4;
        return xnb.AsSpan(i, payloadLength).ToArray();
    }

    private static int Read7BitEncodedInt(byte[] bytes, ref int index)
    {
        int result = 0, shift = 0;
        while (true)
        {
            byte b = bytes[index++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string sourceFile, string outputFile, params string[] extraArgs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var argList = new List<string> { sourceFile, outputFile };
        argList.AddRange(extraArgs);

        var psi = new ProcessStartInfo(_fixture.ExecutablePath)
        {
            Arguments = string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = Path.GetTempPath(),
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CLI process.");

        string stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        string stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, stdout, stderr);
    }

    private static string FindFixturesDir()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return Path.Combine(dir.FullName, "tests", "fixtures");
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}
