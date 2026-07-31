#nullable enable

using System.Diagnostics;
using System.Text.Json;

namespace ShadowDusk.Validation.MgcbPluginGate;

/// <summary>
/// <b>The Phase 29 end-to-end gate: a REAL <c>dotnet mgcb</c> content build, driven through
/// ShadowDusk's MGCB content-processor plugin.</b>
///
/// <para>The in-suite test (<c>MgcbPluginByteIdentityTests</c>) drives the processor in-process
/// and proves the bytes match the CLI. It cannot prove the part that actually broke first in
/// development: that MGCB <i>discovers</i> a <c>/reference:</c>d plugin by reflection, loads it
/// with <c>Assembly.LoadFrom</c>, finds ShadowDusk's <b>native</b> compilers from a process whose
/// base directory is MGCB's own, and serializes our <c>CompiledEffectContent</c> through
/// MonoGame's effect <c>ContentTypeWriter</c> into a real <c>.xnb</c>. That needs a real MGCB, so
/// it needs this driver - <c>dotnet test</c> has no <c>dotnet-mgcb</c>.</para>
///
/// <para>What it asserts, per fixture and per platform:</para>
/// <list type="number">
/// <item><c>dotnet mgcb</c> exits 0 and writes the <c>.xnb</c>.</item>
/// <item>The <c>.mgfx</c> payload inside that <c>.xnb</c> is <b>byte-for-byte</b> what the
///   ShadowDusk CLI binary emits for the same source and profile.</item>
/// <item>The <c>.xnb</c> envelope (header, type-reader manifest, shared-resource count, type id)
///   is <b>byte-for-byte</b> the envelope MGCB writes for its OWN stock <c>EffectProcessor</c> -
///   so the container a consumer's <c>ContentManager</c> reads is unchanged.</item>
/// <item>The payload <b>differs</b> from the stock processor's, which is the positive proof that
///   ShadowDusk, not MGCB's built-in effect compiler, produced it.</item>
/// </list>
///
/// <para>Exits 0 only if every case passes; non-zero (with the failing case named) otherwise.</para>
/// </summary>
internal static class Program
{
    /// <summary>(fixture, MGCB <c>/platform:</c>, ShadowDusk CLI <c>/Profile:</c>).</summary>
    private static readonly (string Fixture, string Platform, string Profile)[] Cases =
    [
        ("Grayscale.fx",       "DesktopGL", "OpenGL"),
        ("Grayscale.fx",       "Windows",   "DirectX_11"),
        ("VertexAndPixel.fx",  "DesktopGL", "OpenGL"),
        ("VertexAndPixel.fx",  "Windows",   "DirectX_11"),
        ("MultiTexture.fx",    "DesktopGL", "OpenGL"),
        ("ForwardLighting.fx", "DesktopGL", "OpenGL"),
        ("SpriteEffect.fx",    "Windows",   "DirectX_11"),
    ];

    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            return 1;
        }
    }

    private static int Run()
    {
        string repoRoot   = FindRepoRoot();
        string mgcb       = LocateMgcb(repoRoot);
        string plugin     = LocateNewest(Path.Combine(repoRoot, "src", "ShadowDusk.MgcbPlugin", "bin"),
                                         "ShadowDusk.MgcbPlugin.dll");
        string cli        = LocateNewest(Path.Combine(repoRoot, "src", "ShadowDusk.Cli", "bin"),
                                         OperatingSystem.IsWindows() ? "ShadowDuskCLI.exe" : "ShadowDuskCLI");
        string fixtures   = Path.Combine(repoRoot, "tests", "fixtures", "shaders");

        Console.WriteLine($"mgcb   : {mgcb}");
        Console.WriteLine($"plugin : {plugin}");
        Console.WriteLine($"cli    : {cli}");
        Console.WriteLine();

        string work = Path.Combine(Path.GetTempPath(), "shadowdusk_mgcb_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        int failures = 0;
        try
        {
            foreach ((string fixture, string platform, string profile) in Cases)
            {
                string label = $"{fixture} /platform:{platform}";
                try
                {
                    CheckCase(mgcb, plugin, cli, fixtures, work, fixture, platform, profile);
                    Console.WriteLine($"  PASS  {label}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"  FAIL  {label}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* non-fatal */ }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"MGCB plugin gate: all {Cases.Length} cases PASSED"
            : $"MGCB plugin gate: {failures} of {Cases.Length} cases FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckCase(
        string mgcb, string plugin, string cli, string fixtures, string work,
        string fixture, string platform, string profile)
    {
        string caseDir = Path.Combine(work, $"{Path.GetFileNameWithoutExtension(fixture)}_{platform}");
        Directory.CreateDirectory(caseDir);

        // The fixture is referenced IN PLACE, never copied: several corpus shaders
        // `#include "Macros.fxh"` from the fixtures directory, and a copy leaves the include
        // unresolvable. This also matches what a consumer's .mgcb does - it points at the
        // shader where the shader lives.
        string source = Path.Combine(fixtures, fixture);

        // Arm 1: MGCB + the ShadowDusk plugin.
        WriteMgcb(Path.Combine(caseDir, "shadowdusk.mgcb"), platform, "binsd", "objsd", source,
                  reference: plugin,
                  importer: "ShadowDuskEffectImporter",
                  processor: "ShadowDuskEffectProcessor");
        RunMgcb(mgcb, caseDir, "shadowdusk.mgcb");

        // Arm 2: the SAME MGCB with its OWN stock effect processor, as the envelope oracle.
        WriteMgcb(Path.Combine(caseDir, "stock.mgcb"), platform, "binstock", "objstock", source,
                  reference: null,
                  importer: "EffectImporter",
                  processor: "EffectProcessor");
        RunMgcb(mgcb, caseDir, "stock.mgcb");

        string assetName = Path.GetFileNameWithoutExtension(fixture) + ".xnb";
        byte[] sdXnb    = File.ReadAllBytes(Path.Combine(caseDir, "binsd", assetName));
        byte[] stockXnb = File.ReadAllBytes(Path.Combine(caseDir, "binstock", assetName));

        XnbEffect sd    = XnbEffect.Parse(sdXnb);
        XnbEffect stock = XnbEffect.Parse(stockXnb);

        // (3) The envelope a consumer's ContentManager reads must be unchanged. Everything from
        // the magic through the payload-length field is compared byte for byte; the file-size
        // field in the header and the payload length legitimately differ, so they are excluded
        // and asserted structurally instead.
        if (!sdXnb.AsSpan(0, 6).SequenceEqual(stockXnb.AsSpan(0, 6)))
            throw new InvalidOperationException("xnb header differs from MGCB's own stock build");
        if (!sdXnb.AsSpan(10, sd.PayloadOffset - 14).SequenceEqual(stockXnb.AsSpan(10, stock.PayloadOffset - 14)))
            throw new InvalidOperationException(
                "xnb type-reader manifest / shared-resource count / type id differs from MGCB's own stock build");

        // (4) ShadowDusk really did the work: MGCB's built-in compiler produces different bytes.
        if (sd.Payload.AsSpan().SequenceEqual(stock.Payload))
            throw new InvalidOperationException(
                "the plugin's payload equals MGCB's stock compiler's - the build did not go through ShadowDusk");

        // (2) THE BAR: byte-for-byte the CLI's output.
        string cliOut = Path.Combine(caseDir, "cli.mgfx");
        RunProcess(cli, [source, cliOut, $"/Profile:{profile}"], caseDir);
        byte[] cliBytes = File.ReadAllBytes(cliOut);

        if (!sd.Payload.AsSpan().SequenceEqual(cliBytes))
        {
            throw new InvalidOperationException(
                $"plugin payload ({sd.Payload.Length} bytes) is NOT byte-identical to the CLI's " +
                $"({cliBytes.Length} bytes) for /Profile:{profile}");
        }
    }

    private static void WriteMgcb(
        string path, string platform, string outputDir, string intermediateDir, string sourcePath,
        string? reference, string importer, string processor)
    {
        var lines = new List<string>
        {
            $"/outputDir:{outputDir}",
            $"/intermediateDir:{intermediateDir}",
            $"/platform:{platform}",
            "/config:",
            "/profile:Reach",
            "/compress:False",
            string.Empty,
        };

        if (reference is not null)
            lines.Add($"/reference:{reference}");

        lines.Add(string.Empty);
        lines.Add($"#begin {sourcePath}");
        lines.Add($"/importer:{importer}");
        lines.Add($"/processor:{processor}");
        lines.Add($"/build:{sourcePath};{Path.GetFileNameWithoutExtension(sourcePath)}");

        File.WriteAllLines(path, lines);
    }

    private static void RunMgcb(string mgcb, string workingDirectory, string responseFile)
    {
        (int exitCode, string output) = RunProcessRaw(
            "dotnet", [mgcb, "/@:" + responseFile], workingDirectory);

        if (exitCode != 0)
            throw new InvalidOperationException($"dotnet mgcb ({responseFile}) exited {exitCode}:{Environment.NewLine}{output}");
    }

    private static void RunProcess(string fileName, string[] arguments, string workingDirectory)
    {
        (int exitCode, string output) = RunProcessRaw(fileName, arguments, workingDirectory);
        if (exitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {exitCode}:{Environment.NewLine}{output}");
    }

    private static (int ExitCode, string Output) RunProcessRaw(
        string fileName, string[] arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{fileName}'");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>
    /// Finds the pinned <c>dotnet-mgcb</c> in the NuGet package cache, using the version the
    /// repo's tool manifest names. Deliberately not <c>dotnet tool run mgcb</c>: that needs the
    /// working directory to sit under the manifest, which this driver's temp workspace does not.
    /// Fails loudly with the exact restore command rather than skipping - a gate that quietly
    /// does nothing is worse than no gate.
    /// </summary>
    private static string LocateMgcb(string repoRoot)
    {
        string manifest = Path.Combine(repoRoot, ".config", "dotnet-tools.json");
        if (!File.Exists(manifest))
            throw new FileNotFoundException($"tool manifest not found at {manifest}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        string version = document.RootElement
            .GetProperty("tools")
            .GetProperty("dotnet-mgcb")
            .GetProperty("version")
            .GetString()
            ?? throw new InvalidOperationException("dotnet-mgcb has no version in .config/dotnet-tools.json");

        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        string mgcb = Path.Combine(packages, "dotnet-mgcb", version, "tools", "net8.0", "any", "mgcb.dll");
        if (!File.Exists(mgcb))
        {
            throw new FileNotFoundException(
                $"dotnet-mgcb {version} is not in the NuGet cache ({mgcb}). " +
                "Run `dotnet tool restore` from the repository root and try again.");
        }

        return mgcb;
    }

    /// <summary>
    /// The most recently written copy of <paramref name="fileName"/> under a project's
    /// <c>bin/</c>, across Debug and Release - the build you are about to ship, not a stale one.
    /// </summary>
    private static string LocateNewest(string binDirectory, string fileName)
    {
        if (!Directory.Exists(binDirectory))
        {
            throw new DirectoryNotFoundException(
                $"{binDirectory} does not exist. Build the solution first (`dotnet build ShadowDusk.slnx`).");
        }

        string? newest = null;
        DateTime newestStamp = DateTime.MinValue;
        foreach (string candidate in Directory.EnumerateFiles(binDirectory, fileName, SearchOption.AllDirectories))
        {
            DateTime stamp = File.GetLastWriteTimeUtc(candidate);
            if (stamp > newestStamp)
            {
                newestStamp = stamp;
                newest = candidate;
            }
        }

        return newest
            ?? throw new FileNotFoundException(
                $"{fileName} not found under {binDirectory}. Build the solution first (`dotnet build ShadowDusk.slnx`).");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the repository root (ShadowDusk.slnx)");
    }
}

/// <summary>
/// The minimum XNB reader needed to pull an effect out of a content-pipeline output file:
/// header, type-reader manifest, shared-resource count, object type id, then the effect's
/// length-prefixed bytes. Uncompressed only, which is what <c>/compress:False</c> produces.
/// </summary>
internal sealed record XnbEffect(int PayloadOffset, byte[] Payload)
{
    public static XnbEffect Parse(byte[] bytes)
    {
        if (bytes.Length < 10 || bytes[0] != 'X' || bytes[1] != 'N' || bytes[2] != 'B')
            throw new InvalidOperationException("not an XNB file");
        if ((bytes[5] & 0x80) != 0)
            throw new InvalidOperationException("compressed XNB is not supported by this gate (use /compress:False)");

        int i = 10;
        int readerCount = Read7BitEncodedInt(bytes, ref i);
        for (int r = 0; r < readerCount; r++)
        {
            int nameLength = Read7BitEncodedInt(bytes, ref i);
            i += nameLength;   // reader type name
            i += 4;            // reader version
        }

        Read7BitEncodedInt(bytes, ref i);            // shared-resource count
        Read7BitEncodedInt(bytes, ref i);            // type id of the primary object

        int payloadLength = BitConverter.ToInt32(bytes, i);
        i += 4;

        return new XnbEffect(i, bytes.AsSpan(i, payloadLength).ToArray());
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
}
