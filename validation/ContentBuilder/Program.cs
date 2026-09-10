// ContentBuilder = the Phase 63 (issue #203) rung-4 gate for the MonoGame 3.8.5 Content Builder
// delivery shape (the ShadowDusk.ContentPipeline package).
//
// THE CLAIM UNDER TEST: a MonoGame 3.8.5 game on the template-default Content Builder project
// adds one PackageReference, passes `new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor()`
// in GetContentCollection, and gets an .xnb that is ShadowDusk's bytes in MonoGame's own
// envelope, loads through the unchanged Content.Load<Effect>, and renders like the stock build.
//
// Nothing short of a REAL ContentBuilder can prove that, for two reasons no `dotnet test` can
// see: (1) ContentBuilder.Run calls Assembly.GetTypes() on every assembly in this process's
// dependency graph with NO guard, so this run is the only place ShadowDusk's real graph is
// proven scan-clean (the reason Vortice.Direct3D12 was dropped); and (2) the Builder, not
// MGCB, resolves importers/processors, hands the importer an absolute path, and writes the
// .xnb through its own ContentCompiler. So this driver:
//
//   1. runs a real ContentBuilder subclass over the fixture set with TWO content roots -
//      `stock` (MonoGame's own EffectImporter/EffectProcessor, the in-process 3.8.5
//      mgfxc-equivalent oracle, which writes MGFX v11) and `sd` (ShadowDusk.ContentPipeline's
//      pair, passed as instances) - per platform (Windows, DesktopGL);
//   2. asserts per asset: the `sd` payload is byte-for-byte the ShadowDuskCLI binary's for the
//      platform's target; the .xnb envelope through the type id is byte-for-byte the stock
//      build's; the payloads differ (positive control);
//   3. loads BOTH Windows .xnbs through a real ContentManager.Load<Effect>(assetName) on
//      MonoGame 3.8.5 WindowsDX and renders both through the identical SpriteBatch path,
//      requiring the images to be PIXEL-IDENTICAL. (DesktopGL is compile + byte assertions
//      only: the render host is the DX11 runtime.)
//
// Mirrors validation/XnbContentLoad, which proves the same thing for the direct .xnb writer.

using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Framework.Content.Pipeline.Builder;
using ShadowDusk.ContentPipeline;

namespace ShadowDusk.Validation.ContentBuilderGate;

/// <summary>One platform pass of the Builder: the platform, the CLI profile whose bytes the ShadowDusk arm must equal, the fixtures, and whether the DX11 host renders it.</summary>
internal sealed record PlatformCase(TargetPlatform Platform, string CliProfile, string[] Fixtures, bool Render);

/// <summary>One asset built on the Windows pass, ready for the render half.</summary>
internal sealed record RenderJob(string AssetName, string StockRoot, string ShadowDuskRoot);

/// <summary>The rung-4 verdict for one asset.</summary>
internal sealed record RenderOutcome(string Name, bool Identical, string Detail);

/// <summary>
/// The consumer's Builder, exactly as the ShadowDusk.ContentPipeline README shows it, plus a
/// second content root that builds the same files through MonoGame's stock pair as the
/// envelope + render oracle. The instances are passed on purpose: with both pairs loaded,
/// extension auto-discovery picks MonoGame's (measured, Phase 63 OQ3).
/// </summary>
internal sealed class GateBuilder : ContentBuilder
{
    public override IContentCollection GetContentCollection()
    {
        var content = new ContentCollection();

        content.SetContentRoot("stock");
        content.Include<WildcardRule>("Effects/*.fx", new EffectImporter(), new EffectProcessor());

        content.SetContentRoot("sd");
        content.Include<WildcardRule>("Effects/*.fx", new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor());

        return content;
    }
}

