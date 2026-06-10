// FnaValidation = the Phase 39 rung-3/4 proof harness.
//
// For each corpus shader it compiles BOTH arms, loads BOTH into REAL FNA
// (Effect -> FNA3D -> MojoShader; rung 3), renders BOTH (rung 4) — PS-only effects
// over the cat image via the normal SpriteBatch path, VS-driven effects through the
// custom-geometry quad scene (FnaScene.VsQuad) — and compares the pixels in-process:
//
//   Arm A (candidate): ShadowDusk in-memory, PlatformTarget.Fna
//                      (vkd3d SM<=3 + Fx2EffectWriter — the SHIPPING pipeline).
//   Arm B (reference): the system d3dcompiler_47 D3DCompile("fx_2_0") — Microsoft's
//                      own fx_2_0 compiler, byte-identical to fxc (TEST ORACLE ONLY).
//
// One process / one GraphicsDevice => same-backend comparison by construction
// (FNA3D_FORCE_DRIVER=D3D11 pins the backend). PASS = zero pixels whose max
// per-channel delta exceeds 4/255 — the same tolerance compare_dx.py applies to the
// Phase 18 cross-compiler (vkd3d-vs-mgfxc) comparison, because different compilers
// legitimately produce tiny float divergence.
//
// Exit code 0 iff every GATE shader PASSes — the Phase 17 PS-only set plus the
// VS-driven set (the 17-VS analog); FnaShaderInputs.Corpus is the authoritative
// list — and the FnaMultiPassStates non-vacuousness content guard holds.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation.Fna;

const int Tolerance = 4; // per-channel, /255 — mirrors compare_dx.py --tolerance default

// Pin the FNA3D backend BEFORE any FNA code runs (determinism; D3D11 is FNA3D's
// default on Windows anyway, but never rely on a default for a validation gate).
Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", "D3D11");

// Capture MojoShader/FNA3D error text (FNA3D logs, it does not throw). Assigning the
// hook before the Game is constructed stops FNA's default Console hook from claiming it.
var fna3dErrors = new List<string>();
FNALoggerEXT.LogError = msg => fna3dErrors.Add(msg);

string repoRoot = FnaShaderInputs.FindRepoRoot();
string catPath = FnaShaderInputs.CatPath(repoRoot);
string outRoot = Path.Combine(repoRoot, "validation", "output-fna");
string refOutDir = Path.Combine(outRoot, "reference");
string candOutDir = Path.Combine(outRoot, "candidate");

Console.WriteLine($"[fna] cat:       {catPath}");
Console.WriteLine($"[fna] reference: {refOutDir}   (d3dcompiler_47 fx_2_0 oracle)");
Console.WriteLine($"[fna] candidate: {candOutDir}   (ShadowDusk PlatformTarget.Fna)");
Console.WriteLine($"[fna] tolerance: {Tolerance}/255 per channel (compare_dx.py parity)\n");

// ---------------------------------------------------------------------------
// Compile both arms for every corpus shader
// ---------------------------------------------------------------------------

var compiler = new EffectCompiler();
var cases = new List<ShaderCase>();

