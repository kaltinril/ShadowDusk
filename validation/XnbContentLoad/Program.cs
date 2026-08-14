// XnbContentLoad = the Phase 60 (issue #199) rung-4 gate.
//
// THE CLAIM UNDER TEST: a consumer replaces their content pipeline with ShadowDusk and
// "does not change any lines of code at all" - they keep calling Content.Load<Effect>("Foo").
//
// Nothing short of a real ContentManager can prove that. `new Effect(gd, bytes)` proves the
// PAYLOAD loads (already covered elsewhere); it says nothing about the XNB container, the
// platform-byte whitelist, or the type-reader manifest, which are exactly what Content.Load
// validates and reject-on-mismatch. So this driver:
//
//   1. builds each fixture's .xnb through STOCK dotnet-mgcb          (the mgfxc oracle arm)
//   2. builds the same fixture through ShadowDusk + XnbWriter        (the arm under test)
//   3. drops each in its own content directory and loads BOTH with a real
//      ContentManager.Load<Effect>(assetName)  - no `new Effect`, no hand-parse
//   4. renders both through the identical SpriteBatch path and requires the images to be
//      PIXEL-IDENTICAL.
//
// Step 3 is also the demonstration Phase 60 OQ1/OQ5 demanded: the file is dropped where the
// mgfxc-built .xnb sat, loaded by the name the consumer already uses, with no companion file
// and no consumer code change. Reasoning about that was explicitly not good enough.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Validation.XnbContentLoad;

/// <summary>(fixture, MGCB <c>/platform:</c>, ShadowDusk target).</summary>
internal sealed record Case(string Fixture, string Platform, PlatformTarget Target);