internal static class Program
{
    private static readonly PlatformCase[] Cases =
    [
        new(TargetPlatform.Windows,   "DirectX_11", ["Grayscale.fx", "VertexAndPixel.fx", "MultiTexture.fx", "SpriteEffect.fx"], Render: true),
        // SpriteEffect.fx is the known Phase 41 GAP-1 GL half (macro-defined techniques, SD0010),
        // identical through every route, so the GL pass carries the three GL-compilable fixtures.
        new(TargetPlatform.DesktopGL, "OpenGL",     ["Grayscale.fx", "VertexAndPixel.fx", "MultiTexture.fx"],                    Render: false),
    ];

    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            return 1;
        }
    }

    private static int Run()
    {
        string repoRoot = FindRepoRoot();
        string cli      = LocateNewest(Path.Combine(repoRoot, "src", "ShadowDusk.Cli", "bin"), "ShadowDuskCLI.exe");
        string fixtures = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        string catPath  = Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
        string outDir   = Path.Combine(repoRoot, "validation", "output-contentbuilder");

        Console.WriteLine($"[cb] Content.Pipeline : {typeof(ContentBuilder).Assembly.GetName().Version} ({typeof(ContentBuilder).Assembly.Location})");
        Console.WriteLine($"[cb] MonoGame.Framework: {typeof(Game).Assembly.GetName().Version}");
        Console.WriteLine($"[cb] ContentPipeline   : {typeof(ShadowDuskEffectProcessor).Assembly.Location}");
        Console.WriteLine($"[cb]   compiled against : Content.Pipeline {typeof(ShadowDuskEffectProcessor).Assembly.GetReferencedAssemblies().First(a => a.Name == "MonoGame.Framework.Content.Pipeline").Version}");
        Console.WriteLine($"[cb] cli               : {cli}");
        Console.WriteLine($"[cb] Vortice.Direct3D12 in graph: {AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Vortice.Direct3D12")} (must stay false)");
        Console.WriteLine();

        if (!File.Exists(catPath))
            throw new FileNotFoundException($"cat image not found: {catPath}");

        string work = Path.Combine(Path.GetTempPath(), "shadowdusk_cb_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        int failures = 0;
        var renderJobs = new List<RenderJob>();

        try
        {
            foreach (PlatformCase c in Cases)
            {
                Console.WriteLine($"=== Builder pass: -p {c.Platform} (ShadowDusk arm must equal ShadowDuskCLI /Profile:{c.CliProfile}) ===");

                // The consumer's source tree: Assets/Effects/*.fx plus every .fxh beside them, so
                // #include resolves exactly as it does from a game's Content directory.
                string sourceDirName = $"Assets-{c.Platform}";
                string effectsDir = Path.Combine(work, sourceDirName, "Effects");
                Directory.CreateDirectory(effectsDir);
                foreach (string fixture in c.Fixtures)
                    File.Copy(Path.Combine(fixtures, fixture), Path.Combine(effectsDir, fixture), overwrite: true);
                foreach (string header in Directory.GetFiles(fixtures, "*.fxh"))
                    File.Copy(header, Path.Combine(effectsDir, Path.GetFileName(header)), overwrite: true);

                string outputDirName = $"out-{c.Platform}";
                var builder = new GateBuilder();
                bool ran = builder.Run(new ContentBuilderParams
                {
                    Mode                  = ContentBuilderMode.Builder,
                    WorkingDirectory      = work,
                    SourceDirectory       = sourceDirName,
                    OutputDirectory       = outputDirName,
                    IntermediateDirectory = $"obj-{c.Platform}",
                    Platform              = c.Platform,
                    GraphicsProfile       = GraphicsProfile.HiDef,
                    CompressContent       = false,
                    Rebuild               = true,
                });

                Console.WriteLine($"[cb] Run returned {ran}; succeeded={builder.SucceededToBuild} failed={builder.FailedToBuild}");
                int expected = c.Fixtures.Length * 2;
                if (!ran || builder.FailedToBuild != 0 || builder.SucceededToBuild != expected)
                {
                    failures++;
                    Console.WriteLine($"  FAIL  -p {c.Platform}: expected {expected} successful builds (stock + sd per fixture), " +
                                      $"got succeeded={builder.SucceededToBuild} failed={builder.FailedToBuild} ran={ran}");
                    continue;
                }

                string outRoot = Path.Combine(work, outputDirName);
                foreach (string fixture in c.Fixtures)
                {
                    string assetName = Path.GetFileNameWithoutExtension(fixture);
                    string label = $"{fixture} -p {c.Platform}";
                    try
                    {
                        string stockXnbPath = FindXnb(Path.Combine(outRoot, "stock"), assetName);
                        string sdXnbPath    = FindXnb(Path.Combine(outRoot, "sd"), assetName);

                        byte[] stockXnb = File.ReadAllBytes(stockXnbPath);
                        byte[] sdXnb    = File.ReadAllBytes(sdXnbPath);
                        XnbEffect stock = XnbEffect.Parse(stockXnb);
                        XnbEffect sd    = XnbEffect.Parse(sdXnb);

                        AssertEnvelopeMatches(stockXnb, stock, sdXnb, sd);

                        // THE BAR: byte-for-byte the CLI binary's output for the same source file.
                        string cliOut = Path.Combine(work, $"cli_{assetName}_{c.Platform}.mgfx");
                        RunProcess(cli, [Path.Combine(effectsDir, fixture), cliOut, $"/Profile:{c.CliProfile}"], work);
                        byte[] cliBytes = File.ReadAllBytes(cliOut);
                        if (!sd.Payload.AsSpan().SequenceEqual(cliBytes))
                        {
                            throw new InvalidOperationException(
                                $"ShadowDusk arm payload ({sd.Payload.Length} bytes) is NOT byte-identical to the CLI's " +
                                $"({cliBytes.Length} bytes) for /Profile:{c.CliProfile}");
                        }

                        Console.WriteLine($"  PASS  {label}: payload == CLI ({cliBytes.Length} B), envelope == stock 3.8.5 " +
                                          $"(stock MGFX v{stock.Payload[4]} {stock.Payload.Length} B, ours v{sd.Payload[4]})");

                        if (c.Render)
                            renderJobs.Add(new RenderJob(assetName, Path.GetDirectoryName(stockXnbPath)!, Path.GetDirectoryName(sdXnbPath)!));
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Console.WriteLine($"  FAIL  {label}: {ex.Message}");
                    }
                }

                Console.WriteLine();
            }

            // The rung-4 half: Content.Load<Effect> on MonoGame 3.8.5 WindowsDX, both arms,
            // pixel-identical. Only the Windows pass renders (the host is the DX11 runtime).
            Console.WriteLine("=== Content.Load<Effect> + render on MonoGame 3.8.5 WindowsDX ===");
            if (renderJobs.Count == 0)
            {
                failures++;
                Console.WriteLine("  FAIL  nothing to render - the Windows pass produced no assets");
            }
            else
            {
                using var game = new ContentLoadRenderer(catPath, outDir, renderJobs);
                game.Run();

                foreach (RenderOutcome o in game.Outcomes)
                {
                    if (!o.Identical)
                        failures++;
                    Console.WriteLine($"  [{(o.Identical ? "OK  " : "FAIL")}] {o.Name,-16} {o.Detail}");
                }

                if (game.Outcomes.Count != renderJobs.Count)
                {
                    failures++;
                    Console.WriteLine($"  FAIL  rendered {game.Outcomes.Count} of {renderJobs.Count} assets");
                }
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* non-fatal */ }
        }

        int assets = Cases.Sum(c => c.Fixtures.Length);
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"Content Builder gate: all {assets} assets PASSED (real 3.8.5 ContentBuilder, envelope + CLI byte-identity + rung-4 render)"
            : $"Content Builder gate: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The one <c>.xnb</c> for an asset under a content root, wherever the Builder placed it.</summary>
    private static string FindXnb(string root, string assetName)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"content root {root} was not produced");

        string[] hits = Directory.GetFiles(root, assetName + ".xnb", SearchOption.AllDirectories);
        return hits.Length == 1
            ? hits[0]
            : throw new InvalidOperationException($"expected exactly one {assetName}.xnb under {root}, found {hits.Length}");
    }

    /// <summary>
    /// Everything a <c>ContentManager</c> reads before the payload must be byte-identical to
    /// the stock build's (header sans file size; type-reader manifest; shared-resource count;
    /// type id). The payloads MUST differ, or ShadowDusk did not produce the bytes.
    /// </summary>
    private static void AssertEnvelopeMatches(byte[] stockXnb, XnbEffect stock, byte[] sdXnb, XnbEffect sd)
    {
        if (!stockXnb.AsSpan(0, 6).SequenceEqual(sdXnb.AsSpan(0, 6)))
        {
            throw new InvalidOperationException(
                $"xnb header differs: stock {Describe(stockXnb)} vs ShadowDusk {Describe(sdXnb)}");
        }

        if (!stockXnb.AsSpan(10, stock.PayloadOffset - 14).SequenceEqual(sdXnb.AsSpan(10, sd.PayloadOffset - 14)))
            throw new InvalidOperationException("xnb type-reader manifest / shared-resource count / type id differs from the stock 3.8.5 build");

        if (BitConverter.ToInt32(sdXnb, 6) != sdXnb.Length)
            throw new InvalidOperationException($"file-size field {BitConverter.ToInt32(sdXnb, 6)} != actual length {sdXnb.Length}");

        if (stock.Payload.AsSpan().SequenceEqual(sd.Payload))
            throw new InvalidOperationException("the ShadowDusk arm's payload equals the stock processor's - the build did not go through ShadowDusk");

        static string Describe(byte[] b) =>
            $"'{(char)b[0]}{(char)b[1]}{(char)b[2]}' platform='{(char)b[3]}' version={b[4]} flags=0x{b[5]:x2}";
    }

    private static void RunProcess(string fileName, string[] arguments, string workingDirectory)
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {process.ExitCode}:{Environment.NewLine}{stdout}{stderr}");
    }

    /// <summary>
    /// The most recently written copy of <paramref name="fileName"/> under a project's
    /// <c>bin/</c>, across Debug and Release - the build you are about to ship, not a stale one.
    /// </summary>
    private static string LocateNewest(string binDirectory, string fileName)
    {
        if (!Directory.Exists(binDirectory))
            throw new DirectoryNotFoundException($"{binDirectory} does not exist. Build src/ShadowDusk.Cli first.");

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

        return newest ?? throw new FileNotFoundException($"{fileName} not found under {binDirectory}. Build src/ShadowDusk.Cli first.");
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

/// <summary>
/// The minimum XNB reader needed to pull an effect out of a content-pipeline output file:
/// header, type-reader manifest, shared-resource count, object type id, then the effect's
/// length-prefixed bytes. Uncompressed only (<c>CompressContent = false</c>).
/// </summary>
internal sealed record XnbEffect(int PayloadOffset, byte[] Payload)
{
    public static XnbEffect Parse(byte[] bytes)
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

/// <summary>
/// Loads each asset from BOTH content roots through a real <see cref="ContentManager"/> -
/// <c>Content.Load&lt;Effect&gt;(assetName)</c>, the exact call a consumer already has in their
/// game - renders both through an identical SpriteBatch path, and compares the results pixel
/// for pixel in-process. The same recipe as validation/XnbContentLoad, on MonoGame 3.8.5.
/// </summary>
internal sealed class ContentLoadRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<RenderJob> _jobs;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<RenderOutcome> Outcomes { get; } = [];

    public ContentLoadRenderer(string catPath, string outDir, IReadOnlyList<RenderJob> jobs)
    {
        _catPath = catPath;
        _outDir  = outDir;
        _jobs    = jobs;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk Content Builder validation (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using FileStream fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        Directory.CreateDirectory(_outDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        GraphicsDevice.Clear(Color.Black);

        foreach (RenderJob job in _jobs)
            Outcomes.Add(CompareOne(job));

        _done = true;
        Exit();
    }

    private RenderOutcome CompareOne(RenderJob job)
    {
        // Two managers, two roots, one asset name - each arm loaded exactly the way a
        // consumer's game loads it, by name.
        using var stockContent = new ContentManager(Services, job.StockRoot);
        using var sdContent    = new ContentManager(Services, job.ShadowDuskRoot);

        Effect stock;
        Effect ours;

        try
        {
            stock = stockContent.Load<Effect>(job.AssetName);
        }
        catch (Exception ex)
        {
            return new RenderOutcome(job.AssetName, false,
                $"the STOCK 3.8.5 .xnb failed to load: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            ours = sdContent.Load<Effect>(job.AssetName);
        }
        catch (Exception ex)
        {
            return new RenderOutcome(job.AssetName, false,
                $"ShadowDusk's .xnb failed Content.Load<Effect>: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Color[] stockPixels = Render(stock, job.AssetName + ".stock.png");
            Color[] ourPixels   = Render(ours, job.AssetName + ".shadowdusk.png");

            int differing = 0;
            int maxDelta = 0;
            for (int i = 0; i < stockPixels.Length; i++)
            {
                if (stockPixels[i] == ourPixels[i])
                    continue;
                differing++;
                maxDelta = Math.Max(maxDelta, Math.Max(
                    Math.Max(Math.Abs(stockPixels[i].R - ourPixels[i].R),
                             Math.Abs(stockPixels[i].G - ourPixels[i].G)),
                    Math.Max(Math.Abs(stockPixels[i].B - ourPixels[i].B),
                             Math.Abs(stockPixels[i].A - ourPixels[i].A))));
            }

            return differing == 0
                ? new RenderOutcome(job.AssetName, true,
                    $"Content.Load<Effect> OK on both arms; {stockPixels.Length} px identical")
                : new RenderOutcome(job.AssetName, false,
                    $"{differing}/{stockPixels.Length} px differ from the stock 3.8.5 build (max channel delta {maxDelta})");
        }
        catch (Exception ex)
        {
            return new RenderOutcome(job.AssetName, false, $"render threw: {ex.GetType().Name}: {ex.Message}");
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
