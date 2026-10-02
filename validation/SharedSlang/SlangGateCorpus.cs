#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ShadowDusk.Core;
using ShadowDusk.Slang;

namespace ShadowDusk.Validation.Slang;

/// <summary>
/// Issue #230: the framework-agnostic half of the real-slangc render arms that sit beside
/// <c>validation/SlangFullCorpus</c>'s DirectX_11 gate - <c>validation/SlangFullCorpusDx12</c>,
/// <c>validation/SlangFullCorpusVulkan</c> and <c>validation/FnaValidation -- slang</c>. No
/// MonoGame or FNA type appears here, so the MonoGame drivers and the FNA driver link the same
/// file.
///
/// <para><b>The evidence model, the same on every arm.</b> There is no reference compiler for
/// Slang input, but <see cref="SlangCompiler"/> hands an ordinary <c>.fx</c> (slangc's own HLSL
/// plus a synthesized technique) to the faithful pipeline, and THAT text has one: the target's
/// own reference compiler (<c>mgfxc</c> 3.8.5 for DirectX_12 and Vulkan, <c>fxc /T fx_2_0</c> for
/// FNA). Each arm captures that exact text (<see cref="CaptureAssembledFx"/>), compiles it with
/// the reference compiler, and renders the reference's effect and <see cref="SlangCompiler"/>'s
/// own output in the same real engine on the same device. <c>validation/SlangFullCorpus</c>'s
/// gate 2 separately proves the capture point is pixel-preserving against slangc's raw HLSL, so
/// together they cover the whole route.</para>
/// </summary>
public static class SlangGateCorpus
{
    /// <summary>Set to <c>1</c> to plant the positive-control defects into the GATED rows (the
    /// run is then expected to exit non-zero). Without it the controls still run every time as
    /// their own expected-divergent rows.</summary>
    public const string ControlEnvVar = "SHADOWDUSK_SLANG_CONTROL";

    /// <summary>The shader the pixel-stage control perturbs, and the exact text it swaps.</summary>
    public const string PixelControlShader = "Invert";
    private const string PixelControlFrom = "return float4(1.0 - c.rgb, c.a);";
    private const string PixelControlTo = "return float4(1.0 - c.bgr, c.a);";

    /// <summary>The shader the vertex-stage control perturbs: the matrix multiply is transposed,
    /// the issue-#145 bug class, which only a non-identity asymmetric transform can see.</summary>
    public const string VertexControlShader = "Desaturate";
    private const string VertexControlFrom = "mul(input.Position, WorldViewProjection)";
    private const string VertexControlTo = "mul(WorldViewProjection, input.Position)";

    private static readonly Regex VertexEntry = new(
        """\[\s*shader\s*\(\s*"vertex"\s*\)\s*\]""", RegexOptions.Compiled);

    /// <summary>The 21-shader real-slangc corpus, in the same order <c>validation/SlangFullCorpus</c>
    /// uses: the 17 shipped fixtures plus the four Phase 65 Gum/generics probes.</summary>
    public static string[] Corpus(string repoRoot)
    {
        string shippedDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders", "slang");
        string probeDir = Path.Combine(repoRoot, "plan", "PHASE-65-appendix", "slang-probe", "shaders");

        var files = Directory.GetFiles(shippedDir, "*.slang").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (string name in new[] { "GumGrayscale.slang", "GumTint.slang", "GumBlur.slang", "GenericsProbe.slang" })
            files.Add(Path.Combine(probeDir, name));

        string[] result = files.ToArray();
        if (result.Length < 21)
            throw new InvalidOperationException($"corpus has {result.Length} shaders; expected >= 21");
        return result;
    }

    /// <summary>True when the source declares a <c>[shader("vertex")]</c> entry, i.e. the effect
    /// ships its own vertex shader and must be drawn through geometry it can transform.</summary>
    public static bool HasVertexStage(string slangSource) => VertexEntry.IsMatch(slangSource);