internal static class Program
{
    private static readonly Case[] Cases =
    [
        new("Grayscale.fx",       "Windows", PlatformTarget.DirectX),
        new("VertexAndPixel.fx",  "Windows", PlatformTarget.DirectX),
        new("MultiTexture.fx",    "Windows", PlatformTarget.DirectX),
        new("SpriteEffect.fx",    "Windows", PlatformTarget.DirectX),
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
        string repoRoot = FindRepoRoot();
        string mgcb     = LocateMgcb(repoRoot);
        string fixtures = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        string catPath  = Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
        string outDir   = Path.Combine(repoRoot, "validation", "output-xnb");

        Console.WriteLine($"[xnb] mgcb     : {mgcb}");
        Console.WriteLine($"[xnb] fixtures : {fixtures}");
        Console.WriteLine($"[xnb] out      : {outDir}\n");

        if (!File.Exists(catPath))
            throw new FileNotFoundException($"cat image not found: {catPath}");

        string work = Path.Combine(Path.GetTempPath(), "shadowdusk_xnb_gate_" + Guid.NewGuid().ToString("N"));
        string referenceContent = Path.Combine(work, "content-reference");
        string shadowDuskContent = Path.Combine(work, "content-shadowdusk");
        Directory.CreateDirectory(referenceContent);
        Directory.CreateDirectory(shadowDuskContent);

        var jobs = new List<XnbJob>();

        try
        {
            foreach (Case c in Cases)
            {
                string assetName = Path.GetFileNameWithoutExtension(c.Fixture);
                try
                {
                    BuildReferenceXnb(mgcb, fixtures, work, c, referenceContent);
                    BuildShadowDuskXnb(fixtures, c, shadowDuskContent).GetAwaiter().GetResult();
                    jobs.Add(new XnbJob(assetName, null));
                    Console.WriteLine($"  built  {assetName} ({c.Platform})");
                }
                catch (Exception ex)
                {
                    jobs.Add(new XnbJob(assetName, ex.Message));
                    Console.WriteLine($"  BUILD FAIL  {assetName}: {ex.Message}");
                }
            }

            Console.WriteLine();

            // Envelope + payload assertions that do not need a GPU (C1/C2), done here rather
            // than in `dotnet test` because only this driver has a real mgcb to compare against.
            int envelopeFailures = 0;
            foreach (XnbJob job in jobs.Where(j => j.BuildError is null))
            {
                string reference = Path.Combine(referenceContent, job.AssetName + ".xnb");
                string ours      = Path.Combine(shadowDuskContent, job.AssetName + ".xnb");
                try
                {
                    AssertEnvelopeMatches(File.ReadAllBytes(reference), File.ReadAllBytes(ours));
                    Console.WriteLine($"  envelope OK   {job.AssetName}");
                }
                catch (Exception ex)
                {
                    envelopeFailures++;
                    Console.WriteLine($"  envelope FAIL {job.AssetName}: {ex.Message}");
                }
            }

            Console.WriteLine();

            using var game = new ContentLoadRenderer(catPath, outDir, referenceContent, shadowDuskContent, jobs);
            game.Run();

            int rendered = 0;
            Console.WriteLine("[xnb] Content.Load<Effect> + render results:");
            foreach (XnbOutcome o in game.Outcomes)
            {
                if (o.Identical)
                    rendered++;
                Console.WriteLine($"  [{(o.Identical ? "OK  " : "FAIL")}] {o.Name,-16} {o.Detail}");
            }

            int failures = envelopeFailures + (game.Outcomes.Count - rendered)
                           + jobs.Count(j => j.BuildError is not null);

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? $"XNB Content.Load gate: all {Cases.Length} cases PASSED (envelope + rung-4 render)"
                : $"XNB Content.Load gate: {failures} failure(s)");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* non-fatal */ }
        }
    }

    /// <summary>Arm 1: stock <c>dotnet mgcb</c> - the reference compiler's own <c>.xnb</c>.</summary>
    private static void BuildReferenceXnb(
        string mgcb, string fixtures, string work, Case c, string destination)
    {
        string caseDir = Path.Combine(work, "mgcb_" + Path.GetFileNameWithoutExtension(c.Fixture));
        Directory.CreateDirectory(caseDir);

        string source = Path.Combine(fixtures, c.Fixture);
        string assetName = Path.GetFileNameWithoutExtension(c.Fixture);

        // The fixture is referenced IN PLACE (several corpus shaders #include "Macros.fxh"
        // from the fixtures directory, and a copy leaves the include unresolvable).
        File.WriteAllLines(Path.Combine(caseDir, "build.mgcb"),
        [
            "/outputDir:bin",
            "/intermediateDir:obj",
            $"/platform:{c.Platform}",
            "/config:",
            "/profile:Reach",
            "/compress:False",
            string.Empty,
            $"#begin {source}",
            "/importer:EffectImporter",
            "/processor:EffectProcessor",
            $"/build:{source};{assetName}",
        ]);

        (int exit, string output) = RunProcess("dotnet", [mgcb, "/@:build.mgcb"], caseDir);
        if (exit != 0)
            throw new InvalidOperationException($"dotnet mgcb exited {exit}:{Environment.NewLine}{output}");

        File.Copy(Path.Combine(caseDir, "bin", assetName + ".xnb"),
                  Path.Combine(destination, assetName + ".xnb"), overwrite: true);
    }

    /// <summary>Arm 2: ShadowDusk compiles, and <c>XnbWriter</c> wraps. No MGCB in the picture.</summary>
    private static async Task BuildShadowDuskXnb(string fixtures, Case c, string destination)
    {
        string source = Path.Combine(fixtures, c.Fixture);
        string assetName = Path.GetFileNameWithoutExtension(c.Fixture);

        var compiler = new EffectCompiler();
        var result = await compiler.CompileAsync(
            await File.ReadAllTextAsync(source),
            new CompilerOptions
            {
                Target          = c.Target,
                IncludeResolver = new FileSystemIncludeResolver(),
                SourceFileName  = source,
            });

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                "ShadowDusk compile failed: "
                + string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        }

        // The product API a consumer would call - not a driver-local reimplementation.
        await File.WriteAllBytesAsync(
            Path.Combine(destination, assetName + ".xnb"), result.Value.ToXnb());
    }

    /// <summary>
    /// C1: everything a <c>ContentManager</c> reads before the payload must be byte-identical to
    /// stock MGCB's. The file-size and payload-length int32s legitimately differ (different
    /// compilers, different payload sizes) and are asserted structurally instead.
    /// </summary>
    private static void AssertEnvelopeMatches(byte[] reference, byte[] ours)
    {
        int refPayload = PayloadOffset(reference);
        int ourPayload = PayloadOffset(ours);

        if (!reference.AsSpan(0, 6).SequenceEqual(ours.AsSpan(0, 6)))
        {
            throw new InvalidOperationException(
                $"header differs: reference {Describe(reference)} vs ours {Describe(ours)}");
        }

        // [10, payloadOffset-4) = reader manifest + shared-resource count + type id.
        if (!reference.AsSpan(10, refPayload - 14).SequenceEqual(ours.AsSpan(10, ourPayload - 14)))
            throw new InvalidOperationException("type-reader manifest / shared-resource count / type id differs");

        if (BitConverter.ToInt32(ours, 6) != ours.Length)
            throw new InvalidOperationException(
                $"file-size field {BitConverter.ToInt32(ours, 6)} != actual length {ours.Length}");

        // Positive control: the payloads MUST differ, or the "ShadowDusk produced this" claim is
        // unproven - we would be comparing MGCB against itself.
        if (reference.AsSpan(refPayload).SequenceEqual(ours.AsSpan(ourPayload)))
            throw new InvalidOperationException("payload equals MGCB's - ShadowDusk did not produce these bytes");

        static string Describe(byte[] b) =>
            $"'{(char)b[0]}{(char)b[1]}{(char)b[2]}' platform='{(char)b[3]}' version={b[4]} flags=0x{b[5]:x2}";
    }

    private static int PayloadOffset(byte[] bytes)
    {
        if (bytes.Length < 10 || bytes[0] != 'X' || bytes[1] != 'N' || bytes[2] != 'B')
            throw new InvalidOperationException("not an XNB file");
        if ((bytes[5] & 0xC0) != 0)
            throw new InvalidOperationException("compressed XNB is not supported by this gate");

        int i = 10;
        int readerCount = Read7BitEncodedInt(bytes, ref i);
        for (int r = 0; r < readerCount; r++)
        {
            int nameLength = Read7BitEncodedInt(bytes, ref i);
            i += nameLength + 4;
        }

        Read7BitEncodedInt(bytes, ref i);   // shared-resource count
        Read7BitEncodedInt(bytes, ref i);   // type id
        return i + 4;                       // past the payload-length int32
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

    private static (int ExitCode, string Output) RunProcess(
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

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{fileName}'");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static string LocateMgcb(string repoRoot)
    {
        string manifest = Path.Combine(repoRoot, ".config", "dotnet-tools.json");
        if (!File.Exists(manifest))
            throw new FileNotFoundException($"tool manifest not found at {manifest}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        string version = document.RootElement
            .GetProperty("tools").GetProperty("dotnet-mgcb").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("dotnet-mgcb has no version in .config/dotnet-tools.json");

        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        string mgcb = Path.Combine(packages, "dotnet-mgcb", version, "tools", "net8.0", "any", "mgcb.dll");
        if (!File.Exists(mgcb))
        {
            throw new FileNotFoundException(
                $"dotnet-mgcb {version} is not in the NuGet cache ({mgcb}). "
                + "Run `dotnet tool restore` from the repository root and try again.");
        }

        return mgcb;
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("could not locate the repository root (ShadowDusk.slnx)");
    }
}

/// <summary>One asset present in both content directories (or a build failure).</summary>
internal sealed record XnbJob(string AssetName, string? BuildError);

/// <summary>The rung-4 verdict for one asset.</summary>
internal sealed record XnbOutcome(string Name, bool Identical, string Detail);

/// <summary>
/// Loads each asset from BOTH content directories through a real
/// <see cref="ContentManager"/> - <c>Content.Load&lt;Effect&gt;(assetName)</c>, the exact call a
/// consumer already has in their game - renders both through an identical SpriteBatch path, and
/// compares the results pixel for pixel in-process.
/// </summary>
internal sealed class ContentLoadRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly string _referenceRoot;
    private readonly string _shadowDuskRoot;
    private readonly IReadOnlyList<XnbJob> _jobs;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private ContentManager _referenceContent = null!;
    private ContentManager _shadowDuskContent = null!;
    private bool _done;

    public List<XnbOutcome> Outcomes { get; } = [];

    public ContentLoadRenderer(
        string catPath, string outDir, string referenceRoot, string shadowDuskRoot,
        IReadOnlyList<XnbJob> jobs)
    {
        _catPath        = catPath;
        _outDir         = outDir;
        _referenceRoot  = referenceRoot;
        _shadowDuskRoot = shadowDuskRoot;
        _jobs           = jobs;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk XNB Content.Load validation (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using FileStream fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        Directory.CreateDirectory(_outDir);

        // Two managers, two roots, same asset names - so each arm is loaded exactly the way a
        // consumer's game loads it, by name, with no path trickery.
        _referenceContent  = new ContentManager(Services, _referenceRoot);
        _shadowDuskContent = new ContentManager(Services, _shadowDuskRoot);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        GraphicsDevice.Clear(Color.Black);

        foreach (XnbJob job in _jobs)
        {
            if (job.BuildError is not null)
            {
                Outcomes.Add(new XnbOutcome(job.AssetName, false, $"build failed: {job.BuildError}"));
                continue;
            }
            Outcomes.Add(CompareOne(job.AssetName));
        }

        _done = true;
        Exit();
    }

    private XnbOutcome CompareOne(string assetName)
    {
        Effect reference;
        Effect ours;

        try
        {
            reference = _referenceContent.Load<Effect>(assetName);
        }
        catch (Exception ex)
        {
            return new XnbOutcome(assetName, false,
                $"the REFERENCE (mgcb) .xnb failed to load: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            // THE claim: the consumer's unchanged Content.Load<Effect> call accepts a file
            // ShadowDusk wrote end to end, with MGCB never involved.
            ours = _shadowDuskContent.Load<Effect>(assetName);
        }
        catch (Exception ex)
        {
            return new XnbOutcome(assetName, false,
                $"ShadowDusk's .xnb failed Content.Load<Effect>: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Color[] referencePixels = Render(reference, assetName + ".reference.png");
            Color[] ourPixels       = Render(ours, assetName + ".shadowdusk.png");

            int differing = 0;
            int maxDelta = 0;
            for (int i = 0; i < referencePixels.Length; i++)
            {
                if (referencePixels[i] == ourPixels[i])
                    continue;
                differing++;
                maxDelta = Math.Max(maxDelta, Math.Max(
                    Math.Max(Math.Abs(referencePixels[i].R - ourPixels[i].R),
                             Math.Abs(referencePixels[i].G - ourPixels[i].G)),
                    Math.Max(Math.Abs(referencePixels[i].B - ourPixels[i].B),
                             Math.Abs(referencePixels[i].A - ourPixels[i].A))));
            }

            return differing == 0
                ? new XnbOutcome(assetName, true,
                    $"Content.Load<Effect> OK on both arms; {referencePixels.Length} px identical")
                : new XnbOutcome(assetName, false,
                    $"{differing}/{referencePixels.Length} px differ from the mgfxc build (max channel delta {maxDelta})");
        }
        catch (Exception ex)
        {
            return new XnbOutcome(assetName, false, $"render threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private Color[] Render(Effect effect, string pngName)
    {
        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        var dest = new Rectangle(0, 0, w, h);

        GraphicsDevice.SetRenderTarget(rt);
        GraphicsDevice.Clear(Color.Transparent);

        // Prime SpriteBatch's sprite vertex shader (pixel-only effects need a VS).
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
        _sb.Draw(_cat, dest, Color.White);
        _sb.End();

        _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
        _sb.Draw(_cat, dest, Color.White);
        _sb.End();

        GraphicsDevice.SetRenderTarget(null);

        var pixels = new Color[w * h];
        rt.GetData(pixels);

        using (FileStream outFs = File.Create(Path.Combine(_outDir, pngName)))
            rt.SaveAsPng(outFs, w, h);

        return pixels;
    }
}
