// ShadowDusk scratch Content Builder consumer (Phase 63, issue #203) - exactly what a MonoGame
// 3.8.5 user does:
//   dotnet new mgcb                       (the MonoGame.ContentBuilder.CSharp template)
//   dotnet add package ShadowDusk.ContentPipeline
//   new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor() in GetContentCollection
// No native installs, no flags, no extra steps: the natives must ride inside the package graph
// and load from THIS project's bin.
//
// Copied verbatim into a scratch project by .github/workflows/pack-consume.yml and by
// tools/verify-contentpipeline-packaging.ps1; it is not built in place.
//
// Usage: <exe> <Platform> [<Platform> ...]     e.g.  DesktopGL Windows
// For each platform: run a real ContentBuilder over Assets/Effects/*.fx through the ShadowDusk
// pair, then assert the .xnb payload is byte-for-byte what the SAME packed ShadowDusk.Compiler
// produces for the platform's target - so the edge from this package to the compiler and its
// natives is proven to flow to a cold consumer, not just to compile.

using Microsoft.Xna.Framework.Content.Pipeline;
using MonoGame.Framework.Content.Pipeline.Builder;
using ShadowDusk.Compiler;
using ShadowDusk.ContentPipeline;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ScratchContentBuilder <Platform> [<Platform> ...]");
    return 2;
}

string root = AppContext.BaseDirectory;
string assets = Path.Combine(root, "Assets");
string[] fixtures = Directory.GetFiles(Path.Combine(assets, "Effects"), "*.fx");
if (fixtures.Length == 0)
{
    Console.Error.WriteLine($"no .fx under {assets}/Effects");
    return 2;
}

int failed = 0;
foreach (string platformName in args)
{
    var platform = Enum.Parse<TargetPlatform>(platformName);
    PlatformTarget? target = MgcbPlatformMap.FromPlatformName(platformName);
    if (target is null)
    {
        Console.Error.WriteLine($"FAIL  {platformName}: ShadowDusk has no target for this platform");
        failed++;
        continue;
    }

    var builder = new Builder();
    bool ran = builder.Run(new ContentBuilderParams
    {
        Mode                  = ContentBuilderMode.Builder,
        WorkingDirectory      = root,
        SourceDirectory       = "Assets",
        OutputDirectory       = $"out-{platformName}",
        IntermediateDirectory = $"obj-{platformName}",
        Platform              = platform,
        Rebuild               = true,
    });

    if (!ran || builder.FailedToBuild != 0 || builder.SucceededToBuild != fixtures.Length)
    {
        Console.Error.WriteLine($"FAIL  {platformName}: ContentBuilder ran={ran} succeeded={builder.SucceededToBuild} failed={builder.FailedToBuild} (expected {fixtures.Length})");
        failed++;
        continue;
    }

    foreach (string fixture in fixtures)
    {
        string assetName = Path.GetFileNameWithoutExtension(fixture);
        string label = $"{assetName}.fx -p {platformName} -> {target}";

        string[] xnbs = Directory.GetFiles(Path.Combine(root, $"out-{platformName}"), assetName + ".xnb", SearchOption.AllDirectories);
        if (xnbs.Length != 1)
        {
            Console.Error.WriteLine($"FAIL  {label}: expected one .xnb, found {xnbs.Length}");
            failed++;
            continue;
        }

        byte[] payload = XnbPayload(File.ReadAllBytes(xnbs[0]));

        // The same packed library, called the way the processor calls it (same SourceFileName:
        // the Builder hands the importer the absolute path).
        var result = new EffectCompiler().Compile(File.ReadAllText(fixture), new CompilerOptions
        {
            Target          = target.Value,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = Path.GetFullPath(fixture),
        });

        if (result.IsFailure)
        {
            Console.Error.WriteLine($"FAIL  {label}: the packed compiler failed: " +
                                    string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
            failed++;
            continue;
        }

        if (!payload.AsSpan().SequenceEqual(result.Value.Data))
        {
            Console.Error.WriteLine($"FAIL  {label}: .xnb payload ({payload.Length} B) != the packed compiler's bytes ({result.Value.Data.Length} B)");
            failed++;
            continue;
        }

        bool magicOk = payload.Length > 4 && payload[0] == (byte)'M' && payload[1] == (byte)'G' && payload[2] == (byte)'F' && payload[3] == (byte)'X';
        if (!magicOk)
        {
            Console.Error.WriteLine($"FAIL  {label}: payload is not an MGFX container");
            failed++;
            continue;
        }

        Console.WriteLine($"OK    {label}: {payload.Length} B, MGFX v{payload[4]}, byte-identical to the packed ShadowDusk.Compiler");
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} Content Builder consumer check(s) FAILED");
    return 1;
}

Console.WriteLine($"All Content Builder consumer checks succeeded for {string.Join(", ", args)} - ShadowDusk.ContentPipeline is consumable on this OS");
return 0;

static byte[] XnbPayload(byte[] b)
{
    if (b.Length < 10 || b[0] != 'X' || b[1] != 'N' || b[2] != 'B') throw new InvalidOperationException("not an XNB file");
    if ((b[5] & 0xC0) != 0) throw new InvalidOperationException("compressed XNB is not expected here");
    int i = 10;
    int readers = Read7(b, ref i);
    for (int r = 0; r < readers; r++) { int len = Read7(b, ref i); i += len + 4; }
    Read7(b, ref i); Read7(b, ref i);
    int payloadLength = BitConverter.ToInt32(b, i); i += 4;
    return b.AsSpan(i, payloadLength).ToArray();

    static int Read7(byte[] bytes, ref int index)
    {
        int result = 0, shift = 0;
        while (true)
        {
            byte x = bytes[index++];
            result |= (x & 0x7F) << shift;
            if ((x & 0x80) == 0) return result;
            shift += 7;
        }
    }
}

/// <summary>The consumer's Builder, exactly as the package README shows it.</summary>
public sealed class Builder : ContentBuilder
{
    public override IContentCollection GetContentCollection()
    {
        var content = new ContentCollection();
        // Pass the INSTANCES: auto-discovery would pick MonoGame's own EffectImporter/EffectProcessor.
        content.Include<WildcardRule>("Effects/*.fx", new ShadowDuskEffectImporter(), new ShadowDuskEffectProcessor());
        return content;
    }
}