    private static readonly Regex FragmentEntry = new(
        """\[\s*shader\s*\(\s*"(?:fragment|pixel)"\s*\)\s*\]\s*\w+\s+\w+\s*\((?<params>[^)]*)\)""",
        RegexOptions.Compiled);

    /// <summary>
    /// True when the fragment entry consumes a <c>COLOR</c> interpolant (directly or through its
    /// input struct), i.e. its input signature is SpriteBatch's own vertex output
    /// (<c>SV_Position, COLOR0, TEXCOORD0</c>). A PS that reads only <c>SV_Position, TEXCOORD0</c>
    /// does NOT link against SpriteBatch's vertex shader on DirectX_12 (PSO creation fails with
    /// E_INVALIDARG, measured identically for mgfxc's build and ours) and would read the wrong
    /// location on Vulkan, so such effects are drawn behind a donor vertex shader instead.
    /// </summary>
    public static bool PixelStageReadsColor(string slangSource)
    {
        Match entry = FragmentEntry.Match(slangSource);
        if (!entry.Success)
            throw new InvalidOperationException("no [shader(\"fragment\")] entry found");
        string parameters = entry.Groups["params"].Value;
        if (parameters.Contains("COLOR", StringComparison.Ordinal))
            return true;

        Match typed = Regex.Match(parameters.Trim(), @"^(?:in\s+)?(?<type>[A-Za-z_]\w*)\s+\w+$");
        if (!typed.Success)
            return false;
        Match body = Regex.Match(slangSource, @"struct\s+" + Regex.Escape(typed.Groups["type"].Value) + @"\s*\{(?<body>[^}]*)\}");
        return body.Success && body.Groups["body"].Value.Contains("COLOR", StringComparison.Ordinal);
    }

    /// <summary>
    /// The donor effect: a pass-through vertex shader whose output signature is exactly
    /// <c>SV_Position, TEXCOORD0</c>, compiled by the REFERENCE compiler (so the harness never
    /// depends on the thing under test) and used identically for both arms of a row.
    /// </summary>
    public const string DonorFx = """
        struct DonorIn  { float4 Position : POSITION0;   float2 TexCoord : TEXCOORD0; };
        struct DonorOut { float4 Position : SV_Position; float2 TexCoord : TEXCOORD0; };

        DonorOut DonorVS(DonorIn input)
        {
            DonorOut o;
            o.Position = input.Position;
            o.TexCoord = input.TexCoord;
            return o;
        }

        float4 DonorPS(DonorOut input) : SV_Target
        {
            return float4(input.TexCoord, 0, 1);
        }

        technique Donor
        {
            pass P0
            {
                VertexShader = compile vs_6_0 DonorVS();
                PixelShader = compile ps_6_0 DonorPS();
            }
        }
        """;

    public static bool ControlRequested() =>
        Environment.GetEnvironmentVariable(ControlEnvVar) == "1";

    /// <summary>The pixel-stage control: swaps two colour channels of <c>Invert.slang</c>'s result.</summary>
    public static string PerturbPixel(string invertSource) =>
        ReplaceExactlyOnce(invertSource, PixelControlFrom, PixelControlTo, PixelControlShader);

    /// <summary>The vertex-stage control: transposes <c>Desaturate.slang</c>'s transform.</summary>
    public static string PerturbVertex(string desaturateSource) =>
        ReplaceExactlyOnce(desaturateSource, VertexControlFrom, VertexControlTo, VertexControlShader);

