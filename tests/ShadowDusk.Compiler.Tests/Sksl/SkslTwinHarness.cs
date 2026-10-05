#nullable enable

using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using SkiaSharp;
using Shouldly;

namespace ShadowDusk.Compiler.Tests.Sksl;

/// <summary>
/// One <c>.fx</c> fixture and its hand-written <c>.slang</c> twin (same math, Slang spelling),
/// with the uniform values both are rendered under.
/// </summary>
/// <param name="Name">Case name, also the twin's file stem.</param>
/// <param name="FxSource">The <c>.fx</c> the twin is checked against.</param>
/// <param name="TreatVaryingsAsUniforms">Interpolants opted into per-draw uniforms (none today: COLOR0 converts by default).</param>
/// <param name="Uniforms">Value per uniform name, set identically on both renders.</param>
/// <param name="MutateFrom">Text in the twin that the positive control replaces.</param>
/// <param name="MutateTo">Replacement that must visibly change the render.</param>
internal sealed record SlangTwinCase(
    string Name,
    Func<string> FxSource,
    string[] TreatVaryingsAsUniforms,
    IReadOnlyDictionary<string, float[]> Uniforms,
    string MutateFrom,
    string MutateTo);

/// <summary>
/// The shared evidence half of the Slang-to-SkSL routes. Each twin is converted from its
/// <c>.fx</c> and from its <c>.slang</c> (by whichever Slang route the caller supplies), both
/// SkSL texts are rendered in real SkiaSharp on the CPU raster, and the two images must agree
/// within the same <see cref="Tolerance"/> the <c>.fx</c> render evidence uses (SkSL runs at
/// <c>half</c> precision). A mutated twin must NOT agree, so the comparison provably can fail.
/// </summary>
internal static class SkslTwinHarness
{
    public const int Size = 32;
    public const int Tolerance = 2;
    private const string ResolutionUniform = "ShadowDusk_Resolution";

    public delegate Result<SkslConversion, ShaderError[]> SlangRoute(string slangSource, SkslConvertOptions options);

    /// <summary>Left-right lerp between two uniform colors; the no-texture case.</summary>
    internal const string GradientFx = """
        float4 LeftColor;
        float4 RightColor;
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return lerp(LeftColor, RightColor, uv.x);
        }
        technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
        """;

    private static readonly float[] Quarter = [0.25f, 0.25f, 0.25f, 0f];

    public static readonly IReadOnlyList<SlangTwinCase> Cases =
    [
        // COLOR0 converts by default to the synthesized ShadowDusk_Color uniform (issue #368).
        new("GumGrayscale", () => Fixture("third-party", "Gum", "MonoGameInCode-Grayscale.fx"), [],
            new Dictionary<string, float[]> { ["ShadowDusk_Color"] = [0.5f, 1f, 1f, 1f] },
            "0.587", "0.887"),
        new("Gradient", () => GradientFx, [],
            new Dictionary<string, float[]>
            {
                ["LeftColor"] = [1f, 0f, 0f, 1f],
                ["RightColor"] = [0f, 0f, 1f, 1f],
            },
            "uv.x", "uv.y"),
        new("Sepia", () => Fixture("Sepia.fx"), [],
            new Dictionary<string, float[]> { ["_sepiaTone"] = [1.2f, 1.0f, 0.8f] },
            "0.59", "0.89"),
        new("Bloom", () => Fixture("Saturate.fx"), [],
            new Dictionary<string, float[]>
            {
                ["BloomThreshold"] = Quarter,
                ["BloomIntensity"] = [2f],
                ["BloomSaturation"] = [0.8f],
            },
            "saturate(color - BloomThreshold)", "saturate(color + BloomThreshold)"),
        new("Scanlines", () => Fixture("Scanlines.fx"), [],
            new Dictionary<string, float[]>
            {
                ["_attenuation"] = [0.2f],
                ["_linesFactor"] = [40f],
            },
            "sin(uv.y", "cos(uv.y"),
        new("Dots", () => Fixture("Dots.fx"), [],
            new Dictionary<string, float[]>
            {
                ["angle"] = [0.5f],
                ["scale"] = [0.5f],
                ["ScreenSize"] = [320f, 320f],
            },
            "average * 10.0", "average * 2.0"),
        new("Overlay", () => Fixture("MultiTextureOverlay.fx"), [],
            new Dictionary<string, float[]>(),
            "(2.0 * color * blend)", "(1.0 * color * blend)"),
    ];

    public static SlangTwinCase Get(string name) => Cases.Single(c => c.Name == name);

    public static string ReadFx(SlangTwinCase twin) => twin.FxSource();

    private static string Fixture(params string[] parts) => File.ReadAllText(FixturePath(parts));

    public static string ReadSlang(SlangTwinCase twin) =>
        File.ReadAllText(FixturePath("slang-sksl", twin.Name + ".slang"));

    public static SkslConvertOptions Options(SlangTwinCase twin, string sourceName) => new()
    {
        SourceName = sourceName,
        TreatVaryingsAsUniforms = twin.TreatVaryingsAsUniforms,
    };

    public static SkslConversion Succeeded(Result<SkslConversion, ShaderError[]> result) =>
        result.IsSuccess ? result.Value : throw new Xunit.Sdk.XunitException(Describe(result));