foreach (FnaShaderInputs.CorpusShader shader in FnaShaderInputs.Corpus)
{
    string fx = Path.Combine(repoRoot, shader.RelativePath);
    if (!File.Exists(fx))
    {
        cases.Add(new ShaderCase(shader.Name, shader.Gate, null, $".fx not found: {fx}", null, $".fx not found: {fx}", null, shader.Scene));
        continue;
    }

    string src = await File.ReadAllTextAsync(fx);

    // PROFILE PARITY: the reference arm prepends `#define OPENGL 1` to select the
    // corpus template's ps_3_0/vs_3_0 branch (matching ShadowDusk's FNA profile
    // policy). That is only sound if the OPENGL conditional contains nothing but the
    // standard SHADERMODEL/SV_POSITION defines — verify per shader, exclude otherwise.
    string? parity = ReferenceFx2Compiler.CheckOpenGlParity(src);
    if (parity is not null)
    {
        cases.Add(new ShaderCase(shader.Name, shader.Gate,
            null, $"macro-parity-unsafe: {parity}",
            null, $"macro-parity-unsafe: {parity}",
            parity, shader.Scene));
        continue;
    }

    // Arm A — candidate: ShadowDusk in-memory (the product pipeline).
    byte[]? candBytes = null;
    string? candError = null;
    var result = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.Fna,
        SourceFileName = fx,
        IncludeResolver = new FileSystemIncludeResolver(),
    });
    if (result.IsFailure)
        candError = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
    else
        candBytes = result.Value.Data;

    // Scene-classification guard (the Appendix-G vacuous-coverage trap): a VS-bearing
    // effect misfiled under the Sprite scene runs its own VS against SpriteBatch's
    // pixel-space vertices — degenerate in BOTH arms, i.e. a vacuous PASS. Tie each
    // row's scene flag to what the compiled candidate binary actually embeds.
    if (candBytes is not null)
    {
        bool hasVs = ContainsVertexShaderStream(candBytes);
        bool expectsVs = shader.Scene == FnaScene.VsQuad;
        if (hasVs != expectsVs)
        {
            candError = $"scene misclassified: candidate .fxb {(hasVs ? "embeds" : "lacks")} " +
                        $"a vertex shader but the corpus row is FnaScene.{shader.Scene}";
            candBytes = null;
        }
    }

    // Arm B — reference: system d3dcompiler_47 fx_2_0 (oracle).
    ReferenceFx2Compiler.ReferenceResult reference = ReferenceFx2Compiler.Compile(fx, src);

    cases.Add(new ShaderCase(shader.Name, shader.Gate,
        reference.Bytes, reference.Error, candBytes, candError, null, shader.Scene));

    // Persist both arms' .fxb for offline inspection.
    string fxbDir = Path.Combine(outRoot, "fxb");
    Directory.CreateDirectory(Path.Combine(fxbDir, "reference"));
    Directory.CreateDirectory(Path.Combine(fxbDir, "candidate"));
    if (reference.Bytes is not null)
        await File.WriteAllBytesAsync(Path.Combine(fxbDir, "reference", shader.Name + ".fxb"), reference.Bytes);
    if (candBytes is not null)
        await File.WriteAllBytesAsync(Path.Combine(fxbDir, "candidate", shader.Name + ".fxb"), candBytes);
}

// ---------------------------------------------------------------------------
// Diagnostic experiment (Phase 39 Dissolve bisection): prove the prescribed
// D3d9BytecodePatcher recipe end-to-end WITHOUT touching the product pipeline.
//
// Root cause: MojoShader's MOJOSHADER_printFloat does `value = (unsigned long) arg`
// — `unsigned long` is 32-bit on Windows, so any def literal with |f| >= 2^32
// overflows (UB; observed result 0) and the translated HLSL gets "±0.0". vkd3d
// lowers `discard` as `texkill` of a register loaded with the sentinel
// -4294967296.0 (0xCF800000 = -2^32) — exactly over the limit — so MojoShader
// emits `const float4 cN = float4(…, -0.0, …)` and `-0.0 < 0.0` is false: the
// kill NEVER fires (fxc's biggest def literals are ±1, never tripping this).
//
// The recipe: clamp every finite def literal with |f| >= 2^32 to the same-signed
// largest float BELOW 2^32 (magnitude 0x4F7FFFFF = 4294967040.0), which survives
// MOJOSHADER_printFloat exactly and preserves the only thing the sentinel needs —
// its sign. These rows render the clamped candidate bytes against the unmodified
// oracle in REAL FNA; PASS = the recipe is proven on the actual failing shader.
foreach (string name in new[] { "Dissolve", "FnaProbeClip" })
{
    ShaderCase? original = cases.FirstOrDefault(c => c.Name == name);
    if (original?.CandidateBytes is null || original.ReferenceBytes is null)
        continue;
    cases.Add(original with
    {
        Name = name + "Clamped",
        Gate = false,
        CandidateBytes = ClampBigDefLiterals(original.CandidateBytes),
    });
}

// True iff the fx_2_0 binary embeds a vertex-shader token stream (version token
// 0xFFFE02xx / 0xFFFE03xx at a dword-aligned offset). Everything in the container is
// dword-aligned; no other aligned dword plausibly carries a 0xFFFE HIGH word with a
// version-shaped low word (comment tokens put 0xFFFE in the LOW word; name-pool bytes
// are ASCII < 0x80; float blobs would need a NaN payload neither arm emits).
static bool ContainsVertexShaderStream(byte[] fxb)
{
    for (int i = 0; i + 4 <= fxb.Length; i += 4)
    {
        uint tok = BitConverter.ToUInt32(fxb, i);
        if ((tok >> 16) == 0xFFFE && (tok & 0xFFFF) is >= 0x0100 and <= 0x03FF)
            return true;
    }
    return false;
}

