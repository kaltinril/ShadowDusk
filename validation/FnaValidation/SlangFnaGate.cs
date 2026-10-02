#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Core;
using ShadowDusk.Slang;
using ShadowDusk.Validation.Slang;

namespace ShadowDusk.Validation.Fna;

/// <summary>
/// Issue #230: the FNA arm of the real-slangc Slang render gate (<c>dotnet run --project
/// validation/FnaValidation -c Release -- slang</c>). Same harness, same device, same
/// tolerance as the <c>.fx</c> corpus gate in Program.cs; only the inputs differ.
///
/// <list type="bullet">
/// <item><b>Candidate</b>: <see cref="SlangCompiler"/> with <see cref="PlatformTarget.Fna"/> (slangc
/// HLSL, then vkd3d-shader SM3 + <c>Fx2EffectWriter</c>) - the product route.</item>
/// <item><b>Reference</b>: Microsoft's fx_2_0 compiler (<see cref="ReferenceFx2Compiler"/>, byte-identical
/// to <c>fxc /T fx_2_0</c>) on the assembled <c>.fx</c> <see cref="SlangCompiler"/> handed the pipeline.
/// </item>
/// <item><b>Candidate via <c>.xnb</c></b>: the renderer's Phase 64 arm, the same candidate bytes
/// through a real FNA <c>ContentManager.Load&lt;Effect&gt;</c>.</item>
/// </list>
///
/// <para><b>Reference input parity.</b> slangc emits DX10-style texture objects
/// (<c>Texture2D</c> + <c>SamplerState</c> + <c>T.Sample(S, uv)</c>), and the fx_2_0 compiler
/// refuses them ("This sampler is used with a DX10-style texture intrinsic. This is not
/// implemented in this version of the compiler", measured on 13 of the 21 shaders). For those
/// shaders the reference compiles the same text with ONLY the texture declarations and sample
/// calls respelled in DX9 effect syntax (<see cref="RespellTexturesForFx2"/>): the texture
/// becomes an effect <c>texture2D</c> of the same name, the sampler a <c>sampler2D</c> on the same
/// register bound to it through <c>sampler_state</c>, and each <c>T.Sample(S, uv)</c> a
/// <c>tex2D(S, uv)</c>, which is the same SM3 texld. Anything else texture-shaped fails the row
/// loudly. The 8 texture-free shaders compile byte-for-byte as assembled.</para>
///
/// <para><b>Positive controls</b>, as on the MonoGame arms: <c>Invert</c> with two channels swapped
/// and <c>Desaturate</c> with its transform transposed run every time as rows that MUST diverge;
/// <see cref="SlangGateCorpus.ControlEnvVar"/>=1 plants them into the gated rows.</para>
/// </summary>
public static class SlangFnaGate
{
    private const int Tolerance = 4; // per channel /255, the .fx FNA gate's own tolerance