    /// <summary>
    /// The exact <c>.fx</c> text <see cref="SlangCompiler"/> hands to its downstream compiler for
    /// <paramref name="target"/>: the same capture technique Phase 66 A3's tests and
    /// <c>validation/SlangFullCorpus</c>'s gate 2 use.
    /// </summary>
    public static string CaptureAssembledFx(string slangSource, string sourceFileName, PlatformTarget target)
    {
        var capture = new CapturingStubCompiler();
        var options = new CompilerOptions { Target = target, SourceFileName = sourceFileName };
        var result = new SlangCompiler(capture).Compile(slangSource, options);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                "SlangCompiler (capture): " + string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        }
        return capture.CapturedFxText
               ?? throw new InvalidOperationException("SlangCompiler never reached the downstream compiler");
    }

    // The shader-model header SlangCompiler.AssembleFx writes. It selects vs_3_0/ps_3_0 whenever
    // SM4 is undefined, which includes DirectX_12 and Vulkan (their macro sets define SM6, not SM4).
    // ShadowDusk compiles those targets at SM6 whatever the pass names; mgfxc 3.8.5 refuses the
    // text outright ("Invalid DirectX 12 pixel profile 'ps_3_0'! Requires ps_6_0", measured).
    private const string AssembledHeader =
        "#if SM4\n" +
        "    #define VS_SHADERMODEL vs_4_0_level_9_1\n" +
        "    #define PS_SHADERMODEL ps_4_0_level_9_1\n" +
        "#else\n" +
        "    #define VS_SHADERMODEL vs_3_0\n" +
        "    #define PS_SHADERMODEL ps_3_0\n" +
        "#endif\n";

    private const string Sm6Header =
        "#if SM6\n" +
        "    #define VS_SHADERMODEL vs_6_0\n" +
        "    #define PS_SHADERMODEL ps_6_0\n" +
        "#elif SM4\n" +
        "    #define VS_SHADERMODEL vs_4_0_level_9_1\n" +
        "    #define PS_SHADERMODEL ps_4_0_level_9_1\n" +
        "#else\n" +
        "    #define VS_SHADERMODEL vs_3_0\n" +
        "    #define PS_SHADERMODEL ps_3_0\n" +
        "#endif\n";

    /// <summary>
    /// PROFILE PARITY for the SM6 targets (the FNA gate's <c>#define OPENGL 1</c> precedent): the
    /// assembled <c>.fx</c> with ONLY its shader-model header changed so an SM6 build selects
    /// <c>vs_6_0</c>/<c>ps_6_0</c>, which is what mgfxc 3.8.5 requires. The HLSL body is untouched.
    /// The driver then proves the change is a no-op for ShadowDusk (byte-identical output from
    /// either text), so both compilers demonstrably build the same program. Throws if the header
    /// is not found verbatim: a wrapper change must turn the gate red, not silently skip parity.
    /// </summary>
    public static string WithSm6ProfileHeader(string assembledFx) =>
        ReplaceExactlyOnce(assembledFx, AssembledHeader, Sm6Header, "assembled .fx shader-model header");

    private static readonly Regex UnregisteredTexture = new(
        """^(?<decl>[ \t]*Texture2D(?:\s*<[^>;{}]*>)?\s+[A-Za-z_]\w*)\s*;""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex UnregisteredSampler = new(
        """^(?<decl>[ \t]*SamplerState\s+[A-Za-z_]\w*)\s*;""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// PROFILE PARITY for Vulkan: gives every texture/sampler declaration that has no register
    /// an explicit one, numbered in declaration order (<c>t0, t1, ...</c> / <c>s0, s1, ...</c>).
    /// Since issue #252 SlangCompiler strips slangc's own registers, and real mgfxc 3.8.5's
    /// Vulkan effect for an auto-numbered texture crashes MonoGame DesktopVK on the first draw
    /// (<c>IndexOutOfRangeException</c>, measured on all 8 textured corpus shaders; the known
    /// MonoGame SlotOffset bug behind <c>validation/CandidateVulkan</c>'s note). The driver
    /// proves the change is a no-op for ShadowDusk (byte-identical output from either text),
    /// exactly as for the SM6 header.
    /// </summary>
    public static string WithExplicitTextureRegisters(string fx)
    {
        int t = 0, s = 0;
        fx = UnregisteredTexture.Replace(fx, m => $"{m.Groups["decl"].Value} : register(t{t++});");
        return UnregisteredSampler.Replace(fx, m => $"{m.Groups["decl"].Value} : register(s{s++});");
    }

    // ---------------------------------------------------------------------------- mgfxc 3.8.5

    /// <summary>The reference compiler for DirectX_12 and Vulkan: the real <c>dotnet-mgfxc</c> 3.8.5,
    /// put in the NuGet cache by the driver's <c>PackageDownload</c> (nothing is referenced).</summary>
    public const string MgfxcVersion = "3.8.5";

    public static string LocateMgfxc()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string dll = Path.Combine(packages, "dotnet-mgfxc", MgfxcVersion, "tools", "net8.0", "any", "mgfxc.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"dotnet-mgfxc {MgfxcVersion} is not in the NuGet cache ({dll}). Restore the driver " +
                "(its PackageDownload fetches it) and try again.");
        }
        return dll;
    }

    /// <summary>Compiles <paramref name="fxPath"/> with the real mgfxc; returns the <c>.mgfx</c>
    /// bytes, or mgfxc's own output verbatim on failure.</summary>
    public static async Task<(byte[]? Bytes, string? Error)> RunMgfxcAsync(
        string mgfxcDll, string fxPath, string outPath, string profile)
    {
        if (File.Exists(outPath))
            File.Delete(outPath);

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(fxPath)!,
        };
        psi.ArgumentList.Add(mgfxcDll);
        psi.ArgumentList.Add(fxPath);
        psi.ArgumentList.Add(outPath);
        psi.ArgumentList.Add($"/Profile:{profile}");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet mgfxc");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = ((await stdout) + (await stderr)).Trim();

        if (process.ExitCode != 0 || !File.Exists(outPath))
            return (null, $"mgfxc exit {process.ExitCode}: {output}");
        return (await File.ReadAllBytesAsync(outPath), null);
    }

    // ---------------------------------------------------------------------------- comparison

    /// <summary>Max per-channel delta, and the number of pixels whose max channel delta exceeds
    /// <paramref name="tolerance"/>. Both buffers are tightly packed RGBA8.</summary>
    public static (int MaxDelta, int OverTolerance) Compare(byte[] a, byte[] b, int tolerance)
    {
        if (a.Length != b.Length)
            throw new InvalidOperationException($"image sizes differ ({a.Length} vs {b.Length} bytes)");

        int maxDelta = 0, over = 0;
        for (int p = 0; p < a.Length; p += 4)
        {
            int pixelMax = 0;
            for (int c = 0; c < 4; c++)
                pixelMax = Math.Max(pixelMax, Math.Abs(a[p + c] - b[p + c]));
            maxDelta = Math.Max(maxDelta, pixelMax);
            if (pixelMax > tolerance)
                over++;
        }
        return (maxDelta, over);
    }

    /// <summary>Fraction of pixels that differ from the first pixel: a blank or flat frame (the
    /// "renders nothing" symptom) scores near zero.</summary>
    public static double VariedFraction(byte[] rgba)
    {
        int pixels = rgba.Length / 4, varied = 0;
        for (int p = 1; p < pixels; p++)
        {
            int o = p * 4;
            if (rgba[o] != rgba[0] || rgba[o + 1] != rgba[1] || rgba[o + 2] != rgba[2] || rgba[o + 3] != rgba[3])
                varied++;
        }
        return pixels == 0 ? 0 : (double)varied / pixels;
    }

    public static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("could not locate the repository root (ShadowDusk.slnx)");
    }

    public static string CatPath(string repoRoot) =>
        Path.Combine(repoRoot, "samples", "ShaderViewer", "Content", "cat.jpg");

    private static string ReplaceExactlyOnce(string text, string from, string to, string what)
    {
        string normalized = text.Replace("\r\n", "\n");
        int first = normalized.IndexOf(from, StringComparison.Ordinal);
        if (first < 0 || normalized.IndexOf(from, first + from.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"{what}: expected exactly one occurrence of '{from.Trim()}' - the source changed; update the gate.");
        }
        return normalized.Replace(from, to, StringComparison.Ordinal);
    }

    /// <summary>Captures the assembled <c>.fx</c> and returns an empty success, so
    /// <see cref="SlangCompiler"/>'s result plumbing is satisfied.</summary>
    private sealed class CapturingStubCompiler : IShaderCompiler
    {
        public string? CapturedFxText { get; private set; }

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            CapturedFxText = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, []));
        }
    }
}