    public static string Describe(Result<SkslConversion, ShaderError[]> result) =>
        string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));

    public static void AssertParity(SlangTwinCase twin, SlangRoute route)
    {
        SkslConversion fromFx = Succeeded(SkslConverter.Convert(ReadFx(twin), Options(twin, twin.Name + ".fx")));
        SkslConversion fromSlang = Succeeded(route(ReadSlang(twin), Options(twin, twin.Name + ".slang")));

        fromSlang.ChildShaders.Count.ShouldBe(fromFx.ChildShaders.Count);
        fromSlang.SynthesizedUniforms.OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(fromFx.SynthesizedUniforms.OrderBy(n => n, StringComparer.Ordinal));

        byte[] fxPixels = Render(fromFx, twin);
        byte[] slangPixels = Render(fromSlang, twin);

        // A blank or constant render would make any two images "agree".
        DistinctPixels(fxPixels).ShouldBeGreaterThan(8,
            $"{twin.Name}: the .fx render is flat, so agreement proves nothing");

        int maxDiff = MaxChannelDiff(fxPixels, slangPixels);
        maxDiff.ShouldBeLessThanOrEqualTo(Tolerance,
            $"{twin.Name}: Slang-sourced SkSL diverges from the .fx-sourced one by {maxDiff}/255\n" +
            $"--- .fx SkSL ---\n{fromFx.SkslText}\n--- .slang SkSL ---\n{fromSlang.SkslText}");
    }

    /// <summary>The control: a twin with one deliberate math change must fall outside the tolerance.</summary>
    public static void AssertMutatedTwinDiverges(SlangTwinCase twin, SlangRoute route)
    {
        string source = ReadSlang(twin);
        source.ShouldContain(twin.MutateFrom, Case.Sensitive);
        string mutated = source.Replace(twin.MutateFrom, twin.MutateTo, StringComparison.Ordinal);

        SkslConversion fromFx = Succeeded(SkslConverter.Convert(ReadFx(twin), Options(twin, twin.Name + ".fx")));
        SkslConversion fromMutated = Succeeded(route(mutated, Options(twin, twin.Name + ".slang")));

        int maxDiff = MaxChannelDiff(Render(fromFx, twin), Render(fromMutated, twin));
        maxDiff.ShouldBeGreaterThan(Tolerance,
            $"{twin.Name}: mutating '{twin.MutateFrom}' to '{twin.MutateTo}' did not move the render " +
            "past the tolerance, so this comparison could not detect a wrong conversion");
    }

    public static byte[] Render(SkslConversion conversion, SlangTwinCase twin)
    {
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull($"Skia's own compiler rejected the emission:\n{errors}\n--- SkSL ---\n{conversion.SkslText}");

        var values = new Dictionary<string, float[]>(twin.Uniforms);
        if (effect.Uniforms.Contains(ResolutionUniform))
            values[ResolutionUniform] = [Size, Size];

        effect.Uniforms.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
            values.Keys.OrderBy(n => n, StringComparer.Ordinal),
            $"{twin.Name}: the effect's uniforms are not the ones the case sets");

        var uniforms = new SKRuntimeEffectUniforms(effect);
        foreach ((string name, float[] value) in values)
        {
            if (value.Length == 1)
                uniforms[name] = value[0];
            else
                uniforms[name] = value;
        }

        var bitmaps = new List<SKBitmap>();
        try
        {
            var children = new SKRuntimeEffectChildren(effect);
            for (int i = 0; i < conversion.ChildShaders.Count; i++)
            {
                SKBitmap bitmap = ChildPattern(i);
                bitmaps.Add(bitmap);
                children[conversion.ChildShaders[i]] = bitmap.ToShader();
            }

            using SKShader shader = effect.ToShader(uniforms, children);
            using var target = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(target))
            using (var paint = new SKPaint { Shader = shader })
            {
                canvas.DrawRect(new SKRect(0, 0, Size, Size), paint);
            }
            return target.Bytes;
        }
        finally
        {
            foreach (SKBitmap bitmap in bitmaps)
                bitmap.Dispose();
        }
    }

    // Children are bound by position: the twin and the .fx sample the same textures in the same
    // order, though the child names differ (a legacy `sampler s0` names its child differently).
    private static SKBitmap ChildPattern(int index)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float u = x / (Size - 1f), v = y / (Size - 1f);
                (float r, float g, float b) = index == 0
                    ? (u, v, 0.25f + 0.5f * ((x + y) % 2))
                    : (v, 1f - u, 0.2f + 0.6f * (((x / 4) + (y / 4)) % 2));
                bitmap.SetPixel(x, y, new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
            }
        }
        return bitmap;
    }

    public static int MaxChannelDiff(byte[] a, byte[] b)
    {
        a.Length.ShouldBe(b.Length);
        int max = 0;
        for (int i = 0; i < a.Length; i++)
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    private static int DistinctPixels(byte[] pixels)
    {
        var seen = new HashSet<uint>();
        for (int i = 0; i + 3 < pixels.Length; i += 4)
            seen.Add(BitConverter.ToUInt32(pixels, i));
        return seen.Count;
    }

    private static string FixturePath(params string[] parts)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine([dir.FullName, "tests", "fixtures", "shaders", .. parts]);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
