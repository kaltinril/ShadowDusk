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
/// shape from quietly becoming a second compile path. The one deliberate difference is on the
/// MGFX v11 targets, where the <c>.xnb</c> stores MGCB's <c>&lt;unknown&gt;</c> source name
/// instead of the path (issue #280); that test asserts nothing else differs.</para>
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

    /// <summary>
    /// Issue #280: on the MGFX v11 targets (DirectX 12, Vulkan) the effect stores a source-file
    /// string per shader. An <c>.xnb</c> is content-pipeline output, and the reference for
    /// content-pipeline output is MGCB (mgfxc has no <c>.xnb</c> mode), whose stock
    /// <c>EffectProcessor</c> writes <c>&lt;unknown&gt;</c> there. So the CLI's <c>.xnb</c> must
    /// too: built from two directories it is the same file, it names neither, and its payload is
    /// the <c>.mgfx</c> the same invocation writes with only that string (and the effect key
    /// derived from it) replaced. The <c>.mgfx</c> itself keeps the path as passed (mgfxc CLI
    /// parity, issue #274).
    /// </summary>
    [Theory]
    [InlineData("Vulkan", 'V')]
    [InlineData("DirectX_12", 'G')]
    public async Task XnbOutputOnAV11Target_RecordsTheStockUnknownSourceFile_NotTheBuildPath(
        string profile, char expectedPlatform)
    {
        const string Stock = "<unknown>";
        string root = Path.Combine(Path.GetTempPath(), "shadowdusk_issue280_" + Guid.NewGuid().ToString("N"));
        string dirOne = Path.Combine(root, "checkout-one");
        string dirTwo = Path.Combine(root, "a", "deeper", "checkout-two");

        try
        {
            string sourceOne = CopyFixture(dirOne);
            string sourceTwo = CopyFixture(dirTwo);

            string xnbOne = Path.Combine(dirOne, "Grayscale.xnb");
            string xnbTwo = Path.Combine(dirTwo, "Grayscale.xnb");
            string mgfx   = Path.Combine(dirOne, "Grayscale.mgfx");

            (int exitOne, _, string errOne) = await RunCliAsync(sourceOne, xnbOne, $"/Profile:{profile}");
            (int exitTwo, _, string errTwo) = await RunCliAsync(sourceTwo, xnbTwo, $"/Profile:{profile}");
            (int exitMgfx, _, string errMgfx) = await RunCliAsync(sourceOne, mgfx, $"/Profile:{profile}");
            exitOne.ShouldBe(0, errOne);
            exitTwo.ShouldBe(0, errTwo);
            exitMgfx.ShouldBe(0, errMgfx);

            byte[] bytesOne  = await File.ReadAllBytesAsync(xnbOne);
            byte[] bytesTwo  = await File.ReadAllBytesAsync(xnbTwo);
            byte[] mgfxBytes = await File.ReadAllBytesAsync(mgfx);

            ((char)bytesOne[3]).ShouldBe(expectedPlatform);

            bytesTwo.ShouldBe(bytesOne,
                $"the {profile} .xnb must not change with the directory the source was built from");

            foreach (string dir in new[] { dirOne, dirTwo })
            {
                ContainsUtf8(bytesOne, dir).ShouldBeFalse(
                    $"the {profile} .xnb must not carry the build machine's directory ({dir})");
            }

            byte[] payload = ExtractPayload(bytesOne);
            var reader = MgfxBlobReader.Parse(payload);
            reader.MgfxVersion.ShouldBe((byte)11);
            reader.Shaders.ShouldNotBeEmpty();
            reader.Shaders.ShouldAllBe(s => s.SourceFile == Stock,
                $"every {profile} shader record in the .xnb must carry what stock MGCB writes");

            // The .mgfx is unchanged: the path exactly as passed (mgfxc CLI parity).
            MgfxBlobReader.Parse(mgfxBytes).Shaders.ShouldAllBe(s => s.SourceFile == sourceOne,
                "the .mgfx output keeps the source path as passed, like mgfxc");

            // One pipeline: the .xnb payload is the .mgfx with only the embedded name replaced.
            payload.ShouldBe(MgfxEmbeddedSourceName.Replace(mgfxBytes, sourceOne, Stock),
                "the .xnb payload must be the .mgfx bytes with only the embedded source name replaced");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* non-fatal */ }
        }
    }

    private static string CopyFixture(string directory)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "Grayscale.fx");
        File.Copy(Path.Combine(FixturesDir, "shaders", "Grayscale.fx"), target);
        return target;
    }

    private static bool ContainsUtf8(byte[] haystack, string text)
        => haystack.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(text)) >= 0;

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

    [Fact]
    public async Task XnbOutputWithImplicitDefaultProfile_WarnsSD0029_ButStillSucceeds()
    {
        // Phase 64 C2: the CLI default (DirectX_11, mgfxc parity) is unchanged and the file is
        // correct for WindowsDX, so exit 0 and a valid 'w' container. The ADVISORY is the new
        // part: without it a DesktopGL game's Content.Load fails with a message that names
        // neither the profile nor this tool. Naming the profile - even DirectX_11 itself -
        // keeps stderr empty, so the MGCB empty-stderr contract for explicit invocations holds.
        string source   = Path.Combine(FixturesDir, "shaders", "Grayscale.fx");
        string implicitOut = Path.Combine(Path.GetTempPath(), $"Grayscale_{Guid.NewGuid():N}.xnb");
        string explicitOut = Path.Combine(Path.GetTempPath(), $"Grayscale_{Guid.NewGuid():N}.xnb");

        try
        {
            (int implicitExit, _, string implicitErr) = await RunCliAsync(source, implicitOut);
            (int explicitExit, _, string explicitErr) = await RunCliAsync(source, explicitOut, "/Profile:DirectX_11");

            implicitExit.ShouldBe(0, "the advisory is a warning, never a failure");
            implicitErr.ShouldContain("warning SD0029", Case.Sensitive);
            implicitErr.ShouldContain("/Profile:OpenGL", Case.Sensitive);
            ((char)(await File.ReadAllBytesAsync(implicitOut))[3]).ShouldBe('w');

            explicitExit.ShouldBe(0);
            explicitErr.ShouldBeEmpty("an explicitly named target must not warn, whichever target it is");
        }
        finally
        {
            foreach (string f in new[] { implicitOut, explicitOut })
                if (File.Exists(f)) File.Delete(f);
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
        ChildProcessResult run = await CliProcess.RunAsync(
            _fixture.ExecutablePath, [sourceFile, outputFile, .. extraArgs], TimeSpan.FromSeconds(60));
        return (run.ExitCode, run.Stdout, run.Stderr);
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
