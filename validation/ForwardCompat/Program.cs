// Phase 35 Area A — FORWARD-COMPAT version-matrix cell.
//
// Identical to validation/Candidate/Program.cs in every way EXCEPT the MonoGame
// runtime version, which is supplied at build time via the
// ForwardCompatMonoGameVersion property (a project-local VersionOverride — the
// product's pin in Directory.Packages.props is untouched). The matrix runner
// (run-forwardcompat.ps1) builds this ONE project once per version in the matrix
// {3.8.2.1105 floor, 3.8.4.1 latest stable, ...} and labels each run via the
// MATRIX_VERSION_LABEL env var.
//
// Each run compiles the SM3 PS-only corpus with the UNCHANGED ShadowDusk
// EffectCompiler (default options => v10 GL .mgfx), loads those exact bytes into a
// REAL DesktopGL Effect of the selected version, renders the cat, and writes 10
// PNGs to output/versionmatrix/<label>/. A runtime-integrity guard fails the cell
// if the loaded MonoGame version doesn't match the requested label.
//
// The goal: prove the v10 .mgfx the consumer already gets loads + renders the SAME
// on every MonoGame version we support. compare_forwardcompat.py then checks every
// version's renders pixel-identical to each other (forward-compat: same bytes,
// different runtime) and within tolerance of the mgfxc baseline goldens (fidelity).

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation;

string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string catPath = ShaderInputs.CatPath(repoRoot);

// The version-matrix runner builds this ONE project once per MonoGame version
// (via -p:ForwardCompatMonoGameVersion=<v>) and labels each run via this env var,
// so each version's renders land in their own folder. The label is also the
// VersionOverride value the project was built with.
string versionLabel = Environment.GetEnvironmentVariable("MATRIX_VERSION_LABEL")?.Trim() ?? "";

// Prove which MonoGame runtime is actually loaded — this is the whole point.
var mgAsm = typeof(Microsoft.Xna.Framework.Graphics.Effect).Assembly;
string mgVersion = mgAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? mgAsm.GetName().Version?.ToString() ?? "(unknown)";

if (versionLabel.Length == 0) versionLabel = mgVersion;

// Integrity guard: if the runner told us which version to expect, the runtime
// actually loaded MUST match — otherwise the matrix cell is a lie. Compare on the
// Major.Minor.Build prefix (e.g. "3.8.4") so a +metadata suffix can't false-fail
// but 3.8.4 vs 3.8.2 can't silently pass either.
string expectPrefix = string.Join('.', versionLabel.Split('.').Take(3));
if (!mgVersion.Contains(expectPrefix))
{
    Console.Error.WriteLine(
        $"[matrix] INTEGRITY FAILURE: expected MonoGame {versionLabel} (prefix '{expectPrefix}') " +
        $"but the loaded runtime is {mgVersion}. The VersionOverride did not take effect.");
    return 2;
}

string outDir = Path.Combine(repoRoot, "validation", "output", "versionmatrix", versionLabel);

Console.WriteLine($"[matrix] MonoGame runtime: {mgAsm.GetName().Name} {mgVersion}  (label: {versionLabel})");
Console.WriteLine($"[matrix] (product pin stays 3.8.2.1105; this project uses VersionOverride)");
Console.WriteLine($"[matrix] cat: {catPath}");
Console.WriteLine($"[matrix] shaders: {shaderDir}");
Console.WriteLine($"[matrix] out: {outDir}\n");

var compiler = new EffectCompiler();
var jobs = new System.Collections.Generic.List<ShaderJob>();

foreach (var name in ShaderInputs.ShaderNames)
{
    string fx = Path.Combine(shaderDir, name + ".fx");
    if (!File.Exists(fx))
    {
        jobs.Add(new ShaderJob(name, null, $".fx not found: {fx}"));
        continue;
    }

    string src = await File.ReadAllTextAsync(fx);
    // Default options => Target=OpenGL, MgfxVersion=10. The product is unchanged.
    var result = await compiler.CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.OpenGL,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fx,
    });

    if (result.IsFailure)
    {
        jobs.Add(new ShaderJob(name, null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))));
    }
    else
    {
        // Persist the raw .mgfx so the format can be inspected/diffed offline.
        // The bytes are runtime-independent (the product is unchanged), so they
        // live once at the matrix root, not per-version.
        string mgfxDir = Path.Combine(outDir, "..", "_mgfx");
        Directory.CreateDirectory(mgfxDir);
        await File.WriteAllBytesAsync(Path.Combine(mgfxDir, name + ".mgfx"), result.Value.Data);
        jobs.Add(new ShaderJob(name, result.Value.Data, null));
    }
}

Console.WriteLine("[matrix] compile results:");
foreach (var j in jobs)
    Console.WriteLine($"  [{(j.Bytes is null ? "FAIL" : "OK  ")}] {j.Name,-12} {(j.CompileError ?? $"{j.Bytes!.Length} bytes")}");
Console.WriteLine();

using var game = new EffectImageRenderer(catPath, outDir, jobs, ShaderInputs.SetParams);
game.Run();

int ok = 0;
Console.WriteLine("[matrix] load + render results:");
foreach (var o in game.Outcomes)
{
    string status = o is { Loaded: true, Rendered: true } ? "OK  " : "FAIL";
    if (status == "OK  ") ok++;
    Console.WriteLine($"  [{status}] {o.Name,-12} {(o.Error ?? o.PngPath)}");
}
Console.WriteLine($"\n[matrix] {ok}/{game.Outcomes.Count} rendered on MonoGame {mgVersion} (label {versionLabel}).");
return ok == game.Outcomes.Count ? 0 : 1;
