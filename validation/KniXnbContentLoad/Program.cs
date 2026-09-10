// KniXnbContentLoad = the Phase 64 (issue #199) KNI rung-4 gate for the direct .xnb writer.
//
// THE CLAIM UNDER TEST: a KNI consumer replaces their content pipeline with ShadowDusk and
// "does not change any lines of code at all" - they keep calling Content.Load<Effect>("Foo").
//
// Phase 60 proved that on MonoGame WindowsDX (validation/XnbContentLoad). Phase 64 measured
// that on KNI it was BROKEN: KNI 4.2.9001's ContentTypeReaderManager.ResolveReaderType throws
// FileLoadException on the mgcb-shaped type-reader name XnbWriter used to emit (stock mgcb
// .xnb files fail there identically), and 4.3.9001 merely catches the exception. XnbWriter now
// emits the XNA-4.0 name. This driver is the proof, on a REAL KNI runtime, built once per KNI
// version (the csproj's KniVersion property):
//
//   1. builds each fixture's .xnb through STOCK dotnet-mgcb /platform:DesktopGL   (stock arm)
//   2. re-wraps the stock PAYLOAD with XnbWriter                                  (reference arm:
//      the mgfxc payload in a container every KNI version accepts, so the comparison is
//      ShadowDusk-compiler-vs-mgfxc-compiler on one runtime, never container-vs-container)
//   3. builds the same fixture through ShadowDusk + XnbWriter, MGFX v10 AND KNIFX (arms under test)
//   4. loads every arm with a real KNI ContentManager.Load<Effect>(assetName) - no `new Effect`
//   5. renders through one SpriteBatch scene and requires the arms under test to be
//      PIXEL-IDENTICAL (maxd 0) to the reference arm
//   6. asserts the KNI fact that made the fix necessary: on KNI 4.2 the STOCK arm must FAIL
//      to load (FileLoadException) - a positive control proving the reader name is what
//      matters - and on 4.3 it must load and agree with the reference arm at maxd 0.
//
// Exit 0 iff every assertion above holds for every fixture.
//
// Run (both are required; each builds against a different nkast package line):
//   dotnet tool restore
//   dotnet run --project validation/KniXnbContentLoad -c Release                          # 4.2.9001
//   dotnet run --project validation/KniXnbContentLoad -c Release -p:KniVersion=4.3.9001   # 4.3.9001

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Validation.KniXnbContentLoad;

internal static class Program
{
    /// <summary>
    /// Four fixtures: three of <c>validation/XnbContentLoad</c>'s set plus <c>Invert</c> in place of
    /// <c>SpriteEffect</c>, whose <c>TECHNIQUE()</c> macro-defined technique is the open Phase 41
    /// GAP-1 on OpenGL (<c>SD0010</c>; it compiles on DirectX only). Found when Phase 64 first ran
    /// this gate - the phase doc had listed all four of the DX gate's fixtures.
    /// </summary>
    private static readonly string[] Fixtures =
    [
        "Grayscale.fx",
        "VertexAndPixel.fx",
        "MultiTexture.fx",
        "Invert.fx",
    ];