// Walks every D3D9 SM2/SM3 token stream embedded in the fx_2_0 container and clamps
// def-instruction float literals with finite |f| >= 2^32 to sign | 0x4F7FFFFF.
// In-place single-dword rewrites — container offsets/lengths stay valid.
static byte[] ClampBigDefLiterals(byte[] fxb)
{
    byte[] patched = (byte[])fxb.Clone();
    for (int i = 0; i + 4 <= patched.Length; i += 4)
    {
        uint version = BitConverter.ToUInt32(patched, i);
        if (version is not (0xFFFF0300 or 0xFFFE0300 or 0xFFFF0200 or 0xFFFE0200))
            continue;

        int pos = i + 4;
        bool valid = true;
        var defSites = new List<int>();
        while (pos + 4 <= patched.Length)
        {
            uint tok = BitConverter.ToUInt32(patched, pos);
            if (tok == 0x0000FFFF)
                break;
            if ((tok & 0xFFFF) == 0xFFFE && (tok & 0x8000_0000) == 0)
            {
                pos += 4 + (int)((tok >> 16) & 0x7FFF) * 4; // comment (CTAB) — skip
                continue;
            }
            if ((tok & 0x8000_0000) != 0)
            {
                valid = false; // parameter token where an instruction should be — false positive
                break;
            }
            if ((tok & 0xFFFF) == 0x51) // def: dest token + 4 float literal dwords
                defSites.Add(pos);
            pos += 4 + (int)((tok >> 24) & 0xF) * 4;
        }
        if (!valid)
            continue;

        foreach (int site in defSites)
        {
            for (int c = 0; c < 4; c++)
            {
                int off = site + 8 + c * 4;
                uint bits = BitConverter.ToUInt32(patched, off);
                uint magnitude = bits & 0x7FFF_FFFF;
                if (magnitude >= 0x4F80_0000 && magnitude < 0x7F80_0000) // finite, |f| >= 2^32
                    BitConverter.GetBytes((bits & 0x8000_0000) | 0x4F7F_FFFF).CopyTo(patched, off);
            }
        }
        i = pos; // resume scanning past this stream
    }
    return patched;
}

Console.WriteLine("[fna] compile results (ref = d3dcompiler_47 oracle, cand = ShadowDusk):");
foreach (ShaderCase c in cases)
{
    Console.WriteLine($"  ref  [{(c.ReferenceBytes is null ? "FAIL" : "OK  ")}] {c.Name,-22} {(c.ReferenceCompileError ?? $"{c.ReferenceBytes!.Length} bytes")}");
    Console.WriteLine($"  cand [{(c.CandidateBytes is null ? "FAIL" : "OK  ")}] {c.Name,-22} {(c.CandidateCompileError ?? $"{c.CandidateBytes!.Length} bytes")}");
}
Console.WriteLine();

// ---------------------------------------------------------------------------
// Load + render both arms in REAL FNA (rungs 3 + 4)
// ---------------------------------------------------------------------------

List<CaseOutcome> outcomes;
using (var game = new FnaEffectImageRenderer(
    catPath, refOutDir, candOutDir, cases, FnaShaderInputs.SetParams, fna3dErrors))
{
    game.Run();
    outcomes = game.Outcomes;
}

// ---------------------------------------------------------------------------
// Compare pixels in-process and print the verdict table
// ---------------------------------------------------------------------------

static (int MaxDelta, double MeanDelta, int DiffPixels) Compare(Color[] a, Color[] b)
{
    int maxDelta = 0, diffPixels = 0;
    long sum = 0;
    for (int i = 0; i < a.Length; i++)
    {
        int dr = Math.Abs(a[i].R - b[i].R);
        int dg = Math.Abs(a[i].G - b[i].G);
        int db = Math.Abs(a[i].B - b[i].B);
        int da = Math.Abs(a[i].A - b[i].A);
        int pixelMax = Math.Max(Math.Max(dr, dg), Math.Max(db, da));
        if (pixelMax > maxDelta) maxDelta = pixelMax;
        if (pixelMax > Tolerance) diffPixels++;
        sum += dr + dg + db + da;
    }
    return (maxDelta, (double)sum / (a.Length * 4), diffPixels);
}

