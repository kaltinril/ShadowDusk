#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// <para><b>Reference input parity.</b> The reference compiles exactly the <c>.fx</c> text the
/// candidate pipeline was handed. For the 12 textured shaders that text is already in DX9
/// effect syntax: slangc emits DX10-style texture objects, which <c>fxc /T fx_2_0</c> refuses
/// and which, before this gate existed, compiled through ShadowDusk and then crashed real FNA
/// on the first draw; <c>SlangCompiler</c> now respells them for FNA
/// (<c>SlangFx2TextureRespeller</c>, issue #230), and this gate is what proves that respelling
/// renders like <c>fxc</c>'s build of the same text.</para>
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
        var textured = new HashSet<string>();

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
                if (fx.Contains("sampler_state", StringComparison.Ordinal))
                    textured.Add(name);
                string fxPath = Path.Combine(fxDir, name + ".fx");
                await File.WriteAllTextAsync(fxPath, fx);
                if (candBytes is not null)
                    await File.WriteAllBytesAsync(Path.Combine(fxDir, name + ".candidate.fxb"), candBytes);
                var reference = ReferenceFx2Compiler.Compile(fxPath, fx, prependOpenGl: false);
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
            Console.WriteLine($"  ref  [{(c.ReferenceBytes is null ? "FAIL" : "OK  ")}] {c.Name,-22} {(c.ReferenceCompileError ?? $"{c.ReferenceBytes!.Length} bytes")}{(textured.Contains(c.Name) ? " (textured: DX9 respelling)" : "")}");
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
                else if (!SameParams(o.Reference.ParamsSet, o.Candidate.ParamsSet))
                    verdict = "FAIL (parameter tables differ)";
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
            if (!SameParams(o.Reference.ParamsSet, o.Candidate.ParamsSet))
            {
                Console.WriteLine($"{"",-24}params: ref [{string.Join(", ", o.Reference.ParamsSet ?? [])}] cand [{string.Join(", ", o.Candidate.ParamsSet ?? [])}]");
            }
        }
        Console.WriteLine(new string('-', 96));

        bool ok = gatePass == gateTotal && gateTotal == corpus.Length && caught == controls && controls == 2;
        Console.WriteLine($"\n[fna-slang] GATE: {gatePass}/{gateTotal} PASS vs fxc /T fx_2_0 (real FNA Effect load + render, same device, " +
                          $"max gate delta {maxGateDelta}/255, tolerance {Tolerance}; {textured.Count} textured shaders through the DX9 respelling; " +
                          ".xnb arm maxd 0 vs the raw candidate).");
        Console.WriteLine($"[fna-slang] positive controls: {caught}/{controls} diverged from the reference as required.");
        Console.WriteLine(ok ? "[fna-slang] PASSED" : "[fna-slang] FAILED");
        Console.WriteLine($"[fna-slang] PNGs: {outRoot}");
        return ok ? 0 : 1;
    }

    // The parameters each arm's effect exposed and the gate set: they must match, as on the
    // MonoGame arms, or the two effects are not the same program to a game.
    private static bool SameParams(IReadOnlyList<string>? reference, IReadOnlyList<string>? candidate) =>
        reference is null || candidate is null
            ? reference is null && candidate is null
            : reference.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(candidate.OrderBy(n => n, StringComparer.Ordinal));

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
}