    public static async Task<int> RunAsync(List<string> fna3dErrors)
    {
        string repoRoot = SlangGateCorpus.FindRepoRoot();
        string[] corpus = SlangGateCorpus.Corpus(repoRoot);
        string outRoot = Path.Combine(repoRoot, "validation", "output-fna-slang");
        string fxDir = Path.Combine(outRoot, "reference-fx");
        string xnbWorkDir = Path.Combine(Path.GetTempPath(), "shadowdusk_fna_slang_xnb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fxDir);
        bool plantControls = SlangGateCorpus.ControlRequested();

        Console.WriteLine($"[fna-slang] corpus    : {corpus.Length} shaders (ShadowDusk.Slang real-slangc route, PlatformTarget.Fna)");
        Console.WriteLine($"[fna-slang] slangc    : {SlangToolPath.Resolve() ?? "(not found)"}");
        Console.WriteLine("[fna-slang] reference : d3dcompiler_47 fx_2_0 (== fxc /T fx_2_0) on the assembled .fx");
        Console.WriteLine($"[fna-slang] tolerance : {Tolerance}/255 per channel");
        Console.WriteLine($"[fna-slang] out       : {outRoot}");
        if (plantControls)
            Console.WriteLine($"[fna-slang] POSITIVE CONTROL ACTIVE ({SlangGateCorpus.ControlEnvVar}=1): this run is expected to FAIL");
        Console.WriteLine();

        var compiler = new SlangCompiler();
        var cases = new List<ShaderCase>();
        var respelled = new HashSet<string>();

        foreach (string file in corpus)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string source = await File.ReadAllTextAsync(file);
            FnaScene scene = SlangGateCorpus.HasVertexStage(source) ? FnaScene.VsQuad : FnaScene.Sprite;
            var options = new CompilerOptions { Target = PlatformTarget.Fna, SourceFileName = Path.GetFileName(file) };

            (byte[]? candBytes, string? candErr) = Compile(compiler, source, options);

            byte[]? refBytes = null;
            string? refErr = null;
            try
            {
                string fx = SlangGateCorpus.CaptureAssembledFx(source, Path.GetFileName(file), PlatformTarget.Fna);
                string refFx = RespellTexturesForFx2(fx, out bool changed);
                if (changed)
                    respelled.Add(name);
                string fxPath = Path.Combine(fxDir, name + ".fx");
                await File.WriteAllTextAsync(fxPath, refFx);
                var reference = ReferenceFx2Compiler.Compile(fxPath, refFx, prependOpenGl: false);
                (refBytes, refErr) = (reference.Bytes, reference.Error);
            }
            catch (Exception ex)
            {
                refErr = ex.Message;
            }

            bool isControlShader = name is SlangGateCorpus.PixelControlShader or SlangGateCorpus.VertexControlShader;
            if (plantControls && isControlShader)
                (candBytes, candErr) = Compile(compiler, Perturb(name, source), options);

            cases.Add(new ShaderCase(name, Gate: true, refBytes, refErr, candBytes, candErr, null, scene));

            if (isControlShader)
            {
                var (ctlBytes, ctlErr) = Compile(compiler, Perturb(name, source), options);
                cases.Add(new ShaderCase(name + "~control", Gate: false, refBytes, refErr, ctlBytes, ctlErr, null, scene));
            }
        }

        Console.WriteLine("[fna-slang] compile results (ref = fx_2_0 oracle, cand = ShadowDusk.Slang):");
        foreach (ShaderCase c in cases)
        {
            Console.WriteLine($"  ref  [{(c.ReferenceBytes is null ? "FAIL" : "OK  ")}] {c.Name,-22} {(c.ReferenceCompileError ?? $"{c.ReferenceBytes!.Length} bytes")}{(respelled.Contains(c.Name) ? " (DX9 texture spelling)" : "")}");
            Console.WriteLine($"  cand [{(c.CandidateBytes is null ? "FAIL" : "OK  ")}] {c.Name,-22} {(c.CandidateCompileError ?? $"{c.CandidateBytes!.Length} bytes")}");
        }
        Console.WriteLine();

        List<CaseOutcome> outcomes;
        try
        {
            using var game = new FnaEffectImageRenderer(
                SlangGateCorpus.CatPath(repoRoot),
                Path.Combine(outRoot, "reference"), Path.Combine(outRoot, "candidate"), Path.Combine(outRoot, "candidate-xnb"),
                xnbWorkDir, cases, SetParams, fna3dErrors);
            game.Run();
            outcomes = game.Outcomes;
        }
        finally
        {
            try { Directory.Delete(xnbWorkDir, recursive: true); } catch { /* non-fatal */ }
        }

        Console.WriteLine($"{"shader",-22} {"scene",-6} {"ref",-5} {"cand",-5} {"maxd",5} {"over",6} {"varied",7} {"xnb=raw",8}  verdict");
        Console.WriteLine(new string('-', 96));

        int gatePass = 0, gateTotal = 0, caught = 0, controls = 0, maxGateDelta = 0;
        foreach (CaseOutcome o in outcomes)
        {
            byte[]? r = Rgba(o.Reference.Pixels), c = Rgba(o.Candidate.Pixels), x = Rgba(o.CandidateXnb.Pixels);
            int maxd = -1, over = -1, xnbMaxd = -1;
            double varied = -1;
            string verdict;
            FnaScene scene = cases.First(k => k.Name == o.Name).Scene;

            if (r is null || c is null || !o.Reference.Rendered || !o.Candidate.Rendered)
            {
                verdict = "FAIL (load/render)";
            }
            else
            {
                (maxd, over) = SlangGateCorpus.Compare(r, c, Tolerance);
                varied = SlangGateCorpus.VariedFraction(c);
                if (x is not null && o.CandidateXnb.Rendered)
                    xnbMaxd = SlangGateCorpus.Compare(c, x, 0).MaxDelta;

                if (!o.Gate)
                    verdict = over > 0 ? "CAUGHT (control diverged, as required)" : "FAIL (control NOT detected)";
                else if (varied < 0.05)
                    verdict = "FAIL (near-constant image)";
                else if (over > 0)
                    verdict = "FAIL (pixels differ)";
                else if (xnbMaxd != 0)
                    verdict = "FAIL (.xnb arm)";
                else
                    verdict = "PASS";
            }

            if (o.Gate)
            {
                gateTotal++;
                if (verdict == "PASS") gatePass++;
                if (maxd > maxGateDelta) maxGateDelta = maxd;
            }
            else
            {
                controls++;
                if (verdict.StartsWith("CAUGHT", StringComparison.Ordinal)) caught++;
            }

            Console.WriteLine(
                $"{o.Name,-22} {(scene == FnaScene.VsQuad ? "vs" : "sprite"),-6} {(o.Reference.Rendered ? "ok" : "FAIL"),-5} {(o.Candidate.Rendered ? "ok" : "FAIL"),-5} " +
                $"{(maxd >= 0 ? maxd.ToString() : "-"),5} {(over >= 0 ? over.ToString() : "-"),6} {(varied >= 0 ? varied.ToString("P0") : "-"),7} {(xnbMaxd >= 0 ? xnbMaxd.ToString() : "-"),8}  {verdict}");
            if (o.Reference.Error is not null) Console.WriteLine($"{"",-24}ref:  {o.Reference.Error}");
            if (o.Candidate.Error is not null) Console.WriteLine($"{"",-24}cand: {o.Candidate.Error}");
            if (o.CandidateXnb.Error is not null) Console.WriteLine($"{"",-24}xnb:  {o.CandidateXnb.Error}");
            if (o.Reference.ParamsSet is not null && o.Candidate.ParamsSet is not null
                && !o.Reference.ParamsSet.OrderBy(n => n).SequenceEqual(o.Candidate.ParamsSet.OrderBy(n => n)))
            {
                Console.WriteLine($"{"",-24}params: ref [{string.Join(", ", o.Reference.ParamsSet)}] cand [{string.Join(", ", o.Candidate.ParamsSet)}]");
            }
        }
        Console.WriteLine(new string('-', 96));

        bool ok = gatePass == gateTotal && gateTotal == corpus.Length && caught == controls && controls == 2;
        Console.WriteLine($"\n[fna-slang] GATE: {gatePass}/{gateTotal} PASS vs fxc /T fx_2_0 (real FNA Effect load + render, same device, " +
                          $"max gate delta {maxGateDelta}/255, tolerance {Tolerance}; {respelled.Count} reference inputs in DX9 texture spelling; " +
                          ".xnb arm maxd 0 vs the raw candidate).");
        Console.WriteLine($"[fna-slang] positive controls: {caught}/{controls} diverged from the reference as required.");
        Console.WriteLine(ok ? "[fna-slang] PASSED" : "[fna-slang] FAILED");
        Console.WriteLine($"[fna-slang] PNGs: {outRoot}");
        return ok ? 0 : 1;
    }