    private const string StockArm     = "stock-mgcb";
    private const string ReferenceArm = "mgcb-payload-rewrapped";
    private const string V10Arm       = "shadowdusk-v10";
    private const string KnifxArm     = "shadowdusk-knifx";

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
        // ---- Runtime-integrity guard (the KniDesktopGL one, plus the version pin) -------------
        // Prove we are on KNI (nkast's assembly is literally "Xna.Framework.Game"; MonoGame's is
        // "MonoGame.Framework"), and that the KNI that LOADED is the line this build was asked
        // for - a stale obj/ from the other version would otherwise pass as this one.
        AssemblyName xna = typeof(Game).Assembly.GetName();
        string requested = typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "KniVersion")?.Value
            ?? throw new InvalidOperationException("KniVersion assembly metadata missing (csproj)");

        Console.WriteLine($"[kni-xnb] XNA implementation: {xna.Name} {xna.Version} (requested KNI line {requested})");
        bool isKni = xna.Name is not null
            && xna.Name.StartsWith("Xna.Framework", StringComparison.OrdinalIgnoreCase)
            && !xna.Name.StartsWith("MonoGame", StringComparison.OrdinalIgnoreCase);
        if (!isKni)
        {
            Console.WriteLine($"[kni-xnb] FAIL - expected the KNI (nkast 'Xna.Framework.*') runtime, got '{xna.Name}'.");
            return 2;
        }
        string loadedLine = $"{xna.Version!.Major}.{xna.Version.Minor}.{xna.Version.Build}";
        if (loadedLine != requested)
        {
            Console.WriteLine($"[kni-xnb] FAIL - built for KNI {requested} but KNI {loadedLine} loaded (stale build?).");
            return 2;
        }

        // The measured KNI fact (Phase 64 §2.3): 4.2.9001 throws on the mgcb-shaped reader
        // name; 4.3.9001 catches it. Anything else is unmeasured and must be classified before
        // this gate can claim it.
        bool stockMustFail = requested switch
        {
            "4.2.9001" => true,
            "4.3.9001" => false,
            _ => throw new InvalidOperationException(
                $"KNI {requested} is not a measured line - decide whether stock mgcb .xnb loads on it "
                + "(Phase 64 §2.3) and add it here before trusting a verdict"),
        };

        string repoRoot = FindRepoRoot();
        string mgcb     = LocateMgcb(repoRoot);
        string fixtures = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        string catPath  = Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");
        string outDir   = Path.Combine(repoRoot, "validation", "output-xnb-kni", requested);

        Console.WriteLine($"[kni-xnb] mgcb     : {mgcb}");
        Console.WriteLine($"[kni-xnb] fixtures : {fixtures}");
        Console.WriteLine($"[kni-xnb] out      : {outDir}");
        Console.WriteLine($"[kni-xnb] stock mgcb .xnb on this KNI line is expected to: {(stockMustFail ? "FAIL to load (FileLoadException)" : "load")}\n");

        if (!File.Exists(catPath))
            throw new FileNotFoundException($"cat image not found: {catPath}");

        string work = Path.Combine(Path.GetTempPath(), "shadowdusk_kni_xnb_gate_" + Guid.NewGuid().ToString("N"));
        var armRoots = new Dictionary<string, string>
        {
            [StockArm]     = Path.Combine(work, "content-stock"),
            [ReferenceArm] = Path.Combine(work, "content-rewrapped"),
            [V10Arm]       = Path.Combine(work, "content-shadowdusk-v10"),
            [KnifxArm]     = Path.Combine(work, "content-shadowdusk-knifx"),
        };
        foreach (string dir in armRoots.Values)
            Directory.CreateDirectory(dir);

        var jobs = new List<XnbJob>();
        int failures = 0;

        try
        {
            foreach (string fixture in Fixtures)
            {
                string assetName = Path.GetFileNameWithoutExtension(fixture);
                try
                {
                    byte[] stock = BuildStockXnb(mgcb, fixtures, work, fixture);
                    File.WriteAllBytes(Path.Combine(armRoots[StockArm], assetName + ".xnb"), stock);

                    // The reference arm: mgfxc's OWN payload in the container every KNI version
                    // accepts. The PlatformTarget overload derives 'd', exactly the byte mgcb wrote.
                    byte[] stockPayload = ExtractPayload(stock);
                    File.WriteAllBytes(Path.Combine(armRoots[ReferenceArm], assetName + ".xnb"),
                        XnbWriter.Wrap(stockPayload, PlatformTarget.OpenGL));

                    byte[] v10   = BuildShadowDuskXnb(fixtures, fixture, EffectContainer.Mgfx).GetAwaiter().GetResult();
                    byte[] knifx = BuildShadowDuskXnb(fixtures, fixture, EffectContainer.Knifx).GetAwaiter().GetResult();
                    File.WriteAllBytes(Path.Combine(armRoots[V10Arm], assetName + ".xnb"), v10);
                    File.WriteAllBytes(Path.Combine(armRoots[KnifxArm], assetName + ".xnb"), knifx);

                    // Envelope assertions that need no GPU: header identical to mgcb's
                    // ('X' 'N' 'B' 'd' 5 0), the counts and ids identical, the reader name the
                    // XNA-4.0 constant, and the payloads DIFFERENT from mgcb's (positive control
                    // that ShadowDusk, not mgcb, produced them).
                    AssertEnvelope(stock, v10, "v10");
                    AssertEnvelope(stock, knifx, "knifx");

                    jobs.Add(new XnbJob(assetName, null));
                    Console.WriteLine($"  built  {assetName}: stock {stock.Length} B, v10 {v10.Length} B, knifx {knifx.Length} B; envelopes OK");
                }
                catch (Exception ex)
                {
                    jobs.Add(new XnbJob(assetName, ex.Message));
                    failures++;
                    Console.WriteLine($"  BUILD FAIL  {assetName}: {ex.Message}");
                }
            }

            Console.WriteLine();

            using var game = new ContentLoadRenderer(catPath, outDir, armRoots, jobs);
            game.Run();

            Console.WriteLine($"[kni-xnb] Content.Load<Effect> + render results (real KNI {loadedLine} SDL2.GL):");
            foreach (XnbJob job in jobs)
            {
                if (job.BuildError is not null)
                {
                    Console.WriteLine($"  [FAIL] {job.AssetName,-16} build failed: {job.BuildError}");
                    continue;
                }

                Dictionary<string, ArmResult> arms = game.Results[job.AssetName];
                ArmResult reference = arms[ReferenceArm];
                if (reference.Pixels is null)
                {
                    failures++;
                    Console.WriteLine($"  [FAIL] {job.AssetName,-16} the REFERENCE arm (mgcb payload, XnbWriter container) failed: {reference.Error}");
                    continue;
                }

                // (a) the pinned KNI fact about stock mgcb output
                ArmResult stock = arms[StockArm];
                if (stockMustFail)
                {
                    bool failedRight = stock.Pixels is null
                        && stock.Error is not null
                        && stock.Error.Contains("FileLoadException", StringComparison.Ordinal);
                    if (failedRight)
                    {
                        Console.WriteLine($"  [OK  ] {job.AssetName,-16} stock mgcb .xnb REJECTED by KNI {loadedLine} as pinned: {stock.Error}");
                    }
                    else
                    {
                        failures++;
                        Console.WriteLine($"  [FAIL] {job.AssetName,-16} stock mgcb .xnb was expected to fail with FileLoadException on KNI {loadedLine} but "
                                          + (stock.Pixels is null ? $"failed differently: {stock.Error}" : "LOADED - KNI changed; re-measure Phase 64 §2.3 before accepting"));
                    }
                }
                else
                {
                    if (stock.Pixels is null)
                    {
                        failures++;
                        Console.WriteLine($"  [FAIL] {job.AssetName,-16} stock mgcb .xnb must load on KNI {loadedLine}: {stock.Error}");
                    }
                    else
                    {
                        (int diff, int maxd) = Compare(reference.Pixels, stock.Pixels);
                        if (diff == 0)
                            Console.WriteLine($"  [OK  ] {job.AssetName,-16} stock mgcb .xnb loads on KNI {loadedLine} and agrees with the re-wrapped payload ({reference.Pixels.Length} px, maxd 0)");
                        else
                        {
                            failures++;
                            Console.WriteLine($"  [FAIL] {job.AssetName,-16} stock vs re-wrapped: {diff} px differ (maxd {maxd}) - the container changed the render?");
                        }
                    }
                }

                // (b) the arms under test
                foreach (string arm in new[] { V10Arm, KnifxArm })
                {
                    ArmResult r = arms[arm];
                    if (r.Pixels is null)
                    {
                        failures++;
                        Console.WriteLine($"  [FAIL] {job.AssetName,-16} {arm}: Content.Load<Effect> failed: {r.Error}");
                        continue;
                    }
                    (int diff, int maxd) = Compare(reference.Pixels, r.Pixels);
                    if (diff == 0)
                        Console.WriteLine($"  [OK  ] {job.AssetName,-16} {arm}: Content.Load<Effect> OK; {r.Pixels.Length} px identical to the mgcb payload (maxd 0)");
                    else
                    {
                        failures++;
                        Console.WriteLine($"  [FAIL] {job.AssetName,-16} {arm}: {diff}/{r.Pixels.Length} px differ from the mgcb payload (maxd {maxd})");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? $"KNI {loadedLine} XNB Content.Load gate: all {Fixtures.Length} fixtures PASSED (v10 + KNIFX maxd 0 vs mgcb; stock-mgcb {(stockMustFail ? "rejection" : "load")} pinned)"
                : $"KNI {loadedLine} XNB Content.Load gate: {failures} failure(s)");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* non-fatal */ }
        }
    }

    private static (int Differing, int MaxDelta) Compare(Color[] a, Color[] b)
    {
        if (a.Length != b.Length)
            return (Math.Max(a.Length, b.Length), 255);
        int differing = 0, maxDelta = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
                continue;
            differing++;
            maxDelta = Math.Max(maxDelta, Math.Max(
                Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)),
                Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A))));
        }
        return (differing, maxDelta);
    }

    /// <summary>Stock <c>dotnet mgcb /platform:DesktopGL</c> - the reference compiler's own <c>.xnb</c>.</summary>
    private static byte[] BuildStockXnb(string mgcb, string fixtures, string work, string fixture)
    {
        string assetName = Path.GetFileNameWithoutExtension(fixture);
        string caseDir = Path.Combine(work, "mgcb_" + assetName);
        Directory.CreateDirectory(caseDir);
        string source = Path.Combine(fixtures, fixture);

        // The fixture is referenced IN PLACE (several corpus shaders #include "Macros.fxh").
        File.WriteAllLines(Path.Combine(caseDir, "build.mgcb"),
        [
            "/outputDir:bin",
            "/intermediateDir:obj",
            "/platform:DesktopGL",
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

        return File.ReadAllBytes(Path.Combine(caseDir, "bin", assetName + ".xnb"));
    }

    /// <summary>ShadowDusk compiles for OpenGL in the requested container, and <c>ToXnb()</c> wraps.</summary>
    private static async Task<byte[]> BuildShadowDuskXnb(string fixtures, string fixture, EffectContainer container)
    {
        string source = Path.Combine(fixtures, fixture);
        var compiler = new EffectCompiler();
        var result = await compiler.CompileAsync(
            await File.ReadAllTextAsync(source),
            new CompilerOptions
            {
                Target          = PlatformTarget.OpenGL,
                Container       = container,
                IncludeResolver = new FileSystemIncludeResolver(),
                SourceFileName  = source,
            });

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"ShadowDusk compile ({container}) failed: "
                + string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        }

        // The product API a consumer would call - not a driver-local reimplementation.
        return result.Value.ToXnb();
    }

    private static void AssertEnvelope(byte[] stock, byte[] ours, string label)
    {
        Envelope s = ParseEnvelope(stock);
        Envelope o = ParseEnvelope(ours);

        if (!stock.AsSpan(0, 6).SequenceEqual(ours.AsSpan(0, 6)))
            throw new InvalidOperationException($"{label}: header differs from mgcb's (platform byte / version / flags)");
        if (s.ReaderCount != o.ReaderCount || s.ReaderVersion != o.ReaderVersion
            || s.SharedResourceCount != o.SharedResourceCount || s.TypeId != o.TypeId)
            throw new InvalidOperationException($"{label}: reader count / reader version / shared-resource count / type id differ from mgcb's");
        if (o.ReaderName != XnbWriter.EffectReaderTypeName)
            throw new InvalidOperationException($"{label}: reader name is not the XNA-4.0 string: '{o.ReaderName}'");
        if (s.ReaderName == o.ReaderName)
            throw new InvalidOperationException($"{label}: mgcb now writes the XNA-4.0 name too - the stock-arm control below no longer isolates the reader name; re-measure");
        if (BitConverter.ToInt32(ours, 6) != ours.Length)
            throw new InvalidOperationException($"{label}: file-size field != actual length");
        if (stock.AsSpan(s.PayloadOffset).SequenceEqual(ours.AsSpan(o.PayloadOffset)))
            throw new InvalidOperationException($"{label}: payload equals mgcb's - ShadowDusk did not produce these bytes");
    }

    private sealed record Envelope(
        int ReaderCount, string ReaderName, int ReaderVersion, int SharedResourceCount, int TypeId, int PayloadOffset);

    private static Envelope ParseEnvelope(byte[] bytes)
    {
        if (bytes.Length < 10 || bytes[0] != 'X' || bytes[1] != 'N' || bytes[2] != 'B')
            throw new InvalidOperationException("not an XNB file");
        if ((bytes[5] & 0xC0) != 0)
            throw new InvalidOperationException("compressed XNB is not supported by this gate");

        int i = 10;
        int readerCount = Read7BitEncodedInt(bytes, ref i);
        if (readerCount != 1)
            throw new InvalidOperationException($"expected exactly one type reader for an effect, found {readerCount}");
        int nameLength = Read7BitEncodedInt(bytes, ref i);
        string name = Encoding.UTF8.GetString(bytes, i, nameLength);
        i += nameLength;
        int readerVersion = BitConverter.ToInt32(bytes, i);
        i += 4;
        int shared = Read7BitEncodedInt(bytes, ref i);
        int typeId = Read7BitEncodedInt(bytes, ref i);
        return new Envelope(readerCount, name, readerVersion, shared, typeId, i + 4);
    }

    private static byte[] ExtractPayload(byte[] xnb)
    {
        Envelope e = ParseEnvelope(xnb);
        int length = BitConverter.ToInt32(xnb, e.PayloadOffset - 4);
        return xnb.AsSpan(e.PayloadOffset, length).ToArray();
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

    private static (int ExitCode, string Output) RunProcess(string fileName, string[] arguments, string workingDirectory)
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

    /// <summary>The pinned <c>dotnet-mgcb</c> from <c>.config/dotnet-tools.json</c> (same as <c>validation/XnbContentLoad</c>).</summary>
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
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

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

/// <summary>One asset present in every content directory (or a build failure).</summary>
internal sealed record XnbJob(string AssetName, string? BuildError);

/// <summary>What one arm of one asset did inside real KNI: pixels, or the load/render error.</summary>
internal sealed record ArmResult(Color[]? Pixels, string? Error);

/// <summary>
/// Loads each asset from EVERY arm's content directory through a real KNI
/// <see cref="ContentManager"/> - <c>Content.Load&lt;Effect&gt;(assetName)</c>, the exact call a
/// consumer already has in their game - and renders each through an identical SpriteBatch path.
/// A load failure is recorded verbatim (exception type + message, inner included), never
/// swallowed: on KNI 4.2 one arm is EXPECTED to fail and the caller asserts how.
/// </summary>
internal sealed class ContentLoadRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyDictionary<string, string> _armRoots;
    private readonly IReadOnlyList<XnbJob> _jobs;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public Dictionary<string, Dictionary<string, ArmResult>> Results { get; } = new();

    public ContentLoadRenderer(
        string catPath, string outDir, IReadOnlyDictionary<string, string> armRoots, IReadOnlyList<XnbJob> jobs)
    {
        _catPath  = catPath;
        _outDir   = outDir;
        _armRoots = armRoots;
        _jobs     = jobs;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile           = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk KNI XNB Content.Load validation (headless)";
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

        foreach (XnbJob job in _jobs)
        {
            if (job.BuildError is not null)
                continue;

            var perArm = new Dictionary<string, ArmResult>();
            foreach ((string arm, string root) in _armRoots)
            {
                // One manager per arm and per asset, rooted at a bare directory holding only
                // <asset>.xnb - loaded by name, the way a consumer's game loads it.
                var content = new ContentManager(Services, root);
                Effect effect;
                try
                {
                    effect = content.Load<Effect>(job.AssetName);
                }
                catch (Exception ex)
                {
                    perArm[arm] = new ArmResult(null, Describe(ex));
                    content.Dispose();
                    continue;
                }

                try
                {
                    perArm[arm] = new ArmResult(Render(effect, $"{job.AssetName}.{arm}.png"), null);
                }
                catch (Exception ex)
                {
                    perArm[arm] = new ArmResult(null, "render threw: " + Describe(ex));
                }
                finally
                {
                    content.Unload();
                    content.Dispose();
                }
            }
            Results[job.AssetName] = perArm;
        }

        _done = true;
        Exit();
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}"
        + (ex.InnerException is null ? "" : $" | inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");

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