Console.WriteLine($"{"shader",-22} {"gate",-4} {"refload",-8} {"candload",-9} {"refrender",-10} {"candrender",-11} {"maxd",5} {"mean",8} {"diff>4",7}  verdict");
Console.WriteLine(new string('-', 110));

int gatePass = 0, gateTotal = 0;
var failures = new List<string>();

foreach (CaseOutcome o in outcomes)
{
    string verdict;
    int maxd = -1, diffPx = -1;
    double mean = -1;

    if (o.Reference.Pixels is not null && o.Candidate.Pixels is not null
        && o.Reference.Pixels.Length == o.Candidate.Pixels.Length)
    {
        (maxd, mean, diffPx) = Compare(o.Reference.Pixels, o.Candidate.Pixels);
        // PASS requires both arms to have RENDERED cleanly, not merely produced
        // comparable pixels: a symmetric soft failure (e.g. the shared SetParams
        // delegate throwing identically in both arms) yields identical wrong pixels —
        // without this clause that would count as a gate PASS while the table prints
        // refrender/candrender FAIL.
        verdict = !o.Reference.Rendered || !o.Candidate.Rendered
            ? "FAIL (render errors)"
            : diffPx == 0 ? "PASS" : "FAIL (pixels differ)";
    }
    else
    {
        verdict = "FAIL (no comparison)";
    }

    if (o.Gate)
    {
        gateTotal++;
        if (verdict == "PASS") gatePass++;
    }
    if (verdict != "PASS")
        failures.Add(o.Name);

    Console.WriteLine(
        $"{o.Name,-22} {(o.Gate ? "GATE" : "    "),-4} " +
        $"{(o.Reference.Loaded ? "ok" : "FAIL"),-8} {(o.Candidate.Loaded ? "ok" : "FAIL"),-9} " +
        $"{(o.Reference.Rendered ? "ok" : "FAIL"),-10} {(o.Candidate.Rendered ? "ok" : "FAIL"),-11} " +
        $"{(maxd >= 0 ? maxd.ToString() : "-"),5} {(mean >= 0 ? mean.ToString("F3") : "-"),8} {(diffPx >= 0 ? diffPx.ToString() : "-"),7}  {verdict}");

    if (o.Reference.Error is not null)
        Console.WriteLine($"{"",-28}ref:  {o.Reference.Error}");
    if (o.Candidate.Error is not null)
        Console.WriteLine($"{"",-28}cand: {o.Candidate.Error}");
}

Console.WriteLine(new string('-', 110));

// Non-vacuousness invariant (Appendix G): FnaMultiPassStates' candidate image must
// show pass 1 THROUGH pass 2 (cat-texture variance under the half-green overlay).
// The arm-vs-arm compare alone cannot see an FNA-side regression that stops honoring
// the in-pass blend states — BOTH arms would render the same flat green and stay
// delta-0. A flat image here means the manual 2026-06-09 "cat through half-green"
// observation no longer holds; fail the gate loudly. (The cat contributes thousands
// of distinct colors; a flat or near-flat image has almost none.)
bool nonVacuous = true;
CaseOutcome? mps = outcomes.FirstOrDefault(o => o.Name == "FnaMultiPassStates");
if (mps is not null)
{
    int distinct = mps.Candidate.Pixels?.Distinct().Count() ?? 0;
    if (distinct < 16)
    {
        nonVacuous = false;
        Console.WriteLine($"[fna] NON-VACUOUSNESS FAIL: FnaMultiPassStates candidate is near-flat " +
                          $"({distinct} distinct colors) — in-pass blend states are no longer observably honored.");
        failures.Add("FnaMultiPassStates (non-vacuous content guard)");
    }
}

// Derive the breakdown from the corpus so this line can never drift from the flags.
int gateSprite = cases.Count(c => c.Gate && c.Scene == FnaScene.Sprite);
int gateVs = cases.Count(c => c.Gate && c.Scene == FnaScene.VsQuad);
Console.WriteLine($"\n[fna] GATE: {gatePass}/{gateTotal} shaders PASS ({gateSprite} PS-only + {gateVs} VS-driven; " +
                  $"both arms compile, load in real FNA, render cleanly, zero pixels over {Tolerance}/255).");
if (failures.Count > 0)
    Console.WriteLine($"[fna] non-PASS shaders: {string.Join(", ", failures)}");
Console.WriteLine($"[fna] PNGs: {outRoot}");

return gatePass == gateTotal && nonVacuous ? 0 : 1;