    private static IReadOnlyList<string> SetParams(Effect effect, Texture2D cat, Texture2D mask)
    {
        var (set, error) = SlangShaderParams.Apply(effect, cat);
        if (error is not null)
            throw new InvalidOperationException(error);
        return set;
    }

    private static string Perturb(string name, string source) =>
        name == SlangGateCorpus.PixelControlShader
            ? SlangGateCorpus.PerturbPixel(source)
            : SlangGateCorpus.PerturbVertex(source);

    private static (byte[]? Bytes, string? Error) Compile(SlangCompiler compiler, string source, CompilerOptions options)
    {
        var result = compiler.Compile(source, options);
        return result.IsSuccess
            ? (result.Value.Data, null)
            : (null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
    }

    private static byte[]? Rgba(Color[]? pixels)
    {
        if (pixels is null)
            return null;
        var rgba = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].R;
            rgba[i * 4 + 1] = pixels[i].G;
            rgba[i * 4 + 2] = pixels[i].B;
            rgba[i * 4 + 3] = pixels[i].A;
        }
        return rgba;
    }

    // ------------------------------------------------------------------ DX9 texture respelling

    private static readonly Regex TextureDecl = new(
        @"^[ \t]*Texture2D(?:\s*<[^>]*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\(\s*t\d+\s*\))?\s*;[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SamplerDecl = new(
        @"^[ \t]*SamplerState\s+(?<name>[A-Za-z_]\w*)\s*(?<reg>:\s*register\(\s*s\d+\s*\))?\s*;[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SampleCall = new(
        @"\b(?<tex>[A-Za-z_]\w*)\s*\.\s*Sample\s*\(\s*(?<samp>[A-Za-z_]\w*)\s*,",
        RegexOptions.Compiled);

    /// <summary>
    /// Respells slangc's DX10-style texture objects in the DX9 effect syntax fx_2_0 accepts,
    /// changing nothing else. Each sampler must be used with exactly one texture; any other
    /// texture-object construct (SampleLevel, Load, a second texture type, ...) left behind
    /// throws, so a corpus change the respelling does not model turns the row red instead of
    /// compiling something different.
    /// </summary>
    internal static string RespellTexturesForFx2(string fx, out bool changed)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal); // sampler -> texture
        foreach (Match m in SampleCall.Matches(fx))
        {
            string tex = m.Groups["tex"].Value, samp = m.Groups["samp"].Value;
            if (pairs.TryGetValue(samp, out string? existing) && existing != tex)
                throw new InvalidOperationException($"sampler '{samp}' is used with two textures ('{existing}', '{tex}'); fx_2_0 cannot express that");
            pairs[samp] = tex;
        }

        changed = pairs.Count > 0 || TextureDecl.IsMatch(fx) || SamplerDecl.IsMatch(fx);
        if (!changed)
            return fx;

        string result = TextureDecl.Replace(fx, m => $"texture2D {m.Groups["name"].Value};");
        result = SamplerDecl.Replace(result, m =>
        {
            string samp = m.Groups["name"].Value;
            if (!pairs.TryGetValue(samp, out string? tex))
                throw new InvalidOperationException($"sampler '{samp}' is declared but never used in a T.Sample(S, ...) call");
            string reg = m.Groups["reg"].Success ? " " + m.Groups["reg"].Value.Trim() : "";
            return $"sampler2D {samp}{reg} = sampler_state {{ Texture = <{tex}>; }};";
        });
        result = SampleCall.Replace(result, m => $"tex2D({m.Groups["samp"].Value},");

        foreach (string leftover in new[] { "Texture2D", "SamplerState", ".Sample", "SampleLevel", ".Load(" })
        {
            if (result.Contains(leftover, StringComparison.Ordinal))
                throw new InvalidOperationException($"DX9 respelling left '{leftover}' behind; the respelling does not model this shader");
        }
        return result;
    }
}
